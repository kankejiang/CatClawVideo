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

// 引擎函数指针表 EngineFns 已在 harness4.c 前半部分声明

static EngineFns g_eng;
static int g_ctrl_port = 0;
static long g_task_id = 0;
static char g_task_name[256] = "";
static int g_task_is_magnet = 0;
static int g_last_st = -1, g_last_err = -1;
static long g_last_done = -1, g_last_total = -1;

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
    if (st != g_last_st || err != g_last_err || done != g_last_done) {
        g_last_st = st; g_last_err = err; g_last_done = done; g_last_total = total;
        if (r == 9000) ctrl_report("status", g_task_id, st, err, done, total, "");
    }
    // 有数据了就试着拿播放地址（每次都要重新取：引擎的本地服务是一次性的）
    if ((st == 1 || st == 2 || st == 4) && g_eng.localUrl && g_task_name[0]) {
        char abs[600];
        snprintf(abs, sizeof abs, "%s/%s", EMU_SAVE_PATH, g_task_name);
        typedef jint (*fn_lu)(JNIEnv *, jobject, jstring, jobject);
        JObj *lu = new_obj("com/xunlei/downloadlib/parameter/XLTaskLocalUrl");
        jint lr = ((fn_lu)g_eng.localUrl)(env, thiz, (jstring)abs, (jobject)lu);
        if (lr == 9000) {
            const char *u = obj_get_str(lu, "mStrUrl");
            if (u && u[0] == 'h') {
                // 存起来给代理用（新端口！）
                const char *p = u + 7;
                const char *colon = strchr(p, ':');
                if (colon) {
                    int np = atoi(colon + 1);
                    if (np > 0) g_engine_port = np;
                }
                snprintf(g_engine_abs, sizeof g_engine_abs, "%s", abs);
                const char *slash = strchr(p, '/');
                if (slash) snprintf(g_engine_path, sizeof g_engine_path, "%s", slash);
                printf("[ctrl] ✅ 播放地址 = %s\n", u);
                ctrl_report("play", g_task_id, st, err, done, total, slash ? slash : "");
            }
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
