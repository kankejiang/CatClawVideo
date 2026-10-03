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

/* 属性表按架构二选一（gen_props.py 生成）：
 *   默认 v1/props_gen.h    —— aarch64 现网表
 *   -DX86_GUEST → props_gen_x64.h —— x86 mini guest 表（含 nativebridge/libndk 属性组） */
#ifdef X86_GUEST
#include "props_gen_x64.h"
#else
#include "props_gen.h"
#endif


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
    /* bionic 契约：阻塞到该属性串号 != old_serial（返回 1），或 relative_timeout 到点（返回 0）。
     * 2026-10-03 CPU 排障（profiler 实证）：调用方传进来的 pi 可能来自 **libpropfix** 的
     * find（LD_PRELOAD 链里它排在我们前面），其占位 prop_info 的 serial 与我们 g_pool
     * 的编号不是一套 —— 按 pi->serial 比较会得到"永远刚变化"，秒回 ⇒ libbase 的
     * WaitForProperty 退化成纯用户态忙循环（adbd 单线程恒定 100% utime/stime=0）。
     * 处置：不再比较任何串号，统一睡 20ms 后返回 —— wait 的语义在本 guest 退化为
     * "每次至多 20ms 的轮询间隔"，对编译期写死+进程内 set 的静态表足够。 */
    (void) pi;
    (void) old_serial;
    (void) relative_timeout;
    ensure_pool();
    struct timespec ts = {0, 20 * 1000 * 1000};   /* 20ms */
    nanosleep(&ts, NULL);
    if (new_serial_ptr) *new_serial_ptr = g_serial;
    return 1;
}

/* 有些代码走的是这个（等待任意属性变化）。 */

/* ── wait_any 接管（2026-10-03 CPU 排障）──
 * bionic 老接口：阻塞到「任意」属性串号变化，返回其 prop_info。
 * 不接管时调用方落到真 bionic —— guest 没有 /dev/__properties__，
 * 属性区未初始化 ⇒ 秒回 NULL ⇒ adbd 的监视线程**纯用户态**忙循环
 * （实测单线程恒定 100% utime / stime=0，从开机开始，即 adbd 空转的最后一块拼图）。
 * 这里用 20ms 步进轮询模拟阻塞；表是编译期写死+进程内 set，灵敏度足够。 */
const prop_info *__system_property_wait_any(uint32_t old_serial) {
    /* 同 __system_property_wait：调用方手里的 old_serial 可能来自 libpropfix 的编号，
     * 与我们 g_serial 不是一套 ⇒ 比较恒真 ⇒ 忙循环。统一退化为 20ms 轮询。 */
    (void) old_serial;
    ensure_pool();
    struct timespec ts = {0, 20 * 1000 * 1000};   /* 20ms */
    nanosleep(&ts, NULL);
    return &g_pool[0];
}

/* ── 极简采样 profiler（PROPPROF=1 启用，2026-10-03 CPU 排障）─────────────────
 * 背景：adbd 一个线程纯用户态 100% 空转（stime=0 ⇒ 无任何系统调用），内核接口
 * （syscall/kstkeip/ptrace）全被权限挡死。这里用 ITIMER_PROF + SIGPROF 直接采样
 * RIP，攒 90 秒直方图后由监控线程用 dladdr 归属模块并打到 stderr，供宿主侧
 * 用 llvm-addr2line 对着 adbd/libc 符号化。只诊断不干预，采样成本可忽略。 */
#include <signal.h>
#include <sys/time.h>
#include <pthread.h>
#include <dlfcn.h>
#include <unistd.h>

#define PROF_SLOTS 256
static uintptr_t g_prof_pc[PROF_SLOTS];
static unsigned long long g_prof_n[PROF_SLOTS];
static int g_prof_used;

static void prof_handler(int sig, siginfo_t *si, void *uctx) {
    (void) sig; (void) si;
    ucontext_t *uc = (ucontext_t *) uctx;
#if defined(__aarch64__)
    uintptr_t pc = (uintptr_t) uc->uc_mcontext.pc;
#elif defined(__x86_64__)
    uintptr_t pc = (uintptr_t) uc->uc_mcontext.gregs[16];   /* x86_64: gregs[16] = RIP */
#else
    uintptr_t pc = 0;
#endif
    if (!pc) return;
    for (int i = 0; i < g_prof_used; i++) {
        uintptr_t d = pc > g_prof_pc[i] ? pc - g_prof_pc[i] : g_prof_pc[i] - pc;
        if (d < 96) { g_prof_n[i]++; return; }
    }
    if (g_prof_used < PROF_SLOTS) {
        g_prof_pc[g_prof_used] = pc;
        g_prof_n[g_prof_used] = 1;
        g_prof_used++;
    }
}

static void *prof_monitor(void *arg) {
    (void) arg;
    for (int round = 0; round < 3; round++) {
        sleep(90);
        fprintf(stderr, "[prof] ===== round %d (%d slots) =====\n", round, g_prof_used);
        for (int i = 0; i < g_prof_used; i++) {
            void *pc = (void *) g_prof_pc[i];
            Dl_info info;
            const char *mod = "?";
            uintptr_t base = 0;
            if (dladdr(pc, &info) && info.dli_fname) {
                mod = info.dli_fname;
                base = (uintptr_t) info.dli_fbase;
            }
            fprintf(stderr, "[prof] pc=%lx n=%llu mod=%s off=%lx\n",
                    (unsigned long) pc, g_prof_n[i], mod,
                    (unsigned long) (pc - base));
        }
        fflush(stderr);
    }
    return NULL;
}

static void prof_init(void) __attribute__((constructor));
static void prof_init(void) {
    if (!getenv("PROPPROF")) return;
    struct sigaction sa;
    memset(&sa, 0, sizeof sa);
    sa.sa_sigaction = prof_handler;
    sa.sa_flags = SA_SIGINFO | SA_RESTART;
    sigaction(SIGPROF, &sa, NULL);
    struct itimerval it;
    it.it_interval.tv_sec = 0;
    it.it_interval.tv_usec = 5 * 1000;   /* 5ms */
    it.it_value = it.it_interval;
    setitimer(ITIMER_PROF, &it, NULL);
    pthread_t th;
    pthread_create(&th, NULL, prof_monitor, NULL);
}

int __system_property_wdev_l(const prop_info *pi, uint32_t old_serial, const char *value, int vlen) {
    (void) pi; (void) old_serial; (void) value; (void) vlen;
    return 0;
}

/* ── 桥进程内的 fork 拦截（2026-10-01 扫码链路实测）─────────────────────────
 * 现象：壳的爬虫代码在桥进程里起子进程（Go 代理自更新、chmod、shell 探测……）时，
 *       **父进程**（ART）当场 SIGSEGV：PC 恒落在 /memfd:jit-cache (deleted) 区，
 *       退出码 139，随后整个 x86 mini guest 陪葬，用户侧症状就是「点扫码登录没反应」
 *       （点击落在已经死掉的桥上）。
 * 关键更正：把那条 arm64 二进制的下载/执行挡掉之后（GoProxy 改报
 *       `path= chmod=false reason=下载失败`），**同一时刻依旧 [sig] s=11**
 *       （2026-10-01 03:33:30.831 实测）。⇒ 致命的不是子进程跑的是什么，而是
 *       「桥进程 fork 出子进程」这件事本身在这个 guest 里就不安全。
 *       （早先两条猜测——LD_PRELOAD 被继承踩坏父进程、ENOEXEC 回落 sh 解析二进制——
 *        都被这一条推翻，报告 §11.1/§11.5 已更正。）
 * 处置：在 artlaunch 进程里让 fork/vfork/posix_spawn* 直接失败（EAGAIN）。
 *       Java 侧只会看到 IOException，壳记一条 start failed 继续跑，桥与 guest 都不再陪葬。
 *       本垫片只预加载进 artlaunch（init 里那一行 LD_PRELOAD=），constructor 又已
 *       unsetenv("LD_PRELOAD")，所以 guest 里真正需要 fork 的进程（init/busybox/harness）不受影响。
 */
#include <errno.h>
#include <spawn.h>
#include <dlfcn.h>
#include <fcntl.h>
#include <unistd.h>
#include <sys/types.h>

#define FORK_BLOCK_MAX_LOG 8

static int g_fork_blocked = 0;

static void fork_block_note(const char *who) {
    if (g_fork_blocked++ < FORK_BLOCK_MAX_LOG)
        fprintf(stderr, "[proppreload] 拦下 %s：桥进程 fork 会打死 ART（guest 陪葬）\n", who);
}

/* ── fork 放行白名单（2026-10-02，T1 adb 通道实测）──
 * adbd 被 LD_PRELOAD 挂上本垫片后，它给 adb shell/exec-out fork 子进程也吃 EAGAIN
 * （实测：adb shell 恒报 "fork failed: Try again"）——adbd 的 shell 子进程是
 * adb 通道的存在意义，必须放行。判定：/proc/self/cmdline 含 "adbd"（垫片无法
 * 被 guest 内的 jar 代码欺骗——jar 跑在桥进程里，cmdline 是 artlaunch）。
 * 环境变量 PROPPRELOAD_ALLOW_FORK 是诊断用的显式后门。 */
static int pf_fork_allowed(void) {
    static int cached = -1;
    if (cached < 0) {
        char cl[256];
        int fd, n;
        cached = getenv("PROPPRELOAD_ALLOW_FORK") ? 1 : 0;
        fd = open("/proc/self/cmdline", O_RDONLY);
        if (fd >= 0) {
            n = read(fd, cl, sizeof(cl) - 1);
            close(fd);
            if (n > 0) { cl[n] = 0; if (strstr(cl, "adbd")) cached = 1; }
        }
    }
    return cached;
}

pid_t fork(void) {
    static pid_t (*real)(void) = NULL;
    if (pf_fork_allowed()) {
        if (!real) real = (pid_t (*)(void)) dlsym(RTLD_NEXT, "fork");
        if (real) return real();
    }
    fork_block_note("fork"); errno = EAGAIN; return -1;
}
pid_t vfork(void) {
    static pid_t (*real)(void) = NULL;
    if (pf_fork_allowed()) {
        /* 2026-10-03：ALLOW_FORK 下改走**真 fork**，绝不真 vfork——
         * vfork 子进程与父进程共享地址空间/栈，子进程 exec 完成前的任何动作
         * （bionic 的 fd 整理、proppreload 插桩残余）都写在共享页上；实测 Go 代理
         * 子进程一崩（139），父 ART 随后 jit-cache SIGSEGV 陪葬（桥读循环退出）。
         * fork 的子进程 COW 隔离，子进程怎么死都伤不到父进程；桥内 exec 频率低
         * （Go 代理自更新、adbd），页表拷贝开销可忽略。 */
        if (!real) real = (pid_t (*)(void)) dlsym(RTLD_NEXT, "fork");
        if (real) return real();
    }
    fork_block_note("vfork"); errno = EAGAIN; return -1;
}
pid_t __clone(int (*fn)(void *), void *stack, int flags, void *arg, ...) {
    (void) fn; (void) stack; (void) flags; (void) arg;
    fork_block_note("__clone"); errno = EAGAIN; return -1;
}

/* posix_spawn 系列返回**正数错误码**（不是 -1/errno），bionic 就是这么规定的。 */
/* ── adbd 白名单（2026-10-02）──
 * 05:20 版 bridge.jar 把 adbd 的拉起移进了桥（Server.startAdbd，走 ProcessBuilder/
 * posix_spawn），而桥进程挂着本垫片 ⇒ posix_spawn 被拦 EAGAIN ⇒ adbd 起不来
 * （实测："[adbd] 启动失败: Cannot run program /system/bin/adbd: error=11"）。
 * adbd 是我们自己的编排组件（非 jar 的任意 fork），按路径放行；jar 的其它 fork 仍拦。 */
static int pf_allow_exec(const char *p) {
    /* 桥自己拉起的编排组件：adbd + surfaceflinger + 各 HAL 服务（/vendor/bin/hw/、
     * /system/bin/hw/）。jar 的任意 fork（Go 代理等）仍拦。 */
    if (!p) return 0;
    return strstr(p, "/adbd") || strstr(p, "/surfaceflinger") ||
           strstr(p, "/bin/hw/") || strstr(p, "weston");
}

int posix_spawn(pid_t *pid, const char *path, const posix_spawn_file_actions_t *fa,
                const posix_spawnattr_t *at, char *const argv[], char *const envp[]) {
    static int (*real)(pid_t *, const char *, const posix_spawn_file_actions_t *,
                       const posix_spawnattr_t *, char *const [], char *const []) = NULL;
    if (!real) real = (int (*)(pid_t *, const char *, const posix_spawn_file_actions_t *,
                               const posix_spawnattr_t *, char *const [], char *const []))
        dlsym(RTLD_NEXT, "posix_spawn");
    if (pf_allow_exec(path) && real) return real(pid, path, fa, at, argv, envp);
    fork_block_note("posix_spawn"); if (pid) *pid = -1; return EAGAIN;
}

int posix_spawnp(pid_t *pid, const char *file, const posix_spawn_file_actions_t *fa,
                 const posix_spawnattr_t *at, char *const argv[], char *const envp[]) {
    static int (*real)(pid_t *, const char *, const posix_spawn_file_actions_t *,
                       const posix_spawnattr_t *, char *const [], char *const []) = NULL;
    if (!real) real = (int (*)(pid_t *, const char *, const posix_spawn_file_actions_t *,
                               const posix_spawnattr_t *, char *const [], char *const []))
        dlsym(RTLD_NEXT, "posix_spawnp");
    if (pf_allow_exec(file) && real) return real(pid, file, fa, at, argv, envp);
    fork_block_note("posix_spawnp"); if (pid) *pid = -1; return EAGAIN;
}

int posix_clone_file_actions(const posix_spawn_file_actions_t *fa, posix_spawn_file_actions_t *to) {
    (void) fa; (void) to;
    fork_block_note("posix_clone_file_actions"); return EINVAL;
}

/* ⚠ 试过再拦 `syscall(__NR_clone/57/58)`（qrG，2026-10-01 03:45:47）：**倒退**——
 * 一次启动 6ms 内连爆 7 条 [sig] s=11 pc=fffffffffffffb17（=把 -1 当指针解引用），
 * 桥读循环随即退出。原因是替身 syscall 少报参数时按 6 个 va_arg 读会读到脏值，
 * bionic 里合法的 syscall 调用被一起打坏。⇒ 只保留符号级的 fork/vfork/posix_spawn* 拦截。
 */

static void __attribute__((constructor)) proppreload_note(void) {
    ensure_pool();
    /* 2026-10-02 静默化：这行每进程一条、走 stderr，exec-out/adb 二进制通道
     * （如 screencap 的 PNG）被它污染——adbd 的 LD_PRELOAD 会被 exec-out 的
     * sh 与目标进程继承，stdout/stderr 在 adbd 侧合并。诊断价值 < 通道纯净性。 */
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[proppreload] 接管 Android 属性 API：%d 条\n", NPROPS);
    /* 立刻把 LD_PRELOAD 从**本进程环境**里抹掉：它会被 artlaunch 的每个子进程继承，
     * 于是壳的 Go 代理自更新（fork 后 exec /data/files/moyu_go/pvideo-arm64-v8a，
     * x86_64 内核认不了 arm64 ELF → 回落 busybox sh 逐行「解析」那 3MB 二进制）里，
     * 我们的 constructor 在 fork 与 exec 之间又跑了一遍 —— 往与父进程共享的页上写，
     * 父进程（ART）随后 SIGSEGV（退出码 139），整个 guest 跟着没了。
     * 2026-10-01 实测：5/5 次「GoProxy start failed」都紧跟着 [sig] s=11 + 桥退出 139。
     * 本进程早在此前就已完成符号插入（interposition 是进程内的），撤掉环境变量不影响
     * 已生效的接管，只让子进程干净地不加载它。 */
    unsetenv("LD_PRELOAD");
}

#ifdef X86_GUEST
/* ── nativebridge namespace 判定接管（2026-09-27，Guard 转译最后一环）────────────
 * 现象：壳 DexNative.<clinit> 的 System.load(arm64 so) 报
 *   dlopen failed: ".fty…" is for EM_AARCH64 (183) instead of EM_X86_64 (62)
 * 链路：System.load → libnativeloader；classloader namespace 创建时
 * NativeLoaderNamespace::Create 逐段调 NativeBridgeIsPathSupported(search_path)
 * 决定 namespace 是否 bridged；ndk_translation 的该回调对我们所有路径都返回 false
 * （实测含官方 /data/app/…/lib/arm64 模式）→ namespace 永久 not-bridged →
 * arm64 so 全走 bionic dlopen → 架构不符拒绝。
 * 接管语义：/data/catclaw 前缀（桥的 librarySearchPath 根）→ true——壳的
 * classloader namespace 只服务壳的 arm64 so，全量 bridge 是正确语义；namespace
 * bridged 后加载走 NativeBridgeLoadLibraryExt → ndk arm64 linker 转译（已实测通）。
 * 其余路径 → false，与原实现实测值一致（原实现对一切路径都拒绝）。
 * LD_PRELOAD 位于全局组最前，libnativeloader 的 PLT 解析会命中本实现。 */
int NativeBridgeIsPathSupported(const char *path) {
    return path != NULL && strncmp(path, "/data/catclaw", 13) == 0;
}
#endif

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


