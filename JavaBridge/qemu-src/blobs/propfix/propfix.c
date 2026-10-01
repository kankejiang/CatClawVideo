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

static char g_keys[MAXK][128];
static char g_vals[MAXK][VLEN];
static int g_n = -1;                 // -1 = 还没解析

// 给 __system_property_find 用：每个 key 一块私有的 prop_info 占位内存
static uint8_t g_fake[MAXK][128];

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
    if (i >= 0) return (const void *)g_fake[i];
    return real ? real(name) : NULL;
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
