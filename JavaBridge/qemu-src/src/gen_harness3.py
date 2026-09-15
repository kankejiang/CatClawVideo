"""把 harness2.c 升级成 harness3.c：补上最小 JavaVM + 完整调用链。

为什么要 JavaVM：迅雷引擎是多线程的，线程里要拿 JNIEnv 只能走
JavaVM.AttachCurrentThread / GetEnv。返回 NULL 会让线程一进去就崩。
"""
import io
import re

SRC = r'D:\Code\_scratch_tb\thunder-harness\harness2.c'
DST = r'D:\Code\_scratch_tb\thunder-harness\harness3.c'

JAVA_VM = r'''
// ═══════════ 最小 JavaVM（多线程引擎必需）═══════════
// 引擎会起线程；线程里要拿 JNIEnv 只能走 JavaVM 的 AttachCurrentThread/GetEnv。
// 若 JavaVM 是 NULL 或不返回 env，线程一进去就崩 —— 这与「崩在引擎自己起的线程里」吻合。
// 这里给一个永远返回同一个 env 的最小实现。
static jint vm_AttachCurrentThread(JavaVM *vm, JNIEnv **penv, void *args) {
    (void)vm; (void)args;
    if (penv) *penv = (JNIEnv *)g_env_ptr;
    return JNI_OK;
}
static jint vm_AttachCurrentThreadAsDaemon(JavaVM *vm, JNIEnv **penv, void *args) {
    return vm_AttachCurrentThread(vm, penv, args);
}
static jint vm_DetachCurrentThread(JavaVM *vm) { (void)vm; return JNI_OK; }
static jint vm_GetEnv(JavaVM *vm, void **penv, jint version) {
    (void)vm; (void)version;
    if (penv) *penv = (JNIEnv *)g_env_ptr;
    return JNI_OK;
}
static jint vm_DestroyJavaVM(JavaVM *vm) { (void)vm; return JNI_OK; }

static struct JNIInvokeInterface g_vm_iface;
static JavaVM g_vm = &g_vm_iface;

static jint my_GetJavaVM(JNIEnv *env, JavaVM **pvm) {
    (void)env; g_jni_calls++;
    printf("  [jni] GetJavaVM\n");
    if (pvm) *pvm = &g_vm;
    return JNI_OK;
}

// ═══════════ 完整调用链（参数全部从字节码反推）═══════════

// soAppKey = Base64("com.android.providers.downloads" + 0x00 + appId(LE2) + appType)
//   appKey.split("==")[0].replace('^','=')[2..-2] -> Base64 -> rawItems="6001" -> appId=6001
//   APPTYPE_PRODUCT = 1
static const char *SO_APP_KEY = "Y29tLmFuZHJvaWQucHJvdmlkZXJzLmRvd25sb2FkcwBxFwE=";
static const char *EMU_SAVE_PATH = "/thunder-data";

static void fill_random_hex(char *out, int n, const char *alphabet) {
    int la = (int)strlen(alphabet);
    for (int i = 0; i < n; i++) out[i] = alphabet[rand() % la];
    out[n] = 0;
}

static void run_full_chain(JNIEnv *env, void *sdk) {
    typedef jint (*fn_init)(JNIEnv *, jobject, jstring, jstring, jstring, jstring, jstring,
                            jstring, jstring, jstring, jint, jint, jint);
    typedef jint (*fn_magnet)(JNIEnv *, jobject, jstring, jstring, jstring, jobject);
    typedef jint (*fn_status)(JNIEnv *, jobject, jlong, jobject, jint, jint);
    typedef jint (*fn_local)(JNIEnv *, jobject, jstring, jobject);
    typedef jint (*fn_uni)(JNIEnv *, jobject);

    fn_init init = (fn_init)dlsym(sdk, "Java_com_xunlei_downloadlib_XLLoader_init");
    fn_magnet createMagnet =
        (fn_magnet)dlsym(sdk, "Java_com_xunlei_downloadlib_XLLoader_createBtMagnetTask");
    fn_status status =
        (fn_status)dlsym(sdk, "Java_com_xunlei_downloadlib_XLLoader_getBtSubTaskStatus");
    fn_local localUrl =
        (fn_local)dlsym(sdk, "Java_com_xunlei_downloadlib_XLLoader_getLocalUrl");
    fn_uni unInit = (fn_uni)dlsym(sdk, "Java_com_xunlei_downloadlib_XLLoader_unInit");
    if (!init || !createMagnet) {
        printf("[chain] ✗ 关键入口缺失\n");
        return;
    }

    JObj *thiz = new_obj("com/xunlei/downloadlib/XLLoader");
    char peerid[64], guid[64];
    fill_random_hex(peerid, 36, "0123456789ABCDEF");
    strcpy(peerid + 36, "004V");
    fill_random_hex(guid, 14, "0123456789abcdef");
    guid[14] = '_';
    fill_random_hex(guid + 15, 12, "0123456789abcdef");

    printf("[chain] ── init ──\n");
    jint rc = init(env, (jobject)thiz,
                   (jstring)SO_APP_KEY,
                   (jstring)"com.android.providers.downloads",
                   (jstring)"1.0.0",
                   (jstring)"",
                   (jstring)peerid,
                   (jstring)guid,
                   (jstring)EMU_SAVE_PATH,
                   (jstring)EMU_SAVE_PATH,
                   1,    // networkType
                   1,    // permissionLevel  （对齐 XLTaskHelper.init）
                   0);   // queryConfOnInit
    printf("[chain] ← init 返回 %d（0=成功）\n", (int)rc);

    printf("[chain] ── createBtMagnetTask ──\n");
    const char *magnet = getenv("MAGNET");
    if (!magnet) magnet = "magnet:?xt=urn:btih:1363FB911E8603FDE757C9B02979D508DE2D195B";
    printf("[chain]   magnet = %s\n", magnet);
    JObj *taskId = new_obj("com/xunlei/downloadlib/parameter/GetTaskId");
    jint mrc = createMagnet(env, (jobject)thiz, (jstring)magnet, (jstring)EMU_SAVE_PATH,
                            (jstring)"cc-test", (jobject)taskId);
    long id = obj_get_long(taskId, "mTaskId");
    printf("[chain] ← createBtMagnetTask 返回 %d，taskId = %ld\n", (int)mrc, id);

    if (id > 0 && status) {
        JObj *st = new_obj("com/xunlei/downloadlib/parameter/BtTaskStatus");
        for (int i = 0; i < 10; i++) {
            jint s = status(env, (jobject)thiz, (jlong)id, (jobject)st, 0, 0);
            printf("[chain]   轮询#%d 返回 %d，mStatus=%s\n", i, (int)s,
                   obj_get_str(st, "mStatus"));
            sleep(1);
        }
    }

    if (localUrl) {
        JObj *lu = new_obj("com/xunlei/downloadlib/parameter/XLTaskLocalUrl");
        jint lrc = localUrl(env, (jobject)thiz, (jstring)"cc-test", (jobject)lu);
        printf("[chain] ← getLocalUrl 返回 %d，mStrUrl = %s\n", (int)lrc, obj_get_str(lu, "mStrUrl"));
    }

    if (unInit) unInit(env, (jobject)thiz);
    printf("[chain] 结束，JNI 总调用 %d 次\n", g_jni_calls);
}

'''

src = io.open(SRC, encoding='utf-8').read()

# 1) 全局 env 指针（JavaVM 回调要用）
src = src.replace('static int g_jni_calls = 0;',
                  'static int g_jni_calls = 0;\nstatic struct JNINativeInterface *g_env_ptr = NULL;')

# 2) 去掉旧的 GetJavaVM 桩
src = re.sub(r'static jint my_GetJavaVM\(JNIEnv \*e, JavaVM \*\*vm\)[^\n]*\n', '', src)

# 3) 插入 JavaVM 与调用链（放在 main 之前）
src = src.replace('int main(void) {', JAVA_VM + 'int main(void) {', 1)

# 4) 让 env 指针可全局访问 + 注册 JavaVM 表
src = src.replace(
    '    struct JNINativeInterface *envp = &iface;\n    JNIEnv *env = (JNIEnv *)&envp;',
    '    struct JNINativeInterface *envp = &iface;\n'
    '    g_env_ptr = &iface;\n'
    '    JNIEnv *env = (JNIEnv *)&envp;')
src = src.replace('    iface.GetJavaVM = my_GetJavaVM;',
                  '    iface.GetJavaVM = my_GetJavaVM;\n'
                  '    memset(&g_vm_iface, 0, sizeof(g_vm_iface));\n'
                  '    g_vm_iface.DestroyJavaVM = vm_DestroyJavaVM;\n'
                  '    g_vm_iface.AttachCurrentThread = vm_AttachCurrentThread;\n'
                  '    g_vm_iface.DetachCurrentThread = vm_DetachCurrentThread;\n'
                  '    g_vm_iface.GetEnv = vm_GetEnv;\n'
                  '    g_vm_iface.AttachCurrentThreadAsDaemon = vm_AttachCurrentThreadAsDaemon;')

# 5) 需要 unistd.h（sleep）与 stdlib.h（rand）
if '#include <unistd.h>' not in src:
    src = src.replace('#include <stdarg.h>', '#include <stdarg.h>\n#include <unistd.h>')

io.open(DST, 'w', encoding='utf-8', newline='\n').write(src)
print('已生成 harness3.c，行数', src.count('\n'))
