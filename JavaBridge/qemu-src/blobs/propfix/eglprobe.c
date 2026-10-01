// eglprobe.c —— B1.1/P0.1：最简 EGL 探针（隔离实验）
//
// 目的：SurfaceFlinger 里 EGL 的失败信息被 liblog 吞掉/乱码。这里在**独立进程**里走同一条
// EGL 路径（同样的 LD_PRELOAD/PROPFIX 环境），mesa 的 stderr 与每一步的 errno 都直接可见。
//
// 编（108 上 NDK）：
//   x86_64-linux-android33-clang -O2 -o eglprobe eglprobe.c -lEGL -lGLESv2
#define _GNU_SOURCE
#include <EGL/egl.h>
#include <EGL/eglext.h>   // EGL_RECORDABLE_ANDROID 在 eglext.h 里（NDK 的 egl.h 没有）
#include <dlfcn.h>        // 直接 dlopen mesa 的 EGL（绕开 loader 的决定性实验）
#include <stdio.h>
#include <string.h>

int main(void) {
    printf("[eglprobe] 开始\n");

    EGLDisplay d = eglGetDisplay(EGL_DEFAULT_DISPLAY);
    printf("[eglprobe] eglGetDisplay=%p err=0x%x\n", (void *)d, eglGetError());
    if (d == EGL_NO_DISPLAY) return 1;

    EGLint maj = -1, min = -1;
    EGLBoolean ok = eglInitialize(d, &maj, &min);
    printf("[eglprobe] eglInitialize=%d (v=%d.%d) err=0x%x\n", ok, maj, min, eglGetError());

    const char *ver = eglQueryString(d, EGL_VERSION);
    const char *ven = eglQueryString(d, EGL_VENDOR);
    const char *apis = eglQueryString(d, EGL_CLIENT_APIS);
    printf("[eglprobe] VERSION=%s\n", ver ? ver : "(null)");
    printf("[eglprobe] VENDOR=%s\n", ven ? ven : "(null)");
    printf("[eglprobe] CLIENT_APIS=%s\n", apis ? apis : "(null)");

    // 先问"任意配置"，再看总数
    EGLint n = 0;
    EGLBoolean c0 = eglChooseConfig(d, NULL, NULL, 0, &n);
    printf("[eglprobe] chooseConfig(NULL) ok=%d total=%d err=0x%x\n", c0, n, eglGetError());

    EGLConfig cfgs[128];
    EGLint cnt = 0;
    EGLint attrs[] = {
        EGL_SURFACE_TYPE, EGL_WINDOW_BIT | EGL_PBUFFER_BIT,
        EGL_RENDERABLE_TYPE, EGL_OPENGL_ES2_BIT,
        EGL_RED_SIZE, 8, EGL_GREEN_SIZE, 8, EGL_BLUE_SIZE, 8, EGL_ALPHA_SIZE, 8,
        EGL_NONE
    };
    EGLBoolean c1 = eglChooseConfig(d, attrs, cfgs, 128, &cnt);
    printf("[eglprobe] chooseConfig(RGBA8888/ES2) ok=%d n=%d err=0x%x\n", c1, cnt, eglGetError());

    EGLint attrs2[] = {
        EGL_SURFACE_TYPE, EGL_WINDOW_BIT | EGL_PBUFFER_BIT,
        EGL_RECORDABLE_ANDROID, 1,
        EGL_RENDERABLE_TYPE, EGL_OPENGL_ES2_BIT,
        EGL_RED_SIZE, 8, EGL_GREEN_SIZE, 8, EGL_BLUE_SIZE, 8, EGL_ALPHA_SIZE, 8,
        EGL_NONE
    };
    cnt = 0;
    EGLBoolean c2 = eglChooseConfig(d, attrs2, cfgs, 128, &cnt);
    printf("[eglprobe] chooseConfig(+RECORDABLE) ok=%d n=%d err=0x%x\n", c2, cnt, eglGetError());

    if (cnt > 0) {
        EGLint id = -1, rt = -1, st = -1, rs = -1, gs = -1, bs = -1, as = -1;
        eglGetConfigAttrib(d, cfgs[0], EGL_CONFIG_ID, &id);
        eglGetConfigAttrib(d, cfgs[0], EGL_RENDERABLE_TYPE, &rt);
        eglGetConfigAttrib(d, cfgs[0], EGL_SURFACE_TYPE, &st);
        eglGetConfigAttrib(d, cfgs[0], EGL_RED_SIZE, &rs);
        eglGetConfigAttrib(d, cfgs[0], EGL_GREEN_SIZE, &gs);
        eglGetConfigAttrib(d, cfgs[0], EGL_BLUE_SIZE, &bs);
        eglGetConfigAttrib(d, cfgs[0], EGL_ALPHA_SIZE, &as);
        printf("[eglprobe] 第一个配置: id=%d rt=0x%x st=0x%x RGBA=%d%d%d%d\n",
               id, rt, st, rs, gs, bs, as);
    }

    // ── 决定性实验：绕开 libEGL.so loader，直接 dlopen mesa 的 EGL 并调它的入口 ──
    // 若 mesa 自己能用，说明"loader 拒绝接管"是唯一障碍 ⇒ 走薄适配层（自己的 preload 把 EGL
    // 入口转发给 mesa）即可救活显示栈；若 mesa 自己也不行，问题就在 mesa/环境本身。
    printf("\n[eglprobe] === 直接调用 mesa 的 EGL ===\n");
    void *h = dlopen("/system/lib64/egl/libEGL_mesa.so", RTLD_NOW | RTLD_LOCAL);
    printf("[eglprobe] dlopen(libEGL_mesa.so) = %p %s\n", h, h ? "" : dlerror());
    if (h) {
        typedef void *(*fn_gd)(void *);
        typedef unsigned (*fn_gi)(void *, int *, int *);
        typedef const char *(*fn_qs)(void *, int);
        typedef unsigned (*fn_cc)(void *, const int *, void **, int, int *);
        fn_gd gd = (fn_gd)dlsym(h, "eglGetDisplay");
        fn_gi gi = (fn_gi)dlsym(h, "eglInitialize");
        fn_qs qs = (fn_qs)dlsym(h, "eglQueryString");
        fn_cc cc = (fn_cc)dlsym(h, "eglChooseConfig");
        printf("[eglprobe] 入口: gd=%p gi=%p qs=%p cc=%p\n",
               (void *)gd, (void *)gi, (void *)qs, (void *)cc);
        if (gd && gi && qs && cc) {
            void *d2 = gd((void *)0);
            int a = -1, b = -1;
            unsigned ok2 = gi(d2, &a, &b);
            printf("[eglprobe] mesa: display=%p init=%u v=%d.%d VERSION=%s VENDOR=%s\n",
                   d2, ok2, a, b,
                   qs(d2, 0x3054) ? qs(d2, 0x3054) : "(null)",
                   qs(d2, 0x3053) ? qs(d2, 0x3053) : "(null)");
            void *cfgs[128];
            int n2 = 0;
            int at[] = { 0x3033, 0x0004 | 0x0001, 0x3040, 0x0004,
                         0x3024, 8, 0x3023, 8, 0x3022, 8, 0x3021, 8, 0x3038 };
            unsigned ok3 = cc(d2, at, cfgs, 128, &n2);
            printf("[eglprobe] mesa: chooseConfig ok=%u n=%d\n", ok3, n2);
        }
    }

    printf("[eglprobe] 结束\n");
    return 0;
}
