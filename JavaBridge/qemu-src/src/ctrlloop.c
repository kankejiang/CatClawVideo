// ═══════════════════════════════════════════════════════════════════════
//  控制通道：宿主 App ⇄ guest harness
//
//  qemu 用户网络里 **10.0.2.2 = 宿主的 loopback**（SLIRP 会把 guest 发往
//  10.0.2.2:<port> 的连接转到宿主 127.0.0.1:<port>）。所以：
//    · 宿主 App 起一个极小的 HTTP 服务（控制端）
//    · guest 每秒 GET /task 问"有没有新任务"
//    · 拿到任务后驱动迅雷引擎，并把状态/播放地址 POST 回 /report
//
//  协议（纯文本，一行一条）：
//    宿主 → guest（/task 的响应体）
//      NONE
//      PING
//      TASK MAGNET <magnet-uri> <显示名>
//      TASK URL <url> <显示名>
//      STOP                 停掉当前任务
//    guest → 宿主（GET /report?ev=..&id=..&st=..&err=..&msg=..）
//      ev=ready    引擎就绪
//      ev=started  任务已创建（id=任务号）
//      ev=status   进度（st=引擎状态 err=错误码 done=/total= size）
//      ev=play     播放地址可用（msg=URL 路径）
//      ev=error    出错
//
//  ⚠️ 所有引擎调用都必须在**主线程**里做（引擎状态线程相关，别的线程会得到
//     9102 XL_SDK_NOT_INIT），所以这里是被 main_loop 顺序调用的。
// ═══════════════════════════════════════════════════════════════════════

#include <sys/select.h>
#include <dirent.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <strings.h>   // strcasecmp（KICK 命令参数解析）

// 引擎函数指针表 EngineFns 已在 harness4.c 前半部分声明

static EngineFns g_eng;
static int g_ctrl_port = 0;
static long g_task_id = 0;
static char g_task_name[256] = "";
static int g_task_is_magnet = 0;
static int g_last_st = -1, g_last_err = -1;
static long g_last_done = -1, g_last_total = -1;
// 磁力两阶段：1 = 等种子落盘（宿主去展开文件列表）；2 = 已按宿主指定文件起真下载
static int g_mag_stage = 0;
static char g_dl_target[600] = "";
static char g_mag_torrent[600] = "";   // 磁力阶段一落盘的 .torrent 绝对路径（DL 用它，宿主不必回传）
static int g_torrent_reported = 0, g_play_reported = 0;
static int g_dl_index = -1;   // 磁力下载阶段选中的子文件 index（getBtSubTaskInfo 要它）

void ctrl_register_engine(EngineFns *e) { g_eng = *e; }

// ── 网络小工具 ──
static int tcp_connect_ip(const char *ip, int port) {
    int fd = socket(AF_INET, SOCK_STREAM, 0);
    if (fd < 0) return -1;
    struct timeval tv = {3, 0};
    setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));
    setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &tv, sizeof(tv));
    struct sockaddr_in a; memset(&a, 0, sizeof a);
    a.sin_family = AF_INET; a.sin_port = htons((unsigned short)port);
    if (inet_pton(AF_INET, ip, &a.sin_addr) != 1) { close(fd); return -1; }
    if (connect(fd, (struct sockaddr *)&a, sizeof a) != 0) { close(fd); return -1; }
    return fd;
}

/** 极简 HTTP GET：返回响应体（不含头）。成功返回 0。 */
static int http_get_body(const char *ip, int port, const char *path, char *out, int outsz) {
    int fd = tcp_connect_ip(ip, port);
    if (fd < 0) return -1;
    char req[768];
    snprintf(req, sizeof req,
             "GET %s HTTP/1.0\r\nHost: %s:%d\r\nConnection: close\r\n\r\n", path, ip, port);
    if (write(fd, req, strlen(req)) < 0) { close(fd); return -1; }
    static char buf[8192];
    int n = 0, r;
    while (n < (int)sizeof(buf) - 1 && (r = (int)read(fd, buf + n, sizeof(buf) - 1 - n)) > 0) n += r;
    close(fd);
    if (n <= 0) return -1;
    buf[n] = 0;
    char *body = strstr(buf, "\r\n\r\n");
    if (!body) return -1;
    body += 4;
    snprintf(out, outsz, "%s", body);
    return 0;
}

static void url_encode(const char *in, char *out, int outsz) {
    static const char *hex = "0123456789ABCDEF";
    int j = 0;
    for (int i = 0; in && in[i] && j < outsz - 4; i++) {
        unsigned char c = (unsigned char)in[i];
        if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
            c == '-' || c == '_' || c == '.' || c == '~') {
            out[j++] = (char)c;
        } else if (c == ' ') {
            out[j++] = '+';
        } else {
            out[j++] = '%'; out[j++] = hex[c >> 4]; out[j++] = hex[c & 15];
        }
    }
    out[j] = 0;
}

/** 上报给宿主：GET /report?ev=...&id=...&st=...&err=...&done=...&total=...&msg=... */
static void ctrl_report(const char *ev, long id, int st, int err, long done, long total, const char *msg) {
    if (!g_ctrl_port) return;
    char m[1200];
    url_encode(msg ? msg : "", m, sizeof m);
    char path[1600];
    snprintf(path, sizeof path,
             "/report?ev=%s&id=%ld&st=%d&err=%d&done=%ld&total=%ld&msg=%s",
             ev, id, st, err, done, total, m);
    char body[256];
    if (http_get_body("10.0.2.2", g_ctrl_port, path, body, sizeof body) != 0)
        printf("[ctrl] （上报失败，宿主控制端没起？）\n");
}

// ── 任务 ──
static void start_task(int is_magnet, const char *uri, const char *name) {
    JNIEnv *env = (JNIEnv *)g_eng.env;
    jobject thiz = (jobject)g_eng.thiz;
    JObj *tid = new_obj("com/xunlei/downloadlib/parameter/GetTaskId");
    jint r;
    if (is_magnet) {
        typedef jint (*fn_m)(JNIEnv *, jobject, jstring, jstring, jstring, jobject);
        r = ((fn_m)g_eng.createMagnet)(env, thiz, (jstring)uri, (jstring)EMU_SAVE_PATH,
                                       (jstring)name, (jobject)tid);
    } else {
        typedef jint (*fn_p)(JNIEnv *, jobject, jstring, jstring, jstring, jstring, jstring,
                             jstring, jstring, jint, jint, jobject);
        r = ((fn_p)g_eng.createP2sp)(env, thiz, (jstring)uri, (jstring)"", (jstring)"",
                                     (jstring)"", (jstring)"", (jstring)EMU_SAVE_PATH,
                                     (jstring)name, 1, 1, (jobject)tid);
    }
    long id = obj_get_long(tid, "mTaskId");
    printf("[ctrl] 建任务(%s) 返回 %d，id=%ld\n", is_magnet ? "磁力" : "直链", (int)r, id);
    if (r != 9000 || id <= 0) {
        ctrl_report("error", id, (int)r, 0, 0, 0, "任务创建失败");
        return;
    }
    g_task_id = id;
    g_task_is_magnet = is_magnet;
    // 磁力：先只到「种子落盘」为止 —— 真正的媒体下载要等宿主展开文件列表后用 DL 命令指定
    g_mag_stage = is_magnet ? 1 : 0;
    g_dl_target[0] = 0;
    g_torrent_reported = 0; g_play_reported = 0;
    if (is_magnet) snprintf(g_mag_torrent, sizeof g_mag_torrent, "%s/%s", EMU_SAVE_PATH, name);
    else g_mag_torrent[0] = 0;
    snprintf(g_task_name, sizeof g_task_name, "%s", name);
    g_last_st = g_last_err = -1; g_last_done = g_last_total = -1;

    typedef jint (*fn_l1)(JNIEnv *, jobject, jlong);
    typedef jint (*fn_l3)(JNIEnv *, jobject, jlong, jint, jint);
    if (g_eng.startTask)
        printf("[ctrl]   startTask(%ld) → %d\n", id, (int)((fn_l1)g_eng.startTask)(env, thiz, (jlong)id));
    if (g_eng.gsState)
        printf("[ctrl]   setTaskGsState(%ld,0,2) → %d\n", id,
               (int)((fn_l3)g_eng.gsState)(env, thiz, (jlong)id, 0, 2));
    ctrl_report("started", id, 0, 0, 0, 0, name);
}

// ── 磁力第二阶段：宿主展开 .torrent 得到文件列表后，指定要下的那个文件 ──
//   严格对齐手机端 XLTaskHelper.addTorrentTask() 的反编译结果：
//     getTorrentInfo → createBtTask(param{createMode=1, filePath=dir, maxConcurrent=3, seqId=递增})
//     → [多文件] deselectBtSubTask(未选中的那些)  ← 是**反选**，不是正选
//     → startTask → setTaskGsState(id, 被选中的 index, 2)   ← 第二参是索引，不是 0
static void start_dl(const char *torrentPath, const char *dir, const char *relPath,
                     int index, const char *excludeCsv) {
    JNIEnv *env = (JNIEnv *)g_eng.env;
    jobject thiz = (jobject)g_eng.thiz;
    if (!g_eng.createBtTask) { ctrl_report("error", 0, 0, 0, 0, 0, "引擎没有 createBtTask"); return; }
    // 种子路径：宿主可传 "-" 表示「就用我刚下发的那条磁力落盘的种子」
    if (!torrentPath[0] || !strcmp(torrentPath, "-")) {
        if (!g_mag_torrent[0]) { ctrl_report("error", 0, 0, 0, 0, 0, "没有已知种子路径"); return; }
        torrentPath = g_mag_torrent;
    }
    printf("[ctrl] 用种子 %s\n", torrentPath);

    // ★ 对齐手机 addTorrentTask 的**第一行**：引擎自己解析一遍种子建内部索引。
    //   少了这一步，BT 任务会 setTaskGsState → 9303 INDEX_NOT_READY 并以 114004 收场。
    if (g_eng.sdk) {
        typedef jint (*fn_tinfo)(JNIEnv *, jobject, jstring, jobject);
        fn_tinfo getTorrentInfo = (fn_tinfo)dlsym((void *)g_eng.sdk,
                                   "Java_com_xunlei_downloadlib_XLLoader_getTorrentInfo");
        if (getTorrentInfo) {
            JObj *tinfo = new_obj("com/xunlei/downloadlib/parameter/TorrentInfo");
            jint tr = getTorrentInfo(env, (jobject)thiz, (jstring)torrentPath, (jobject)tinfo);
            printf("[ctrl]   getTorrentInfo(%s) → %d（建索引）\n", torrentPath, (int)tr);
        } else {
            printf("[ctrl]   ⚠ 找不到 getTorrentInfo\n");
        }
    }

    // ★ 先把下载目录建出来 —— 迅雷不一定会自己 mkdir；目录不存在时任务会"跑着但零字节"
    mkdir(dir, 0755);
    {
        struct stat sb2;
        printf("[ctrl]   下载目录 %s → %s\n", dir, stat(dir, &sb2) == 0 ? "已存在" : "创建失败");
    }

    static int s_seq = 0;
    typedef jint (*fn_bttask)(JNIEnv *, jobject, jstring, jstring, jint, jint, jint, jobject);
    JObj *tid = new_obj("com/xunlei/downloadlib/parameter/GetTaskId");
    jint r = ((fn_bttask)g_eng.createBtTask)(env, thiz, (jstring)torrentPath, (jstring)dir,
                                             3 /*maxConcurrent*/, 1 /*createMode*/, ++s_seq /*seqId 递增*/,
                                             (jobject)tid);
    long id = obj_get_long(tid, "mTaskId");
    printf("[ctrl] 建下载任务(BT) 返回 %d（9000=成功），id=%ld seq=%d\n", (int)r, id, s_seq);
    // ★ 9128 自愈：同 btih 的任务句柄还挂在引擎里（引擎任务中途死亡 err=114010 后宿主重发 DL 必现，
    //   引擎侧没有 deleteTask）。dlsym stopTask 停掉旧任务 → 重新 getTorrentInfo + createBtTask 重试一次；
    //   下载目录里的已下载数据还在（tmpfs），引擎重建任务后续传，不必从头下。
    if ((r != 9000 || id <= 0) && r == 9128 && g_task_id > 0 && g_eng.sdk) {
        typedef jint (*fn_stop)(JNIEnv *, jobject, jlong);
        fn_stop stopTask = (fn_stop)dlsym((void *)g_eng.sdk,
                                          "Java_com_xunlei_downloadlib_XLLoader_stopTask");
        printf("[ctrl]   9128=同种子任务已存在(id=%ld)，尝试 stopTask 清理（fn=%p）\n", g_task_id, (void *)stopTask);
        if (stopTask) {
            int sr = (int)stopTask(env, (jobject)thiz, (jlong)g_task_id);
            printf("[ctrl]   stopTask(%ld) → %d（9000=成功；9104/9119=任务不存在/未运行）\n", g_task_id, sr);
            if (g_eng.sdk) {   // 引擎内部索引可能随任务一起被清，重建一遍保险（手机端也是每次先取）
                typedef jint (*fn_tinfo)(JNIEnv *, jobject, jstring, jobject);
                fn_tinfo getTorrentInfo = (fn_tinfo)dlsym((void *)g_eng.sdk,
                                           "Java_com_xunlei_downloadlib_XLLoader_getTorrentInfo");
                if (getTorrentInfo) {
                    JObj *tinfo = new_obj("com/xunlei/downloadlib/parameter/TorrentInfo");
                    printf("[ctrl]   getTorrentInfo 重取 → %d\n",
                           (int)getTorrentInfo(env, (jobject)thiz, (jstring)torrentPath, (jobject)tinfo));
                }
            }
            jint r2 = ((fn_bttask)g_eng.createBtTask)(env, thiz, (jstring)torrentPath, (jstring)dir,
                                                      3, 1, ++s_seq, (jobject)tid);
            long id2 = obj_get_long(tid, "mTaskId");
            printf("[ctrl]   重试建下载任务(BT) 返回 %d，id=%ld seq=%d\n", (int)r2, id2, s_seq);
            if (r2 == 9000 && id2 > 0) { r = r2; id = id2; }
        }
    }
    if (r != 9000 || id <= 0) { ctrl_report("error", id, (int)r, 0, 0, 0, "BT 下载任务创建失败"); return; }

    // 资源开关：早期「救火」时加的。⚠ 2026-09-16 A/B 实锤：手机端 jar 里**没有**这些调用
    //   （XLTaskHelper 只调 createBtTask/deselect/start/gsState），**发了就零速度**（四通道全 0，
    //   见 mag27），不发则 P2P 满速（mag26/mag28）。默认不发，EXTRA=1 才发（仅实验用）。
    if (g_eng.sdk && getenv("EXTRA")) {
        typedef int (*fn_allow)(long long, int);
        typedef int (*fn_sw)(long long);
        fn_allow allowRes  = (fn_allow)dlsym((void *)g_eng.sdk, "XLSetTaskAllowUseResource");
        fn_sw   switchRes  = (fn_sw)dlsym((void *)g_eng.sdk, "XLSwitchOriginToAllResDownload");
        if (allowRes)  printf("[ctrl]   XLSetTaskAllowUseResource(%ld,1) → %d\n", id, allowRes((long long)id, 1));
        if (switchRes) printf("[ctrl]   XLSwitchOriginToAllResDownload(%ld) → %d\n", id, switchRes((long long)id));
    }
    // 反选：把「不要的文件」剔掉（对齐 addTorrentTask 的行为）
    if (g_eng.sdk && excludeCsv && excludeCsv[0]) {
        typedef jint (*fn_sel)(JNIEnv *, jobject, jlong, jobject);
        fn_sel desel = (fn_sel)dlsym((void *)g_eng.sdk,
                                     "Java_com_xunlei_downloadlib_XLLoader_deselectBtSubTask");
        int idxs[64], ni = 0;
        for (const char *q = excludeCsv; *q && ni < 64; ) {
            idxs[ni++] = atoi(q);
            while (*q && *q != ',') q++;
            if (*q == ',') q++;
        }
        if (desel && ni > 0) {
            JObj *iset = new_obj("com/xunlei/downloadlib/parameter/BtIndexSet");
            JIntArray *ia = new_int_array(ni);
            for (int i = 0; i < ni; i++) ia->v[i] = idxs[i];
            put_field(iset, mk_fid("mIndexSet", "[I"), 3, 0, ia);
            printf("[ctrl]   deselectBtSubTask(%ld, %d 个：", id, ni);
            for (int i = 0; i < ni; i++) printf("%s%d", i ? "," : "", idxs[i]);
            printf(") → %d\n", (int)desel(env, (jobject)thiz, (jlong)id, (jobject)iset));
        } else {
            printf("[ctrl]   ⚠ 找不到 deselectBtSubTask 或反选清单为空\n");
        }
    }
    typedef jint (*fn_l1)(JNIEnv *, jobject, jlong);
    typedef jint (*fn_l3)(JNIEnv *, jobject, jlong, jint, jint);
    if (g_eng.startTask)
        printf("[ctrl]   startTask(%ld) → %d\n", id, (int)((fn_l1)g_eng.startTask)(env, thiz, (jlong)id));
    if (g_eng.gsState)
        printf("[ctrl]   setTaskGsState(%ld,%d,2) → %d\n", id, index,
               (int)((fn_l3)g_eng.gsState)(env, thiz, (jlong)id, (jint)index, 2));
    // ★ 边下边播的「预取模式」+ 重查索引（同上：A/B 实锤发了有害；默认不发，EXTRA=1 才发）
    if (g_eng.sdk && getenv("EXTRA")) {
        typedef int (*fn_l1x)(long long);
        fn_l1x prefetch = (fn_l1x)dlsym((void *)g_eng.sdk, "XLEnterPrefetchMode");
        fn_l1x requery  = (fn_l1x)dlsym((void *)g_eng.sdk, "XLRequeryIndex");
        if (prefetch) printf("[ctrl]   XLEnterPrefetchMode(%ld) → %d\n", id, prefetch((long long)id));
        if (requery)  printf("[ctrl]   XLRequeryIndex(%ld) → %d\n", id, requery((long long)id));
    }

    g_task_id = id;
    g_task_is_magnet = 1;
    g_mag_stage = 2;
    g_dl_index = index;
    snprintf(g_dl_target, sizeof g_dl_target, "%s/%s", dir, relPath);
    snprintf(g_task_name, sizeof g_task_name, "%s", relPath);
    g_last_st = g_last_err = -1; g_last_done = g_last_total = -1;
    g_play_reported = 0; g_torrent_reported = 0;
    printf("[ctrl] 下载目标 = %s\n", g_dl_target);
    ctrl_report("started", id, 0, 0, 0, 0, relPath);
}

// ── 调试：列目录（看引擎到底有没有落文件）──
static void list_dir(const char *path) {
    DIR *d = opendir(path);
    if (!d) { printf("[ctrl] LS %s → 打不开\n", path); ctrl_report("ls", 0, 0, 0, 0, 0, "打不开"); return; }
    struct dirent *e;
    static char buf[1100];
    buf[0] = 0;
    printf("[ctrl] LS %s:\n", path);
    while ((e = readdir(d))) {
        if (!strcmp(e->d_name, ".") || !strcmp(e->d_name, "..")) continue;
        printf("[ctrl]   %s\n", e->d_name);
        if (strlen(buf) < sizeof buf - 160) { strncat(buf, e->d_name, sizeof buf - strlen(buf) - 1); strncat(buf, " | ", sizeof buf - strlen(buf) - 1); }
    }
    closedir(d);
    ctrl_report("ls", 0, 0, 0, 0, 0, buf);
}

// ── 调试：读文件尾部（最多 1100 字节，走 msg 通道）──
static void cat_file(const char *path) {
    FILE *f = fopen(path, "rb");
    if (!f) { printf("[ctrl] CAT %s → 打不开\n", path); ctrl_report("cat", 0, 0, 0, 0, 0, "打不开"); return; }
    static char buf[8192];
    size_t n = fread(buf, 1, sizeof buf - 1, f);
    fclose(f);
    if (n > 1100) memmove(buf, buf + (n - 1100), 1100), n = 1100;
    buf[n] = 0;
    for (size_t i = 0; i < n; i++) {
        unsigned char c = (unsigned char)buf[i];
        if ((c < 9) || (c > 13 && c < 32) || c > 126) buf[i] = '.';
    }
    printf("[ctrl] CAT %s（%d 字节，取尾部）:\n%s\n", path, (int)n, buf);
    ctrl_report("cat", 0, 0, 0, 0, 0, buf);
}

// ── 调试：打开引擎自己的日志（XLSetReleaseLog；README 说在 init 路径下会崩，这里晚调试试）──
static void enable_engine_log(void) {
    if (!g_eng.sdk) return;
    typedef jint (*fn_ison)(void);
    fn_ison fOn = (fn_ison)dlsym((void *)g_eng.sdk, "XLIsLogTurnOn");
    if (fOn) printf("[ctrl] XLIsLogTurnOn() = %d\n", (int)fOn());
    typedef int (*fn_rel)(int, void *);
    fn_rel fRel = (fn_rel)dlsym((void *)g_eng.sdk, "XLSetReleaseLog");
    if (fRel) {
        struct { const char *path; unsigned pathSize, maxCount, maxSize; } cfg;
        cfg.path = "/thunder-data";
        cfg.pathSize = (unsigned)strlen(cfg.path);
        cfg.maxCount = 5; cfg.maxSize = 4 * 1024 * 1024;
        printf("[ctrl] 调 XLSetReleaseLog(1, {%s})...\n", cfg.path);
        fflush(stdout);
        printf("[ctrl] ← XLSetReleaseLog 返回 %d\n", fRel(1, &cfg));
        if (fOn) printf("[ctrl] XLIsLogTurnOn() 现在 = %d\n", (int)fOn());
    }
}

/** 每次轮询：取任务状态，必要时上报播放地址 */
static void poll_task(void) {
    if (g_task_id <= 0 || !g_eng.getTaskInfo) return;
    JNIEnv *env = (JNIEnv *)g_eng.env;
    jobject thiz = (jobject)g_eng.thiz;
    typedef jint (*fn_ti)(JNIEnv *, jobject, jlong, jint, jobject);
    JObj *ti = new_obj("com/xunlei/downloadlib/parameter/XLTaskInfo");
    jint r = ((fn_ti)g_eng.getTaskInfo)(env, thiz, (jlong)g_task_id, 0, (jobject)ti);
    int st = obj_get_int(ti, "mTaskStatus");
    int err = obj_get_int(ti, "mErrorCode");
    long done = obj_get_long(ti, "mDownloadSize");
    long total = obj_get_long(ti, "mFileSize");
    printf("[ctrl] t=%ld st=%d err=%d 已下载=%ld/%ld 速度=%ld\n",
           (long)time(NULL), st, err, done, total, obj_get_long(ti, "mDownloadSpeed"));
    // ★ 诊断：CID/GCID = 迅雷给内容算的索引号；mQueryIndexStatus = 「向 hub 查索引」的状态。
    //   BT 任务报 114004 时，看这两个就能判断是「没算出来」还是「hub 拒了」。
    printf("[ctrl]   diag cid=%s gcid=%s queryIdx=%d infoLen=%d 附加源=%ld\n",
           obj_get_str(ti, "mCid"), obj_get_str(ti, "mGcid"),
           obj_get_int(ti, "mQueryIndexStatus"), obj_get_int(ti, "mInfoLen"),
           obj_get_long(ti, "mAdditionalResCount"));
    printf("[ctrl]   speed P2S=%ld P2P=%ld Scdn=%ld Origin=%ld 慢速源=%ld\n",
           obj_get_long(ti, "mP2SSpeed"), obj_get_long(ti, "mP2PSpeed"),
           obj_get_long(ti, "mScdnSpeed"), obj_get_long(ti, "mOriginSpeed"),
           obj_get_long(ti, "mAdditionalResPeerBytes"));
    if (st != g_last_st || err != g_last_err || done != g_last_done) {
        g_last_st = st; g_last_err = err; g_last_done = done; g_last_total = total;
        if (r == 9000) ctrl_report("status", g_task_id, st, err, done, total, "");
    }
    // ★ 复现手机 playFile() 的关键动作：起下载后**每秒 getBtSubTaskInfo(tid, index)**。
    //   少了它，引擎可能一直停在"已建任务但不拉数据"（手机端就是在循环里调它直到能播）。
    if (g_mag_stage == 2 && g_dl_index >= 0 && g_eng.getBtSubTaskInfo) {
        typedef jint (*fn_bti)(JNIEnv *, jobject, jlong, jint, jobject);
        JObj *det = new_obj("com/xunlei/downloadlib/parameter/BtSubTaskDetail");
        jint r2 = ((fn_bti)g_eng.getBtSubTaskInfo)(env, thiz, (jlong)g_task_id, g_dl_index, (jobject)det);
        printf("[ctrl]   getBtSubTaskInfo(%ld,%d) → %d\n", g_task_id, g_dl_index, (int)r2);
    }

    // 磁力阶段一：种子落盘就上报，宿主去展开文件列表（.torrent 是 bencode，宿主侧解析更省事）
    if (g_mag_stage == 1 && g_task_id > 0 && g_task_name[0]) {
        char tp[600];
        snprintf(tp, sizeof tp, "%s/%s", EMU_SAVE_PATH, g_task_name);
        struct stat sb;
        if (stat(tp, &sb) == 0 && sb.st_size > 100 && !g_torrent_reported) {
            g_torrent_reported = 1;
            printf("[ctrl] 🌱 种子已落盘：%s（%ld 字节）\n", tp, (long)sb.st_size);
            ctrl_report("torrent", g_task_id, st, err, done, total, tp);
        }
    }

    // 有数据了就试着拿播放地址（每次都要重新取：引擎的本地服务是一次性的）
    if ((st == 1 || st == 2 || st == 4) && g_eng.localUrl && g_task_name[0]) {
        typedef jint (*fn_lu)(JNIEnv *, jobject, jstring, jobject);
        // 候选路径：磁力阶段二用宿主指定的绝对路径；再兜底「保存目录 + 文件名」
        // （引擎把文件放哪取决于种子的 info 结构，多试一条更稳）
        char cand[2][600]; int nc = 0;
        if (g_mag_stage == 2 && g_dl_target[0]) {
            snprintf(cand[nc++], sizeof cand[0], "%s", g_dl_target);
            const char *bs = strrchr(g_task_name, '/');
            snprintf(cand[nc++], sizeof cand[0], "%s/%s", EMU_SAVE_PATH, bs ? bs + 1 : g_task_name);
        } else {
            snprintf(cand[nc++], sizeof cand[0], "%s/%s", EMU_SAVE_PATH, g_task_name);
        }
        for (int ci = 0; ci < nc; ci++) {
            JObj *lu = new_obj("com/xunlei/downloadlib/parameter/XLTaskLocalUrl");
            jint lr = ((fn_lu)g_eng.localUrl)(env, thiz, (jstring)cand[ci], (jobject)lu);
            if (lr != 9000) { printf("[ctrl]   getLocalUrl(%s) → %d\n", cand[ci], (int)lr); continue; }
            const char *u = obj_get_str(lu, "mStrUrl");
            if (!u || u[0] != 'h') continue;
            // 存起来给代理用（新端口！）
            const char *p = u + 7;
            const char *colon = strchr(p, ':');
            if (colon) {
                int np = atoi(colon + 1);
                if (np > 0) g_engine_port = np;
            }
            snprintf(g_engine_abs, sizeof g_engine_abs, "%s", cand[ci]);
            const char *slash = strchr(p, '/');
            if (slash) snprintf(g_engine_path, sizeof g_engine_path, "%s", slash);
            printf("[ctrl] ✅ 播放地址 = %s\n", u);
            if (!g_play_reported) {
                g_play_reported = 1;
                ctrl_report("play", g_task_id, st, err, done, total, slash ? slash : "");
            }
            break;
        }
    }
}

// ── 主事件循环：控制通道 + 代理握手都在这个线程里跑（引擎要求）──
static void main_loop(void) {
    long last_poll = 0;
    char body[2048];
    for (;;) {
        // ① 代理线程的"重新武装"请求
        if (g_rearm_req) {
            g_rearm_req = 0;
            g_rearm_port = do_rearm();
            g_rearm_done = 1;
        }
        // ② 控制通道：每秒问一次
        if (g_ctrl_port && http_get_body("10.0.2.2", g_ctrl_port, "/task", body, sizeof body) == 0) {
            char *cmd = body;
            while (*cmd == ' ' || *cmd == '\n' || *cmd == '\r') cmd++;
            if (!strncmp(cmd, "TASK ", 5)) {
                // TASK MAGNET|URL <uri> <name>
                char kind[16] = "", uri[1024] = "", name[256] = "";
                if (sscanf(cmd + 5, "%15s %1023s %255s", kind, uri, name) >= 2) {
                    start_task(!strcmp(kind, "MAGNET"), uri, name[0] ? name : "download.bin");
                }
            } else if (!strncmp(cmd, "DL ", 3)) {
                // DL <torrentPath>|<dir>|<relPath>|<index>|<exclude-csv> —— 用 | 分隔，路径里的空格不会拆错
                char *parts[5] = {0}; int np = 0;
                parts[np++] = cmd + 3;
                for (char *q = cmd + 3; *q && np < 5; q++) {
                    if (*q == '|') { *q = 0; parts[np++] = q + 1; }
                }
                {
                    char *e = parts[np - 1];
                    size_t L = strlen(e);
                    while (L && (e[L - 1] == '\n' || e[L - 1] == '\r')) e[--L] = 0;
                }
                if (np >= 4) start_dl(parts[0], parts[1], parts[2], atoi(parts[3]), np >= 5 ? parts[4] : "");
                else ctrl_report("error", 0, 0, 0, 0, 0, "DL 参数不完整");
            } else if (!strncmp(cmd, "CAT ", 4)) {
                char *pp = cmd + 4; while (*pp == ' ') pp++;
                char *ee = pp + strlen(pp); while (ee > pp && (ee[-1] == '\n' || ee[-1] == '\r')) *--ee = 0;
                cat_file(pp);
            } else if (!strncmp(cmd, "RELLOG", 6)) {
                enable_engine_log();
            } else if (!strncmp(cmd, "LS", 2)) {
                char *pp = cmd + 2; while (*pp == ' ') pp++;
                char *ee = pp + strlen(pp); while (ee > pp && (ee[-1] == '\n' || ee[-1] == '\r')) *--ee = 0;
                list_dir(pp[0] ? pp : EMU_SAVE_PATH);
            } else if (!strncmp(cmd, "KICK", 4)) {
                // KICK [REQUERY|PREFETCH|BOTH]：任务运行中对引擎「踢一脚」（宿主按需触发）。
                //   REQUERY  = XLRequeryIndex：向 hub 重查资源（实测源断光→速度归零→114010 自杀，
                //              疑似 hub 资源列表过期；创建时发有害，运行中救援未验证——本命令就是实验入口）
                //   PREFETCH = XLEnterPrefetchMode：边下边播预取模式（引擎按读位置优先下载 → seek 到哪下到哪）
                //   与 EXTRA=1 的区别：EXTRA 在任务创建瞬间连发四个调用（A/B 实锤零速度），KICK 由宿主
                //   在任务健康运行后按需单发，时序不同，不受该结论约束。
                const char *ka = cmd + 4; while (*ka == ' ') ka++;
                char *ee = (char *)ka + strlen(ka); while (ee > ka && (ee[-1] == '\n' || ee[-1] == '\r')) *--ee = 0;
                if (g_task_id > 0 && g_eng.sdk) {
                    typedef int (*fn_l1x)(long long);
                    fn_l1x requery  = (fn_l1x)dlsym((void *)g_eng.sdk, "XLRequeryIndex");
                    fn_l1x prefetch = (fn_l1x)dlsym((void *)g_eng.sdk, "XLEnterPrefetchMode");
                    int wantRq = (ka[0] == 0 || !strcasecmp(ka, "REQUERY") || !strcasecmp(ka, "BOTH"));
                    int wantPf = (!strcasecmp(ka, "PREFETCH") || !strcasecmp(ka, "BOTH"));
                    int rq = -1, pf = -1;
                    if (wantRq && requery)  rq = requery((long long)g_task_id);
                    if (wantPf && prefetch) pf = prefetch((long long)g_task_id);
                    printf("[ctrl] KICK %s → requery=%d prefetch=%d（task=%ld；fn=%p/%p）\n",
                           ka[0] ? ka : "BOTH", rq, pf, g_task_id, (void *)requery, (void *)prefetch);
                    char m[80];
                    snprintf(m, sizeof m, "requery=%d prefetch=%d", rq, pf);
                    ctrl_report("kick", g_task_id, 0, 0, 0, 0, m);
                } else {
                    printf("[ctrl] KICK 忽略：无活动任务（id=%ld）\n", g_task_id);
                    ctrl_report("kick", g_task_id, 0, 0, 0, 0, "no-task");
                }
            } else if (!strncmp(cmd, "STOP", 4)) {
                if (g_task_id > 0 && g_eng.startTask) { g_task_id = 0; ctrl_report("stopped", 0, 0, 0, 0, 0, ""); }
            } else if (!strncmp(cmd, "PING", 4)) {
                ctrl_report("pong", g_task_id, g_last_st, g_last_err, g_last_done, g_last_total, "");
            }
        }
        // ③ 任务状态：每 5 秒
        long now = (long)time(NULL);
        if (g_task_id > 0 && now - last_poll >= 5) { last_poll = now; poll_task(); }
        usleep(200000);
    }
}
