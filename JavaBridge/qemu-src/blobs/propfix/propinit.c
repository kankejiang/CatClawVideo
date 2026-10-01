// propinit.c —— B1.1/P0.1（2026-10-01）
//
// 目的：在 guest 里建一个**真正的 Android 属性区**（/dev/__properties__），并写入跨进程约定的键。
//
// 为什么必须这么做：我们的 /proppreload.so 是**每进程一份、编译期写死**的属性表，
//   ① 桥（Java）set 的属性别的进程看不到 → HIDL 客户端死等 hwservicemanager.ready；
//   ② bionic 的 LD_PRELOAD 生效条件是 ro.debuggable=1，而它读的是**真属性区** —— 真区不存在时
//      ro.debuggable 为空 ⇒ LD_PRELOAD 被忽略（这解释了实测里 libpropfix 的调试输出一直不出现）。
// 真属性区一建，上面两条同时解决：所有进程共享同一份属性。
//
// bionic 里这两个符号是 init 自己用的（私有但导出），NDK 头文件不声明，这里自己声明。
//
// 编（108 上 NDK）：
//   x86_64-linux-android33-clang -O2 -o propinit propinit.c
#define _GNU_SOURCE
#include <stdio.h>
#include <string.h>
#include <stdarg.h>
#include <errno.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <unistd.h>

// ⚠ 这两个符号在 AOSP 里是 LIBC_PRIVATE（init 自用）：NDK 的 stub libc 不导出它们，
// 直接引用会链接失败（实测）。改为运行时解析：先 dlvsym(…, "LIBC_PRIVATE")，再退回 dlsym。
#include <dlfcn.h>
#include <sys/system_properties.h>   // __system_property_get（NDK 头里有）
typedef int (*area_init_fn)(const char *);
typedef int (*add_fn)(const char *, unsigned int, const char *, unsigned int);

// 一行诊断同时落两处：stderr（→ 串口 → 宿主 qemu-console-art.log）与 /data/propinit.err
// （给只读探针/adb 取，不必等 logd）。见 T4 交付文档"给 T1 的请求 R3"。
static void say(const char *fmt, ...) {
    va_list ap;
    fputs("[propinit] ", stderr);
    va_start(ap, fmt);
    vfprintf(stderr, fmt, ap);
    va_end(ap);
    fputc('\n', stderr);
    fflush(stderr);
    FILE *f = fopen("/data/propinit.err", "ae");
    if (f) {
        fputs("[propinit] ", f);
        va_start(ap, fmt);
        vfprintf(f, fmt, ap);
        va_end(ap);
        fputc('\n', f);
        fclose(f);
    }
}

static void *resolve_private(const char *name) {
    void *h = dlopen("libc.so", RTLD_NOW);
    if (!h) h = RTLD_DEFAULT;
    void *p = dlvsym(h, name, "LIBC_PRIVATE");
    if (!p) p = dlsym(h, name);
    if (!p) p = dlsym(RTLD_DEFAULT, name);
    return p;
}

typedef struct { const char *k; const char *v; } KV;

int main(void) {
    // ⚠ 2026-10-02（E1 定因）：原来这里只打一句"旧属性区已存在（非链接）"——**那句不可信**：
    //   readlink() 对"根本不存在"的目录同样返回 -1（ENOENT），代码却没区分，
    //   于是每次开机都打印"已存在"，把"旧区导致 area_init=-1"这个假设说得像有证据。
    //   现在每一步都带 errno，并额外 stat 一遍 property_info（bionic 建区真正要读的文件）。
    //   同时把同样的行写一份到 /data/propinit.err（给只读探针取，见 T4 的 R3）。
    say("[propinit] === 属性区定因开始 ===");
    {
        char lb[256];
        errno = 0;
        int n = readlink("/dev/__properties__", lb, sizeof(lb) - 1);
        if (n >= 0) {
            lb[n] = 0;
            say("[propinit] /dev/__properties__ 是符号链接 → %s", lb);
        } else {
            say("[propinit] readlink 失败: %s（ENOENT=根本没有旧区）", strerror(errno));
        }
        errno = 0;
        if (remove("/dev/__properties__") != 0) {
            say("[propinit] remove(目录) 失败: %s", strerror(errno));
            errno = 0;
            if (remove("/dev/__properties__/properties_serial") != 0)
                say("[propinit] remove(properties_serial) 失败: %s", strerror(errno));
        } else {
            say("[propinit] 旧区已移除");
        }
        errno = 0;
        if (mkdir("/dev/__properties__", 0755) != 0)
            say("[propinit] mkdir 失败: %s", strerror(errno));
    }
    area_init_fn p_area_init = (area_init_fn)resolve_private("__system_property_area_init");
    add_fn p_add = (add_fn)resolve_private("__system_property_add");
    if (!p_area_init || !p_add) { say("无法解析 libc 私有符号（area_init=%p add=%p）", (void*)p_area_init, (void*)p_add); return 2; }
    errno = 0;
    int r = p_area_init("/dev/__properties__");
    say("[propinit] area_init(/dev/__properties__) = %d, errno=%d (%s)", r, errno, strerror(errno));
    {
        struct stat st;
        errno = 0;
        say("[propinit] stat 目录: %s", stat("/dev/__properties__", &st) == 0 ? "OK" : strerror(errno));
        errno = 0;
        say("[propinit] property_info: %s",
            stat("/dev/__properties__/property_info", &st) == 0 ? "在" : strerror(errno));
        errno = 0;
        say("[propinit] properties_serial: %s",
            stat("/dev/__properties__/properties_serial", &st) == 0 ? "在" : strerror(errno));
    }
    if (r != 0) {
        // 已存在（重复调用）不算致命：继续尝试写入
    }

    static const KV kvs[] = {
        // LD_PRELOAD 的开关（bionic 用它决定是否加载 LD_PRELOAD 里的库）
        { "ro.debuggable", "1" },
        // 图形栈：HAL 变体 + DRM 设备（取自 108 上跑通的 Waydroid 配置）
        { "ro.hardware", "waydroid" },
        { "ro.hardware.egl", "mesa" },
        { "ro.hardware.hwcomposer", "waydroid" },
        { "ro.hardware.gralloc", "minigbm_gbm_mesa" },
        { "gralloc.gbm.device", "/dev/dri/renderD128" },
        { "ro.opengles.version", "196610" },
        // HIDL：注册表就绪标记（客户端靠它判断能不能用）
        { "hwservicemanager.ready", "true" },
        { "vndservicemanager.ready", "true" },
        // RenderEngine 后端（本 SF 只认 skiagl/gles —— 没有 CPU 后端，所以必须给 GL）
        { "debug.renderengine.backend", "skiagl" },
        // adb
        { "service.adb.tcp.port", "5555" },
        { "ro.adb.secure", "0" },
        { "persist.adb.tls_server.enable", "0" },
        // bionic linker 自己的调试开关（只有在**真属性区**里才生效）：打开后 linker 会
        // 直接打印每次 dlopen/dlsym 的结果与失败原因 —— 正是定位"EGL 驱动为何不被接管"要的。
        { "debug.ld.all", "dlopen,dlsym,dlerror" },
        // 一些服务启动时会读的基础值
        { "ro.build.version.sdk", "33" },
        { "ro.build.version.release", "13" },
        { "ro.product.cpu.abilist", "x86_64,arm64-v8a,x86,armeabi-v7a,armeabi" },
        { "ro.product.cpu.abilist64", "x86_64,arm64-v8a" },
        { "ro.zygote", "zygote64" },
        { "ro.crypto.state", "unencrypted" },
        { "ro.crypto.type", "none" },
        { "ro.hardware.vulkan", "intel" },
    };

    int n = 0;
    for (unsigned i = 0; i < sizeof(kvs) / sizeof(kvs[0]); i++) {
        int rc = p_add(kvs[i].k, (unsigned)strlen(kvs[i].k),
                                       kvs[i].v, (unsigned)strlen(kvs[i].v));
        if (rc == 0) n++;
        else say("add %s 失败 (rc=%d, errno=%d %s)", kvs[i].k, rc, errno, strerror(errno));
    }
    say("写入 %d/%d 条", n, (int)(sizeof(kvs) / sizeof(kvs[0])));

    // 自检：用真属性区读回来（这会走 bionic 自己的实现，而不是任何 shim）
    char buf[256];
    int got = __system_property_get("ro.hardware.egl", buf);
    say("读回 ro.hardware.egl = %s (len=%d)", got ? buf : "(空)", got);
    got = __system_property_get("ro.debuggable", buf);
    say("读回 ro.debuggable = %s (len=%d)", got ? buf : "(空)", got);
    return 0;
}
