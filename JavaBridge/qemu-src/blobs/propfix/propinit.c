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
#include <sys/stat.h>
#include <sys/types.h>
#include <unistd.h>

// ⚠ 这两个符号在 AOSP 里是 LIBC_PRIVATE（init 自用）：NDK 的 stub libc 不导出它们，
// 直接引用会链接失败（实测）。改为运行时解析：先 dlvsym(…, "LIBC_PRIVATE")，再退回 dlsym。
#include <dlfcn.h>
#include <sys/system_properties.h>   // __system_property_get（NDK 头里有）
typedef int (*area_init_fn)(const char *);
typedef int (*add_fn)(const char *, unsigned int, const char *, unsigned int);

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
    // ⚠ 实测：guest 里**已经存在** /dev/__properties__（adb 侧是 Permission denied 而非"不存在"），
    // 而 __system_property_area_init 对已存在的区域返回 -1、之后 add 全部 rc=-1。
    // 这里先把它挪走（我们是 init 阶段的 root），再建一份干净的。
    // 代价：那份里的属性会丢 —— 但它们本来就是 proppreload 假表的产物，没有真值可丢。
    {
        char buf[256];
        int n = readlink("/dev/__properties__", buf, sizeof(buf) - 1);
        if (n >= 0) {
            buf[n] = 0;
            fprintf(stderr, "[propinit] 旧属性区是符号链接 → %s\n", buf);
        } else {
            fprintf(stderr, "[propinit] 旧属性区已存在（非链接），先移除\n");
        }
        if (remove("/dev/__properties__") != 0)
            remove("/dev/__properties__/properties_serial");   // 目录形态兜底
    }
    mkdir("/dev/__properties__", 0755);
    area_init_fn p_area_init = (area_init_fn)resolve_private("__system_property_area_init");
    add_fn p_add = (add_fn)resolve_private("__system_property_add");
    if (!p_area_init || !p_add) { fprintf(stderr, "[propinit] 无法解析 libc 私有符号（area_init=%p add=%p）\n", (void*)p_area_init, (void*)p_add); return 2; }
    int r = p_area_init("/dev/__properties__");
    fprintf(stderr, "[propinit] area_init(%s) = %d\n", "/dev/__properties__", r);
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
        else fprintf(stderr, "[propinit] add %s 失败 (rc=%d)\n", kvs[i].k, rc);
    }
    fprintf(stderr, "[propinit] 写入 %d/%d 条\n", n, (int)(sizeof(kvs) / sizeof(kvs[0])));

    // 自检：用真属性区读回来（这会走 bionic 自己的实现，而不是任何 shim）
    char buf[256];
    int got = __system_property_get("ro.hardware.egl", buf);
    fprintf(stderr, "[propinit] 读回 ro.hardware.egl = %s (len=%d)\n", got ? buf : "(空)", got);
    got = __system_property_get("ro.debuggable", buf);
    fprintf(stderr, "[propinit] 读回 ro.debuggable = %s (len=%d)\n", got ? buf : "(空)", got);
    return 0;
}
