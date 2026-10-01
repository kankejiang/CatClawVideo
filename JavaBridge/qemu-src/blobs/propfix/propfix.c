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


// ── 诊断 + 兜底：GraphicBufferAllocator::allocate（**成员函数** ⇒ this 是隐式首参！）──
// ⚠ 血泪教训：allocate / allocateHelper 都是成员函数，签名必须带 `void *self`，
//   否则读到参数整体错位一格、转发时把垃圾当 this ⇒ 真实 allocateHelper 收到截断野指针 ⇒ SIGSEGV
//   （我们追了多轮的"libui 野指针"就是这里 ✗）。
//   Android 13 的两个重载（mangled 名见 asm 标签）：
//     8 参: (w, h, format, layerCount, usage, handle, stride, string&)
//     9 参: (w, h, format, layerCount, usage, handle, stride, unsigned long, string&)
typedef int32_t pf_status_t;

static pf_status_t pf_alloc_common(void *self, uint32_t w, uint32_t h, int fmt, uint32_t layers,
                                   uint64_t usage) {
    // 尺寸兜底：Waydroid HWC 上报的显示宽度是未初始化垃圾（实测每轮不同）
    // ⇒ 夹到 weston 的真实尺寸（init: --width=1280 --height=720），否则 w*h*4 ≈ 12TB ⇒ bad_alloc。
    if (self && (w > (1u << 20) || h > (1u << 20))) {
        if (getenv("PROPFIX_DEBUG"))
            fprintf(stderr, "[propfix] !! HWC 尺寸荒谬 w=%u h=%u ⇒ 夹到 1280x720\n", w, h);
        w = 1280; h = 720;
    }
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] >> allocate self=%p w=%u h=%u fmt=0x%x layers=%u usage=0x%llx\n",
                self, w, h, fmt, layers, (unsigned long long)usage);
    return 0;   // 占位，实际转发在下面（本函数内联展开）
}

pf_status_t gba8(void *self, uint32_t w, uint32_t h, int fmt, uint32_t layers, uint64_t usage,
                 void **handle, uint32_t *stride, void *err)
    __asm__("_ZN7android22GraphicBufferAllocator8allocateEjjijmPPK13native_handlePjNSt3__112basic_stringIcNS6_11char_traitsIcEENS6_9allocatorIcEEEE");
pf_status_t gba8(void *self, uint32_t w, uint32_t h, int fmt, uint32_t layers, uint64_t usage,
                 void **handle, uint32_t *stride, void *err) {
    static pf_status_t (*real)(void *, uint32_t, uint32_t, int, uint32_t, uint64_t, void **, uint32_t *, void *) = NULL;
    if (!real) real = (pf_status_t (*)(void *, uint32_t, uint32_t, int, uint32_t, uint64_t, void **, uint32_t *, void *))
        dlsym(RTLD_NEXT, "_ZN7android22GraphicBufferAllocator8allocateEjjijmPPK13native_handlePjNSt3__112basic_stringIcNS6_11char_traitsIcEENS6_9allocatorIcEEEE");
    if (self && (w > (1u << 20) || h > (1u << 20))) {
        if (getenv("PROPFIX_DEBUG")) fprintf(stderr, "[propfix] !! HWC 尺寸荒谬 w=%u h=%u ⇒ 1280x720\n", w, h);
        w = 1280; h = 720;
    }
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] >> allocate(8) self=%p w=%u h=%u fmt=0x%x usage=0x%llx\n",
                self, w, h, fmt, (unsigned long long)usage);
    pf_status_t rc = real ? real(self, w, h, fmt, layers, usage, handle, stride, err) : -1;
    if (getenv("PROPFIX_DEBUG")) fprintf(stderr, "[propfix] << allocate(8) rc=%d\n", rc);
    if (rc == 5 && real && (!getenv("PROPFIX_USAGE") || getenv("PROPFIX_USAGE")[0] != '0')) {
        unsigned long masked = usage & 0xF0Full;
        if (getenv("PROPFIX_DEBUG"))
            fprintf(stderr, "[propfix]   usage 0x%lx 被拒 ⇒ 掩码为 0x%lx 重试\n", usage, masked);
        rc = real(self, w, h, fmt, layers, masked, handle, stride, err);
        if (getenv("PROPFIX_DEBUG")) fprintf(stderr, "[propfix]   << 掩码重试 rc=%d\n", rc);
    }
    return rc;
}

pf_status_t gba9(void *self, uint32_t w, uint32_t h, int fmt, uint32_t layers, uint64_t usage,
                 void **handle, uint32_t *stride, unsigned long extra, void *err)
    __asm__("_ZN7android22GraphicBufferAllocator8allocateEjjijmPPK13native_handlePjmNSt3__112basic_stringIcNS6_11char_traitsIcEENS6_9allocatorIcEEEE");
pf_status_t gba9(void *self, uint32_t w, uint32_t h, int fmt, uint32_t layers, uint64_t usage,
                 void **handle, uint32_t *stride, unsigned long extra, void *err) {
    static pf_status_t (*real)(void *, uint32_t, uint32_t, int, uint32_t, uint64_t, void **, uint32_t *, unsigned long, void *) = NULL;
    if (!real) real = (pf_status_t (*)(void *, uint32_t, uint32_t, int, uint32_t, uint64_t, void **, uint32_t *, unsigned long, void *))
        dlsym(RTLD_NEXT, "_ZN7android22GraphicBufferAllocator8allocateEjjijmPPK13native_handlePjmNSt3__112basic_stringIcNS6_11char_traitsIcEENS6_9allocatorIcEEEE");
    if (self && (w > (1u << 20) || h > (1u << 20))) {
        if (getenv("PROPFIX_DEBUG")) fprintf(stderr, "[propfix] !! HWC 尺寸荒谬 w=%u h=%u ⇒ 1280x720\n", w, h);
        w = 1280; h = 720;
    }
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] >> allocate(9) self=%p w=%u h=%u fmt=0x%x usage=0x%llx\n",
                self, w, h, fmt, (unsigned long long)usage);
    pf_status_t rc = real ? real(self, w, h, fmt, layers, usage, handle, stride, extra, err) : -1;
    if (getenv("PROPFIX_DEBUG")) fprintf(stderr, "[propfix] << allocate(9) rc=%d\n", rc);
    if (rc == 5 && real && (!getenv("PROPFIX_USAGE") || getenv("PROPFIX_USAGE")[0] != '0')) {
        // rc=5 = mapper NO_RESOURCES。掩码掉 minigbm 可能不认的高位后重试一次。
        // 保留：SW_READ_NEVER(0x1) SW_WRITE_NEVER(0x2) SW_READ_OFTEN(0x4) SW_WRITE_OFTEN(0x8)
        //      HW_TEXTURE(0x100) HW_RENDER(0x200) HW_2D(0x400) HW_COMPOSER(0x800)
        unsigned long masked = usage & 0xF0Full;
        if (getenv("PROPFIX_DEBUG"))
            fprintf(stderr, "[propfix]   usage 0x%lx 被拒 ⇒ 掩码为 0x%lx 重试\n", usage, masked);
        rc = real(self, w, h, fmt, layers, masked, handle, stride, extra, err);
        if (getenv("PROPFIX_DEBUG")) fprintf(stderr, "[propfix]   << 掩码重试 rc=%d\n", rc);
    }
    return rc;
}

// ── 诊断：变参日志接口（ALOGD 走这里，不经过 __android_log_write）──
// ⚠ 前置声明：log_interesting/log_critical 定义在本块之后（本块插在文件前部）
static int log_interesting(const char *tag);
static int log_critical(int prio);
#include <stdarg.h>

int __android_log_vprint(int prio, const char *tag, const char *fmt, va_list ap) {
    static int (*real)(int, const char *, const char *, va_list) = NULL;
    if (!real)
        real = (int (*)(int, const char *, const char *, va_list))dlsym(RTLD_NEXT, "__android_log_vprint");
    if (getenv("PROPFIX_LOGV") && getenv("PROPFIX_DEBUG") && (log_interesting(tag) || log_critical(prio))) {
        char buf[1024];
        va_list cp;
        va_copy(cp, ap);
        vsnprintf(buf, sizeof(buf), fmt ? fmt : "(null)", cp);
        va_end(cp);
        fprintf(stderr, "[alogv:%s/%d] %s\n", tag ? tag : "?", prio, buf);
    }
    return real ? real(prio, tag, fmt, ap) : 0;
}

int __android_log_print(int prio, const char *tag, const char *fmt, ...) {
    static int (*real)(int, const char *, const char *, ...) = NULL;
    if (!real)
        real = (int (*)(int, const char *, const char *, ...))dlsym(RTLD_NEXT, "__android_log_print");
    if (getenv("PROPFIX_LOGV") && getenv("PROPFIX_DEBUG") && (log_interesting(tag) || log_critical(prio))) {
        char buf[1024];
        va_list ap;
        va_start(ap, fmt);
        vsnprintf(buf, sizeof(buf), fmt ? fmt : "(null)", ap);
        va_end(ap);
        fprintf(stderr, "[alogp:%s/%d] %s\n", tag ? tag : "?", prio, buf);
    }
    // ⚠ 关键修正：必须用 __android_log_vprint 转发 va_list。
    //   早先用 real(prio, tag, fmt, ap2) 调**变参**函数 ⇒ liblog 把 va_list 当变参读 ⇒
    //   %s 拿到垃圾指针 ⇒ strlen/vsnprintf 崩溃（实测回溯）⇒ 是 shim 自己在崩 SF。
    static int (*realv)(int, const char *, const char *, va_list) = NULL;
    if (!realv)
        realv = (int (*)(int, const char *, const char *, va_list))
            dlsym(RTLD_NEXT, "__android_log_vprint");
    if (!realv) realv = (int (*)(int, const char *, const char *, va_list))
            dlsym(RTLD_NEXT, "__android_log_print");
    va_list ap2;
    va_start(ap2, fmt);
    int rc = realv ? realv(prio, tag, fmt, ap2) : 0;
    va_end(ap2);
    return rc;
}


// ── 诊断：hw_get_module（libhardware 的模块查找，普通 C 符号，可稳定 interpose）──
// 目的：libui 的 GraphicBufferMapper 走 IMapper::getService(inst, getStub=true) 的 passthrough 分支，
// 最终很可能落到 hw_get_module；把 id 与返回值打出来即可确认它拼的名字与失败点。
struct hw_module_t;   // 前置声明（只需指针）

int hw_get_module(const char *id, const struct hw_module_t **module) {
    static int (*real)(const char *, const struct hw_module_t **) = NULL;
    if (!real) real = (int (*)(const char *, const struct hw_module_t **))dlsym(RTLD_NEXT, "hw_get_module");
    const struct hw_module_t *m0 = module ? *module : NULL;
    int rc = real ? real(id, module) : -99;
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] hw_get_module(id=%s) -> rc=%d module=%p->%p\n",
                id ? id : "(null)", rc, (const void *)m0, module ? (const void *)*module : NULL);
    return rc;
}

int hw_get_module_by_class(const char *class_id, const char *inst, const struct hw_module_t **module) {
    static int (*real)(const char *, const char *, const struct hw_module_t **) = NULL;
    if (!real) real = (int (*)(const char *, const char *, const struct hw_module_t **))
        dlsym(RTLD_NEXT, "hw_get_module_by_class");
    int rc = real ? real(class_id, inst, module) : -99;
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] hw_get_module_by_class(class=%s, inst=%s) -> rc=%d module=%p\n",
                class_id ? class_id : "(null)", inst ? inst : "(null)", rc,
                module ? (const void *)*module : NULL);
    return rc;
}


// ── 诊断：HIDL 服务查找的唯一漏斗 ──
// android::hardware::details::getRawServiceInternal(const string& desc, const string& inst,
//                                                    bool retry, bool getStub) -> sp<RefBase>
// sp<> 非平凡 ⇒ 按 x86-64 SysV 用隐式 sret（第一个参数是返回槽指针）。
// 目的：看清 libui 的 GraphicBufferMapper 到底怎么要 mapper、结果是否 null。


// ── configstore@1.0 的临时兼容：用 @1.1 重查 ──
// libc++ std::string 的构造/析构（mangled 名固定，dlsym 拿；对象 24 字节，栈上分配）
typedef void (*pf_str_ctor_t)(void *, const char *);
typedef void (*pf_str_dtor_t)(void *);
static int pf_try_higher_minor(void *sret, const void *inst, unsigned char retry, unsigned char getStub,
                               void (*real)(void *, const void *, const void *, unsigned char, unsigned char),
                               const char *desc) {
    static pf_str_ctor_t ctor = NULL;
    static pf_str_dtor_t dtor = NULL;
    if (!ctor)
        ctor = (pf_str_ctor_t)dlsym(RTLD_NEXT,
            "_ZNSt3__112basic_stringIcNS_11char_traitsIcEENS_9allocatorIcEEEC1EPKc");
    if (!ctor)
        ctor = (pf_str_ctor_t)dlsym(RTLD_NEXT,
            "_ZNSt3__112basic_stringIcNS_11char_traitsIcEENS_9allocatorIcEEEC2EPKc");
    if (!dtor)
        dtor = (pf_str_dtor_t)dlsym(RTLD_NEXT,
            "_ZNSt3__112basic_stringIcNS_11char_traitsIcEENS_9allocatorIcEEED1Ev");
    if (!dtor)
        dtor = (pf_str_dtor_t)dlsym(RTLD_NEXT,
            "_ZNSt3__112basic_stringIcNS_11char_traitsIcEENS_9allocatorIcEEED2Ev");
    if (!ctor || !dtor || !desc) return 0;
    if (!strstr(desc, "configstore@1.0")) return 0;      // 只修这一处，最小风险
    char fixed[256];
    snprintf(fixed, sizeof(fixed), "%s", desc);
    char *p = strstr(fixed, "@1.0");
    if (!p) return 0;
    p[2] = '1';                                          // @1.0 -> @1.1
    char nsbuf[32] __attribute__((aligned(16)));
    memset(nsbuf, 0, sizeof(nsbuf));
    ctor(nsbuf, fixed);
    real(sret, (const void *)nsbuf, inst, retry, getStub);
    dtor(nsbuf);
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] configstore@1.0 兼容重查 -> %s : raw=%p\n",
                fixed, sret ? *(void **)sret : NULL);
    return 1;
}

// ── 安全解码 libc++ std::string（alternate layout）──
// 实测证据：描述符对象前 16 字节形如 41 00.. 38 00..，对应 {cap=65, size=56}
// 而 android.hardware.configstore@1.1::ISurfaceFlingerConfigs 正好 56 字符 ⇒ 布局 = {cap, size, data}。
// 判别：先看 +16 处是否像一个合法指针且 cap>=size 且 size<256 ⇒ 视为 long；否则按 short（数据在 +1）。
static const char *pf_cstr(const void *o) {
    if (!o) return NULL;
    const unsigned char *b = (const unsigned char *)o;
    const char *cand = NULL;
    memcpy(&cand, b + 16, sizeof(cand));
    size_t cap = 0, size = 0;
    memcpy(&cap, b, sizeof(cap));
    memcpy(&size, b + 8, sizeof(size));
    if (cand && cand > (const char *)0x10000 && cap >= size && size > 0 && size < 256)
        return cand;                                   // long
    return (const char *)o + 1;                        // short（首字节 = size<<1）
}

static void dump_bytes(const char *tag, const void *p) {
    if (!p) { fprintf(stderr, "[propfix]   %s=(null)\n", tag); return; }
    const unsigned char *b = (const unsigned char *)p;
    char hex[64], asc[20];
    int k = 0, a = 0;
    for (int i = 0; i < 16; i++) {
        k += snprintf(hex + k, sizeof(hex) - k, "%02x", b[i]);
        asc[a++] = (b[i] >= 32 && b[i] < 127) ? (char)b[i] : '.';
    }
    asc[a] = 0;
    fprintf(stderr, "[propfix]   %s: %s  |%s|\n", tag, hex, asc);
}

void grs_raw(void *sret, const void *desc, const void *inst, unsigned char retry, unsigned char getStub)
    __asm__("_ZN7android8hardware7details21getRawServiceInternalERKNSt3__112basic_stringIcNS2_11char_traitsIcEENS2_9allocatorIcEEEESA_bb");
void grs_raw(void *sret, const void *desc, const void *inst, unsigned char retry, unsigned char getStub) {
    static void (*real)(void *, const void *, const void *, unsigned char, unsigned char) = NULL;
    if (!real)
        real = (void (*)(void *, const void *, const void *, unsigned char, unsigned char))
            dlsym(RTLD_NEXT, "_ZN7android8hardware7details21getRawServiceInternalERKNSt3__112basic_stringIcNS2_11char_traitsIcEENS2_9allocatorIcEEEESA_bb");
    // ⚠ 先打日志再调 real：实测 real 在 configstore@1.0 的查找里会崩（libhidlbase+0x7a9），
    //   若把日志放在 real 之后，就永远看不到"它到底在查哪个描述符"。
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] >> getRawServiceInternal(p3=%d p4=%d) desc=[%s] inst=[%s]\n",
                (int)retry, (int)getStub,
                pf_cstr(desc) ? pf_cstr(desc) : "?", pf_cstr(inst) ? pf_cstr(inst) : "?");
    // ⚠ 关键：configstore@1.0 的**改写必须在 real 之前**。
    //   实测：SF 静态初始化里唯一一次查找就是 configstore@1.0::ISurfaceFlingerConfigs/default，
    //   而 real（libhidlbase）在该查找上**当场崩溃** ⇒ 放在 real 之后的兜底永远执行不到。
    //   镜像里的服务只注册 @1.1 ⇒ 直接把描述符就地改成 @1.1 再查（查完还原，保持对象原样）。
    char *pf_at = NULL;
    const char *pf_d = pf_cstr(desc);
    if (pf_d) {
        pf_at = strstr((char *)pf_d, "@1.0");
        if (pf_at && strstr(pf_d, "configstore")) {
            pf_at[3] = '1';   // @1.0 -> @1.1（minor 在下标 3）
            if (getenv("PROPFIX_DEBUG"))
                fprintf(stderr, "[propfix] configstore@1.0 -> @1.1（避开崩溃路径）: %s\n", pf_d);
        } else {
            pf_at = NULL;
        }
    }
    if (real) real(sret, desc, inst, retry, getStub);
    if (pf_at) pf_at[3] = '0';                       // 还原
    void *raw0 = sret ? *(void **)sret : NULL;
    if (getenv("PROPFIX_DEBUG")) {
        void *raw = sret ? *(void **)sret : NULL;
        fprintf(stderr, "[propfix] getRawServiceInternal(p3=%d p4=%d) -> raw=%p  desc=%s  inst=%s\n",
                (int)retry, (int)getStub, raw,
                pf_cstr(desc) ? pf_cstr(desc) : "?", pf_cstr(inst) ? pf_cstr(inst) : "?");
    }
}


#include <stdbool.h>

// ── SF 配置读取垫片（27 个标量 sysprop）──
// 背景：SF 静态初始化经 libSurfaceFlingerProp.so 的 sysprop::* 读 configstore；
// 镜像里的服务只注册 1.1，而 libhidlbase 处理该回复时崩溃（本轮实证）⇒ SF 启动即死。
// 做法：这些函数都是**导出**的普通函数 ⇒ 逐个拦掉，直接返回调用方传入的默认值
// （等价于"设备无 configstore"时的 AOSP 兜底行为，安全且语义正确）。
// 开关：PROPFIX_SFPROP=0 可关闭（默认开启）。
static int sfprop_on(void) { const char *v = getenv("PROPFIX_SFPROP"); return !(v && v[0] == '0'); }


bool sfp_use_vr_flinger(bool d) __asm__("_ZN7android7sysprop14use_vr_flingerEb");
bool sfp_use_vr_flinger(bool d) {
    if (!sfprop_on()) { static bool (*real)(bool) = NULL;
        if (!real) real = (bool (*)(bool))dlsym(RTLD_NEXT, "_ZN7android7sysprop14use_vr_flingerEb");
        return real ? real(d) : d; }
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] sysprop::use_vr_flinger(%d) -> %d（垫片）\n", (int)d, (int)d);
    return d;
}

bool sfp_has_HDR_display(bool d) __asm__("_ZN7android7sysprop15has_HDR_displayEb");
bool sfp_has_HDR_display(bool d) {
    if (!sfprop_on()) { static bool (*real)(bool) = NULL;
        if (!real) real = (bool (*)(bool))dlsym(RTLD_NEXT, "_ZN7android7sysprop15has_HDR_displayEb");
        return real ? real(d) : d; }
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] sysprop::has_HDR_display(%d) -> %d（垫片）\n", (int)d, (int)d);
    return d;
}

bool sfp_enable_sdr_dimming(bool d) __asm__("_ZN7android7sysprop18enable_sdr_dimmingEb");
bool sfp_enable_sdr_dimming(bool d) {
    if (!sfprop_on()) { static bool (*real)(bool) = NULL;
        if (!real) real = (bool (*)(bool))dlsym(RTLD_NEXT, "_ZN7android7sysprop18enable_sdr_dimmingEb");
        return real ? real(d) : d; }
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] sysprop::enable_sdr_dimming(%d) -> %d（垫片）\n", (int)d, (int)d);
    return d;
}

bool sfp_enable_layer_caching(bool d) __asm__("_ZN7android7sysprop20enable_layer_cachingEb");
bool sfp_enable_layer_caching(bool d) {
    if (!sfprop_on()) { static bool (*real)(bool) = NULL;
        if (!real) real = (bool (*)(bool))dlsym(RTLD_NEXT, "_ZN7android7sysprop20enable_layer_cachingEb");
        return real ? real(d) : d; }
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] sysprop::enable_layer_caching(%d) -> %d（垫片）\n", (int)d, (int)d);
    return d;
}

bool sfp_use_context_priority(bool d) __asm__("_ZN7android7sysprop20use_context_priorityEb");
bool sfp_use_context_priority(bool d) {
    if (!sfprop_on()) { static bool (*real)(bool) = NULL;
        if (!real) real = (bool (*)(bool))dlsym(RTLD_NEXT, "_ZN7android7sysprop20use_context_priorityEb");
        return real ? real(d) : d; }
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] sysprop::use_context_priority(%d) -> %d（垫片）\n", (int)d, (int)d);
    return d;
}

bool sfp_has_wide_color_display(bool d) __asm__("_ZN7android7sysprop22has_wide_color_displayEb");
bool sfp_has_wide_color_display(bool d) {
    if (!sfprop_on()) { static bool (*real)(bool) = NULL;
        if (!real) real = (bool (*)(bool))dlsym(RTLD_NEXT, "_ZN7android7sysprop22has_wide_color_displayEb");
        return real ? real(d) : d; }
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] sysprop::has_wide_color_display(%d) -> %d（垫片）\n", (int)d, (int)d);
    return d;
}

bool sfp_refresh_rate_switching(bool d) __asm__("_ZN7android7sysprop22refresh_rate_switchingEb");
bool sfp_refresh_rate_switching(bool d) {
    if (!sfprop_on()) { static bool (*real)(bool) = NULL;
        if (!real) real = (bool (*)(bool))dlsym(RTLD_NEXT, "_ZN7android7sysprop22refresh_rate_switchingEb");
        return real ? real(d) : d; }
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] sysprop::refresh_rate_switching(%d) -> %d（垫片）\n", (int)d, (int)d);
    return d;
}

bool sfp_ignore_hdr_camera_layers(bool d) __asm__("_ZN7android7sysprop24ignore_hdr_camera_layersEb");
bool sfp_ignore_hdr_camera_layers(bool d) {
    if (!sfprop_on()) { static bool (*real)(bool) = NULL;
        if (!real) real = (bool (*)(bool))dlsym(RTLD_NEXT, "_ZN7android7sysprop24ignore_hdr_camera_layersEb");
        return real ? real(d) : d; }
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] sysprop::ignore_hdr_camera_layers(%d) -> %d（垫片）\n", (int)d, (int)d);
    return d;
}

bool sfp_enable_protected_contents(bool d) __asm__("_ZN7android7sysprop25enable_protected_contentsEb");
bool sfp_enable_protected_contents(bool d) {
    if (!sfprop_on()) { static bool (*real)(bool) = NULL;
        if (!real) real = (bool (*)(bool))dlsym(RTLD_NEXT, "_ZN7android7sysprop25enable_protected_contentsEb");
        return real ? real(d) : d; }
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] sysprop::enable_protected_contents(%d) -> %d（垫片）\n", (int)d, (int)d);
    return d;
}

bool sfp_support_kernel_idle_timer(bool d) __asm__("_ZN7android7sysprop25support_kernel_idle_timerEb");
bool sfp_support_kernel_idle_timer(bool d) {
    if (!sfprop_on()) { static bool (*real)(bool) = NULL;
        if (!real) real = (bool (*)(bool))dlsym(RTLD_NEXT, "_ZN7android7sysprop25support_kernel_idle_timerEb");
        return real ? real(d) : d; }
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] sysprop::support_kernel_idle_timer(%d) -> %d（垫片）\n", (int)d, (int)d);
    return d;
}

bool sfp_enable_frame_rate_override(bool d) __asm__("_ZN7android7sysprop26enable_frame_rate_overrideEb");
bool sfp_enable_frame_rate_override(bool d) {
    if (!sfprop_on()) { static bool (*real)(bool) = NULL;
        if (!real) real = (bool (*)(bool))dlsym(RTLD_NEXT, "_ZN7android7sysprop26enable_frame_rate_overrideEb");
        return real ? real(d) : d; }
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] sysprop::enable_frame_rate_override(%d) -> %d（垫片）\n", (int)d, (int)d);
    return d;
}

bool sfp_running_without_sync_framework(bool d) __asm__("_ZN7android7sysprop30running_without_sync_frameworkEb");
bool sfp_running_without_sync_framework(bool d) {
    if (!sfprop_on()) { static bool (*real)(bool) = NULL;
        if (!real) real = (bool (*)(bool))dlsym(RTLD_NEXT, "_ZN7android7sysprop30running_without_sync_frameworkEb");
        return real ? real(d) : d; }
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] sysprop::running_without_sync_framework(%d) -> %d（垫片）\n", (int)d, (int)d);
    return d;
}

bool sfp_start_graphics_allocator_service(bool d) __asm__("_ZN7android7sysprop32start_graphics_allocator_serviceEb");
bool sfp_start_graphics_allocator_service(bool d) {
    if (!sfprop_on()) { static bool (*real)(bool) = NULL;
        if (!real) real = (bool (*)(bool))dlsym(RTLD_NEXT, "_ZN7android7sysprop32start_graphics_allocator_serviceEb");
        return real ? real(d) : d; }
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] sysprop::start_graphics_allocator_service(%d) -> %d（垫片）\n", (int)d, (int)d);
    return d;
}

bool sfp_force_hwc_copy_for_virtual_displays(bool d) __asm__("_ZN7android7sysprop35force_hwc_copy_for_virtual_displaysEb");
bool sfp_force_hwc_copy_for_virtual_displays(bool d) {
    if (!sfprop_on()) { static bool (*real)(bool) = NULL;
        if (!real) real = (bool (*)(bool))dlsym(RTLD_NEXT, "_ZN7android7sysprop35force_hwc_copy_for_virtual_displaysEb");
        return real ? real(d) : d; }
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] sysprop::force_hwc_copy_for_virtual_displays(%d) -> %d（垫片）\n", (int)d, (int)d);
    return d;
}

bool sfp_use_content_detection_for_refresh_rate(bool d) __asm__("_ZN7android7sysprop38use_content_detection_for_refresh_rateEb");
bool sfp_use_content_detection_for_refresh_rate(bool d) {
    if (!sfprop_on()) { static bool (*real)(bool) = NULL;
        if (!real) real = (bool (*)(bool))dlsym(RTLD_NEXT, "_ZN7android7sysprop38use_content_detection_for_refresh_rateEb");
        return real ? real(d) : d; }
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] sysprop::use_content_detection_for_refresh_rate(%d) -> %d（垫片）\n", (int)d, (int)d);
    return d;
}

bool sfp_update_device_product_info_on_hotplug_reconnect(bool d) __asm__("_ZN7android7sysprop47update_device_product_info_on_hotplug_reconnectEb");
bool sfp_update_device_product_info_on_hotplug_reconnect(bool d) {
    if (!sfprop_on()) { static bool (*real)(bool) = NULL;
        if (!real) real = (bool (*)(bool))dlsym(RTLD_NEXT, "_ZN7android7sysprop47update_device_product_info_on_hotplug_reconnectEb");
        return real ? real(d) : d; }
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] sysprop::update_device_product_info_on_hotplug_reconnect(%d) -> %d（垫片）\n", (int)d, (int)d);
    return d;
}

int sfp_set_idle_timer_ms(int d) __asm__("_ZN7android7sysprop17set_idle_timer_msEi");
int sfp_set_idle_timer_ms(int d) {
    if (!sfprop_on()) { static int (*real)(int) = NULL;
        if (!real) real = (int (*)(int))dlsym(RTLD_NEXT, "_ZN7android7sysprop17set_idle_timer_msEi");
        return real ? real(d) : d; }
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] sysprop::set_idle_timer_ms(%d) -> %d（垫片）\n", (int)d, (int)d);
    return d;
}

int sfp_max_graphics_width(int d) __asm__("_ZN7android7sysprop18max_graphics_widthEi");
int sfp_max_graphics_width(int d) {
    if (!sfprop_on()) { static int (*real)(int) = NULL;
        if (!real) real = (int (*)(int))dlsym(RTLD_NEXT, "_ZN7android7sysprop18max_graphics_widthEi");
        return real ? real(d) : d; }
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] sysprop::max_graphics_width(%d) -> %d（垫片）\n", (int)d, (int)d);
    return d;
}

int sfp_set_touch_timer_ms(int d) __asm__("_ZN7android7sysprop18set_touch_timer_msEi");
int sfp_set_touch_timer_ms(int d) {
    if (!sfprop_on()) { static int (*real)(int) = NULL;
        if (!real) real = (int (*)(int))dlsym(RTLD_NEXT, "_ZN7android7sysprop18set_touch_timer_msEi");
        return real ? real(d) : d; }
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] sysprop::set_touch_timer_ms(%d) -> %d（垫片）\n", (int)d, (int)d);
    return d;
}

int sfp_max_graphics_height(int d) __asm__("_ZN7android7sysprop19max_graphics_heightEi");
int sfp_max_graphics_height(int d) {
    if (!sfprop_on()) { static int (*real)(int) = NULL;
        if (!real) real = (int (*)(int))dlsym(RTLD_NEXT, "_ZN7android7sysprop19max_graphics_heightEi");
        return real ? real(d) : d; }
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] sysprop::max_graphics_height(%d) -> %d（垫片）\n", (int)d, (int)d);
    return d;
}

int sfp_set_display_power_timer_ms(int d) __asm__("_ZN7android7sysprop26set_display_power_timer_msEi");
int sfp_set_display_power_timer_ms(int d) {
    if (!sfprop_on()) { static int (*real)(int) = NULL;
        if (!real) real = (int (*)(int))dlsym(RTLD_NEXT, "_ZN7android7sysprop26set_display_power_timer_msEi");
        return real ? real(d) : d; }
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] sysprop::set_display_power_timer_ms(%d) -> %d（垫片）\n", (int)d, (int)d);
    return d;
}

int sfp_display_update_imminent_timeout_ms(int d) __asm__("_ZN7android7sysprop34display_update_imminent_timeout_msEi");
int sfp_display_update_imminent_timeout_ms(int d) {
    if (!sfprop_on()) { static int (*real)(int) = NULL;
        if (!real) real = (int (*)(int))dlsym(RTLD_NEXT, "_ZN7android7sysprop34display_update_imminent_timeout_msEi");
        return real ? real(d) : d; }
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] sysprop::display_update_imminent_timeout_ms(%d) -> %d（垫片）\n", (int)d, (int)d);
    return d;
}

long sfp_vsync_event_phase_offset_ns(long d) __asm__("_ZN7android7sysprop27vsync_event_phase_offset_nsEl");
long sfp_vsync_event_phase_offset_ns(long d) {
    if (!sfprop_on()) { static long (*real)(long) = NULL;
        if (!real) real = (long (*)(long))dlsym(RTLD_NEXT, "_ZN7android7sysprop27vsync_event_phase_offset_nsEl");
        return real ? real(d) : d; }
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] sysprop::vsync_event_phase_offset_ns(%d) -> %d（垫片）\n", (int)d, (int)d);
    return d;
}

long sfp_max_virtual_display_dimension(long d) __asm__("_ZN7android7sysprop29max_virtual_display_dimensionEl");
long sfp_max_virtual_display_dimension(long d) {
    if (!sfprop_on()) { static long (*real)(long) = NULL;
        if (!real) real = (long (*)(long))dlsym(RTLD_NEXT, "_ZN7android7sysprop29max_virtual_display_dimensionEl");
        return real ? real(d) : d; }
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] sysprop::max_virtual_display_dimension(%d) -> %d（垫片）\n", (int)d, (int)d);
    return d;
}

long sfp_vsync_sf_event_phase_offset_ns(long d) __asm__("_ZN7android7sysprop30vsync_sf_event_phase_offset_nsEl");
long sfp_vsync_sf_event_phase_offset_ns(long d) {
    if (!sfprop_on()) { static long (*real)(long) = NULL;
        if (!real) real = (long (*)(long))dlsym(RTLD_NEXT, "_ZN7android7sysprop30vsync_sf_event_phase_offset_nsEl");
        return real ? real(d) : d; }
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] sysprop::vsync_sf_event_phase_offset_ns(%d) -> %d（垫片）\n", (int)d, (int)d);
    return d;
}

long sfp_max_frame_buffer_acquired_buffers(long d) __asm__("_ZN7android7sysprop33max_frame_buffer_acquired_buffersEl");
long sfp_max_frame_buffer_acquired_buffers(long d) {
    if (!sfprop_on()) { static long (*real)(long) = NULL;
        if (!real) real = (long (*)(long))dlsym(RTLD_NEXT, "_ZN7android7sysprop33max_frame_buffer_acquired_buffersEl");
        return real ? real(d) : d; }
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] sysprop::max_frame_buffer_acquired_buffers(%d) -> %d（垫片）\n", (int)d, (int)d);
    return d;
}

long sfp_present_time_offset_from_vsync_ns(long d) __asm__("_ZN7android7sysprop33present_time_offset_from_vsync_nsEl");
long sfp_present_time_offset_from_vsync_ns(long d) {
    if (!sfprop_on()) { static long (*real)(long) = NULL;
        if (!real) real = (long (*)(long))dlsym(RTLD_NEXT, "_ZN7android7sysprop33present_time_offset_from_vsync_nsEl");
        return real ? real(d) : d; }
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] sysprop::present_time_offset_from_vsync_ns(%d) -> %d（垫片）\n", (int)d, (int)d);
    return d;
}


// ── 探针：mapper passthrough 入口 + mapper 单例构造 ──
// ① HIDL_FETCH_IMapper(const char*) -> IMapper*（passthrough 实现由 libhidlbase dlopen 该库后调用）
void *HIDL_FETCH_IMapper(const char *name) {
    static void *(*real)(const char *) = NULL;
    if (!real) real = (void *(*)(const char *))dlsym(RTLD_NEXT, "HIDL_FETCH_IMapper");
    void *r = real ? real(name) : NULL;
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] HIDL_FETCH_IMapper(%s) -> %p\n", name ? name : "?", r);
    return r;
}

// ② GraphicBufferMapper 构造（this 在 rdi）：只记录
void gbm_ctor(void *self) __asm__("_ZN7android19GraphicBufferMapperC1Ev");
void gbm_ctor(void *self) {
    static void (*real)(void *) = NULL;
    if (!real) real = (void (*)(void *))dlsym(RTLD_NEXT, "_ZN7android19GraphicBufferMapperC1Ev");
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] GraphicBufferMapper::ctor(this=%p)\n", self);
    if (real) real(self);
}


// ── 探针：GraphicBufferAllocator 的构造 / allocateHelper 成员 dump ──
static void dump_words(const char *tag, const void *self, int n) {
    if (!self) { fprintf(stderr, "[propfix] %s self=NULL\n", tag); return; }
    const unsigned long *w = (const unsigned long *)self;
    fprintf(stderr, "[propfix] %s self=%p:", tag, self);
    for (int i = 0; i < n; i++) fprintf(stderr, " [%d]=0x%lx", i, w[i]);
    fprintf(stderr, "\n");
}

void gba_ctor(void *self) __asm__("_ZN7android22GraphicBufferAllocatorC1Ev");
void gba_ctor(void *self) {
    static void (*real)(void *) = NULL;
    if (!real) real = (void (*)(void *))dlsym(RTLD_NEXT, "_ZN7android22GraphicBufferAllocatorC1Ev");
    if (real) real(self);
    if (getenv("PROPFIX_DEBUG")) dump_words("GBA::ctor", self, 8);
}

typedef long (*gba_helper_t)(void *, unsigned int, unsigned int, int, unsigned int,
                             unsigned long, const void **, unsigned int *, void *, unsigned char);
long gba_helper(void *self, unsigned int w, unsigned int h, int fmt, unsigned int layers,
                unsigned long usage, const void **handle, unsigned int *stride, void *err,
                unsigned char importBuffer)
    __asm__("_ZN7android22GraphicBufferAllocator14allocateHelperEjjijmPPK13native_handlePjNSt3__112basic_stringIcNS6_11char_traitsIcEENS6_9allocatorIcEEEEb");
long gba_helper(void *self, unsigned int w, unsigned int h, int fmt, unsigned int layers,
                unsigned long usage, const void **handle, unsigned int *stride, void *err,
                unsigned char importBuffer) {
    static gba_helper_t real = NULL;
    if (!real) real = (gba_helper_t)dlsym(RTLD_NEXT,
        "_ZN7android22GraphicBufferAllocator14allocateHelperEjjijmPPK13native_handlePjNSt3__112basic_stringIcNS6_11char_traitsIcEENS6_9allocatorIcEEEEb");
    if (getenv("PROPFIX_DEBUG")) {
        dump_words("GBA::allocateHelper 进入", self, 8);
        fprintf(stderr, "[propfix]   args w=%u h=%u fmt=0x%x layers=%u usage=0x%lx import=%d\n",
                w, h, fmt, layers, usage, (int)importBuffer);
    }
    long rc = real ? real(self, w, h, fmt, layers, usage, handle, stride, err, importBuffer) : -1;
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] GBA::allocateHelper 返回 rc=%ld\n", rc);
    return rc;
}


// ── 探针：图形设备 open ──
#include <fcntl.h>
#include <errno.h>
static int pf_dev_interesting(const char *p) {
    if (!p) return 0;
    return strstr(p, "/dev/dri") || strstr(p, "/dev/dma_heap") || strstr(p, "/dev/ion") ||
           strstr(p, "/dev/kgsl") || strstr(p, "/dev/gpu") || strstr(p, "/dev/mali");
}
int open(const char *path, int flags, ...) {
    static int (*real)(const char *, int, ...) = NULL;
    if (!real) real = (int (*)(const char *, int, ...))dlsym(RTLD_NEXT, "open");
    mode_t mode = 0;
    if (flags & O_CREAT) { va_list ap; va_start(ap, flags); mode = (mode_t)va_arg(ap, int); va_end(ap); }
    int fd = real ? real(path, flags, mode) : -1;
    if (pf_dev_interesting(path) && getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] open(%s, 0x%x) -> fd=%d errno=%d\n", path, flags, fd,
                fd < 0 ? errno : 0);
    return fd;
}

int openat(int dirfd, const char *path, int flags, ...) {
    static int (*real)(int, const char *, int, ...) = NULL;
    if (!real) real = (int (*)(int, const char *, int, ...))dlsym(RTLD_NEXT, "openat");
    mode_t mode = 0;
    if (flags & O_CREAT) { va_list ap; va_start(ap, flags); mode = (mode_t)va_arg(ap, int); va_end(ap); }
    int fd = real ? real(dirfd, path, flags, mode) : -1;
    if (pf_dev_interesting(path) && getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] openat(%d, %s, 0x%x) -> fd=%d errno=%d\n", dirfd, path, flags, fd,
                fd < 0 ? errno : 0);
    return fd;
}

int open64(const char *path, int flags, ...) {
    static int (*real)(const char *, int, ...) = NULL;
    if (!real) real = (int (*)(const char *, int, ...))dlsym(RTLD_NEXT, "open64");
    mode_t mode = 0;
    if (flags & O_CREAT) { va_list ap; va_start(ap, flags); mode = (mode_t)va_arg(ap, int); va_end(ap); }
    int fd = real ? real(path, flags, mode) : -1;
    if (pf_dev_interesting(path) && getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] open64(%s, 0x%x) -> fd=%d errno=%d\n", path, flags, fd,
                fd < 0 ? errno : 0);
    return fd;
}


// ── 探针：gbm / drm 打开与分配 ──
/* ── GBM 回退层（2026-10-02）──
 * 实测：mapper 走 libgbm_mesa_wrapper（cros/minigbm 风格），对 virtio_gpu 的 card0 与
 * renderD128 都建不出设备（card0=EINVAL / renderD 分配=EACCES→rc=5→SF abort
 * "output buffer not gpu writeable"）；而直连 libgbm_mesa 对 card0 分配成功（gbmprobe 实证）。
 * 故 wrapper 的 gbm_* 调用失败时整套切到 mesa 实现——device/bo 必须同源，用 g_from_mesa 标记。 */
static void *g_gbm_mesa_h;
static int g_from_mesa;
static void *gbm_mesa_sym(const char *n) {
    if (!g_gbm_mesa_h) g_gbm_mesa_h = dlopen("libgbm_mesa.so", RTLD_NOW);
    if (!g_gbm_mesa_h && getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] gbm 回退：dlopen libgbm_mesa.so 失败 %s\n", dlerror());
    return g_gbm_mesa_h ? dlsym(g_gbm_mesa_h, n) : NULL;
}

void *gbm_create_device(int fd) {
    static void *(*real)(int) = NULL;
    if (!real) real = (void *(*)(int))dlsym(RTLD_NEXT, "gbm_create_device");
    void *r = real ? real(fd) : NULL;
    g_from_mesa = 0;
    if (!r) {
        void *(*f)(int) = (void *(*)(int))gbm_mesa_sym("gbm_create_device");
        if (f) { r = f(fd); g_from_mesa = 1; }
    }
    if (getenv("PROPFIX_DEBUG")) {
        char link[64], buf[512];
        snprintf(link, sizeof link, "/proc/self/fd/%d", fd);
        ssize_t n = readlink(link, buf, sizeof buf - 1);
        if (n < 0) { buf[0] = '?'; buf[1] = 0; n = 1; }
        buf[n] = 0;
        fprintf(stderr, "[propfix] gbm_create_device(fd=%d=%s) -> %p errno=%d(%s) mesa=%d\n",
                fd, buf, r, r ? 0 : errno, r ? "-" : strerror(errno), g_from_mesa);
    }
    return r;
}

void *gbm_bo_create(void *gbm, uint32_t width, uint32_t height, uint32_t format, uint32_t flags) {
    static void *(*real)(void *, uint32_t, uint32_t, uint32_t, uint32_t) = NULL;
    if (!real) real = (void *(*)(void *, uint32_t, uint32_t, uint32_t, uint32_t))dlsym(RTLD_NEXT, "gbm_bo_create");
    void *r;
    if (g_from_mesa) {
        void *(*f)(void *, uint32_t, uint32_t, uint32_t, uint32_t) =
            (void *(*)(void *, uint32_t, uint32_t, uint32_t, uint32_t))gbm_mesa_sym("gbm_bo_create");
        r = f ? f(gbm, width, height, format, flags) : NULL;
    } else {
        r = real ? real(gbm, width, height, format, flags) : NULL;
    }
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] gbm_bo_create(gbm=%p w=%u h=%u fmt=0x%x flags=0x%x) -> %p\n",
                gbm, width, height, format, flags, r);
    return r;
}


/* ── 其余 wrapper UND 符号：real(wrapper) 成功用 wrapper，失败切 mesa（bo/device 同源跟随）── */
void gbm_bo_destroy(void *bo) {
    static void (*real)(void *) = NULL;
    if (!real) real = (void (*)(void *))dlsym(RTLD_NEXT, "gbm_bo_destroy");
    if (!g_from_mesa) { if (real) real(bo); return; }
    void (*f)(void *) = (void (*)(void *))gbm_mesa_sym("gbm_bo_destroy");
    if (f) f(bo);
}

void gbm_device_destroy(void *dev) {
    static void (*real)(void *) = NULL;
    if (!real) real = (void (*)(void *))dlsym(RTLD_NEXT, "gbm_device_destroy");
    if (!g_from_mesa) { if (real) real(dev); return; }
    void (*f)(void *) = (void (*)(void *))gbm_mesa_sym("gbm_device_destroy");
    if (f) f(dev);
}

int gbm_device_get_fd(void *dev) {
    static int (*real)(void *) = NULL;
    if (!real) real = (int (*)(void *))dlsym(RTLD_NEXT, "gbm_device_get_fd");
    if (g_from_mesa) {
        int (*f)(void *) = (int (*)(void *))gbm_mesa_sym("gbm_device_get_fd");
        return f ? f(dev) : -1;
    }
    return real ? real(dev) : -1;
}

int gbm_bo_get_fd(void *bo) {
    static int (*real)(void *) = NULL;
    if (!real) real = (int (*)(void *))dlsym(RTLD_NEXT, "gbm_bo_get_fd");
    if (g_from_mesa) {
        int (*f)(void *) = (int (*)(void *))gbm_mesa_sym("gbm_bo_get_fd");
        return f ? f(bo) : -1;
    }
    return real ? real(bo) : -1;
}

uint32_t gbm_bo_get_stride(void *bo) {
    static uint32_t (*real)(void *) = NULL;
    if (!real) real = (uint32_t (*)(void *))dlsym(RTLD_NEXT, "gbm_bo_get_stride");
    if (g_from_mesa) {
        uint32_t (*f)(void *) = (uint32_t (*)(void *))gbm_mesa_sym("gbm_bo_get_stride");
        return f ? f(bo) : 0;
    }
    return real ? real(bo) : 0;
}

uint64_t gbm_bo_get_modifier(void *bo) {
    static uint64_t (*real)(void *) = NULL;
    if (!real) real = (uint64_t (*)(void *))dlsym(RTLD_NEXT, "gbm_bo_get_modifier");
    if (g_from_mesa) {
        uint64_t (*f)(void *) = (uint64_t (*)(void *))gbm_mesa_sym("gbm_bo_get_modifier");
        return f ? f(bo) : 0;
    }
    return real ? real(bo) : 0;
}

void *gbm_bo_import(void *gbm, uint32_t type, int fd, uint32_t flags) {
    static void *(*real)(void *, uint32_t, int, uint32_t) = NULL;
    if (!real) real = (void *(*)(void *, uint32_t, int, uint32_t))dlsym(RTLD_NEXT, "gbm_bo_import");
    if (g_from_mesa) {
        void *(*f)(void *, uint32_t, int, uint32_t) =
            (void *(*)(void *, uint32_t, int, uint32_t))gbm_mesa_sym("gbm_bo_import");
        return f ? f(gbm, type, fd, flags) : NULL;
    }
    return real ? real(gbm, type, fd, flags) : NULL;
}

void *gbm_bo_map(void *bo, uint32_t x, uint32_t y, uint32_t w, uint32_t h,
                 uint32_t flags, uint32_t *stride, void **map_data) {
    static void *(*real)(void *, uint32_t, uint32_t, uint32_t, uint32_t, uint32_t, uint32_t *, void **) = NULL;
    if (!real) real = (void *(*)(void *, uint32_t, uint32_t, uint32_t, uint32_t, uint32_t, uint32_t *, void **))
        dlsym(RTLD_NEXT, "gbm_bo_map");
    if (g_from_mesa) {
        void *(*f)(void *, uint32_t, uint32_t, uint32_t, uint32_t, uint32_t, uint32_t *, void **) =
            (void *(*)(void *, uint32_t, uint32_t, uint32_t, uint32_t, uint32_t, uint32_t *, void **))
            gbm_mesa_sym("gbm_bo_map");
        return f ? f(bo, x, y, w, h, flags, stride, map_data) : NULL;
    }
    return real ? real(bo, x, y, w, h, flags, stride, map_data) : NULL;
}

void gbm_bo_unmap(void *bo, void *map_data) {
    static void (*real)(void *, void *) = NULL;
    if (!real) real = (void (*)(void *, void *))dlsym(RTLD_NEXT, "gbm_bo_unmap");
    if (g_from_mesa) {
        void (*f)(void *, void *) = (void (*)(void *, void *))gbm_mesa_sym("gbm_bo_unmap");
        if (f) f(bo, map_data);
        return;
    }
    if (real) real(bo, map_data);
}

int gbm_device_get_format_modifier_plane_count(void *dev, uint32_t fmt, uint64_t modifier) {
    static int (*real)(void *, uint32_t, uint64_t) = NULL;
    if (!real) real = (int (*)(void *, uint32_t, uint64_t))dlsym(RTLD_NEXT, "gbm_device_get_format_modifier_plane_count");
    if (g_from_mesa) {
        int (*f)(void *, uint32_t, uint64_t) = (int (*)(void *, uint32_t, uint64_t))
            gbm_mesa_sym("gbm_device_get_format_modifier_plane_count");
        return f ? f(dev, fmt, modifier) : 0;
    }
    return real ? real(dev, fmt, modifier) : 0;
}

void *gbm_bo_create_with_modifiers2(void *gbm, uint32_t width, uint32_t height, uint32_t format,
                                    const uint64_t *modifiers, const unsigned int count, uint32_t flags) {
    static void *(*real)(void *, uint32_t, uint32_t, uint32_t, const uint64_t *, const unsigned int, uint32_t) = NULL;
    if (!real) real = (void *(*)(void *, uint32_t, uint32_t, uint32_t, const uint64_t *, const unsigned int, uint32_t))
        dlsym(RTLD_NEXT, "gbm_bo_create_with_modifiers2");
    if (g_from_mesa) {
        void *(*f)(void *, uint32_t, uint32_t, uint32_t, const uint64_t *, const unsigned int, uint32_t) =
            (void *(*)(void *, uint32_t, uint32_t, uint32_t, const uint64_t *, const unsigned int, uint32_t))
            gbm_mesa_sym("gbm_bo_create_with_modifiers2");
        return f ? f(gbm, width, height, format, modifiers, count, flags) : NULL;
    }
    return real ? real(gbm, width, height, format, modifiers, count, flags) : NULL;
}

int drmOpen(const char *name, const char *busid) {
    static int (*real)(const char *, const char *) = NULL;
    if (!real) real = (int (*)(const char *, const char *))dlsym(RTLD_NEXT, "drmOpen");
    int fd = real ? real(name, busid) : -1;
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] drmOpen(%s, %s) -> fd=%d\n", name ? name : "?", busid ? busid : "?", fd);
    return fd;
}

int drmOpenWithType(const char *name, const char *busid, int type) {
    static int (*real)(const char *, const char *, int) = NULL;
    if (!real) real = (int (*)(const char *, const char *, int))dlsym(RTLD_NEXT, "drmOpenWithType");
    int fd = real ? real(name, busid, type) : -1;
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] drmOpenWithType(%s, %s, %d) -> fd=%d\n",
                name ? name : "?", busid ? busid : "?", type, fd);
    return fd;
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
            if (getenv("PROPFIX_DEBUG"))
                fprintf(stderr, "[propfix] read %s = %s\n", g_keys[i], g_vals[i]);
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
            if (getenv("PROPFIX_DEBUG"))
                fprintf(stderr, "[propfix] read_callback %s = %s\n", g_keys[i], g_vals[i]);
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
    static int _n1 = 0;
    if (getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] android_dlopen_ext(%s, flags=0x%x, extinfo=%p) -> %s\n",
                filename ? filename : "(null)", flags, extinfo, h ? "OK" : "FAIL");
    if (h && filename && getenv("PROPFIX_DEBUG") && _n1 < 300)
        fprintf(stderr, "[propfix] dlopen#%d 成功: %s\n", ++_n1, filename);
    if (!h && getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] dlopen 失败: %s → %s\n", filename ? filename : "(null)", dlerror());
    else if (h && getenv("PROPFIX_DEBUG") && filename &&
             (strstr(filename, "dri") || strstr(filename, "gallium") || strstr(filename, "egl") ||
              strstr(filename, "mapper") || strstr(filename, "gralloc") ||
              strstr(filename, "impl") || strstr(filename, "hw/")))
        fprintf(stderr, "[propfix] dlopen 成功: %s\n", filename);
    return h;
}

void *dlopen(const char *filename, int flags) {
    static void *(*real)(const char *, int) = NULL;
    if (!real) real = (void *(*)(const char *, int))dlsym(RTLD_NEXT, "dlopen");
    void *h = real ? real(filename, flags) : NULL;
    static int _n2 = 0;
    if (h && filename && getenv("PROPFIX_DEBUG") && _n2 < 300)
        fprintf(stderr, "[propfix] dlopen#%d 成功: %s\n", ++_n2, filename);
    // 放宽到"任何失败都打"：EGL 的 DRI 驱动内部加载失败是静默的（上层只看到"没有配置"），
    // 实测 SF 请求标准配置但驱动返回 0 个 —— 真相就在这里的 dlopen 结果里。
    if (!h && getenv("PROPFIX_DEBUG"))
        fprintf(stderr, "[propfix] dlopen 失败: %s → %s\n", filename ? filename : "(null)", dlerror());
    else if (h && getenv("PROPFIX_DEBUG") && filename &&
             (strstr(filename, "dri") || strstr(filename, "gallium") || strstr(filename, "egl") ||
              strstr(filename, "mapper") || strstr(filename, "gralloc") ||
              strstr(filename, "impl") || strstr(filename, "hw/")))
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
