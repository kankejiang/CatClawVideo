// 迅雷引擎 harness v2：迷你 JNIEnv + 首次真调用
//
// v1 已证明：qemu-aarch64 能跑起 bionic aarch64 程序，且 libxl_thunder_sdk.so 能完整 dlopen。
// v2 要证明的是**第二个关口**：这个 native 层接不接受我们自己造的 JNIEnv。
//
// 它的 API 是「填充 Java 参数对象」风格：
//   public native int getDownloadLibVersion(GetDownloadLibVersion v)   // v.mVersion 由 native 回填
// ⇒ native 侧会调 GetObjectClass → GetFieldID → SetObjectField(obj, fid, NewStringUTF(...))
// 所以只要把这 4 个 JNI 函数实现对，就能拿到它回填的东西。
//
// 编译： aarch64-linux-android21-clang harness2.c -o harness2 -ldl
// 运行： qemu-aarch64 -L <android-rootfs> ./harness2

#include <jni.h>
#include <dlfcn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdarg.h>
#include <unistd.h>
#include <sys/mount.h>
#include <sys/stat.h>
#include <errno.h>
#include "jni_trap.h"   // 233 个槽位陷阱：谁被调用就打印自己的槽位名

// ↓↓↓ DNS 拦截层：bionic 在无 netd 环境里 getaddrinfo 必失败，必须自己实现 ↓↓↓
#include "dnshook.c"

// ═══════════ 极简 Java 对象模型 ═══════════
// 我们不需要真 JVM：参数对象只是「按字段名存值」的容器，
// jclass 用对象自身身份表示，jfieldID 用 {name,sig} 描述。

#define MAX_FIELDS 24
#define MAX_OBJS 32

typedef struct {
    char name[64];
    char sig[80];
    int kind;          // 0=int 1=long 2=object
    long i;
    void *o;
} JField;

typedef struct {
    char cls[96];
    JField f[MAX_FIELDS];
    int n;
} JObj;

/// jfieldID 的载体。⚠️ 必须按结构体访问（((JFieldId*)fid)->n），
/// 不能把首成员当指针解引用 —— 首成员是**内联字符数组**，不是 char*。
typedef struct {
    char n[64];
    char s[80];
} JFieldId;

static JObj g_objs[MAX_OBJS];
static int g_nobj = 0;
static int g_jni_calls = 0;
static struct JNINativeInterface *g_env_ptr = NULL;

// —— 数组辅助的前置声明（实现在后面的"字节数组 / 整数数组"节）——
typedef struct { jsize n; int *v; } JIntArray;
typedef struct { jsize n; unsigned char *b; } JByteArray;
static int is_int_array(void *p);
static int is_byte_array(void *p);
static JIntArray *new_int_array(jsize n);
static void *obj_get_oval(void *o, const char *name);

static jfieldID mk_fid(const char *n, const char *s) {
    JFieldId *f = (JFieldId *)calloc(1, sizeof(JFieldId));
    snprintf(f->n, sizeof(f->n), "%s", n ? n : "");
    snprintf(f->s, sizeof(f->s), "%s", s ? s : "");
    return (jfieldID)f;
}

static JObj *new_obj(const char *cls) {
    if (g_nobj >= MAX_OBJS) {           // 池满：复用最后一格，但必须清零（否则读到上次的脏字段）
        JObj *o = &g_objs[MAX_OBJS - 1];
        memset(o, 0, sizeof(*o));
        snprintf(o->cls, sizeof(o->cls), "%s", cls);
        return o;
    }
    JObj *o = &g_objs[g_nobj++];
    memset(o, 0, sizeof(*o));
    snprintf(o->cls, sizeof(o->cls), "%s", cls);
    return o;
}

static void *obj_get_oval(void *ov, const char *name) {
    JObj *o = (JObj *)ov;
    if (!o) return NULL;
    for (int i = 0; i < o->n; i++)
        if (!strcmp(o->f[i].name, name)) return o->f[i].o;
    return NULL;
}

static const char *obj_get_str(JObj *o, const char *name) {
    if (!o) return "(obj=null)";
    for (int i = 0; i < o->n; i++)
        if (!strcmp(o->f[i].name, name))
            return o->f[i].o ? (const char *)o->f[i].o : "(null)";
    return "(未回填)";
}

static long obj_get_long(JObj *o, const char *name) {
    if (!o) return -1;
    for (int i = 0; i < o->n; i++)
        if (!strcmp(o->f[i].name, name)) return o->f[i].i;
    return -1;
}

static int obj_get_int(JObj *o, const char *name) {
    return (int)obj_get_long(o, name);
}

// ═══════════ JNI 实现 ═══════════

static jstring my_NewStringUTF(JNIEnv *env, const char *utf) {
    (void)env; g_jni_calls++;
    char *s = strdup(utf ? utf : "");
    printf("  [jni] NewStringUTF(\"%s\")\n", s);
    return (jstring)s;
}

static const char *my_GetStringUTFChars(JNIEnv *env, jstring str, jboolean *isCopy) {
    (void)env; g_jni_calls++;
    if (isCopy) *isCopy = JNI_FALSE;
    return str ? (const char *)str : "";
}

static jclass my_GetObjectClass(JNIEnv *env, jobject obj) {
    (void)env; g_jni_calls++;
    return (jclass)obj;   // 用对象身份当 class 身份
}

static jfieldID my_GetFieldID(JNIEnv *env, jclass clazz, const char *name, const char *sig) {
    (void)env; (void)clazz; g_jni_calls++;
    JFieldId *fid = calloc(1, sizeof(JFieldId));
    snprintf(fid->n, sizeof(fid->n), "%s", name ? name : "");
    snprintf(fid->s, sizeof(fid->s), "%s", sig ? sig : "");
    printf("  [jni] GetFieldID(%s, %s)\n", fid->n, fid->s);
    return (jfieldID)fid;
}

static const char *fid_name(jfieldID fid) {
    return fid ? ((JFieldId *)fid)->n : "";
}

static void put_field(JObj *o, jfieldID fid, int kind, long iv, void *ov) {
    if (!o || !fid) return;
    const char *name = fid_name(fid);
    for (int i = 0; i < o->n; i++)
        if (!strcmp(o->f[i].name, name)) { o->f[i].kind = kind; o->f[i].i = iv; o->f[i].o = ov; return; }
    if (o->n >= MAX_FIELDS) return;
    JField *nf = &o->f[o->n++];
    snprintf(nf->name, sizeof(nf->name), "%s", name);
    nf->kind = kind; nf->i = iv; nf->o = ov;
}

static void dump_obj(const char *tag, JObj *o) {
    if (!o) { printf("  %s: (null)\n", tag); return; }
    printf("  %s <%s> 共 %d 个字段:\n", tag, o->cls, o->n);
    for (int i = 0; i < o->n; i++) {
        JField *f = &o->f[i];
        if (f->kind == 1)      printf("    %-26s = %ld\n", f->name, f->i);
        else if (f->kind == 0) printf("    %-26s = %d\n", f->name, (int)f->i);
        else                   printf("    %-26s = ptr@%p (kind=%d)\n", f->name, f->o, f->kind);
    }
    if (o->n == 0) printf("    （引擎一个字段都没填）\n");
}

static void my_SetObjectField(JNIEnv *env, jobject obj, jfieldID fid, jobject val) {
    (void)env; g_jni_calls++;
    put_field((JObj *)obj, fid, 2, 0, (void *)val);
}

static void my_SetIntField(JNIEnv *env, jobject obj, jfieldID fid, jint v) {
    (void)env; g_jni_calls++;
    put_field((JObj *)obj, fid, 0, v, NULL);
}

static void my_SetLongField(JNIEnv *env, jobject obj, jfieldID fid, jlong v) {
    (void)env; g_jni_calls++;
    put_field((JObj *)obj, fid, 1, (long)v, NULL);
}

static jint my_GetIntField(JNIEnv *env, jobject obj, jfieldID fid) {
    (void)env; g_jni_calls++;
    return (jint)obj_get_long((JObj *)obj, fid_name(fid));
}

static jlong my_GetLongField(JNIEnv *env, jobject obj, jfieldID fid) {
    (void)env; g_jni_calls++;
    return (jlong)obj_get_long((JObj *)obj, fid_name(fid));
}

static jobject my_GetObjectField(JNIEnv *env, jobject obj, jfieldID fid) {
    (void)env; g_jni_calls++;
    if (!obj || !fid) return NULL;
    const char *name = fid_name(fid);
    const char *sig  = ((JFieldId *)fid)->s;
    void *v = obj_get_oval(obj, name);
    if (sig && sig[0] == '[') {           // int[] 之类的数组字段
        if (!v) printf("  [jni] ⚠️ GetObjectField(%s%s) -> NULL（引擎会判 XL_PARAM_ERROR）\n", name, sig);
        return (jobject)v;
    }
    return (jobject)v;                    // 字符串字段：v 就是 NewStringUTF 出来的指针
}

// —— 兜底桩：宁可打印也不要 NULL 崩 ——
static jclass my_FindClass(JNIEnv *e, const char *n) { (void)e; printf("  [jni] FindClass(%s) -> NULL\n", n); g_jni_calls++; return NULL; }
static jmethodID my_GetMethodID(JNIEnv *e, jclass c, const char *n, const char *s) { (void)e; (void)c; printf("  [jni] GetMethodID(%s%s) -> NULL\n", n, s); g_jni_calls++; return NULL; }
static jmethodID my_GetStaticMethodID(JNIEnv *e, jclass c, const char *n, const char *s) { (void)e; (void)c; printf("  [jni] GetStaticMethodID(%s%s) -> NULL\n", n, s); g_jni_calls++; return NULL; }
static jobject my_CallObjectMethod(JNIEnv *e, jobject o, jmethodID m, ...) { (void)e; (void)o; (void)m; printf("  [jni] CallObjectMethod -> NULL\n"); g_jni_calls++; return NULL; }
static jobject my_CallStaticObjectMethod(JNIEnv *e, jclass c, jmethodID m, ...) { (void)e; (void)c; (void)m; printf("  [jni] CallStaticObjectMethod -> NULL\n"); g_jni_calls++; return NULL; }
static void my_CallVoidMethod(JNIEnv *e, jobject o, jmethodID m, ...) { (void)e; (void)o; (void)m; printf("  [jni] CallVoidMethod\n"); g_jni_calls++; }
static jboolean my_ExceptionCheck(JNIEnv *e) { (void)e; g_jni_calls++; return JNI_FALSE; }
static jthrowable my_ExceptionOccurred(JNIEnv *e) { (void)e; g_jni_calls++; return NULL; }
static void my_ExceptionClear(JNIEnv *e) { (void)e; g_jni_calls++; }
static void my_ExceptionDescribe(JNIEnv *e) { (void)e; g_jni_calls++; }
static jint my_ThrowNew(JNIEnv *e, jclass c, const char *m) { (void)e; (void)c; printf("  [jni] ThrowNew(%s)\n", m); g_jni_calls++; return 0; }
static void my_DeleteLocalRef(JNIEnv *e, jobject o) { (void)e; (void)o; g_jni_calls++; }
static jint my_GetVersion(JNIEnv *e) { (void)e; g_jni_calls++; return JNI_VERSION_1_6; }
static jsize my_GetArrayLength(JNIEnv *e, jarray a) {
    (void)e; g_jni_calls++;
    if (is_int_array(a))  return ((JIntArray *)a)->n;
    if (is_byte_array(a)) return ((JByteArray *)a)->n;
    return 0;
}
static jintArray my_NewIntArray(JNIEnv *e, jsize n) {
    (void)e; g_jni_calls++;
    JIntArray *a = new_int_array(n);
    printf("  [jni] NewIntArray(%d)\n", (int)n);
    return (jintArray)a;
}
static jlongArray my_NewLongArray(JNIEnv *e, jsize n) { (void)e; printf("  [jni] NewLongArray(%d)\n", (int)n); g_jni_calls++; return NULL; }
static void my_SetIntArrayRegion(JNIEnv *e, jintArray a, jsize s, jsize l, const jint *b) {
    (void)e; g_jni_calls++;
    if (!is_int_array(a) || !b) return;
    JIntArray *ia = (JIntArray *)a;
    for (jsize i = 0; i < l && s + i < ia->n; i++) ia->v[s + i] = b[i];
}
static void my_GetIntArrayRegion(JNIEnv *e, jintArray a, jsize s, jsize l, jint *b) {
    (void)e; g_jni_calls++;
    if (!is_int_array(a) || !b) return;
    JIntArray *ia = (JIntArray *)a;
    for (jsize i = 0; i < l && s + i < ia->n; i++) b[i] = ia->v[s + i];
}
static jint *my_GetIntArrayElements(JNIEnv *e, jintArray a, jboolean *c) {
    (void)e; g_jni_calls++;
    if (c) *c = JNI_FALSE;
    if (!is_int_array(a)) return NULL;
    return ((JIntArray *)a)->v;      // 直接返回我们自己那块内存，引擎改写即生效
}
static void my_ReleaseIntArrayElements(JNIEnv *e, jintArray a, jint *b, jint m) { (void)e; (void)a; (void)b; (void)m; g_jni_calls++; }

// ═══════════ 整数数组（引擎的状态回填全靠它）═══════════
// 教训：BtTaskStatus 的字段是 `int[] mStatus`（构造器里 new int[n]）。
// 引擎拿到对象后会 GetObjectField(obj,"mStatus","[I")，**拿到 NULL 就返回
// XL_PARAM_ERROR(9112)** —— 所以必须给它一个真实、可写的数组。
#define MAX_IA 16
static JIntArray g_ia[MAX_IA];
static int g_nia = 0;

static int is_int_array(void *p) {
    for (int i = 0; i < g_nia; i++) if ((void *)&g_ia[i] == p) return 1;
    return 0;
}

static JIntArray *new_int_array(jsize n) {
    if (g_nia >= MAX_IA) g_nia = MAX_IA - 1;
    JIntArray *a = &g_ia[g_nia++];
    a->n = n;
    a->v = (int *)calloc(1, (size_t)(n > 0 ? n : 1) * sizeof(int));
    return a;
}

// ═══════════ 字节数组 + NewObject ═══════════
// 实测 native 侧回填字符串的方式是**在 JNI 里 new String(byte[], "utf-8")**：
//   FindClass("java/lang/String") → GetMethodID("<init>","([BLjava/lang/String;)V")
//   → NewByteArray(n) → SetByteArrayRegion(...) → NewStringUTF("utf-8") → NewObject(...)
// 所以必须把字节数组和 NewObject 都实现掉，否则就在 NewObject 处 SIGSEGV。

#define MAX_BA 16
static JByteArray g_ba[MAX_BA];
static int g_nba = 0;

static int is_byte_array(void *p) {
    for (int i = 0; i < g_nba; i++) if ((void *)&g_ba[i] == p) return 1;
    return 0;
}

static jbyteArray my_NewByteArray(JNIEnv *env, jsize n) {
    (void)env; g_jni_calls++;
    if (g_nba >= MAX_BA) g_nba = MAX_BA - 1;
    JByteArray *a = &g_ba[g_nba++];
    a->n = n;
    a->b = (unsigned char *)calloc(1, n > 0 ? (size_t)n : 1);
    printf("  [jni] NewByteArray(%d)\n", n);
    return (jbyteArray)a;
}

static void my_SetByteArrayRegion(JNIEnv *env, jbyteArray arr, jsize start, jsize len, const jbyte *buf) {
    (void)env; g_jni_calls++;
    JByteArray *a = (JByteArray *)arr;
    if (a && a->b && buf && start >= 0 && start + len <= a->n) memcpy(a->b + start, buf, (size_t)len);
    printf("  [jni] SetByteArrayRegion(start=%d, len=%d)\n", (int)start, (int)len);
}

static jbyte *my_GetByteArrayElements(JNIEnv *env, jbyteArray arr, jboolean *isCopy) {
    (void)env; g_jni_calls++;
    if (isCopy) *isCopy = JNI_FALSE;
    JByteArray *a = (JByteArray *)arr;
    return a ? (jbyte *)a->b : NULL;
}

static void my_ReleaseByteArrayElements(JNIEnv *env, jbyteArray arr, jbyte *elems, jint mode) {
    (void)env; (void)arr; (void)elems; (void)mode; g_jni_calls++;
}

/// 共用：从 va_list 里取出 (byte[], String) 并拼成 C 字符串。
/// 实测 native 侧走的是 **NewObjectV**（va_list 变体），不是 NewObject ——
/// 这正是靠槽位陷阱表定位出来的（之前 NewObject 一直没被调用就一直查不出原因）。
static jobject new_object_from_valist(jmethodID mid, va_list ap) {
    void *a1 = va_arg(ap, void *);
    void *a2 = va_arg(ap, void *);
    printf("  [jni] NewObjectV/NewObject(a1=%p, a2=%p)\n", a1, a2);

    JByteArray *ba = is_byte_array(a1) ? (JByteArray *)a1
                   : is_byte_array(a2) ? (JByteArray *)a2 : NULL;
    if (ba && ba->b) {
        char *s = (char *)malloc((size_t)ba->n + 1);
        memcpy(s, ba->b, (size_t)ba->n);
        s[ba->n] = 0;
        printf("  [jni] ★ 构造出的字符串 = \"%s\"\n", s);
        return (jobject)s;
    }
    printf("  [jni] NewObject 参数里没有已知字节数组\n");
    return NULL;
}

static jobject my_NewObjectV(JNIEnv *env, jclass clazz, jmethodID mid, va_list ap) {
    (void)env; (void)clazz; g_jni_calls++;
    return new_object_from_valist(mid, ap);
}

/// 只处理目前实测遇到的 String(byte[], String) 构造；其余打印参数便于继续补。
static jobject my_NewObject(JNIEnv *env, jclass clazz, jmethodID mid, ...) {
    (void)env; (void)clazz; g_jni_calls++;
    va_list ap;
    va_start(ap, mid);
    jobject r = new_object_from_valist(mid, ap);
    va_end(ap);
    return r;
}

static jobject my_NewObjectA(JNIEnv *env, jclass clazz, jmethodID mid, const jvalue *args) {
    (void)env; (void)clazz; (void)mid; g_jni_calls++;
    JByteArray *ba = args ? (JByteArray *)args[0].l : NULL;
    if (ba && ba->b) {
        char *s = (char *)malloc((size_t)ba->n + 1);
        memcpy(s, ba->b, (size_t)ba->n);
        s[ba->n] = 0;
        printf("  [jni] NewObjectA(String) = \"%s\"\n", s);
        return (jobject)s;
    }
    return NULL;
}


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
    typedef jint (*fn_long1)(JNIEnv *, jobject, jlong);
    typedef jint (*fn_long3)(JNIEnv *, jobject, jlong, jint, jint);
    typedef jint (*fn_taskinfo)(JNIEnv *, jobject, jlong, jint, jobject);
    typedef jint (*fn_bttask)(JNIEnv *, jobject, jstring, jstring, jint, jint, jint, jobject);

    fn_init init = (fn_init)dlsym(sdk, "Java_com_xunlei_downloadlib_XLLoader_init");
    fn_magnet createMagnet =
        (fn_magnet)dlsym(sdk, "Java_com_xunlei_downloadlib_XLLoader_createBtMagnetTask");
    fn_status status =
        (fn_status)dlsym(sdk, "Java_com_xunlei_downloadlib_XLLoader_getBtSubTaskStatus");
    fn_local localUrl =
        (fn_local)dlsym(sdk, "Java_com_xunlei_downloadlib_XLLoader_getLocalUrl");
    fn_uni unInit = (fn_uni)dlsym(sdk, "Java_com_xunlei_downloadlib_XLLoader_unInit");
    // ★ XLTaskHelper.addMagentTask 的真正流程里，createBtMagnetTask 返回 9000 之后
    //   还要 startTask + setTaskGsState —— 少了这两步，任务只是被"登记"而从不运行。
    fn_long1 startTask = (fn_long1)dlsym(sdk, "Java_com_xunlei_downloadlib_XLLoader_startTask");
    fn_long3 gsState   = (fn_long3)dlsym(sdk, "Java_com_xunlei_downloadlib_XLLoader_setTaskGsState");
    fn_taskinfo getTaskInfo =
        (fn_taskinfo)dlsym(sdk, "Java_com_xunlei_downloadlib_XLLoader_getTaskInfo");
    // ★ 阶段 B 用：迅雷 SDK 自己就是这么做的（XLTaskHelper.addTorrentTask）
    //   原生签名参数顺序由 XLDownloadManager.createBtTask 的字节码确认：
    //   (mTorrentPath, mFilePath, mMaxConcurrent, mCreateMode, mSeqId, GetTaskId)
    fn_bttask createBtTask =
        (fn_bttask)dlsym(sdk, "Java_com_xunlei_downloadlib_XLLoader_createBtTask");
    printf("[chain]   startTask=%p setTaskGsState=%p\n", (void *)startTask, (void *)gsState);
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
                   9,    // ★ networkType —— 必须落在 XLUtil.getNetworkType() 的取值域里！
                         //   从字节码实测：Context 无效→0；NetworkInfo.TYPE_WIFI(1)→**9**；
                         //   移动网络→5 或按 subtype。先前传的 1 是**枚举外的值**，引擎据此
                         //   认为网络类型未知/受限，于是**完全不做 BT hub 查询** ——
                         //   现象正是「域名解析了、但一个包都不发，直接 114004
                         //   TASK_FAILURE_QUERY_BT_HUB_FAILED」。
                   1,    // permissionLevel
                   0);   // queryConfOnInit
    printf("[chain] ← init 返回 %d（0=成功）\n", (int)rc);

    // XLTaskHelper.init 在 init 之后还做了三件事，一并补上
    typedef jint (*fn_bool1)(JNIEnv *, jobject, jboolean);
    typedef jint (*fn_str1)(JNIEnv *, jobject, jstring);
    typedef jint (*fn_ll)(JNIEnv *, jobject, jlong, jlong);
    fn_bool1 statSw = (fn_bool1)dlsym(sdk, "Java_com_xunlei_downloadlib_XLLoader_setStatReportSwitch");
    fn_str1  osVer  = (fn_str1)dlsym(sdk, "Java_com_xunlei_downloadlib_XLLoader_setOSVersion");
    fn_ll    spdLim = (fn_ll)dlsym(sdk, "Java_com_xunlei_downloadlib_XLLoader_setSpeedLimit");
    if (statSw) printf("[chain]   setStatReportSwitch(false) → %d\n", (int)statSw(env, (jobject)thiz, JNI_FALSE));
    if (osVer)  printf("[chain]   setOSVersion(\"30_alpha\") → %d\n", (int)osVer(env, (jobject)thiz, (jstring)"30_alpha"));
    // ⚠️ X* 系列 C API（XLSetReleaseLog / XLIsLogTurnOn）在 JNI 初始化路径下调用会崩
    //    （libc: Fatal signal 11 SEGV_ACCERR），不要再调；要开日志请改走 slog 配置文件。
    if (spdLim) printf("[chain]   setSpeedLimit(-1,-1) → %d\n", (int)spdLim(env, (jobject)thiz, (jlong)-1, (jlong)-1));

    printf("[chain] ── createBtMagnetTask ──\n");
    const char *magnet = getenv("MAGNET");
    if (!magnet) magnet = "magnet:?xt=urn:btih:1363FB911E8603FDE757C9B02979D508DE2D195B";
    printf("[chain]   magnet = %s\n", magnet);
    JObj *taskId = new_obj("com/xunlei/downloadlib/parameter/GetTaskId");
    jint mrc = createMagnet(env, (jobject)thiz, (jstring)magnet, (jstring)EMU_SAVE_PATH,
                            (jstring)"cc-test", (jobject)taskId);
    long id = obj_get_long(taskId, "mTaskId");
    printf("[chain] ← createBtMagnetTask 返回 %d（9000=XL_NO_ERRNO 成功），taskId = %ld\n", (int)mrc, id);

    // 任务建好但还没跑 —— 必须显式 startTask（并设 GsState），否则永远是"未开始"
    if (id > 0 && startTask) {
        printf("[chain]   startTask(%ld) → %d\n", id, (int)startTask(env, (jobject)thiz, (jlong)id));
        if (gsState)
            printf("[chain]   setTaskGsState(%ld,0,2) → %d\n", id,
                   (int)gsState(env, (jobject)thiz, (jlong)id, 0, 2));
    }

    if (id > 0 && status) {
        JObj *st = new_obj("com/xunlei/downloadlib/parameter/BtTaskStatus");
        // 预置 int[] mStatus：引擎会往这个数组里写状态
        JIntArray *ia = new_int_array(4);
        put_field(st, mk_fid("mStatus", "[I"), 3, 0, ia);
        // ★ 进度监控：每 5 秒读一次引擎的真实进度（状态/大小/各路速度）
        int secs = getenv("MON_SECS") ? atoi(getenv("MON_SECS")) : 30;
        int rounds = secs / 5;
        for (int i = 0; i <= rounds; i++) {
            JObj *ti = new_obj("com/xunlei/downloadlib/parameter/XLTaskInfo");
            jint ir = getTaskInfo ? getTaskInfo(env, (jobject)thiz, (jlong)id, 0, (jobject)ti) : -1;
            printf("[mon] t=%3ds info=%d st=%d err=%d 已下载=%ld/%ld 速度=%ld  P2S=%ld P2P=%ld 慢速源=%ld\n",
                   i * 5, (int)ir,
                   obj_get_int(ti, "mTaskStatus"), obj_get_int(ti, "mErrorCode"),
                   obj_get_long(ti, "mDownloadSize"), obj_get_long(ti, "mFileSize"),
                   obj_get_long(ti, "mDownloadSpeed"),
                   obj_get_long(ti, "mP2SSpeed"), obj_get_long(ti, "mP2PSpeed"),
                   obj_get_long(ti, "mAdditionalResPeerBytes"));
            if (i % 6 == 0) {
                jint s = status(env, (jobject)thiz, (jlong)id, (jobject)st, 0, 0);
                printf("[mon]   子任务状态=%d → mStatus=[%d,%d,%d,%d]\n", (int)s,
                       ia->v[0], ia->v[1], ia->v[2], ia->v[3]);
            }
            sleep(5);
        }
    }

    // ══ 阶段 B：用刚下到的 .torrent 建真正的 BT 下载任务 ══
    // 迅雷 SDK 的分工：createBtMagnetTask 只负责「把 magnet 解析成 .torrent」（会落盘），
    // 真正下载内容要再用 createBtTask(BtTaskParam{torrentPath, filePath, createMode=1,
    // maxConcurrent, seqId}, GetTaskId)，然后 startTask。
    if (createBtTask && id > 0) {
        const char *tp = getenv("TORRENT_PATH") ? getenv("TORRENT_PATH") : "/thunder-data/cc-test";
        printf("[chain] ══ 阶段 B：createBtTask ──\n");
        printf("[chain]   torrentPath = %s\n", tp);
        JObj *tid2 = new_obj("com/xunlei/downloadlib/parameter/GetTaskId");
        jint r2 = createBtTask(env, (jobject)thiz, (jstring)tp, (jstring)EMU_SAVE_PATH,
                               3,   // maxConcurrent（SDK 用 3）
                               1,   // createMode（SDK 用 1）
                               1,   // seqId
                               (jobject)tid2);
        long id2 = obj_get_long(tid2, "mTaskId");
        printf("[chain] ← createBtTask 返回 %d，任务 ID = %ld\n", (int)r2, id2);
        if (id2 > 0) {
            // ★ 引擎导出的 C API：允许使用（P2P/P2SP）资源 + 从"仅原始地址"切到"全部资源下载"
            //   名字上看这正是 BT/磁力任务能拿到 peer 资源的前提。
            {
                typedef int (*fn_allow)(long long, int);
                typedef int (*fn_sw)(long long);
                fn_allow allowRes = (fn_allow)dlsym(sdk, "XLSetTaskAllowUseResource");
                fn_sw   switchRes = (fn_sw)dlsym(sdk, "XLSwitchOriginToAllResDownload");
                if (allowRes) printf("[bt]   XLSetTaskAllowUseResource(%ld,1) → %d\n", id2, allowRes((long long)id2, 1));
                if (switchRes) printf("[bt]   XLSwitchOriginToAllResDownload(%ld) → %d\n", id2, switchRes((long long)id2));
            }
            // ★ 选中要下载的文件（BtIndexSet.mIndexSet = int[]，构造器里分配）
            //   SDK 的 addTorrentTask 里是反选（deselectBtSubTask）不需要的文件，
            //   说明默认全选；但 createBtTask 路径下未必 —— 显式选中 index 0 试试。
            {
                typedef jint (*fn_sel)(JNIEnv *, jobject, jlong, jobject);
                fn_sel selSub = (fn_sel)dlsym(sdk, "Java_com_xunlei_downloadlib_XLLoader_selectBtSubTask");
                if (selSub) {
                    JObj *iset = new_obj("com/xunlei/downloadlib/parameter/BtIndexSet");
                    JIntArray *ia2 = new_int_array(1);
                    ia2->v[0] = 0;
                    put_field(iset, mk_fid("mIndexSet", "[I"), 3, 0, ia2);
                    printf("[bt]   selectBtSubTask(%ld, {0}) → %d\n", id2,
                           (int)selSub(env, (jobject)thiz, (jlong)id2, (jobject)iset));
                } else printf("[bt]   找不到 selectBtSubTask\n");
            }
            if (startTask)
                printf("[chain]   startTask(%ld) → %d\n", id2, (int)startTask(env, (jobject)thiz, (jlong)id2));
            if (gsState)
                printf("[chain]   setTaskGsState(%ld,0,2) → %d\n", id2,
                       (int)gsState(env, (jobject)thiz, (jlong)id2, 0, 2));
            int dsecs = getenv("DL_SECS") ? atoi(getenv("DL_SECS")) : 150;
            // 头 20 秒先观察：如果任务直接判失败(114004)，就试引擎导出的两个补救 API
            for (int i = 0; i <= dsecs / 5; i++) {
                JObj *ti = new_obj("com/xunlei/downloadlib/parameter/XLTaskInfo");
                jint ir = getTaskInfo ? getTaskInfo(env, (jobject)thiz, (jlong)id2, 0, (jobject)ti) : -1;
                if (i == 4 && obj_get_int(ti, "mTaskStatus") == 3) {
                    typedef int (*fn_req)(long long);
                    typedef int (*fn_pref)(long long);
                    fn_req  requery  = (fn_req)dlsym(sdk, "XLRequeryIndex");
                    fn_pref prefetch = (fn_pref)dlsym(sdk, "XLEnterPrefetchMode");
                    printf("[bt]   任务失败(err=%d)，试补救：\n", obj_get_int(ti, "mErrorCode"));
                    if (requery)  printf("[bt]     XLRequeryIndex(%ld) → %d\n", id2, requery((long long)id2));
                    if (prefetch) printf("[bt]     XLEnterPrefetchMode(%ld) → %d\n", id2, prefetch((long long)id2));
                }
                printf("[dl] t=%3ds info=%d st=%d err=%d 已下载=%ld/%ld 速度=%ld  P2S=%ld P2P=%ld  P2P源=%ld\n",
                       i * 5, (int)ir,
                       obj_get_int(ti, "mTaskStatus"), obj_get_int(ti, "mErrorCode"),
                       obj_get_long(ti, "mDownloadSize"), obj_get_long(ti, "mFileSize"),
                       obj_get_long(ti, "mDownloadSpeed"),
                       obj_get_long(ti, "mP2SSpeed"), obj_get_long(ti, "mP2PSpeed"),
                       obj_get_long(ti, "mP2PRecvBytes"));
                sleep(5);
            }
        }
    }

    if (localUrl) {
        const char *fn = getenv("FILENAME") ? getenv("FILENAME") : "cc-test";
        JObj *lu = new_obj("com/xunlei/downloadlib/parameter/XLTaskLocalUrl");
        jint lrc = localUrl(env, (jobject)thiz, (jstring)fn, (jobject)lu);
        printf("[chain] ← getLocalUrl(\"%s\") 返回 %d，mStrUrl = %s\n", fn, (int)lrc,
               obj_get_str(lu, "mStrUrl"));
    }

    if (unInit) unInit(env, (jobject)thiz);
    printf("[chain] 结束，JNI 总调用 %d 次\n", g_jni_calls);
}

int main(void) {
    setvbuf(stdout, NULL, _IONBF, 0);   // 关缓冲：崩溃前也要能看到日志
    printf("[v2] ===== 迷你 JNIEnv + 首次真调用 =====\n");
    {   // DNS 自检：确认拦截层接管了 getaddrinfo
        struct addrinfo hints, *r = NULL;
        memset(&hints, 0, sizeof(hints));
        hints.ai_family = AF_INET;
        hints.ai_socktype = SOCK_STREAM;
        const char *probe = getenv("DNS_PROBE") ? getenv("DNS_PROBE") : "www.baidu.com";
        int rc = getaddrinfo(probe, "80", &hints, &r);
        printf("[v2] DNS 自检 %s → rc=%d%s\n", probe, rc, rc == 0 ? " ✅" : " ✗");
    }

    static struct JNINativeInterface iface;
    memset(&iface, 0, sizeof(iface));
    iface.GetVersion = my_GetVersion;
    iface.FindClass = my_FindClass;
    iface.ExceptionOccurred = my_ExceptionOccurred;
    iface.ExceptionDescribe = my_ExceptionDescribe;
    iface.ExceptionClear = my_ExceptionClear;
    iface.ThrowNew = my_ThrowNew;
    iface.GetObjectClass = my_GetObjectClass;
    iface.GetFieldID = my_GetFieldID;
    iface.GetObjectField = my_GetObjectField;
    iface.GetIntField = my_GetIntField;
    iface.GetLongField = my_GetLongField;
    iface.SetObjectField = my_SetObjectField;
    iface.SetIntField = my_SetIntField;
    iface.SetLongField = my_SetLongField;
    iface.GetMethodID = my_GetMethodID;
    iface.GetStaticMethodID = my_GetStaticMethodID;
    iface.CallObjectMethod = my_CallObjectMethod;
    iface.CallStaticObjectMethod = my_CallStaticObjectMethod;
    iface.CallVoidMethod = my_CallVoidMethod;
    iface.GetStringUTFChars = my_GetStringUTFChars;
    iface.ReleaseStringUTFChars = (void *)my_DeleteLocalRef;
    iface.NewStringUTF = my_NewStringUTF;
    iface.GetArrayLength = my_GetArrayLength;
    iface.NewIntArray = my_NewIntArray;
    iface.NewLongArray = my_NewLongArray;
    iface.NewByteArray = my_NewByteArray;
    iface.SetByteArrayRegion = my_SetByteArrayRegion;
    iface.GetByteArrayElements = my_GetByteArrayElements;
    iface.ReleaseByteArrayElements = my_ReleaseByteArrayElements;
    iface.NewObject = my_NewObject;
    iface.NewObjectV = my_NewObjectV;
    iface.NewObjectA = my_NewObjectA;
    iface.SetIntArrayRegion = my_SetIntArrayRegion;
    iface.GetIntArrayElements = my_GetIntArrayElements;
    iface.ReleaseIntArrayElements = my_ReleaseIntArrayElements;
    iface.DeleteLocalRef = my_DeleteLocalRef;
    iface.ExceptionCheck = my_ExceptionCheck;
    iface.GetJavaVM = my_GetJavaVM;
    memset(&g_vm_iface, 0, sizeof(g_vm_iface));
    g_vm_iface.DestroyJavaVM = vm_DestroyJavaVM;
    g_vm_iface.AttachCurrentThread = vm_AttachCurrentThread;
    g_vm_iface.DetachCurrentThread = vm_DetachCurrentThread;
    g_vm_iface.GetEnv = vm_GetEnv;
    g_vm_iface.AttachCurrentThreadAsDaemon = vm_AttachCurrentThreadAsDaemon;

    // 剩余未实现的槽位全部装陷阱：被 native 调用时能打印槽位号，而不是裸 NULL 跳转崩溃
    {
        void **tbl = (void **)&iface;
        int trapped = 0;
        for (int i = 0; i < JNI_SLOT_COUNT; i++)
            if (!tbl[i]) { tbl[i] = JNI_TRAPS[i]; trapped++; }
        printf("[v2] JNI 槽位：已实现 %d 个，装陷阱 %d 个\n", JNI_SLOT_COUNT - trapped, trapped);
    }

    struct JNINativeInterface *envp = &iface;
    g_env_ptr = &iface;
    JNIEnv *env = (JNIEnv *)&envp;

    void *sdk = dlopen("libxl_thunder_sdk.so", RTLD_NOW);
    if (!sdk) { printf("[v2] dlopen 失败: %s\n", dlerror()); return 1; }
    printf("[v2] 引擎已加载\n");

    // ① getDownloadLibVersion(GetDownloadLibVersion v) —— 取版本号
    typedef jint (*fn_ver)(JNIEnv *, jobject, jobject);
    fn_ver getVer = (fn_ver)dlsym(sdk, "Java_com_xunlei_downloadlib_XLLoader_getDownloadLibVersion");
    if (!getVer) { printf("[v2] 取 getDownloadLibVersion 失败\n"); return 1; }

    JObj *thiz = new_obj("com/xunlei/downloadlib/XLLoader");
    JObj *arg = new_obj("com/xunlei/downloadlib/parameter/GetDownloadLibVersion");

    printf("[v2] → 调用 getDownloadLibVersion()\n");
    jint rc = getVer(env, (jobject)thiz, (jobject)arg);
    printf("[v2] ← 返回 %d ；JNI 调用 %d 次\n", rc, g_jni_calls);
    printf("[v2]    mVersion = %s\n", obj_get_str(arg, "mVersion"));

    // ② XYVodSDK_getVersion() —— 直接返回 String，不需要参数对象
    typedef jstring (*fn_ver2)(JNIEnv *, jobject);
    fn_ver2 getVer2 = (fn_ver2)dlsym(sdk, "Java_com_xunlei_downloadlib_XLLoader_XYVodSDK_1getVersion");
    if (getVer2) {
        printf("[v2] → 调用 XYVodSDK_getVersion()\n");
        jstring s = getVer2(env, (jobject)thiz);
        printf("[v2] ← %s\n", s ? (const char *)s : "(null)");
    }

    // ── 当 initramfs 的 /init 用时：先把伪文件系统挂起来 ──
    // 引擎会读 /proc（自检、线程信息等），不挂会出现奇怪的行为差异。
    mkdir("/proc", 0555);
    mkdir("/dev", 0755);
    mkdir("/tmp", 0777);
    mkdir("/thunder-data", 0777);
    if (mount("proc", "/proc", "proc", 0, NULL) != 0)
        printf("[init] mount /proc 失败 errno=%d（不致命）\n", errno);
    else
        printf("[init] /proc 已挂载\n");
    mount("devtmpfs", "/dev", "devtmpfs", 0, NULL);   // 失败不致命

    printf("[init] ===== 开始完整调用链 =====\n");
    run_full_chain(env, sdk);
    fflush(stdout);

    printf("[v2] ===== 结束，JNI 总调用 %d 次 =====\n", g_jni_calls);
    // 容器/VM 里作为 init：不要退出（退出会 kernel panic）。等一会儿再重启 shell。
    for (;;) sleep(3600);
    return 0;
}
