# -*- coding: utf-8 -*-
"""由 harness3.c 生成 harness4.c：
   1) 迷你 JNI 补上 int[] 对象模型（引擎要 GetObjectField("mStatus","[I") 拿真数组，
      返回 NULL 会直接被判 XL_PARAM_ERROR=9112）
   2) 补上 XLTaskHelper.addMagentTask 里那两步缺失的调用：startTask + setTaskGsState
   3) 轮询时直接打印我们自己数组里的值
"""
import io, sys

SRC = 'harness3.c'
DST = 'harness4.c'
s = io.open(SRC, encoding='utf-8').read()
orig = s

def rep(old, new, tag):
    global s
    if old not in s:
        print('!! 锚点未找到:', tag); sys.exit(1)
    s = s.replace(old, new, 1)
    print('   ok', tag)

# ── 1) 前置声明（数组辅助函数在后面才定义）────────────────
rep(
"""static int g_nobj = 0;
static int g_jni_calls = 0;
static struct JNINativeInterface *g_env_ptr = NULL;""",
"""static int g_nobj = 0;
static int g_jni_calls = 0;
static struct JNINativeInterface *g_env_ptr = NULL;

// —— 数组辅助的前置声明（实现在后面的"字节数组 / 整数数组"节）——
typedef struct { jsize n; int *v; } JIntArray;
typedef struct { jsize n; unsigned char *b; } JByteArray;
static int is_int_array(void *p);
static int is_byte_array(void *p);
static JIntArray *new_int_array(jsize n);
static void *obj_get_oval(void *o, const char *name);

static jfieldID mk_fid(const char *n, const char *s) {
    JFieldId *f = (JFieldId *)calloc(1, sizeof(JFieldId));
    snprintf(f->n, sizeof(f->n), "%s", n ? n : "");
    snprintf(f->s, sizeof(f->s), "%s", s ? s : "");
    return (jfieldID)f;
}""", '前置声明 + mk_fid')

# ── 2) obj_get_oval：按名字取原始指针（区分"没这个字段"与"值是 NULL"）──
rep(
"""static const char *obj_get_str(JObj *o, const char *name) {""",
"""static void *obj_get_oval(void *ov, const char *name) {
    JObj *o = (JObj *)ov;
    if (!o) return NULL;
    for (int i = 0; i < o->n; i++)
        if (!strcmp(o->f[i].name, name)) return o->f[i].o;
    return NULL;
}

static const char *obj_get_str(JObj *o, const char *name) {""", 'obj_get_oval')

# ── 3) 整数数组实现 ────────────────────────────────────
rep(
"""// ═══════════ 字节数组 + NewObject ═══════════""",
"""// ═══════════ 整数数组（引擎的状态回填全靠它）═══════════
// 教训：BtTaskStatus 的字段是 `int[] mStatus`（构造器里 new int[n]）。
// 引擎拿到对象后会 GetObjectField(obj,"mStatus","[I")，**拿到 NULL 就返回
// XL_PARAM_ERROR(9112)** —— 所以必须给它一个真实、可写的数组。
#define MAX_IA 16
static JIntArray g_ia[MAX_IA];
static int g_nia = 0;

static int is_int_array(void *p) {
    for (int i = 0; i < g_nia; i++) if ((void *)&g_ia[i] == p) return 1;
    return 0;
}

static JIntArray *new_int_array(jsize n) {
    if (g_nia >= MAX_IA) g_nia = MAX_IA - 1;
    JIntArray *a = &g_ia[g_nia++];
    a->n = n;
    a->v = (int *)calloc(1, (size_t)(n > 0 ? n : 1) * sizeof(int));
    return a;
}

// ═══════════ 字节数组 + NewObject ═══════════""", '整数数组实现')

# ── 4) 数组相关 JNI 函数换成真实现 ─────────────────────
rep(
"""static jsize my_GetArrayLength(JNIEnv *e, jarray a) { (void)e; (void)a; g_jni_calls++; return 0; }
static jintArray my_NewIntArray(JNIEnv *e, jsize n) { (void)e; printf("  [jni] NewIntArray(%d)\\n", n); g_jni_calls++; return NULL; }
static jlongArray my_NewLongArray(JNIEnv *e, jsize n) { (void)e; printf("  [jni] NewLongArray(%d)\\n", n); g_jni_calls++; return NULL; }
static void my_SetIntArrayRegion(JNIEnv *e, jintArray a, jsize s, jsize l, const jint *b) { (void)e; (void)a; (void)s; (void)l; (void)b; g_jni_calls++; }
static jint *my_GetIntArrayElements(JNIEnv *e, jintArray a, jboolean *c) { (void)e; (void)a; if (c) *c = JNI_FALSE; g_jni_calls++; return NULL; }
static void my_ReleaseIntArrayElements(JNIEnv *e, jintArray a, jint *b, jint m) { (void)e; (void)a; (void)b; (void)m; g_jni_calls++; }""",
"""static jsize my_GetArrayLength(JNIEnv *e, jarray a) {
    (void)e; g_jni_calls++;
    if (is_int_array(a))  return ((JIntArray *)a)->n;
    if (is_byte_array(a)) return ((JByteArray *)a)->n;
    return 0;
}
static jintArray my_NewIntArray(JNIEnv *e, jsize n) {
    (void)e; g_jni_calls++;
    JIntArray *a = new_int_array(n);
    printf("  [jni] NewIntArray(%d)\\n", (int)n);
    return (jintArray)a;
}
static jlongArray my_NewLongArray(JNIEnv *e, jsize n) { (void)e; printf("  [jni] NewLongArray(%d)\\n", (int)n); g_jni_calls++; return NULL; }
static void my_SetIntArrayRegion(JNIEnv *e, jintArray a, jsize s, jsize l, const jint *b) {
    (void)e; g_jni_calls++;
    if (!is_int_array(a) || !b) return;
    JIntArray *ia = (JIntArray *)a;
    for (jsize i = 0; i < l && s + i < ia->n; i++) ia->v[s + i] = b[i];
}
static void my_GetIntArrayRegion(JNIEnv *e, jintArray a, jsize s, jsize l, jint *b) {
    (void)e; g_jni_calls++;
    if (!is_int_array(a) || !b) return;
    JIntArray *ia = (JIntArray *)a;
    for (jsize i = 0; i < l && s + i < ia->n; i++) b[i] = ia->v[s + i];
}
static jint *my_GetIntArrayElements(JNIEnv *e, jintArray a, jboolean *c) {
    (void)e; g_jni_calls++;
    if (c) *c = JNI_FALSE;
    if (!is_int_array(a)) return NULL;
    return ((JIntArray *)a)->v;      // 直接返回我们自己那块内存，引擎改写即生效
}
static void my_ReleaseIntArrayElements(JNIEnv *e, jintArray a, jint *b, jint m) { (void)e; (void)a; (void)b; (void)m; g_jni_calls++; }""", '数组 JNI 函数')

# ── 5) GetObjectField 按签名区分：数组字段 vs 字符串字段 ──
rep(
"""static jobject my_GetObjectField(JNIEnv *env, jobject obj, jfieldID fid) {
    (void)env; g_jni_calls++;
    return (jobject)obj_get_str((JObj *)obj, fid_name(fid));
}""",
"""static jobject my_GetObjectField(JNIEnv *env, jobject obj, jfieldID fid) {
    (void)env; g_jni_calls++;
    if (!obj || !fid) return NULL;
    const char *name = fid_name(fid);
    const char *sig  = ((JFieldId *)fid)->s;
    void *v = obj_get_oval(obj, name);
    if (sig && sig[0] == '[') {           // int[] 之类的数组字段
        if (!v) printf("  [jni] ⚠️ GetObjectField(%s%s) -> NULL（引擎会判 XL_PARAM_ERROR）\\n", name, sig);
        return (jobject)v;
    }
    return (jobject)v;                    // 字符串字段：v 就是 NewStringUTF 出来的指针
}""", 'GetObjectField')

# ── 6) 调用链：dlsym 增加 startTask / setTaskGsState ─────
rep(
"""    typedef jint (*fn_local)(JNIEnv *, jobject, jstring, jobject);
    typedef jint (*fn_uni)(JNIEnv *, jobject);""",
"""    typedef jint (*fn_local)(JNIEnv *, jobject, jstring, jobject);
    typedef jint (*fn_uni)(JNIEnv *, jobject);
    typedef jint (*fn_long1)(JNIEnv *, jobject, jlong);
    typedef jint (*fn_long3)(JNIEnv *, jobject, jlong, jint, jint);""", 'fn 类型')

rep(
"""    fn_uni unInit = (fn_uni)dlsym(sdk, "Java_com_xunlei_downloadlib_XLLoader_unInit");""",
"""    fn_uni unInit = (fn_uni)dlsym(sdk, "Java_com_xunlei_downloadlib_XLLoader_unInit");
    // ★ XLTaskHelper.addMagentTask 的真正流程里，createBtMagnetTask 返回 9000 之后
    //   还要 startTask + setTaskGsState —— 少了这两步，任务只是被"登记"而从不运行。
    fn_long1 startTask = (fn_long1)dlsym(sdk, "Java_com_xunlei_downloadlib_XLLoader_startTask");
    fn_long3 gsState   = (fn_long3)dlsym(sdk, "Java_com_xunlei_downloadlib_XLLoader_setTaskGsState");
    printf("[chain]   startTask=%p setTaskGsState=%p\\n", (void *)startTask, (void *)gsState);""", 'dlsym startTask')

# ── 7) 调用链主体：建任务后启动 + 带 int[] 的轮询 ────────
rep(
"""    long id = obj_get_long(taskId, "mTaskId");
    printf("[chain] ← createBtMagnetTask 返回 %d，taskId = %ld\\n", (int)mrc, id);

    if (id > 0 && status) {
        JObj *st = new_obj("com/xunlei/downloadlib/parameter/BtTaskStatus");
        for (int i = 0; i < 10; i++) {
            jint s = status(env, (jobject)thiz, (jlong)id, (jobject)st, 0, 0);
            printf("[chain]   轮询#%d 返回 %d，mStatus=%s\\n", i, (int)s,
                   obj_get_str(st, "mStatus"));
            sleep(1);
        }
    }""",
"""    long id = obj_get_long(taskId, "mTaskId");
    printf("[chain] ← createBtMagnetTask 返回 %d（9000=XL_NO_ERRNO 成功），taskId = %ld\\n", (int)mrc, id);

    // 任务建好但还没跑 —— 必须显式 startTask（并设 GsState），否则永远是"未开始"
    if (id > 0 && startTask) {
        printf("[chain]   startTask(%ld) → %d\\n", id, (int)startTask(env, (jobject)thiz, (jlong)id));
        if (gsState)
            printf("[chain]   setTaskGsState(%ld,0,2) → %d\\n", id,
                   (int)gsState(env, (jobject)thiz, (jlong)id, 0, 2));
    }

    if (id > 0 && status) {
        JObj *st = new_obj("com/xunlei/downloadlib/parameter/BtTaskStatus");
        // 预置 int[] mStatus：引擎会往这个数组里写状态
        JIntArray *ia = new_int_array(4);
        put_field(st, mk_fid("mStatus", "[I"), 3, 0, ia);
        for (int i = 0; i < 12; i++) {
            jint s = status(env, (jobject)thiz, (jlong)id, (jobject)st, 0, 0);
            printf("[chain]   轮询#%d 返回 %d，mStatus=[%d,%d,%d,%d]\\n", i, (int)s,
                   ia->v[0], ia->v[1], ia->v[2], ia->v[3]);
            sleep(2);
        }
    }""", '调用链主体')

# ── 8) 通用字段转储（诊断：看引擎到底往参数对象里填了什么）──
rep(
"""static void my_SetObjectField(JNIEnv *env, jobject obj, jfieldID fid, jobject val) {""",
"""static void dump_obj(const char *tag, JObj *o) {
    if (!o) { printf("  %s: (null)\\n", tag); return; }
    printf("  %s <%s> 共 %d 个字段:\\n", tag, o->cls, o->n);
    for (int i = 0; i < o->n; i++) {
        JField *f = &o->f[i];
        if (f->kind == 1)      printf("    %-26s = %ld\\n", f->name, f->i);
        else if (f->kind == 0) printf("    %-26s = %d\\n", f->name, (int)f->i);
        else                   printf("    %-26s = ptr@%p (kind=%d)\\n", f->name, f->o, f->kind);
    }
    if (o->n == 0) printf("    （引擎一个字段都没填）\\n");
}

static void my_SetObjectField(JNIEnv *env, jobject obj, jfieldID fid, jobject val) {""", 'dump_obj')

# ── 9) getTaskInfo 诊断（下载状态/速度/大小，最直接的"到底在不在下载"）──
rep(
"""    typedef jint (*fn_long3)(JNIEnv *, jobject, jlong, jint, jint);""",
"""    typedef jint (*fn_long3)(JNIEnv *, jobject, jlong, jint, jint);
    typedef jint (*fn_taskinfo)(JNIEnv *, jobject, jlong, jint, jobject);""", 'fn_taskinfo 类型')

rep(
"""    fn_long3 gsState   = (fn_long3)dlsym(sdk, "Java_com_xunlei_downloadlib_XLLoader_setTaskGsState");""",
"""    fn_long3 gsState   = (fn_long3)dlsym(sdk, "Java_com_xunlei_downloadlib_XLLoader_setTaskGsState");
    fn_taskinfo getTaskInfo =
        (fn_taskinfo)dlsym(sdk, "Java_com_xunlei_downloadlib_XLLoader_getTaskInfo");""", 'dlsym getTaskInfo')

rep(
"""    if (localUrl) {
        JObj *lu = new_obj("com/xunlei/downloadlib/parameter/XLTaskLocalUrl");""",
"""    // ★ 最直接的诊断：任务到底在不在下载（状态 / 速度 / 已下大小）
    if (id > 0 && getTaskInfo) {
        JObj *ti = new_obj("com/xunlei/downloadlib/parameter/XLTaskInfo");
        jint r = getTaskInfo(env, (jobject)thiz, (jlong)id, 0, (jobject)ti);
        printf("[chain] ← getTaskInfo 返回 %d\\n", (int)r);
        dump_obj("XLTaskInfo", ti);
    }

    if (localUrl) {
        JObj *lu = new_obj("com/xunlei/downloadlib/parameter/XLTaskLocalUrl");""", '调用 getTaskInfo')

# ── 10) 把 DNS 拦截层并进同一编译单元（bionic 无 netd 时 getaddrinfo 失效）──
rep(
"""// ═══════════ 极简 Java 对象模型 ═══════════""",
"""// ↓↓↓ DNS 拦截层：bionic 在无 netd 环境里 getaddrinfo 必失败，必须自己实现 ↓↓↓
#include "dnshook.c"

// ═══════════ 极简 Java 对象模型 ═══════════""", 'DNS 拦截层')

# ── 11) main 里加一句自检，确认拦截生效 ──
rep(
"""    printf("[v2] ===== 迷你 JNIEnv + 首次真调用 =====\\n");""",
"""    printf("[v2] ===== 迷你 JNIEnv + 首次真调用 =====\\n");
    {   // DNS 自检：确认拦截层接管了 getaddrinfo
        struct addrinfo hints, *r = NULL;
        memset(&hints, 0, sizeof(hints));
        hints.ai_family = AF_INET;
        hints.ai_socktype = SOCK_STREAM;
        const char *probe = getenv("DNS_PROBE") ? getenv("DNS_PROBE") : "www.baidu.com";
        int rc = getaddrinfo(probe, "80", &hints, &r);
        printf("[v2] DNS 自检 %s → rc=%d%s\\n", probe, rc, rc == 0 ? " ✅" : " ✗");
    }""", 'DNS 自检')

# ── 12) 对象池复用时清零 + 扩容（监控循环会反复 new_obj）──
rep(
"""static JObj *new_obj(const char *cls) {
    if (g_nobj >= MAX_OBJS) return &g_objs[MAX_OBJS - 1];""",
"""static JObj *new_obj(const char *cls) {
    if (g_nobj >= MAX_OBJS) {           // 池满：复用最后一格，但必须清零（否则读到上次的脏字段）
        JObj *o = &g_objs[MAX_OBJS - 1];
        memset(o, 0, sizeof(*o));
        snprintf(o->cls, sizeof(o->cls), "%s", cls);
        return o;
    }""", 'new_obj 复用清零')

rep("""#define MAX_OBJS 16""", """#define MAX_OBJS 32""", 'MAX_OBJS 扩容')

# ── 13) 轮询 → 进度监控循环（每 5 秒看真实状态/速度）──
rep(
"""    if (id > 0 && status) {
        JObj *st = new_obj("com/xunlei/downloadlib/parameter/BtTaskStatus");
        // 预置 int[] mStatus：引擎会往这个数组里写状态
        JIntArray *ia = new_int_array(4);
        put_field(st, mk_fid("mStatus", "[I"), 3, 0, ia);
        for (int i = 0; i < 12; i++) {
            jint s = status(env, (jobject)thiz, (jlong)id, (jobject)st, 0, 0);
            printf("[chain]   轮询#%d 返回 %d，mStatus=[%d,%d,%d,%d]\\n", i, (int)s,
                   ia->v[0], ia->v[1], ia->v[2], ia->v[3]);
            sleep(2);
        }
    }""",
"""    if (id > 0 && status) {
        JObj *st = new_obj("com/xunlei/downloadlib/parameter/BtTaskStatus");
        // 预置 int[] mStatus：引擎会往这个数组里写状态
        JIntArray *ia = new_int_array(4);
        put_field(st, mk_fid("mStatus", "[I"), 3, 0, ia);
        // ★ 进度监控：每 5 秒读一次引擎的真实进度（状态/大小/各路速度）
        int secs = getenv("MON_SECS") ? atoi(getenv("MON_SECS")) : 30;
        int rounds = secs / 5;
        for (int i = 0; i <= rounds; i++) {
            JObj *ti = new_obj("com/xunlei/downloadlib/parameter/XLTaskInfo");
            jint ir = getTaskInfo ? getTaskInfo(env, (jobject)thiz, (jlong)id, 0, (jobject)ti) : -1;
            printf("[mon] t=%3ds info=%d st=%d err=%d 已下载=%ld/%ld 速度=%ld  P2S=%ld P2P=%ld 慢速源=%ld\\n",
                   i * 5, (int)ir,
                   obj_get_int(ti, "mTaskStatus"), obj_get_int(ti, "mErrorCode"),
                   obj_get_long(ti, "mDownloadSize"), obj_get_long(ti, "mFileSize"),
                   obj_get_long(ti, "mDownloadSpeed"),
                   obj_get_long(ti, "mP2SSpeed"), obj_get_long(ti, "mP2PSpeed"),
                   obj_get_long(ti, "mAdditionalResPeerBytes"));
            if (i % 6 == 0) {
                jint s = status(env, (jobject)thiz, (jlong)id, (jobject)st, 0, 0);
                printf("[mon]   子任务状态=%d → mStatus=[%d,%d,%d,%d]\\n", (int)s,
                       ia->v[0], ia->v[1], ia->v[2], ia->v[3]);
            }
            sleep(5);
        }
    }""", '进度监控循环')

# ── 14) 去掉单独那次 getTaskInfo（已并入监控循环），getLocalUrl 用真实文件名 ──
rep(
"""    // ★ 最直接的诊断：任务到底在不在下载（状态 / 速度 / 已下大小）
    if (id > 0 && getTaskInfo) {
        JObj *ti = new_obj("com/xunlei/downloadlib/parameter/XLTaskInfo");
        jint r = getTaskInfo(env, (jobject)thiz, (jlong)id, 0, (jobject)ti);
        printf("[chain] ← getTaskInfo 返回 %d\\n", (int)r);
        dump_obj("XLTaskInfo", ti);
    }

    if (localUrl) {
        JObj *lu = new_obj("com/xunlei/downloadlib/parameter/XLTaskLocalUrl");
        jint lrc = localUrl(env, (jobject)thiz, (jstring)"cc-test", (jobject)lu);
        printf("[chain] ← getLocalUrl 返回 %d，mStrUrl = %s\\n", (int)lrc, obj_get_str(lu, "mStrUrl"));
    }""",
"""    if (localUrl) {
        const char *fn = getenv("FILENAME") ? getenv("FILENAME") : "cc-test";
        JObj *lu = new_obj("com/xunlei/downloadlib/parameter/XLTaskLocalUrl");
        jint lrc = localUrl(env, (jobject)thiz, (jstring)fn, (jobject)lu);
        printf("[chain] ← getLocalUrl(\\"%s\\") 返回 %d，mStrUrl = %s\\n", fn, (int)lrc,
               obj_get_str(lu, "mStrUrl"));
    }""", 'getLocalUrl 用真实文件名')

# ── 15) 阶段 B：用下到的 .torrent 建真正的 BT 下载任务（对齐 XLTaskHelper.addTorrentTask）──
rep(
"""    typedef jint (*fn_taskinfo)(JNIEnv *, jobject, jlong, jint, jobject);""",
"""    typedef jint (*fn_taskinfo)(JNIEnv *, jobject, jlong, jint, jobject);
    typedef jint (*fn_bttask)(JNIEnv *, jobject, jstring, jstring, jint, jint, jint, jobject);""",
 'fn_bttask 类型')

rep(
"""    fn_taskinfo getTaskInfo =
        (fn_taskinfo)dlsym(sdk, "Java_com_xunlei_downloadlib_XLLoader_getTaskInfo");""",
"""    fn_taskinfo getTaskInfo =
        (fn_taskinfo)dlsym(sdk, "Java_com_xunlei_downloadlib_XLLoader_getTaskInfo");
    // ★ 阶段 B 用：迅雷 SDK 自己就是这么做的（XLTaskHelper.addTorrentTask）
    //   原生签名参数顺序由 XLDownloadManager.createBtTask 的字节码确认：
    //   (mTorrentPath, mFilePath, mMaxConcurrent, mCreateMode, mSeqId, GetTaskId)
    fn_bttask createBtTask =
        (fn_bttask)dlsym(sdk, "Java_com_xunlei_downloadlib_XLLoader_createBtTask");""",
 'dlsym createBtTask')

rep(
"""    if (localUrl) {
        const char *fn = getenv("FILENAME") ? getenv("FILENAME") : "cc-test";""",
"""    // ══ 阶段 B：用刚下到的 .torrent 建真正的 BT 下载任务 ══
    // 迅雷 SDK 的分工：createBtMagnetTask 只负责「把 magnet 解析成 .torrent」（会落盘），
    // 真正下载内容要再用 createBtTask(BtTaskParam{torrentPath, filePath, createMode=1,
    // maxConcurrent, seqId}, GetTaskId)，然后 startTask。
    if (createBtTask && id > 0) {
        const char *tp = getenv("TORRENT_PATH") ? getenv("TORRENT_PATH") : "/thunder-data/cc-test";
        printf("[chain] ══ 阶段 B：createBtTask ──\\n");
        printf("[chain]   torrentPath = %s\\n", tp);
        JObj *tid2 = new_obj("com/xunlei/downloadlib/parameter/GetTaskId");
        jint r2 = createBtTask(env, (jobject)thiz, (jstring)tp, (jstring)EMU_SAVE_PATH,
                               3,   // maxConcurrent（SDK 用 3）
                               1,   // createMode（SDK 用 1）
                               1,   // seqId
                               (jobject)tid2);
        long id2 = obj_get_long(tid2, "mTaskId");
        printf("[chain] ← createBtTask 返回 %d，任务 ID = %ld\\n", (int)r2, id2);
        if (id2 > 0) {
            if (startTask)
                printf("[chain]   startTask(%ld) → %d\\n", id2, (int)startTask(env, (jobject)thiz, (jlong)id2));
            if (gsState)
                printf("[chain]   setTaskGsState(%ld,0,2) → %d\\n", id2,
                       (int)gsState(env, (jobject)thiz, (jlong)id2, 0, 2));
            int dsecs = getenv("DL_SECS") ? atoi(getenv("DL_SECS")) : 150;
            for (int i = 0; i <= dsecs / 5; i++) {
                JObj *ti = new_obj("com/xunlei/downloadlib/parameter/XLTaskInfo");
                jint ir = getTaskInfo ? getTaskInfo(env, (jobject)thiz, (jlong)id2, 0, (jobject)ti) : -1;
                printf("[dl] t=%3ds info=%d st=%d err=%d 已下载=%ld/%ld 速度=%ld  P2S=%ld P2P=%ld  P2P源=%ld\\n",
                       i * 5, (int)ir,
                       obj_get_int(ti, "mTaskStatus"), obj_get_int(ti, "mErrorCode"),
                       obj_get_long(ti, "mDownloadSize"), obj_get_long(ti, "mFileSize"),
                       obj_get_long(ti, "mDownloadSpeed"),
                       obj_get_long(ti, "mP2SSpeed"), obj_get_long(ti, "mP2PSpeed"),
                       obj_get_long(ti, "mP2PRecvBytes"));
                sleep(5);
            }
        }
    }

    if (localUrl) {
        const char *fn = getenv("FILENAME") ? getenv("FILENAME") : "cc-test";""",
 '阶段 B：createBtTask')

# ── 16) ★ networkType 必须是 XLUtil.getNetworkType() 的取值域（WiFi = 9 而不是 1）──
rep(
"""                   1,    // networkType
                   1,    // permissionLevel  （对齐 XLTaskHelper.init）
                   0);   // queryConfOnInit""",
"""                   9,    // ★ networkType —— 必须落在 XLUtil.getNetworkType() 的取值域里！
                         //   从字节码实测：Context 无效→0；NetworkInfo.TYPE_WIFI(1)→**9**；
                         //   移动网络→5 或按 subtype。先前传的 1 是**枚举外的值**，引擎据此
                         //   认为网络类型未知/受限，于是**完全不做 BT hub 查询** ——
                         //   现象正是「域名解析了、但一个包都不发，直接 114004
                         //   TASK_FAILURE_QUERY_BT_HUB_FAILED」。
                   1,    // permissionLevel
                   0);   // queryConfOnInit""", 'networkType=9')

# ── 17) 补充 SDK 自己在 init 后做的三个调用（可能影响引擎状态）──
rep(
"""    printf("[chain] ← init 返回 %d（0=成功）\\n", (int)rc);""",
"""    printf("[chain] ← init 返回 %d（0=成功）\\n", (int)rc);

    // XLTaskHelper.init 在 init 之后还做了三件事，一并补上
    typedef jint (*fn_bool1)(JNIEnv *, jobject, jboolean);
    typedef jint (*fn_str1)(JNIEnv *, jobject, jstring);
    typedef jint (*fn_ll)(JNIEnv *, jobject, jlong, jlong);
    fn_bool1 statSw = (fn_bool1)dlsym(sdk, "Java_com_xunlei_downloadlib_XLLoader_setStatReportSwitch");
    fn_str1  osVer  = (fn_str1)dlsym(sdk, "Java_com_xunlei_downloadlib_XLLoader_setOSVersion");
    fn_ll    spdLim = (fn_ll)dlsym(sdk, "Java_com_xunlei_downloadlib_XLLoader_setSpeedLimit");
    if (statSw) printf("[chain]   setStatReportSwitch(false) → %d\\n", (int)statSw(env, (jobject)thiz, JNI_FALSE));
    if (osVer)  printf("[chain]   setOSVersion(\\"30_alpha\\") → %d\\n", (int)osVer(env, (jobject)thiz, (jstring)"30_alpha"));
    if (spdLim) printf("[chain]   setSpeedLimit(-1,-1) → %d\\n", (int)spdLim(env, (jobject)thiz, (jlong)-1, (jlong)-1));""",
 'init 后三个补充调用')

# ── 18) 直接调用引擎导出的 C API：开日志 + 允许使用资源 + 切到全资源下载 ──
rep(
"""    if (osVer)  printf("[chain]   setOSVersion(\\"30_alpha\\") → %d\\n", (int)osVer(env, (jobject)thiz, (jstring)"30_alpha"));""",
"""    if (osVer)  printf("[chain]   setOSVersion(\\"30_alpha\\") → %d\\n", (int)osVer(env, (jobject)thiz, (jstring)"30_alpha"));
    // ⚠️ X* 系列 C API（XLSetReleaseLog / XLIsLogTurnOn）在 JNI 初始化路径下调用会崩
    //    （libc: Fatal signal 11 SEGV_ACCERR），不要再调；要开日志请改走 slog 配置文件。""", 'X* C API 全部禁用')

rep(
"""            if (startTask)
                printf("[chain]   startTask(%ld) → %d\\n", id2, (int)startTask(env, (jobject)thiz, (jlong)id2));""",
"""            // ★ 引擎导出的 C API：允许使用（P2P/P2SP）资源 + 从"仅原始地址"切到"全部资源下载"
            //   名字上看这正是 BT/磁力任务能拿到 peer 资源的前提。
            {
                typedef int (*fn_allow)(long long, int);
                typedef int (*fn_sw)(long long);
                fn_allow allowRes = (fn_allow)dlsym(sdk, "XLSetTaskAllowUseResource");
                fn_sw   switchRes = (fn_sw)dlsym(sdk, "XLSwitchOriginToAllResDownload");
                if (allowRes) printf("[bt]   XLSetTaskAllowUseResource(%ld,1) → %d\\n", id2, allowRes((long long)id2, 1));
                if (switchRes) printf("[bt]   XLSwitchOriginToAllResDownload(%ld) → %d\\n", id2, switchRes((long long)id2));
            }
            if (startTask)
                printf("[chain]   startTask(%ld) → %d\\n", id2, (int)startTask(env, (jobject)thiz, (jlong)id2));""",
 '资源相关 C API')

# ── 19) 失败后的补救调用：XLRequeryIndex / XLEnterPrefetchMode ──
rep(
"""            int dsecs = getenv("DL_SECS") ? atoi(getenv("DL_SECS")) : 150;
            for (int i = 0; i <= dsecs / 5; i++) {
                JObj *ti = new_obj("com/xunlei/downloadlib/parameter/XLTaskInfo");
                jint ir = getTaskInfo ? getTaskInfo(env, (jobject)thiz, (jlong)id2, 0, (jobject)ti) : -1;""",
"""            int dsecs = getenv("DL_SECS") ? atoi(getenv("DL_SECS")) : 150;
            // 头 20 秒先观察：如果任务直接判失败(114004)，就试引擎导出的两个补救 API
            for (int i = 0; i <= dsecs / 5; i++) {
                JObj *ti = new_obj("com/xunlei/downloadlib/parameter/XLTaskInfo");
                jint ir = getTaskInfo ? getTaskInfo(env, (jobject)thiz, (jlong)id2, 0, (jobject)ti) : -1;
                if (i == 4 && obj_get_int(ti, "mTaskStatus") == 3) {
                    typedef int (*fn_req)(long long);
                    typedef int (*fn_pref)(long long);
                    fn_req  requery  = (fn_req)dlsym(sdk, "XLRequeryIndex");
                    fn_pref prefetch = (fn_pref)dlsym(sdk, "XLEnterPrefetchMode");
                    printf("[bt]   任务失败(err=%d)，试补救：\\n", obj_get_int(ti, "mErrorCode"));
                    if (requery)  printf("[bt]     XLRequeryIndex(%ld) → %d\\n", id2, requery((long long)id2));
                    if (prefetch) printf("[bt]     XLEnterPrefetchMode(%ld) → %d\\n", id2, prefetch((long long)id2));
                }""", '失败补救 API')

# ── 20) 显式选中文件（selectBtSubTask + BtIndexSet{mIndexSet=[0]}）──
rep(
"""            if (startTask)
                printf("[chain]   startTask(%ld) → %d\\n", id2, (int)startTask(env, (jobject)thiz, (jlong)id2));""",
"""            // ★ 选中要下载的文件（BtIndexSet.mIndexSet = int[]，构造器里分配）
            //   SDK 的 addTorrentTask 里是反选（deselectBtSubTask）不需要的文件，
            //   说明默认全选；但 createBtTask 路径下未必 —— 显式选中 index 0 试试。
            {
                typedef jint (*fn_sel)(JNIEnv *, jobject, jlong, jobject);
                fn_sel selSub = (fn_sel)dlsym(sdk, "Java_com_xunlei_downloadlib_XLLoader_selectBtSubTask");
                if (selSub) {
                    JObj *iset = new_obj("com/xunlei/downloadlib/parameter/BtIndexSet");
                    JIntArray *ia2 = new_int_array(1);
                    ia2->v[0] = 0;
                    put_field(iset, mk_fid("mIndexSet", "[I"), 3, 0, ia2);
                    printf("[bt]   selectBtSubTask(%ld, {0}) → %d\\n", id2,
                           (int)selSub(env, (jobject)thiz, (jlong)id2, (jobject)iset));
                } else printf("[bt]   找不到 selectBtSubTask\\n");
            }
            if (startTask)
                printf("[chain]   startTask(%ld) → %d\\n", id2, (int)startTask(env, (jobject)thiz, (jlong)id2));""",
 '显式选中文件')

# 去重：JByteArray 的 typedef 已在前置声明区给出，删掉后面那处（否则 typedef 重定义）
_T = 'typedef struct { jsize n; unsigned char *b; } JByteArray;'
_i1 = s.find(_T)
_i2 = s.find(_T, _i1 + 1)
if _i2 != -1:
    s = s[:_i2] + s[_i2 + len(_T) + 1:]
    print('   已删除重复的 JByteArray typedef')

io.open(DST, 'w', encoding='utf-8', newline='\n').write(s)
print('已生成 %s（%d 行，原 %d 行）' % (DST, s.count('\n') + 1, orig.count('\n') + 1))
