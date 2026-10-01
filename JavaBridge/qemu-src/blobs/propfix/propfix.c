// libpropfix.so —— B1.1（2026-10-01）
//
// 目的：让 guest 里的 Android 原生服务看到"跨进程属性"。
//
// 背景：我们的属性层 /proppreload.so 是**每进程一份、编译期写死**的表（strings 实证：无文件/环境
// 变量入口）。于是桥（Java）里 set 的属性，别的进程根本看不见 —— 而 HIDL 客户端是靠属性
// `hwservicemanager.ready` 判断"注册表可用了"的，于是 SurfaceFlinger 死等：
//     [logd] Waited for hwservicemanager.ready for a second, waiting another...
//
// 做法：一个最小的 LD_PRELOAD shim，只回答**环境变量里声明**的那几个属性，其余全部转发给下一个
// 实现（`dlsym(RTLD_NEXT, ...)` → 命中 /proppreload.so 的表；它没接管时命中 bionic libc）。
// 因此 shim 必须排在 /proppreload.so **前面**。
//
// 用法：
//   LD_PRELOAD=/system/lib64/libpropfix.so:/proppreload.so
//   PROPFIX="hwservicemanager.ready=true;ro.hardware.hwcomposer=waydroid"
//
// 编（108 上有 NDK）：
//   x86_64-linux-android33-clang -shared -fPIC -O2 -o libpropfix.so propfix.c -ldl
#define _GNU_SOURCE
#include <dlfcn.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#define MAXK 32
#define VLEN 96

// ── 崩溃现场：装 SIGSEGV/SIGABRT/… 处理器，把信号与回溯直接打到 stderr ──
// 为什么需要：SurfaceFlinger 在 RenderEngine 之后崩了，但日志里只有 crash_dump64
// （连 tombstoned 都没有 ⇒ 拿不到 tombstone），致命信号与回溯根本看不到。
// 用 PROPFIX_CRASH=1 打开（避免影响正常进程）。
#include <execinfo.h>
#include <fcntl.h>   // open() 给崩溃日志用
#include <signal.h>

static void propfix_crash_handler(int sig, siginfo_t *si, void *ctx) {
    (void)ctx;
    void *bt[48];
    int n = backtrace(bt, 48);
    char buf[256];
    int k = snprintf(buf, sizeof(buf),
                     "\n[propfix] !! 收到信号 %d，地址 %p，回溯 %d 层：\n",
                     sig, si ? si->si_addr : (void *)0, n);
    if (k > 0) (void)!write(2, buf, (size_t)k);
    backtrace_symbols_fd(bt, n, 2);   // 直接写 fd 2：崩溃时机不适合 malloc

    // 同时落一份文件：stderr 会被 logd 的噪音冲乱（实测读不干净），文件可以用 adb 直接读。
    int fd = open("/data/crash.log", O_WRONLY | O_CREAT | O_APPEND, 0644);
    if (fd >= 0) {
        if (k > 0) (void)!write(fd, buf, (size_t)k);
        backtrace_symbols_fd(bt, n, fd);
        // 主可执行段的基址：发布版二进制被 strip，backtrace 只显示 "surfaceflinger(+0x0)"，
        // 有了基址就能用 (addr - base) 对照该二进制的**导出符号表**（readelf --dyn-syms）定位函数。
        const char *mp = "/proc/self/maps";
        int mf = open(mp, O_RDONLY);
        if (mf >= 0) {
            char mbuf[2048];
            int got = (int)read(mf, mbuf, sizeof(mbuf) - 1);
            if (got > 0) {
                mbuf[got] = 0;
                (void)!write(fd, "\n[propfix] /proc/self/maps 头部（含主模块基址）:\n", 48);
                (void)!write(fd, mbuf, (size_t)got);
            }
            close(mf);
        }
        close(fd);
    }

    signal(sig, SIG_DFL);
    raise(sig);
}

__attribute__((constructor)) static void propfix_install_crash_handler(void) {
    if (!getenv("PROPFIX_CRASH")) return;
    struct sigaction sa;
    memset(&sa, 0, sizeof(sa));
    sa.sa_sigaction = propfix_crash_handler;
    sa.sa_flags = SA_SIGINFO | SA_RESETHAND;
    sigaction(SIGSEGV, &sa, NULL);
    sigaction(SIGABRT, &sa, NULL);
    sigaction(SIGBUS, &sa, NULL);
    sigaction(SIGILL, &sa, NULL);
    sigaction(SIGFPE, &sa, NULL);
}

// EGL 基本类型放在最前面：下面的拦截块可能被 #if 0 关掉，类型定义不能跟着被关
// （实测踩过一次坑：typedef 落在 #if 0 区里 ⇒ eglChooseConfig 报 unknown type name 'EGLBoolean'）。
typedef int32_t EGLint;
typedef uint32_t EGLBoolean;
typedef void *EGLDisplay;
typedef void *EGLConfig;

// EGL 属性名（诊断打印用）。同样必须在 #if 0 区之外定义 —— 关掉拦截块时仍要能编译。
static const char *egl_attr_name(EGLint a) {
    switch (a) {
        case 0x3021: return "ALPHA_SIZE";
        case 0x3022: return "BLUE_SIZE";
        case 0x3023: return "GREEN_SIZE";
        case 0x3024: return "RED_SIZE";
        case 0x3025: return "DEPTH_SIZE";
        case 0x3026: return "STENCIL_SIZE";
        case 0x3027: return "CONFIG_CAVEAT";
        case 0x3028: return "CONFIG_ID";
        case 0x3029: return "LEVEL";
        case 0x302A: return "MAX_PBUFFER_HEIGHT";
        case 0x302B: return "MAX_PBUFFER_WIDTH";
        case 0x302C: return "NATIVE_RENDERABLE";
        case 0x302D: return "NATIVE_VISUAL_ID";
        case 0x302E: return "NATIVE_VISUAL_TYPE";
        case 0x302F: return "PRESERVED_RESOURCES";
        case 0x3030: return "SAMPLES";
        case 0x3031: return "SAMPLE_BUFFERS";
        case 0x3032: return "SURFACE_TYPE";
        case 0x3033: return "TRANSPARENT_TYPE";
        case 0x3038: return "NONE";
        case 0x3040: return "RENDERABLE_TYPE";
        case 0x3142: return "RECORDABLE_ANDROID";
        case 0x3080: return "ALPHA_MASK_SIZE";
        case 0x3081: return "BIND_TO_TEXTURE_RGB";
        case 0x3082: return "BIND_TO_TEXTURE_RGBA";
        case 0x3083: return "BUFFER_SIZE";
        case 0x3084: return "COLOR_BUFFER_TYPE";
        case 0x3085: return "CONFORMANT";
        case 0x3086: return "LUMINANCE_SIZE";
        case 0x3200: return "COLORSPACE(android)";
        case 0x3201: return "FRAMEBUFFER_TARGET_ANDROID";
        case 0x3202: return "SWAP_BEHAVIOR_PRESERVED(android)";
        default:     return "?";
    }
}

static char g_keys[MAXK][128];
static char g_vals[MAXK][VLEN];
static int g_n = -1;                 // -1 = 还没解析

// 给 __system_property_find 用：每个 key 一块私有的 prop_info 占位内存
static uint8_t g_fake[MAXK][128];


// ── 诊断：GraphicBufferAllocator::allocate ──
// 目的：SF 的 RenderEngine 断言 "output buffer not gpu writeable"，
// 需要知道 SF 究竟用什么 usage 去申请输出缓冲、申请是否成功。
// 做法：C 函数 + asm 标签指定 C++ mangled 名（libui.so 的导出符号，调用方在 SF 里 ⇒ 可拦截）。
// 注意：Android 13 的 usage 是 64 位。
typedef int32_t propfix_status2_t;

static void log_alloc(const char *tag, uint32_t w, uint32_t h, int fmt, uint32_t layers,
                      uint64_t usage, propfix_status2_t rc) {
    if (!getenv("PROPFIX_DEBUG")) return;
    fprintf(stderr, "[propfix] %s(w=%u h=%u fmt=0x%x layers=%u usage=0x%llx) -> %d "
                    "[HW_TEXTURE=%d HW_RENDER=%d HW_2D=%d HW_COMPOSER=%d GPU_DATA=%d]\n",
            tag, w, h, fmt, layers, (unsigned long long)usage, rc,
            (int)((usage & 0x100) != 0), (int)((usage & 0x200) != 0),
            (int)((usage & 0x400) != 0), (int)((usage & 0x800) != 0),
            (int)((usage & 0x1000000ULL) != 0));
}

// 8 参数重载：allocate(w,h,format,layerCount,usage,native_handle const**, uint32_t*, string)
propfix_status2_t gba_alloc8(uint32_t w, uint32_t h, int fmt, uint32_t layers, uint64_t usage,
                             void **handle, uint32_t *stride, void *err)
    __asm__("_ZN7android22GraphicBufferAllocator8allocateEjjijmPPK13native_handlePjNSt3__112basic_stringIcNS6_11char_traitsIcEENS6_9allocatorIcEEEE");
propfix_status2_t gba_alloc8(uint32_t w, uint32_t h, int fmt, uint32_t layers, uint64_t usage,
                             void **handle, uint32_t *stride, void *err) {
    static propfix_status2_t (*real)(uint32_t, uint32_t, int, uint32_t, uint64_t, void **, uint32_t *, void *) = NULL;
    if (!real) real = (propfix_status2_t (*)(uint32_t, uint32_t, int, uint32_t, uint64_t, void **, uint32_t *, void *))
        dlsym(RTLD_NEXT, "_ZN7android22GraphicBufferAllocator8allocateEjjijmPPK13native_handlePjNSt3__112basic_stringIcNS6_11char_traitsIcEENS6_9allocatorIcEEEE");
    propfix_status2_t rc = real ? real(w, h, fmt, layers, usage, handle, stride, err) : -1;
    log_alloc("GraphicBufferAllocator::allocate(8)", w, h, fmt, layers, usage, rc);
    return rc;
}

// 9 参数重载：多一个 unsigned long（可能是 usage2/allocFlags）
propfix_status2_t gba_alloc9(uint32_t w, uint32_t h, int fmt, uint32_t layers, uint64_t usage,
                             void **handle, uint32_t *stride, unsigned long extra, void *err)
    __asm__("_ZN7android22GraphicBufferAllocator8allocateEjjijmPPK13native_handlePjmNSt3__112basic_stringIcNS6_11char_traitsIcEENS6_9allocatorIcEEEE");
propfix_status2_t gba_alloc9(uint32_t w, uint32_t h, int fmt, uint32_t layers, uint64_t usage,
                             void **handle, uint32_t *stride, unsigned long extra, void *err) {
    static propfix_status2_t (*real)(uint32_t, uint32_t, int, uint32_t, uint64_t, void **, uint32_t *, unsigned long, void *) = NULL;
    if (!real) real = (propfix_status2_t (*)(uint32_t, uint32_t, int, uint32_t, uint64_t, void **, uint32_t *, unsigned long, void *))
        dlsym(RTLD_NEXT, "_ZN7android22GraphicBufferAllocator8allocateEjjijmPPK13native_handlePjmNSt3__112basic_stringIcNS6_11char_traitsIcEENS6_9allocatorIcEEEE");
    propfix_status2_t rc = real ? real(w, h, fmt, layers, usage, handle, stride, extra, err) : -1;
    log_alloc("GraphicBufferAllocator::allocate(9)", w, h, fmt, layers, usage, rc);
    return rc;
}


// ── 诊断：直接抓 abort 原文 ──
// 实测：LOG_ALWAYS_FATAL/assert 的文本**不经过** __android_log_write/__android_log_buf_write
// （liblog 内部走 crash 缓冲 + logdw），所以在 shim 里按优先级也抓不到（0 条）。
// 但 abort 文本是由 libc 导出的 android_set_abort_message() 设置的 ⇒ 拦它即可。
void android_set_abort_message(const char *msg) {
    static void (*real)(const char *) = NULL;
    if (!real) real = (void (*)(const char *))dlsym(RTLD_NEXT, "android_set_abort_message");
    fprintf(stderr, "\n[propfix] ==== ABORT 原文 ====\n[propfix] %s\n[propfix] ===================\n",
            msg ? msg : "(null)");
    int fd = open("/data/abort.txt", O_WRONLY | O_CREAT | O_APPEND, 0644);
    if (fd >= 0) {
        char line[1024];
        int n = snprintf(line, sizeof(line), "ABORT: %s\n", msg ? msg : "(null)");
        if (n > 0) (void)!write(fd, line, (size_t)n);
        close(fd);
    }
    if (real) real(msg);
}

static void parse_once(void) {
    if (g_n >= 0) return;
    g_n = 0;
    const char *env = getenv("PROPFIX");
    if (!env || !*env) return;
    char buf[4096];
    strncpy(buf, env, sizeof(buf) - 1);
    buf[sizeof(buf) - 1] = 0;
    char *save1 = NULL;
    for (char *kv = strtok_r(buf, ";", &save1); kv && g_n < MAXK; kv = strtok_r(NULL, ";", &save1)) {
        char *eq = strchr(kv, '=');
        if (!eq) continue;
        *eq = 0;
        const char *k = kv, *v = eq + 1;
        while (*k == ' ') k++;
        while (*v == ' ') v++;
        snprintf(g_keys[g_n], sizeof(g_keys[0]), "%s", k);
        snprintf(g_vals[g_n], sizeof(g_vals[0]), "%s", v);
        g_n++;
    }
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] 接管 %d 个属性\n", g_n);
}

static int idx_of(const char *name) {
    parse_once();
    for (int i = 0; i < g_n; i++)
        if (strcmp(g_keys[i], name) == 0) return i;
    return -1;
}

int __system_property_get(const char *name, char *value) {
    static int (*real)(const char *, char *) = NULL;
    if (!real) real = (int (*)(const char *, char *))dlsym(RTLD_NEXT, "__system_property_get");
    int i = name ? idx_of(name) : -1;
    if (i >= 0) {
        const char *v = g_vals[i];
        if (value) { strncpy(value, v, 91); value[91] = 0; }
        if (getenv("PROPFIX_DEBUG")) fprintf(stderr, "[propfix] get %s = %s\n", name, v);
        return (int)strlen(v);
    }
    return real ? real(name, value) : 0;
}

const void *__system_property_find(const char *name) {
    static const void *(*real)(const char *) = NULL;
    if (!real) real = (const void *(*)(const char *))dlsym(RTLD_NEXT, "__system_property_find");
    int i = name ? idx_of(name) : -1;
    if (i >= 0) {
        if (getenv("PROPFIX_DEBUG")) fprintf(stderr, "[propfix] find %s -> 我方表\n", name);
        return (const void *)g_fake[i];
    }
    const void *r = real ? real(name) : NULL;
    // ⚠ 关键诊断：把"**没找到**"的键也打出来 —— EGL 找不到驱动时，
    // 真正有用的信息是"它问的是哪个键、而这个键不存在"（实测 get 通道只出现过一次，
    // 说明 libEGL 走的是 find/read_callback 这条路）。
    if (getenv("PROPFIX_DEBUG") && name && !r)
        fprintf(stderr, "[propfix] find %s -> NULL（其实现里没有）\n", name);
    return r;
}

int __system_property_read(const void *pi, char *name, char *value) {
    static int (*real)(const void *, char *, char *) = NULL;
    if (!real) real = (int (*)(const void *, char *, char *))dlsym(RTLD_NEXT, "__system_property_read");
    for (int i = 0; i < g_n; i++) {
        if ((const void *)g_fake[i] == pi) {
            if (name) { strncpy(name, g_keys[i], 91); name[91] = 0; }
            if (value) { strncpy(value, g_vals[i], 91); value[91] = 0; }
            return (int)strlen(g_vals[i]);
        }
    }
    return real ? real(pi, name, value) : 0;
}

void __system_property_read_callback(const void *pi,
                                     void (*cb)(void *, const char *, const char *, uint32_t),
                                     void *cookie) {
    static void (*real)(const void *, void (*)(void *, const char *, const char *, uint32_t), void *) = NULL;
    if (!real)
        real = (void (*)(const void *, void (*)(void *, const char *, const char *, uint32_t), void *))
            dlsym(RTLD_NEXT, "__system_property_read_callback");
    for (int i = 0; i < g_n; i++) {
        if ((const void *)g_fake[i] == pi) {
            if (cb) cb(cookie, g_keys[i], g_vals[i], (uint32_t)strlen(g_vals[i]));
            return;
        }
    }
    if (real) real(pi, cb, cookie);
}

uint32_t __system_property_serial(const void *pi) {
    static uint32_t (*real)(const void *) = NULL;
    if (!real) real = (uint32_t (*)(const void *))dlsym(RTLD_NEXT, "__system_property_serial");
    for (int i = 0; i < g_n; i++)
        if ((const void *)g_fake[i] == pi) return 1;
    return real ? real(pi) : 0;
}

// ── 诊断：把 dlopen 的失败原因抓下来 ──
// EGL 驱动加载走 android_dlopen_ext（不是普通 dlopen），失败时上层只打印一句
// "couldn't find an OpenGL ES implementation"，真正的原因在 dlerror() 里。
// 这里两个入口都挂上，失败时把 dlerror 打到控制台。
// （PROPFIX_DEBUG=1 时才打印，避免刷屏。）
#include <dlfcn.h>

// 2026-10-01 曾误判为坏（其实句柄正常）而关闭；现重新启用，用于看 HWC 进程的 EGL 驱动加载
       // （探针里 dlopen(libEGL_mesa.so) = 0xfe1db3c7f55d9c75），而它正是唯一会干扰
       // libEGL loader 驱动加载的挂钩 ⇒ 极可能就是把驱动搞坏的原因。诊断使命已完成，关掉。
typedef void *(*android_dlopen_ext_fn)(const char *, int, const void *);

void *android_dlopen_ext(const char *filename, int flags, const void *extinfo) {
    static android_dlopen_ext_fn real = NULL;
    if (!real) real = (android_dlopen_ext_fn)dlsym(RTLD_NEXT, "android_dlopen_ext");
    void *h = real ? real(filename, flags, extinfo) : NULL;
    if (!h && getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] dlopen 失败: %s → %s\n", filename ? filename : "(null)", dlerror());
    else if (h && getenv("PROPFIX_DEBUG") && filename &&
             (strstr(filename, "dri") || strstr(filename, "gallium") || strstr(filename, "egl")))
        fprintf(stderr, "[propfix] dlopen 成功: %s\n", filename);
    return h;
}

void *dlopen(const char *filename, int flags) {
    static void *(*real)(const char *, int) = NULL;
    if (!real) real = (void *(*)(const char *, int))dlsym(RTLD_NEXT, "dlopen");
    void *h = real ? real(filename, flags) : NULL;
    // 放宽到"任何失败都打"：EGL 的 DRI 驱动内部加载失败是静默的（上层只看到"没有配置"），
    // 实测 SF 请求标准配置但驱动返回 0 个 —— 真相就在这里的 dlopen 结果里。
    if (!h && getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] dlopen 失败: %s → %s\n", filename ? filename : "(null)", dlerror());
    else if (h && getenv("PROPFIX_DEBUG") && filename &&
             (strstr(filename, "dri") || strstr(filename, "gallium") || strstr(filename, "egl")))
        fprintf(stderr, "[propfix] dlopen 成功: %s\n", filename);
    return h;
}

// ── 诊断 + 兜底：eglChooseConfig ──
// 实测 SurfaceFlinger 走到 "no suitable EGLConfig found, giving up" 就退出：
// 它按一组属性向驱动要配置，而我们的 softpipe 一个都不匹配。
// 这里①把请求的属性打出来（看清它要什么），②驱动给不出时用 eglGetConfigs 的第一个配置兜底，
// 让 SF 至少能拿到一个 config 继续往下走。
typedef int32_t EGLint;
typedef uint32_t EGLBoolean;
typedef void *EGLDisplay;
typedef void *EGLConfig;


// ── 诊断：EGL 平台初始化 / GBM ──
// 实测：libEGL_mesa.so 能加载，但**从未尝试加载 dri 驱动**，且 eglChooseConfig 一个配置都不给
// ⇒ 卡在 EGL 平台初始化（GBM 打开 DRM 设备）这一步。这里把关键入口的结果打出来。
void *gbm_create_device(int fd) {
    static void *(*real)(int) = NULL;
    if (!real) real = (void *(*)(int))dlsym(RTLD_NEXT, "gbm_create_device");
    void *d = real ? real(fd) : NULL;
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] gbm_create_device(fd=%d) -> %p %s\n", fd, d,
                d ? "" : "(失败)");
    return d;
}

#if 0  // ⚠ 反向验证（2026-10-01）：这几条 EGL 拦截有副作用 —— 若 libEGL.so loader 内部也经过公共
       // 符号，我们绕这一层就可能破坏它的"驱动接管"，导致它回落成 META-EGL 空壳（实测症状：
       // eglQueryString(EGL_VERSION) == "1.4 Android META-EGL"，eglChooseConfig 拿不到任何配置）。
       // 先关掉验证。gbm_create_device 的日志保留（它只读不写，无副作用）。
void *eglGetDisplay(void *native_display) {
    static void *(*real)(void *) = NULL;
    if (!real) real = (void *(*)(void *))dlsym(RTLD_NEXT, "eglGetDisplay");
    void *dpy = real ? real(native_display) : NULL;
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] eglGetDisplay(native=%p) -> %p %s\n",
                native_display, dpy, dpy ? "" : "(失败)");
    return dpy;
}

EGLBoolean eglInitialize(EGLDisplay dpy, EGLint *major, EGLint *minor) {
    static EGLBoolean (*real)(EGLDisplay, EGLint *, EGLint *) = NULL;
    if (!real)
        real = (EGLBoolean (*)(EGLDisplay, EGLint *, EGLint *))dlsym(RTLD_NEXT, "eglInitialize");
    EGLBoolean ok = real ? real(dpy, major, minor) : 0;
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] eglInitialize(dpy=%p) -> %d (v=%d.%d) %s\n", dpy, ok,
                major ? *major : -1, minor ? *minor : -1, ok ? "" : "(失败)");
    return ok;
}

// 问出 display 背后到底是哪个实现（EGL_VENDOR / EGL_VERSION 会说真话）
const char *eglQueryString(EGLDisplay dpy, EGLint name) {
    static const char *(*real)(EGLDisplay, EGLint) = NULL;
    if (!real) real = (const char *(*)(EGLDisplay, EGLint))dlsym(RTLD_NEXT, "eglQueryString");
    const char *s = real ? real(dpy, name) : NULL;
    // 0x3053=EGL_VENDOR, 0x3054=EGL_VERSION, 0x3055=EGL_EXTENSIONS, 0x308D=EGL_CLIENT_APIS
    if (getenv("PROPFIX_DEBUG") && (name == 0x3053 || name == 0x3054 || name == 0x308D))
        fprintf(stderr, "[propfix] eglQueryString(dpy=%p, 0x%x) -> %s\n", dpy, name,
                s ? s : "(null)");
    return s;
}

#endif  // ← 反向验证结束（eglGetDisplay/eglInitialize/eglQueryString 三条拦截已关）


// ── 诊断：把本进程的 Android 日志转到 stderr ──
// 为什么需要：libEGL.so loader 判定"驱动不可用"时会打 ERROR（tag=libEGL），但那些日志走
// Android 的 log 机制、我们的 fakelogd 抓不全（实测只有属性查询可见、内容看不到）。
// 这里挂住日志写入入口，凡是本进程（SurfaceFlinger）打的日志都直接落到 stderr ⇒ 进 qemu 控制台。
// ⚠ 只打印 libEGL/EGL/MESA/gralloc/hwc 相关，避免刷屏。
// 优先级放行：FATAL(7)/ERROR(6) 无条件打印（abort 原文就在这里）
static int log_critical(int prio) { return prio >= 6; }

static int log_interesting(const char *tag) {
    if (!tag) return 0;
    // ⚠ 放宽：SF 自己的 assert/abort 文本 tag 是 SurfaceFlinger（此前被过滤掉，导致
    // "为什么 abort" 一直看不到）。现在把 SurfaceFlinger/DEBUG/libc 也放行。
    return strstr(tag, "EGL") || strstr(tag, "egl") || strstr(tag, "MESA") ||
           strstr(tag, "gralloc") || strstr(tag, "hwc") || strstr(tag, "HWC") ||
           strstr(tag, "RenderEngine") || strstr(tag, "Composer") ||
           strstr(tag, "SurfaceFlinger") || strstr(tag, "surfaceflinger") ||
           strstr(tag, "DEBUG") || strstr(tag, "libc") || strstr(tag, "crash");
}

int __android_log_write(int prio, const char *tag, const char *text) {
    static int (*real)(int, const char *, const char *) = NULL;
    if (!real) real = (int (*)(int, const char *, const char *))dlsym(RTLD_NEXT, "__android_log_write");
    if (getenv("PROPFIX_DEBUG") && (log_interesting(tag) || log_critical(prio)))
        fprintf(stderr, "[alog:%s/%d] %s\n", tag ? tag : "?", prio, text ? text : "");
    return real ? real(prio, tag, text) : 0;
}

int __android_log_buf_write(int bufId, int prio, const char *tag, const char *text) {
    static int (*real)(int, int, const char *, const char *) = NULL;
    if (!real)
        real = (int (*)(int, int, const char *, const char *))
            dlsym(RTLD_NEXT, "__android_log_buf_write");
    if (getenv("PROPFIX_DEBUG") && (log_interesting(tag) || log_critical(prio)))
        fprintf(stderr, "[alog:%s/%d] %s\n", tag ? tag : "?", prio, text ? text : "");
    return real ? real(bufId, prio, tag, text) : 0;
}


// ── 诊断：服务注册（AIDL/NDK 路径）──
// 现象：screencap 等 AIDL 服务名 SurfaceFlingerAIDL（libgui.so 字符串实证），而 SF 日志里看不到注册行。
// 这里拦截 NDK 的 AServiceManager_addService（纯 C 符号，interpose 安全），
// 把"谁注册了什么、返回什么"打出来，直接判定注册是否发生/成功。
// 注意：binder_status_t 是 int32_t 的枚举（0=OK）。
typedef int32_t propfix_status_t;

propfix_status_t AServiceManager_addService(void *binder, const char *instance) {
    static propfix_status_t (*real)(void *, const char *) = NULL;
    if (!real)
        real = (propfix_status_t (*)(void *, const char *))
            dlsym(RTLD_NEXT, "AServiceManager_addService");
    propfix_status_t rc = real ? real(binder, instance) : -1;
    fprintf(stderr, "[propfix] addService(\"%s\") -> %d %s\n",
            instance ? instance : "(null)", rc, rc == 0 ? "OK" : "**失败**");
    return rc;
}

EGLBoolean eglChooseConfig(EGLDisplay dpy, const EGLint *attrib_list, EGLConfig *configs,
                           EGLint config_size, EGLint *num_config) {
    static EGLBoolean (*real)(EGLDisplay, const EGLint *, EGLConfig *, EGLint, EGLint *) = NULL;
    if (!real) real = (EGLBoolean (*)(EGLDisplay, const EGLint *, EGLConfig *, EGLint, EGLint *))
        dlsym(RTLD_NEXT, "eglChooseConfig");
    int dbg = getenv("PROPFIX_DEBUG") != NULL;
    if (dbg) {
        fprintf(stderr, "[propfix] eglChooseConfig 请求属性:");
        if (attrib_list) {
            for (int i = 0; attrib_list[i] != 0x3038 && i < 64; i += 2)
                fprintf(stderr, " %s=%d", egl_attr_name(attrib_list[i]), attrib_list[i + 1]);
        } else {
            fprintf(stderr, " (null)");
        }
        fprintf(stderr, "\n");
    }
    EGLBoolean ok = real ? real(dpy, attrib_list, configs, config_size, num_config) : 0;
    EGLint n = num_config ? *num_config : 0;

    // 兜底：请求里带 TRANSPARENT_TYPE(0x3034) 时往往一个配置都匹配不到
    // （实测：探针能拿到 45 个配置、RGBA8888/ES2 有 15 个，但 composer 带 TRANSPARENT_TYPE=1 时 n=0
    //  ⇒ 它的 eglInitialize 失败 ⇒ "failed to open hwcomposer device" ⇒ composer 崩 ⇒ SF 干等）。
    // 去掉该属性重试一次：拿到的配置不透明，但足够让 HWC 起 EGL。
    if (n == 0 && attrib_list) {
        EGLint filtered[64];
        int k = 0, skipped = 0;
        for (int i = 0; attrib_list[i] != 0x3038 && i < 60; i += 2) {
            if (attrib_list[i] == 0x3034) { skipped++; continue; }   // TRANSPARENT_TYPE
            filtered[k++] = attrib_list[i];
            filtered[k++] = attrib_list[i + 1];
        }
        filtered[k] = 0x3038;   // EGL_NONE
        if (skipped) {
            EGLBoolean ok2 = real ? real(dpy, filtered, configs, config_size, num_config) : 0;
            EGLint n2 = num_config ? *num_config : 0;
            if (dbg)
                fprintf(stderr, "[propfix] eglChooseConfig 去掉 TRANSPARENT_TYPE 重试: ok=%d n=%d\n", ok2, n2);
            if (n2 > 0) { ok = ok2; n = n2; }
        }
    }
    if (dbg) fprintf(stderr, "[propfix] eglChooseConfig → ok=%d n=%d\n", ok, n);
    if (n == 0 && configs && config_size > 0) {
        static EGLBoolean (*getcfgs)(EGLDisplay, EGLConfig *, EGLint, EGLint *) = NULL;
        if (!getcfgs)
            getcfgs = (EGLBoolean (*)(EGLDisplay, EGLConfig *, EGLint, EGLint *))
                dlsym(RTLD_NEXT, "eglGetConfigs");
        EGLint total = 0;
        if (getcfgs && getcfgs(dpy, configs, config_size, &total) && total > 0) {
            if (num_config) *num_config = 1;
            fprintf(stderr, "[propfix] eglChooseConfig 兜底：改用 eglGetConfigs 的第 1 个配置（共 %d 个）\n", total);
            return 1;
        }
    }
    return ok;
}
