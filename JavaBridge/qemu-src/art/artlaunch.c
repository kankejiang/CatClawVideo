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
#include <dlfcn.h>
#include <stdio.h>
#include <string.h>
#include <signal.h>
#include <stdbool.h>

/* libsigchain 要求**主程序**导出这三个符号（实测 tombstone：
 *   #01 libsigchain.so AddSpecialSignalHandlerFn+20
 *   #02 libart.so art::FaultManager::Init()+140
 *   → "SetSpecialSignalHandlerFn is not exported by the main executable." → abort）
 * app_process64 是靠链接期带上 libsigchain 拿到的；这里自己实现，语义等价：记下 ART 的
 * handler 并真的 sigaction 上去，返回前一个。 */
#define SIGCHAIN_MAX 32
typedef bool (*chain_fn)(int, siginfo_t *, void *);
static chain_fn g_chain[SIGCHAIN_MAX];

chain_fn SetSpecialSignalHandlerFn(int signal, struct sigaction *sa) {
    if (signal < 0 || signal >= SIGCHAIN_MAX || !sa) return NULL;
    chain_fn old = g_chain[signal];
    g_chain[signal] = (chain_fn) sa->sa_sigaction;
    struct sigaction dummy;
    sigaction(signal, sa, &dummy);
    return old;
}

chain_fn AddSpecialSignalHandlerFn(int signal, struct sigaction *sa) {
    return SetSpecialSignalHandlerFn(signal, sa);
}

void RemoveSpecialSignalHandlerFn(int signal, chain_fn handler) {
    if (signal >= 0 && signal < SIGCHAIN_MAX && g_chain[signal] == handler) g_chain[signal] = NULL;
}


typedef jint (*create_vm_fn)(JavaVM **, void **, JavaVMInitArgs *);

/* ⚠ 不要指望在这里塞一个同名 jar 去顶掉 framework 的 android.app.AlertDialog：
 * 实测（2026-09-26）把 /uishadow.jar 放在 BCP 首位、dex 也 zipalign 过，ART 仍然解析到
 * framework.jar 的那份 —— boot classpath 里的重复类不是"先到先得"。
 * 网盘系对话框要上行只能走别的机制（见 Server.java 的 probe op 输出）。 */
static const char *BCP =
    "/system/framework/core-oj.jar:/system/framework/core-libart.jar:/system/framework/conscrypt.jar"
    ":/system/framework/okhttp.jar:/system/framework/bouncycastle.jar:/system/framework/apache-xml.jar"
    ":/system/framework/ext.jar:/system/framework/framework.jar:/system/framework/telephony-common.jar"
    ":/system/framework/voip-common.jar:/system/framework/ims-common.jar"
    ":/system/framework/android.hidl.base-V1.0-java.jar:/system/framework/android.hidl.manager-V1.0-java.jar"
    ":/system/framework/framework-oahl-backward-compatibility.jar:/system/framework/android.test.base.jar";

int main(int argc, char **argv) {
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
    snprintf(bcp_opt, sizeof bcp_opt, "-Xbootclasspath:%s", BCP);
    snprintf(loc_opt, sizeof loc_opt, "-Xbootclasspath-locations:%s", BCP);
    JavaVMOption opts[8];
    int n = 0;
    opts[n++].optionString = cpopt;
    opts[n++].optionString = bcp_opt;
    opts[n++].optionString = loc_opt;

    JavaVMInitArgs args;
    memset(&args, 0, sizeof args);
    args.version = JNI_VERSION_1_6;
    args.nOptions = n;
    args.options = opts;
    args.ignoreUnrecognized = JNI_TRUE;

    JavaVM *vm = NULL;
    JNIEnv *env = NULL;
    jint rc = create(&vm, (void **) &env, &args);
    printf("artlaunch: JNI_CreateJavaVM = %d\n", rc);
    fflush(stdout);
    if (rc != JNI_OK || env == NULL) return 1;

    /* boot classpath 里 android.os.SystemProperties / android.util.Log 的 native 平时由
     * libandroid_runtime 在 zygote 启动时注册；我们没有那条路，所以让预加载的
     * proppreload.so 通过 RegisterNatives 主动补上（详见 proppreload.c 注释）。 */
    {
        int (*reg)(JNIEnv *) = (int (*)(JNIEnv *)) dlsym(RTLD_DEFAULT, "catclaw_register_boot_natives");
        if (reg) printf("artlaunch: boot natives 注册 %d 个\n", reg(env));
        else printf("artlaunch: 没有 catclaw_register_boot_natives（LD_PRELOAD 没生效？）\n");
        if ((*env)->ExceptionCheck(env)) { printf("artlaunch: 注册阶段有异常\n"); (*env)->ExceptionDescribe(env); (*env)->ExceptionClear(env); }
    }

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
