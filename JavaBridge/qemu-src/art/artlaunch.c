/* Minimal ART launcher: calls JNI_CreateJavaVM directly instead of going through
 * /system/bin/app_process64, which aborts with
 *   "Unable to determine ABI list from property ro.product.cpu.abilist64"
 * That message comes from app_process.cpp, not from ART - so a real VM may well
 * start without an Android property store at all. (2026-09-25, V1.)
 *
 * -Xbootclasspath below is the byte-exact order scraped out of
 * /system/framework/arm64/boot.oat, which also records that the image was built
 * with --multi-image and -Xnorelocate; hence those two options.
 *
 * Kept in C on purpose: the NDK's default C++ mode links libc++_shared.so, which
 * is not part of the Android system image we ship in the initrd.
 */
#include <jni.h>
#include <pthread.h>
#include <dlfcn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <signal.h>
#include <stdbool.h>
#include <unistd.h>

/* libsigchain 要求**主程序**导出这三个符号（实测 tombstone：
 *   #01 libsigchain.so AddSpecialSignalHandlerFn+20
 *   #02 libart.so art::FaultManager::Init()+140
 *   → "SetSpecialSignalHandlerFn is not exported by the main executable." → abort）
 * app_process64 是靠链接期带上 libsigchain 拿到的；这里自己实现，语义等价：记下 ART 的
 * handler 并真的 sigaction 上去，返回前一个。 */
#define SIGCHAIN_MAX 32
typedef bool (*chain_fn)(int, siginfo_t *, void *);
static chain_fn g_chain[SIGCHAIN_MAX];

/* 信号哨兵（2026-09-27）：壳真实类首调转译代码后 SIGSEGV 无 dump 直接 139 死，
 * 这里包一层 trampoline——每次信号先 write 日志（信号号/故障码/故障地址）再转发
 * 真实 handler。排查 ndk 转译段 fault 到底有没有进 handler、地址落在哪。 */
static void sig_trampoline(int sig, siginfo_t *info, void *ctx) {
    char buf[96];
    static const char hex[] = "0123456789abcdef";
    uintptr_t a = (uintptr_t) info->si_addr;
    buf[0] = '['; buf[1] = 's'; buf[2] = 'i'; buf[3] = 'g'; buf[4] = ']';
    buf[5] = ' ';
    int n = 6;
    /* 固定格式 "signal=%d code=%d addr=0x<hex16>\n"——只用 write，异步信号安全 */
    buf[n++] = 's'; buf[n++] = '='; buf[n++] = (char) ('0' + sig);
    buf[n++] = ' '; buf[n++] = 'c'; buf[n++] = '=';
    int c = info->si_code;
    if (c < 0) { buf[n++] = '-'; c = -c; }
    if (c > 9) { buf[n++] = (char) ('0' + c / 10); c %= 10; }
    buf[n++] = (char) ('0' + c);
    buf[n++] = ' '; buf[n++] = 'a'; buf[n++] = '=';
    for (int i = 15; i >= 0; i--) buf[n + (15 - i)] = hex[(a >> (i * 4)) & 0xf];
    n += 16;
    buf[n++] = '\n';
    write(2, buf, n);
    chain_fn real = g_chain[sig];
    if (real) real(sig, info, ctx);
    else _exit(128 + sig);   /* 没有真 handler：显式留退出码 */
}

chain_fn SetSpecialSignalHandlerFn(int signal, struct sigaction *sa) {
    if (signal < 0 || signal >= SIGCHAIN_MAX || !sa) return NULL;
    chain_fn old = g_chain[signal];
    g_chain[signal] = (chain_fn) sa->sa_sigaction;
    struct sigaction tramp;
    memcpy(&tramp, sa, sizeof tramp);
    tramp.sa_sigaction = sig_trampoline;
    struct sigaction dummy;
    sigaction(signal, &tramp, &dummy);
    return old;
}

chain_fn AddSpecialSignalHandlerFn(int signal, struct sigaction *sa) {
    return SetSpecialSignalHandlerFn(signal, sa);
}

void RemoveSpecialSignalHandlerFn(int signal, chain_fn handler) {
    if (signal >= 0 && signal < SIGCHAIN_MAX && g_chain[signal] == handler) g_chain[signal] = NULL;
}


typedef jint (*create_vm_fn)(JavaVM **, void **, JavaVMInitArgs *);

/* boot classpath 支持经环境变量覆盖（2026-09-27，x86 mini guest / Android 13 适配）：
 *   CATCLAW_BCP          —— 整条 boot classpath（':' 连接）
 *   CATCLAW_BCP_LOCATIONS —— boot 镜像位置（Android 9 的 multi-image boot.oat 需要；
 *                           13 的 waydroid 镜像无预编译 boot 镜像（JIT 模式），不设即可）
 * 不设时走下面的 9 期默认（aarch64 现网行为零变化）。 */
static const char *BCP =
    "/system/framework/core-oj.jar:/system/framework/core-libart.jar:/system/framework/conscrypt.jar"
    ":/system/framework/okhttp.jar:/system/framework/bouncycastle.jar:/system/framework/apache-xml.jar"
    ":/system/framework/ext.jar:/system/framework/framework.jar:/system/framework/telephony-common.jar"
    ":/system/framework/voip-common.jar:/system/framework/ims-common.jar"
    ":/system/framework/android.hidl.base-V1.0-java.jar:/system/framework/android.hidl.manager-V1.0-java.jar"
    ":/system/framework/framework-oahl-backward-compatibility.jar:/system/framework/android.test.base.jar";

/* 线程域实验：ndk initialize 后新线程的 native 解析是否可用（主线程 vs 新线程对照）。
 * 分步 printf 定位输出断点（2026-09-27：块静默跳过，getvm/线程创建均未检） */
static JavaVM *g_test_vm;
static jobject g_test_sp;
static jmethodID g_test_ng;
static void *thread_runner(void *arg) {
    printf("artlaunch: [线程实验] 线程函数已进入\n"); fflush(stdout);
    JNIEnv *e3 = NULL;
    jint arc = (*g_test_vm)->AttachCurrentThread(g_test_vm, &e3, NULL);
    printf("artlaunch: [线程实验] AttachCurrentThread = %d\n", arc); fflush(stdout);
    if (arc == JNI_OK && e3) {
        jstring r3 = (jstring) (*e3)->CallStaticObjectMethod(e3, g_test_sp, g_test_ng,
            (*e3)->NewStringUTF(e3, "ro.product.cpu.abi"), (*e3)->NewStringUTF(e3, "(def)"));
        if ((*e3)->ExceptionCheck(e3)) {   /* No implementation found 会在这里现形 */
            printf("artlaunch: [线程实验] 新线程 native_get 抛异常：\n");
            (*e3)->ExceptionDescribe(e3);
            (*e3)->ExceptionClear(e3);
        } else {
            const char *s3 = r3 ? (*e3)->GetStringUTFChars(e3, r3, 0) : "(null)";
            printf("artlaunch: [线程实验] 新线程 native_get = %s\n", s3);
            if (r3) (*e3)->ReleaseStringUTFChars(e3, r3, s3);
        }
    }
    printf("artlaunch: [线程实验] 线程函数结束\n"); fflush(stdout);
    return NULL;
}


int main(int argc, char **argv) {
    /* 属性可达性探针：nativebridge 属性是否经 proppreload 可读（libart 同进程同路径）。
     * 输出空值 = 拦截缺口（proppreload 缺 __system_property_find/read_callback 实现）。 */
    {
        char v[92] = {0};
        const char *names[] = {"ro.dalvik.vm.native.bridge", "ro.enable.native.bridge.exec",
                               "ro.dalvik.vm.isa.arm64"};
        for (int i = 0; i < 3; i++) {
            int n = __system_property_get(names[i], v);
            printf("artlaunch: prop[%s] = %.*s\n", names[i], n > 0 ? n : 0, v);
        }
        fflush(stdout);
    }
    /* Android 11+ 的 libnativeloader 需要显式初始化（正常由 AndroidRuntime::StartVM 代劳，
     * 我们直调 JNI_CreateJavaVM 绕过了它）——不初始化的话 CreateVM 内部第一个系统库
     * dlopen（libandroid.so）直接 abort：GetSystemNamespace 失败（2026-09-27 实测）。
     * ⚠ 顺序定生死：LoadNativeBridge 必须先于 InitializeNativeLoader——libnativeloader
     *   初始化时缓存「bridge 可用」标志，先 init 的话永远 false → 所有 classloader
     *   namespace 永久 not-bridged → 壳的 arm64 so 全走 bionic dlopen 报
     *   「EM_AARCH64 instead of EM_X86_64」（2026-09-27 壳加载实测的最后一环根因）。 */
    {
        void *nb = dlopen("libndk_translation.so", RTLD_NOW | RTLD_GLOBAL);
        printf("artlaunch: dlopen libndk_translation = %s\n", nb ? "OK" : dlerror());
        fflush(stdout);
        if (nb) {
            void *nbl = dlopen("libnativebridge.so", RTLD_NOW | RTLD_GLOBAL);
            if (nbl) {
                bool (*LoadNB)(const char*, const void*) =
                    (bool (*)(const char*, const void*)) dlsym(nbl, "LoadNativeBridge");
                if (LoadNB) {
                    /* ⚠ 第二参绝不能传 NULL：LoadNativeBridge 会把它存为全局 runtime callbacks，
                     * ndk initialize 收到后存 g_runtime_callbacks；壳 so 加载路径
                     * CHECK(g_runtime_callbacks) 失败 → ART abort（2026-09-27 实锤）。
                     * 正确值 = libart 静态全局 art::native_bridge_art_callbacks_（未导出，
                     * 9 个函数指针：GetMethodShorty/GetNativeMethodCount/GetNativeMethods/
                     * 4 个静态辅助/DexFile close+defineClass 系——2026-09-27 反汇编 dump）。
                     * 按「运行时基址 + 静态 vaddr」重建：锚点用导出符号
                     * _ZN3art22InitializeNativeBridgeEP7_JNIEnvPKc（静态 vaddr 0x71ccf0）。 */
                    static const uintptr_t kAnchorVaddr = 0x71ccf0;
                    static const uintptr_t kCbVaddr[9] = {
                        0x71cdc0, 0x71d540, 0x71dc30, 0xd077d, 0xb867a,
                        0x71f0e0, 0x9e64b, 0x9b23c, 0x71f9d0
                    };
                    static void *s_fns[9];
                    void *h_art_early = dlopen("libart.so", RTLD_NOW | RTLD_GLOBAL);
                    void *anchor = h_art_early
                        ? dlsym(h_art_early, "_ZN3art22InitializeNativeBridgeEP7_JNIEnvPKc") : NULL;
                    if (anchor) {
                        uintptr_t base = (uintptr_t) anchor - kAnchorVaddr;
                        for (int i = 0; i < 9; i++) s_fns[i] = (void *) (base + kCbVaddr[i]);
                        printf("artlaunch: LoadNativeBridge = %d（callbacks 重建 base=%p）\n",
                               LoadNB("libndk_translation.so", (const void *) s_fns), (void *) base);
                    } else {
                        printf("artlaunch: LoadNativeBridge = %d（无锚点，callbacks=NULL 兜底）\n",
                               LoadNB("libndk_translation.so", NULL));
                    }
                }
            } else printf("artlaunch: dlopen libnativebridge failed: %s\n", dlerror());
        }
        void *nl = dlopen("libnativeloader.so", RTLD_NOW | RTLD_GLOBAL);
        void (*initnl)(void) = nl ? (void (*)(void)) dlsym(nl, "InitializeNativeLoader") : NULL;
        if (initnl) { initnl(); printf("artlaunch: nativeloader 已初始化（NB 之后）\n"); }
        else printf("artlaunch: 无 InitializeNativeLoader（老版本跳过）\n");
        /* 路径判定审计：classloader namespace 的 bridged 标志大概率来自
         * NativeBridgeIsPathSupported(库路径)——看 ndk 认不认壳的 nativeLibraryDir */
        {
            void *nbl2 = dlopen("libnativebridge.so", RTLD_NOW | RTLD_GLOBAL);
            bool (*isAv)(void) = (bool (*)(void)) dlsym(nbl2, "NativeBridgeIsAvailable");
            bool (*isPS)(const char *) = (bool (*)(const char *)) dlsym(nbl2, "NativeBridgeIsPathSupported");
            if (isAv) printf("artlaunch: [审计] NativeBridgeIsAvailable = %d\n", isAv());
            if (isPS) {
                /* ndk 的路径白名单模式探测：官方 app 的 native 库目录以 lib/arm64 结尾 */
                const char *paths[] = {
                    "/data/catclaw/art/lib",
                    "/data/catclaw/art/lib/arm64",
                    "/data/app/~~abc==/com.foo.bar-xyz==/lib/arm64",
                    "/system/lib64/arm64",
                };
                for (int i = 0; i < 4; i++)
                    printf("artlaunch: [审计] IsPathSupported(%s) = %d\n", paths[i], isPS(paths[i]));
            }
        }
        fflush(stdout);
    }

    /* 从第 3 个参数起都是 classpath 条目（':' 连接），后面才是要跑的类名与参数：
     * 用法 artlaunch <类名> <jar|dex>[:jar2...] [参数...]  —— 多 dex 必须同时进 classpath，
     * 否则 okhttp/kotlin-stdlib 之类看不到彼此（d8 分包时尤其明显）。 */
    const char *cls = argc > 1 ? argv[1] : "Hello";
    const char *appjar = argc > 2 ? argv[2] : "/hello.jar";

    void *h = dlopen("libart.so", RTLD_NOW | RTLD_GLOBAL);
    if (!h) { printf("artlaunch: dlopen libart.so failed: %s\n", dlerror()); return 1; }
    create_vm_fn create = (create_vm_fn) dlsym(h, "JNI_CreateJavaVM");
    if (!create) { printf("artlaunch: no JNI_CreateJavaVM: %s\n", dlerror()); return 1; }
    printf("artlaunch: libart ready, JNI_CreateJavaVM=%p\n", (void *) create);
    fflush(stdout);

    char cpopt[512];
    snprintf(cpopt, sizeof cpopt, "-Djava.class.path=%s", appjar);
    /* JavaVMOption 的每个条目必须是 "-Xxxx:<值>" 一整串 —— 按 app_process 的 argv 风格把
     * 选项名和值分成两个 entry，ART 认不出来，结果就是 "Boot classpath is empty"（实测）。 */
    static char bcp_opt[2048], loc_opt[2048];
    const char *bcp = getenv("CATCLAW_BCP");
    if (!bcp) bcp = BCP;
    const char *bcp_loc = getenv("CATCLAW_BCP_LOCATIONS");
    snprintf(bcp_opt, sizeof bcp_opt, "-Xbootclasspath:%s", bcp);
    JavaVMOption opts[8];
    int n = 0;
    opts[n++].optionString = cpopt;
    opts[n++].optionString = bcp_opt;
    if (bcp_loc) {   /* Android 9 的 boot.oat（multi-image）需要；13 无预编译镜像时不传 */
        snprintf(loc_opt, sizeof loc_opt, "-Xbootclasspath-locations:%s", bcp_loc);
        opts[n++].optionString = loc_opt;
    }

    JavaVMInitArgs args;
    memset(&args, 0, sizeof args);
    args.version = JNI_VERSION_1_6;
    args.nOptions = n;
    args.options = opts;
    args.ignoreUnrecognized = JNI_TRUE;

    /* CATCLAW_JVM_EXTRA：宿主按需追加 JVM 选项（空格分隔，如
     * "-Xnoimage-dex2oat -Xnodex2oat"——13 无预编译 boot 镜像时 ART 会现场调 dex2oat
     * 生成 boot 镜像，而那个镜像要求 linker namespace 与 apex 元数据一致，极简 rootfs
     * 里配不齐 → 两个选项都传，走纯 interpreter+JIT，原生 CPU 上开销可接受）。 */
    {
        const char *extra = getenv("CATCLAW_JVM_EXTRA");
        if (extra) {
            char *dup = strdup(extra);
            for (char *tok = strtok(dup, " "); tok != NULL && n < 12; tok = strtok(NULL, " ")) {
                opts[n].optionString = tok;
                printf("artlaunch: 附加 JVM 选项 %s\n", tok);
                n++;
            }
            fflush(stdout);
        }
    }

    JavaVM *vm = NULL;
    JNIEnv *env = NULL;
    jint rc = create(&vm, (void **) &env, &args);
    printf("artlaunch: JNI_CreateJavaVM = %d\n", rc);
    fflush(stdout);
    if (rc != JNI_OK || env == NULL) return 1;
    /* boot classpath 里 android.os.SystemProperties / android.util.Log 的 native 平时由
     * libandroid_runtime 在 zygote 启动时注册；我们没有那条路，所以让预加载的
     * proppreload.so 通过 RegisterNatives 主动补上（详见 proppreload.c 注释）。
     * ⚠ 必须在 nativebridge 初始化（尤其 InitNB）**之前**：ndk initialize 内部会触发
     *   Build.<clinit>（它要读 CPU_ABI 做伪装），那时注册还没就位 → UnsatisfiedLinkError
     *   → Build 类被永久标记 erroneous → 经 DexClassLoader 的壳类调 Build.CPU_ABI 全挂
     *   （2026-09-27 从 InitNB 期间的「Build failed initialization」日志实锤）。 */
    jmethodID ng = NULL;   /* native_get（双参）：自测与线程域实验共用 */
    {
        int (*reg)(JNIEnv *) = (int (*)(JNIEnv *)) dlsym(RTLD_DEFAULT, "catclaw_register_boot_natives");
        if (reg) printf("artlaunch: boot natives 注册 %d 个\n", reg(env));
        else printf("artlaunch: 没有 catclaw_register_boot_natives（LD_PRELOAD 没生效？）\n");
        if ((*env)->ExceptionCheck(env)) { printf("artlaunch: 注册阶段有异常\n"); (*env)->ExceptionDescribe(env); (*env)->ExceptionClear(env); }
    }
    /* 自测：注册后立即经 JNI 验证注册真的生效 */
    {
        jclass sp = (*env)->FindClass(env, "android/os/SystemProperties");
        jmethodID g = sp ? (*env)->GetStaticMethodID(env, sp, "get", "(Ljava/lang/String;)Ljava/lang/String;") : NULL;
        if (g) {
            jstring k = (*env)->NewStringUTF(env, "ro.dalvik.vm.native.bridge");
            jstring r = (jstring) (*env)->CallStaticObjectMethod(env, sp, g, k);
            const char *rs = r ? (*env)->GetStringUTFChars(env, r, 0) : "(null)";
            printf("artlaunch: 自测 SystemProperties.get = %s\n", rs);
            if (r) (*env)->ReleaseStringUTFChars(env, r, rs);
            ng = (*env)->GetStaticMethodID(env, sp, "native_get",
                "(Ljava/lang/String;Ljava/lang/String;)Ljava/lang/String;");
            g_test_sp = (*env)->NewGlobalRef(env, sp);
            if (ng) {
                jstring r2 = (jstring) (*env)->CallStaticObjectMethod(env, sp, ng, k,
                    (*env)->NewStringUTF(env, "(def)"));
                const char *r2s = r2 ? (*env)->GetStringUTFChars(env, r2, 0) : "(null)";
                printf("artlaunch: 自测 native_get 直调 = %s\n", r2s);
                if (r2) (*env)->ReleaseStringUTFChars(env, r2, r2s);
            } else printf("artlaunch: 自测 native_get 的 methodID 拿不到\n");
            if ((*env)->ExceptionCheck(env)) { (*env)->ExceptionDescribe(env); (*env)->ExceptionClear(env); }
        }
        fflush(stdout);
    }
    /* nativebridge 初始化（正确签名+时机）：正常由被绕过的 AndroidRuntime 在
     * CreateVM 后调 InitializeNativeBridge(env, isa)——libart 只 Load 不 Init。 */
    {
        void *nbl = dlopen("libnativebridge.so", RTLD_NOW | RTLD_GLOBAL);
        bool (*InitNB)(JNIEnv*, const char*) =
            (bool (*)(JNIEnv*, const char*)) dlsym(nbl, "InitializeNativeBridge");
        fflush(stdout);
        /* PreInitialize：拉起 arm64 bridge 进程（app_process64）准备环境——
         * 官方顺序 Load → PreInitialize → Initialize。wrapper 的 arm64 linker64 现在读
         * /system/etc/ld.config.arm64.txt（mk 生成），依赖解析不再误中 x86 库。 */
        bool (*PreNB)(const char*, const char*) =
            (bool (*)(const char*, const char*)) dlsym(nbl, "PreInitializeNativeBridge");
        if (PreNB) printf("artlaunch: PreInitializeNativeBridge = %d\n", PreNB("/data/catclaw", "arm64"));
        fflush(stdout);
        if (InitNB) printf("artlaunch: InitializeNativeBridge = %d\n", InitNB(env, "arm64"));
        else printf("artlaunch: 无 InitializeNativeBridge 符号\n");
        fflush(stdout);
    }
    /* 审计 1：Build 类状态——ndk initialize 触发的 <clinit> 现在应成功（注册已提前）。
     * 若这里读 CPU_ABI 都抛异常，说明 Build 已 erroneous，壳链路必死。 */
    {
        jclass b = (*env)->FindClass(env, "android/os/Build");
        if (!b) {
            printf("artlaunch: [审计] FindClass(Build) 失败（erroneous!）\n");
            (*env)->ExceptionDescribe(env); (*env)->ExceptionClear(env);
        } else {
            jfieldID f = (*env)->GetStaticFieldID(env, b, "CPU_ABI", "Ljava/lang/String;");
            if (!f) {
                printf("artlaunch: [审计] CPU_ABI 字段拿不到\n");
                (*env)->ExceptionDescribe(env); (*env)->ExceptionClear(env);
            } else {
                jstring v = (jstring) (*env)->GetStaticObjectField(env, b, f);
                if ((*env)->ExceptionCheck(env)) {
                    printf("artlaunch: [审计] 读 CPU_ABI 抛异常（erroneous!）\n");
                    (*env)->ExceptionDescribe(env); (*env)->ExceptionClear(env);
                } else {
                    const char *vs = v ? (*env)->GetStringUTFChars(env, v, 0) : "(null)";
                    printf("artlaunch: [审计] Build.CPU_ABI = %s\n", vs);
                    if (v) (*env)->ReleaseStringUTFChars(env, v, vs);
                }
            }
        }
        fflush(stdout);
    }
    /* 审计 2：SystemProperties 方法修饰符——proppreload 有 2 个方法报「non-native」，
     * 用反射 Modifier 实锤哪些方法在 LOS20 的 dex 里真不是 native（决定注册表怎么补）。 */
    {
        jclass sp2 = (*env)->FindClass(env, "android/os/SystemProperties");
        if (sp2) {
            const char *names[] = {"native_get", "native_get", "native_get", "get", "native_find"};
            const char *sigs[] = {"(Ljava/lang/String;)Ljava/lang/String;",
                                  "(Ljava/lang/String;Ljava/lang/String;)Ljava/lang/String;",
                                  "(J)Ljava/lang/String;",
                                  "(Ljava/lang/String;Ljava/lang/String;)Ljava/lang/String;",
                                  "(Ljava/lang/String;)J"};
            jclass mth = (*env)->FindClass(env, "java/lang/reflect/Method");
            jmethodID gm = mth ? (*env)->GetMethodID(env, mth, "getModifiers", "()I") : NULL;
            jclass mod = (*env)->FindClass(env, "java/lang/reflect/Modifier");
            jmethodID isn = mod ? (*env)->GetStaticMethodID(env, mod, "isNative", "(I)Z") : NULL;
            for (int i = 0; i < 5; i++) {
                jmethodID mid = (*env)->GetStaticMethodID(env, sp2, names[i], sigs[i]);
                if (!mid) { (*env)->ExceptionClear(env);
                    printf("artlaunch: [审计] %s%s: methodID 拿不到\n", names[i], sigs[i]); continue; }
                if (!gm || !isn) { printf("artlaunch: [审计] 反射设施不可用\n"); break; }
                jobject rm = (*env)->ToReflectedMethod(env, sp2, mid, JNI_TRUE);
                if (!rm) { (*env)->ExceptionClear(env); continue; }
                jint flags = (*env)->CallIntMethod(env, rm, gm);
                jboolean isN = (*env)->CallStaticBooleanMethod(env, mod, isn, flags);
                printf("artlaunch: [审计] %s %s: flags=0x%x native=%d\n", names[i], sigs[i], flags, isN);
                if ((*env)->ExceptionCheck(env)) { (*env)->ExceptionDescribe(env); (*env)->ExceptionClear(env); }
                (*env)->DeleteLocalRef(env, rm);
            }
        } else { (*env)->ExceptionDescribe(env); (*env)->ExceptionClear(env); }
        fflush(stdout);
    }
    /* 审计 3：nativebridge 对 arm64 so 的判定与加载——壳的 System.load 走了 bionic dlopen
     * （「is for EM_AARCH64 instead of EM_X86_64」），说明 ART 的加载分支没交给 nativebridge。
     * 这里直接问 libnativebridge：判定函数认不认 arm64、转译加载链路通不通。 */
    {
        void *nbl3 = dlopen("libnativebridge.so", RTLD_NOW | RTLD_GLOBAL);
        if (nbl3) {
            bool (*isSup)(const char *) = (bool (*)(const char *)) dlsym(nbl3, "NativeBridgeIsSupported");
            void *(*nbLoad)(const char *, int) = (void *(*)(const char *, int)) dlsym(nbl3, "NativeBridgeLoadLibrary");
            if (isSup)
                printf("artlaunch: [审计] IsSupported(/system/lib64/arm64/libc++.so) = %d\n",
                       isSup("/system/lib64/arm64/libc++.so"));
            else printf("artlaunch: [审计] 无 NativeBridgeIsSupported 符号\n");
            if (nbLoad) {
                void *h = nbLoad("/system/lib64/arm64/libc++.so", RTLD_NOW);
                printf("artlaunch: [审计] NativeBridgeLoadLibrary(arm64 libc++.so) = %s\n",
                       h ? "OK" : dlerror());
                if ((*env)->ExceptionCheck(env)) { (*env)->ExceptionDescribe(env); (*env)->ExceptionClear(env); }
            } else printf("artlaunch: [审计] 无 NativeBridgeLoadLibrary 符号\n");
        }
        fflush(stdout);
    }
    /* 线程域实验：新线程 AttachCurrentThread 后调同一 native_get——
     * 主线程成功而新线程失败 = ndk initialize 对非主线程 JNI 域的破坏 */
    {
        printf("artlaunch: [线程实验] 块进入 ng=%p sp=%p\n", (void *) ng, (void *) g_test_sp); fflush(stdout);
        JavaVM *vm2 = NULL;
        void *vml = dlopen("libart.so", RTLD_NOW | RTLD_GLOBAL);
        if (!vml) printf("artlaunch: [线程实验] dlopen libart 失败: %s\n", dlerror());
        if (vml) {
            /* 真实签名是三参 (vmBuf, bufLen, nVMs)——之前误声明成两参，
             * nv 的地址被当 bufLen=0 传入、出参永不写入 → nv 恒 0 → 实验静默跳过 */
            jint (*getvm)(JavaVM **, jsize, jsize *) = NULL;
            *(void **) &getvm = dlsym(vml, "JNI_GetCreatedJavaVMs");
            jsize nv = 0;
            jint grc = getvm ? getvm(&vm2, 1, &nv) : -1000;
            printf("artlaunch: [线程实验] GetCreatedJavaVMs rc=%d nv=%d vm=%p\n",
                   grc, nv, (void *) vm2); fflush(stdout);
            if (getvm && grc == JNI_OK && nv > 0 && vm2) {
                g_test_vm = vm2;
                g_test_ng = ng;
                pthread_t th;
                int pc = pthread_create(&th, NULL, thread_runner, NULL);
                printf("artlaunch: [线程实验] pthread_create = %d\n", pc); fflush(stdout);
                if (pc == 0) pthread_join(th, NULL);
            } else {
                printf("artlaunch: [线程实验] 取 VM 失败，对照实验跳过\n"); fflush(stdout);
            }
        }
        printf("artlaunch: [线程实验] 块结束\n"); fflush(stdout);
    }
    /* 桥启动：FindClass(cls) → main(String[]) */
    char slash[192];
    size_t ci = 0;
    for (; cls[ci] && ci + 1 < sizeof(slash); ci++) slash[ci] = (cls[ci] == '.') ? '/' : cls[ci];
    slash[ci] = 0;                       /* ART 的 FindClass 要 JNI 形式：a/b/C，不是 a.b.C */
    jclass c = (*env)->FindClass(env, slash);
    if (!c) { printf("artlaunch: FindClass(%s) failed\n", cls); (*env)->ExceptionDescribe(env); return 1; }
    jmethodID m = (*env)->GetStaticMethodID(env, c, "main", "([Ljava/lang/String;)V");
    if (!m) { printf("artlaunch: no main(String[])\n"); return 1; }
    jobjectArray arr = (*env)->NewObjectArray(env, argc > 3 ? argc - 3 : 0,
        (*env)->FindClass(env, "java/lang/String"), NULL);
    for (int i = 3; i < argc; i++) {
        jstring js = (*env)->NewStringUTF(env, argv[i]);
        (*env)->SetObjectArrayElement(env, arr, i - 3, js);
    }

    (*env)->CallStaticVoidMethod(env, c, m, arr);
    if ((*env)->ExceptionCheck(env)) { printf("artlaunch: java threw\n"); (*env)->ExceptionDescribe(env); return 1; }
    printf("artlaunch: done\n");
    fflush(stdout);
    (*vm)->DestroyJavaVM(vm);
    return 0;
}
