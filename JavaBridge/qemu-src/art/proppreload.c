/* proppreload.c — LD_PRELOAD 进 guest 里任意 bionic 进程的 Android 属性垫片。
 *
 * 为什么要它（2026-09-25 实测）：guest 里没有 Android init，`/dev/__properties__` 不存在，
 * 于是 `/system/bin/app_process64` 走到
 *   app_process: Unable to determine ABI list from property ro.product.cpu.abilist64.
 * 就 abort。而 bionic 的属性 API 是 libc 导出、跨库调用走 PLT，所以预加载一份自己的实现
 * 就能整体接管 —— 比搬 Android init（要 SELinux/coldboot/binder，本内核大概率没有 binder）
 * 便宜得多，也比离线造 Android 9 的序列化属性区（版本格式风险，错了还静默失效）可靠。
 * 附带好处：以后要调 dalvik.vm.*、ro.product.cpu.*、设备指纹，改这张表就行。
 *
 * prop_info 按 bionic 布局镜像（name[32] + union{value[92] / serial(4)+value[88]}），
 * 以防有老代码直接读 pi->value 而不走 read 回调。
 */
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <strings.h>
#include <time.h>
#include <stdlib.h>

#define PROP_NAME_MAX 32
#define PROP_VALUE_MAX 92

typedef struct prop_info {
    char name[PROP_NAME_MAX];
    union {
        char value[PROP_VALUE_MAX];
        struct {
            volatile uint32_t serial;
            char value[PROP_VALUE_MAX - (int) sizeof(uint32_t)];
        } v;
    } u;
} prop_info;

typedef struct { char name[PROP_NAME_MAX]; char value[PROP_VALUE_MAX]; } PV;

/* Android 9 (API 28) arm64 模拟器镜像的等价值；顺序无所谓，查找是线性的。 */
#include "props_gen.h"


#define NPROPS ((int) (sizeof(g_props) / sizeof(g_props[0])))
static uint32_t g_serial = 16;
/* 读的时候用静态池，保证返回的 prop_info* 在进程生命周期内一直有效。 */
static prop_info g_pool[NPROPS];
static int g_pool_ready = 0;

static int find_index(const char *name) {
    if (!name) return -1;
    for (int i = NPROPS - 1; i >= 0; i--)   /* 从后往前：覆盖项生效 */
        if (!strcmp(g_props[i].name, name)) return i;
    return -1;
}

static void ensure_pool(void) {
    if (g_pool_ready) return;
    for (int i = 0; i < NPROPS; i++) {
        snprintf(g_pool[i].name, PROP_NAME_MAX, "%s", g_props[i].name);
        snprintf(g_pool[i].u.v.value, PROP_VALUE_MAX - (int) sizeof(uint32_t), "%s", g_props[i].value);
        g_pool[i].u.v.serial = g_serial;
    }
    g_pool_ready = 1;
}

const prop_info *__system_property_find(const char *name) {
    ensure_pool();
    int i = find_index(name);
    return i < 0 ? NULL : &g_pool[i];
}

uint32_t __system_property_serial(const prop_info *pi) { return pi ? pi->u.v.serial : 0; }

uint32_t __system_property_area_serial(void) { return g_serial; }

uint32_t __system_property_read(const prop_info *pi, char *name, char *value) {
    if (!pi) return 0;
    if (name) snprintf(name, PROP_NAME_MAX, "%s", pi->name);
    if (value) snprintf(value, PROP_VALUE_MAX, "%s", pi->u.v.value);
    return pi->u.v.serial;
}

void __system_property_read_callback(const prop_info *pi,
                                     void (*cb)(void *cookie, const char *name, const char *value,
                                                uint32_t serial),
                                     void *cookie) {
    if (!pi || !cb) return;
    cb(cookie, pi->name, pi->u.v.value, pi->u.v.serial);
}

static int g_trace = -1;
static int g_traced = 0;
static void trace_get(const char *name, const char *value) {
    if (g_trace < 0) g_trace = getenv("PROPPRINT") ? 1 : 0;
    if (g_trace && g_traced < 40) { g_traced++; fprintf(stderr, "[proppreload] get %-34s = %s\n", name ? name : "(null)", value); }
}

int __system_property_get(const char *name, char *value) {
    ensure_pool();
    int i = find_index(name);
    if (!value) return 0;
    value[0] = '\0';
    if (i < 0) { trace_get(name, ""); return 0; }
    size_t n = strlen(g_props[i].value);
    if (n >= PROP_VALUE_MAX) n = PROP_VALUE_MAX - 1;
    memcpy(value, g_props[i].value, n);
    value[n] = '\0';
    return (int) n;
}

int __system_property_set(const char *key, const char *value) {
    if (!key) return -1;
    ensure_pool();
    int i = find_index(key);
    if (i < 0) {
        fprintf(stderr, "[propprop] set 了表外的属性 %s（忽略）\n", key);
        return 0;
    }
    snprintf(g_props[i].value, PROP_VALUE_MAX, "%s", value ? value : "");
    snprintf(g_pool[i].u.v.value, PROP_VALUE_MAX - (int) sizeof(uint32_t), "%s", value ? value : "");
    g_pool[i].u.v.serial = (g_serial += 16);
    return 0;
}

const prop_info *__system_property_find_nth(unsigned n) {
    ensure_pool();
    return (int) n < NPROPS ? &g_pool[n] : NULL;
}

int __system_property_count(void) { return NPROPS; }

int __system_property_foreach(void (*report)(const prop_info *pi, void *cookie), void *cookie) {
    ensure_pool();
    for (int i = 0; i < NPROPS; i++)
        if (report) report(&g_pool[i], cookie);
    return NPROPS;
}

int __system_property_wait(const prop_info *pi, uint32_t old_serial, uint32_t *new_serial_ptr,
                           const struct timespec *relative_timeout) {
    (void) relative_timeout;
    ensure_pool();
    uint32_t now = pi ? pi->u.v.serial : g_serial;
    if (new_serial_ptr) *new_serial_ptr = now;
    return now == old_serial ? 0 : 1;
}

/* 有些代码走的是这个（等待任意属性变化）。 */
int __system_property_wdev_l(const prop_info *pi, uint32_t old_serial, const char *value, int vlen) {
    (void) pi; (void) old_serial; (void) value; (void) vlen;
    return 0;
}

static void __attribute__((constructor)) proppreload_note(void) {
    ensure_pool();
    fprintf(stderr, "[proppreload] 接管 Android 属性 API：%d 条\n", NPROPS);
}

/* ── ashmem 替身 ─────────────────────────────────────────────────────────────
 * 实测（2026-09-25，假 logd 捞回来的）：ART 起来后死在
 *   ashmem_create_region failed for 'Sentinel fault page': No such file or directory
 *   ashmem_create_region failed for 'non moving space': ...
 * 因为本内核（Alpine linux-virt）没有 /dev/ashmem —— ashmem 早就从主线内核移除了。
 * 但 `ashmem_create_region()` 是 **libcutils 导出、libart 经 PLT 调用**的普通函数，
 * 预加载定义在全局作用域里优先于 libcutils ⇒ 可以整体用 memfd/POSIX shm 顶掉：
 * 语义上 ART 只把 ashmem 当"可 mmap 的匿名共享内存"，pin/unpin/prot 都是尽力而为。
 */
#include <fcntl.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <unistd.h>

/* bionic 头里不给 shm_open 声明（要 _GNU_SOURCE 那套），直接下 syscall。 */
#ifndef SYS_memfd_create
#define SYS_memfd_create 279          /* aarch64 */
#endif
#ifndef MFD_CLOEXEC
#define MFD_CLOEXEC 1u
#endif
static int memfd_shim(const char *nm) {
    long r = syscall(SYS_memfd_create, nm && *nm ? nm : "art", MFD_CLOEXEC);
    if (r < 0) perror("[ashshim] memfd_create");
    return (int) r;
}

static unsigned g_shm_seq = 0;

int ashmem_create_region(const char *name, size_t size) {
    (void) name;
    char nm[32];
    snprintf(nm, sizeof nm, "art-%u", g_shm_seq++);
    int fd = memfd_shim(nm);
    if (fd < 0) return -1;
    if (size && ftruncate(fd, (off_t) size) < 0) { perror("[ashshim] ftruncate"); close(fd); return -1; }
    (void) name;
    return fd;
}

int ashmem_set_prot_region(int fd, int prot) { (void) fd; (void) prot; return 0; }
int ashmem_get_prot_region(int fd, int *prot) { (void) fd; if (prot) *prot = PROT_READ | PROT_WRITE; return 0; }
int ashmem_pin_region(int fd, size_t off, size_t *len) { (void) fd; (void) off; if (len) *len = 0; return 0; }
int ashmem_unpin_region(int fd, size_t off, size_t *len) { (void) fd; (void) off; if (len) *len = 0; return 0; }
int ashmem_get_size_region(int fd, size_t *size) {
    struct stat st;
    if (!size) return -1;
    if (fstat(fd, &st) < 0) return -1;
    *size = (size_t) st.st_size;
    return 0;
}
int ashmem_valid(int fd) { struct stat st; return fstat(fd, &st) == 0 ? 1 : 0; }

/* ─────────────────────────────────────────────────────────────────────────────
 * JNI 层：android.os.SystemProperties + android.util.Log
 *
 * 为什么必须（2026-09-25 P3 实测）：SystemProperties 的 native 是由 libandroid_runtime.so 的
 * register_android_os_SystemProperties() 注册的，而那是 AndroidRuntime::start()（zygote）才会做的。
 * 我们只用 libart + boot classpath，于是
 *   UnsatisfiedLinkError: No implementation found for android.os.SystemProperties.native_get
 *   → android.os.Build.<clinit> 失败 → 读 Build.CPU_ABI 的代码全废
 * 而 Guard 壳的 DexNative.<clinit> 第一句就是 Build.CPU_ABI.contains("64")，所以这一步不通后面都免谈。
 *
 * 两条路一起走：① 按 JNI 命名规范导出符号（ART 对 boot classpath 的 native 会去全局符号里 dlsym）；
 *               ② 导出 catclaw_register_boot_natives()，由 artlaunch 起来后主动 RegisterNatives。
 * Log 那两个顺手做掉：shell 的日志就能从串口看得见。
 * ───────────────────────────────────────────────────────────────────────────── */
#include <jni.h>

static const char *pv_get(const char *name, char *buf) {
    buf[0] = 0;
    int i = find_index(name);
    if (i >= 0) snprintf(buf, PROP_VALUE_MAX, "%s", g_props[i].value);
    return buf;
}

static const char *jstr(JNIEnv *env, jstring s, char *buf, int cap) {
    if (!s) { buf[0] = 0; return buf; }
    const char *c = (*env)->GetStringUTFChars(env, s, NULL);
    if (!c) { buf[0] = 0; return buf; }
    snprintf(buf, cap, "%s", c);
    (*env)->ReleaseStringUTFChars(env, s, c);
    return buf;
}

#define KEYBUF char kb[PROP_NAME_MAX]; char vb[PROP_VALUE_MAX]

JNIEXPORT jstring JNICALL Java_android_os_SystemProperties_native_1get__Ljava_lang_String_2
        (JNIEnv *env, jclass c, jstring key) {
    KEYBUF; return (*env)->NewStringUTF(env, pv_get(jstr(env, key, kb, sizeof kb), vb));
}

JNIEXPORT jstring JNICALL Java_android_os_SystemProperties_native_1get__Ljava_lang_String_2Ljava_lang_String_2
        (JNIEnv *env, jclass c, jstring key, jstring def) {
    KEYBUF;
    const char *v = pv_get(jstr(env, key, kb, sizeof kb), vb);
    if (*v) return (*env)->NewStringUTF(env, v);
    if (def) (*env)->EnsureLocalCapacity(env, 1);
    return def;
}

JNIEXPORT jint JNICALL Java_android_os_SystemProperties_native_1get_1int__Ljava_lang_String_2I
        (JNIEnv *env, jclass c, jstring key, jint def) {
    KEYBUF; const char *v = pv_get(jstr(env, key, kb, sizeof kb), vb);
    return *v ? (jint) strtol(v, NULL, 0) : def;
}

JNIEXPORT jlong JNICALL Java_android_os_SystemProperties_native_1get_1long__Ljava_lang_String_2J
        (JNIEnv *env, jclass c, jstring key, jlong def) {
    KEYBUF; const char *v = pv_get(jstr(env, key, kb, sizeof kb), vb);
    return *v ? (jlong) strtoll(v, NULL, 0) : def;
}

JNIEXPORT jboolean JNICALL Java_android_os_SystemProperties_native_1get_1boolean__Ljava_lang_String_2Z
        (JNIEnv *env, jclass c, jstring key, jboolean def) {
    KEYBUF; const char *v = pv_get(jstr(env, key, kb, sizeof kb), vb);
    if (!*v) return def;
    if (!strcmp(v, "1") || !strcasecmp(v, "true") || !strcasecmp(v, "yes") || !strcasecmp(v, "on")) return JNI_TRUE;
    if (!strcmp(v, "0") || !strcasecmp(v, "false") || !strcasecmp(v, "no") || !strcasecmp(v, "off")) return JNI_FALSE;
    return def;
}

JNIEXPORT void JNICALL Java_android_os_SystemProperties_native_1set__Ljava_lang_String_2Ljava_lang_String_2
        (JNIEnv *env, jclass c, jstring key, jstring val) {
    KEYBUF; char vb2[PROP_VALUE_MAX];
    snprintf(vb2, sizeof vb2, "%s", jstr(env, val, vb, sizeof vb));
    int i = find_index(jstr(env, key, kb, sizeof kb));
    if (i >= 0) { snprintf(g_props[i].value, PROP_VALUE_MAX, "%s", vb2); g_pool[i].u.v.serial += 2; }
    else fprintf(stderr, "[proppreload] set 未知属性 %s=%s（只读表，忽略）\n", kb, vb2);
}

JNIEXPORT jint JNICALL Java_android_os_SystemProperties_native_1find_1prop__Ljava_lang_String_2_3B
        (JNIEnv *env, jclass c, jstring key, jbyteArray def) {
    KEYBUF; const char *v = pv_get(jstr(env, key, kb, sizeof kb), vb);
    int n = (int) strlen(v);
    if (n >= PROP_VALUE_MAX) n = PROP_VALUE_MAX - 1;
    if (def) {
        jbyte tmp[PROP_VALUE_MAX];
        memcpy(tmp, v, n + 1);
        (*env)->SetByteArrayRegion(env, def, 0, (jsize) (n + 1), tmp);
    }
    return n;
}

JNIEXPORT void JNICALL Java_android_os_SystemProperties_native_1add_1change_1callback(JNIEnv *env, jclass c) { }
JNIEXPORT void JNICALL Java_android_os_SystemProperties_native_1report_1sysprop_1change(JNIEnv *env, jclass c) { }

JNIEXPORT jboolean JNICALL Java_android_util_Log_isLoggable__Ljava_lang_String_2I
        (JNIEnv *env, jclass c, jstring tag, jint level) { return JNI_TRUE; }

/* Log$PreloadHolder.<clinit> 会调它（P4 实测 UnsatisfiedLinkError 导致整条 SpiderDebug.log 抛异常），
 * 真值来自 liblog 的 LOG_ID_MAX_PAYLOAD 属性，4000 就是 Android 9 的默认。 */
JNIEXPORT jint JNICALL Java_android_util_Log_logger_1entry_1max_1payload_1native(JNIEnv *env, jclass c) {
    return 4000;
}

JNIEXPORT void JNICALL Java_android_util_Log_logger_1close(JNIEnv *env, jclass c) { }

JNIEXPORT jint JNICALL Java_android_util_Log_println_1native__IILjava_lang_String_2Ljava_lang_String_2
        (JNIEnv *env, jclass c, jint prio, jint tid, jstring tag, jstring msg) {
    static const char *lvl[] = {"?", "?", "V", "D", "I", "W", "E", "A"};
    KEYBUF; char mb[512];
    fprintf(stdout, "[jlog:%s %s:%d] %s\n", lvl[prio >= 0 && prio <= 7 ? prio : 0],
            jstr(env, tag, kb, sizeof kb), (int) tid, jstr(env, msg, mb, sizeof mb));
    fflush(stdout);
    return 0;
}

static JNINativeMethod g_props_methods[] = {
    {"native_get",      "(Ljava/lang/String;)Ljava/lang/String;",                       (void *) Java_android_os_SystemProperties_native_1get__Ljava_lang_String_2},
    {"native_get",      "(Ljava/lang/String;Ljava/lang/String;)Ljava/lang/String;",     (void *) Java_android_os_SystemProperties_native_1get__Ljava_lang_String_2Ljava_lang_String_2},
    {"native_get_int",  "(Ljava/lang/String;I)I",                                       (void *) Java_android_os_SystemProperties_native_1get_1int__Ljava_lang_String_2I},
    {"native_get_long", "(Ljava/lang/String;J)J",                                       (void *) Java_android_os_SystemProperties_native_1get_1long__Ljava_lang_String_2J},
    {"native_get_boolean", "(Ljava/lang/String;Z)Z",                                    (void *) Java_android_os_SystemProperties_native_1get_1boolean__Ljava_lang_String_2Z},
    {"native_set",      "(Ljava/lang/String;Ljava/lang/String;)V",                      (void *) Java_android_os_SystemProperties_native_1set__Ljava_lang_String_2Ljava_lang_String_2},
    {"native_find_prop", "(Ljava/lang/String;[B)I",                                     (void *) Java_android_os_SystemProperties_native_1find_1prop__Ljava_lang_String_2_3B},
    {"native_add_change_callback", "()V",                                               (void *) Java_android_os_SystemProperties_native_1add_1change_1callback},
    {"native_report_sysprop_change", "()V",                                             (void *) Java_android_os_SystemProperties_native_1report_1sysprop_1change},
};

static JNINativeMethod g_log_methods[] = {
    {"isLoggable",     "(Ljava/lang/String;I)Z",   (void *) Java_android_util_Log_isLoggable__Ljava_lang_String_2I},
    {"println_native", "(IILjava/lang/String;Ljava/lang/String;)I", (void *) Java_android_util_Log_println_1native__IILjava_lang_String_2Ljava_lang_String_2},
    {"logger_entry_max_payload_native", "()I", (void *) Java_android_util_Log_logger_1entry_1max_1payload_1native},
    {"logger_close",   "()V",                     (void *) Java_android_util_Log_logger_1close},
};

/* ── Looper/Handler 一族：MessageQueue 的 4 个 native 平时也由 libandroid_runtime 注册
 *    （2026-09-25 P3 实测：prepareMainLooper → UnsatisfiedLinkError nativeInit）。
 *    这里用 epoll + eventfd 自己实现，语义就是 android_os_MessageQueue.cpp 的那套：
 *    pollOnce(timeout) 等到超时或被 wake；wake 用 eventfd，一次 poll 把计数排空。
 *    SystemClock 三个也是 libandroid_runtime 的，Handler 一跑就要。 */
#include <errno.h>
#include <sys/epoll.h>
#include <sys/eventfd.h>
#include <sys/resource.h>
#include <unistd.h>

struct mq_shim { int ep; int ev; };

JNIEXPORT jlong JNICALL Java_android_os_MessageQueue_nativeInit(JNIEnv *env, jclass c) {
    struct mq_shim *m = (struct mq_shim *) calloc(1, sizeof *m);
    m->ep = epoll_create1(EPOLL_CLOEXEC);
    m->ev = eventfd(0, EFD_NONBLOCK | EFD_CLOEXEC);
    struct epoll_event it;
    memset(&it, 0, sizeof it);
    it.events = EPOLLIN;
    it.data.fd = m->ev;
    if (m->ep < 0 || m->ev < 0 || epoll_ctl(m->ep, EPOLL_CTL_ADD, m->ev, &it) < 0)
        fprintf(stderr, "[shim] MessageQueue.nativeInit 失败: %s\n", strerror(errno));
    return (jlong) (intptr_t) m;
}

JNIEXPORT void JNICALL Java_android_os_MessageQueue_nativeDestroy(JNIEnv *env, jclass c, jlong ptr) {
    struct mq_shim *m = (struct mq_shim *) (intptr_t) ptr;
    if (m) { if (m->ep >= 0) close(m->ep); if (m->ev >= 0) close(m->ev); free(m); }
}

JNIEXPORT void JNICALL Java_android_os_MessageQueue_nativePollOnce(JNIEnv *env, jobject thiz, jlong ptr, jint timeout) {
    struct mq_shim *m = (struct mq_shim *) (intptr_t) ptr;
    if (!m) return;
    struct epoll_event got;
    for (;;) {
        int n = epoll_wait(m->ep, &got, 1, timeout < 0 ? -1 : (int) timeout);
        if (n == 0) return;                       /* 超时：MessageQueue.next() 自己会再转一圈 */
        if (n > 0) {
            unsigned long long v;
            while (read(m->ev, &v, sizeof v) > 0) { }
            return;
        }
        if (errno == EINTR) continue;
        fprintf(stderr, "[shim] epoll_wait: %s\n", strerror(errno));
        return;
    }
}

JNIEXPORT void JNICALL Java_android_os_MessageQueue_nativeWake(JNIEnv *env, jclass c, jlong ptr) {
    struct mq_shim *m = (struct mq_shim *) (intptr_t) ptr;
    if (!m) return;
    static const unsigned long long one = 1;
    if (write(m->ev, &one, sizeof one) < 0) fprintf(stderr, "[shim] wake: %s\n", strerror(errno));
}

static jlong clock_ms(int id) {
    struct timespec ts;
    return clock_gettime((clockid_t) id, &ts) == 0 ? (jlong) ts.tv_sec * 1000 + ts.tv_nsec / 1000000 : 0;
}

JNIEXPORT jlong JNICALL Java_android_os_SystemClock_uptimeMillis(JNIEnv *env, jclass c) { return clock_ms(CLOCK_MONOTONIC); }
JNIEXPORT jlong JNICALL Java_android_os_SystemClock_elapsedRealtime(JNIEnv *env, jclass c) { return clock_ms(CLOCK_BOOTTIME); }
JNIEXPORT jlong JNICALL Java_android_os_SystemClock_currentThreadTimeMillis(JNIEnv *env, jclass c) { return clock_ms(CLOCK_THREAD_CPUTIME_ID); }
JNIEXPORT jlong JNICALL Java_android_os_SystemClock_elapsedRealtimeNanos(JNIEnv *env, jclass c) {
    struct timespec ts;
    return clock_gettime(CLOCK_BOOTTIME, &ts) == 0 ? (jlong) ts.tv_sec * 1000000000LL + ts.tv_nsec : 0;
}

JNIEXPORT void JNICALL Java_android_os_ThreadPool_nativeSetThreadPriority(JNIEnv *env, jclass c, jint tid, jint prio) {
    if (setpriority(PRIO_PROCESS, (id_t) tid, (int) prio) < 0 && getenv("SHIM_VERBOSE"))
        fprintf(stderr, "[shim] setpriority(%d,%d): %s\n", (int) tid, (int) prio, strerror(errno));
}

JNIEXPORT jint JNICALL Java_android_os_ThreadPool_nativeGetThreadPriority(JNIEnv *env, jclass c, jint tid) {
    errno = 0;
    int p = getpriority(PRIO_PROCESS, (id_t) tid);
    return (p < 0 && errno) ? 0 : p;
}

static JNINativeMethod g_mq_methods[] = {
    {"nativeInit",    "()J",          (void *) Java_android_os_MessageQueue_nativeInit},
    {"nativeDestroy", "(J)V",         (void *) Java_android_os_MessageQueue_nativeDestroy},
    {"nativePollOnce","(JI)V",        (void *) Java_android_os_MessageQueue_nativePollOnce},
    {"nativeWake",    "(J)V",         (void *) Java_android_os_MessageQueue_nativeWake},
};

static JNINativeMethod g_clock_methods[] = {
    {"uptimeMillis",          "()J",  (void *) Java_android_os_SystemClock_uptimeMillis},
    {"elapsedRealtime",       "()J",  (void *) Java_android_os_SystemClock_elapsedRealtime},
    {"elapsedRealtimeNanos",  "()J",  (void *) Java_android_os_SystemClock_elapsedRealtimeNanos},
    {"currentThreadTimeMillis","()J", (void *) Java_android_os_SystemClock_currentThreadTimeMillis},
};

static JNINativeMethod g_pool_methods[] = {
    {"nativeSetThreadPriority", "(II)V", (void *) Java_android_os_ThreadPool_nativeSetThreadPriority},
    {"nativeGetThreadPriority", "(I)I",  (void *) Java_android_os_ThreadPool_nativeGetThreadPriority},
};


/* 逐个注册：AOSP 小版本之间方法表有出入，整批 RegisterNatives 会因一个缺失而全失败。 */
static int reg_all(JNIEnv *env, const char *cls, JNINativeMethod *ms, int n) {
    jclass c = (*env)->FindClass(env, cls);
    if (!c) { (*env)->ExceptionClear(env); fprintf(stderr, "[proppreload] 没有 %s\n", cls); return 0; }
    int ok = 0;
    for (int i = 0; i < n; i++) {
        if ((*env)->RegisterNatives(env, c, &ms[i], 1) == JNI_OK) ok++;
        else { (*env)->ExceptionClear(env); fprintf(stderr, "[proppreload] 注册不上 %s.%s%s\n", cls, ms[i].name, ms[i].signature); }
    }
    fprintf(stderr, "[proppreload] %s 注册了 %d/%d 个 native\n", cls, ok, n);
    return ok;
}

int catclaw_register_boot_natives(JNIEnv *env) {
    ensure_pool();
    int n = reg_all(env, "android/os/SystemProperties", g_props_methods,
                    (int) (sizeof g_props_methods / sizeof g_props_methods[0]))
          + reg_all(env, "android/util/Log", g_log_methods,
                    (int) (sizeof g_log_methods / sizeof g_log_methods[0]))
          + reg_all(env, "android/os/MessageQueue", g_mq_methods,
                    (int) (sizeof g_mq_methods / sizeof g_mq_methods[0]))
          + reg_all(env, "android/os/SystemClock", g_clock_methods,
                    (int) (sizeof g_clock_methods / sizeof g_clock_methods[0]))
          + reg_all(env, "android/os/ThreadPool", g_pool_methods,
                    (int) (sizeof g_pool_methods / sizeof g_pool_methods[0]));
    fprintf(stderr, "[proppreload] boot native 共 %d 个\n", n);
    return n;
}


