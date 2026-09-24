// ═══════════════════════════════════════════════════════════════════════
//  Guard 模块：ftyguard*.so 的 QEMU 承载（2026-09-24，用户拍板架构）
//
//  壳框架（BaseSpiderGuard）的解密/签名/proxyInvoke 全部是 ARM native（so 里），
//  桥进程（x64 JVM）跑不了。本模块在 QEMU guest 里：
//    · 经宿主 /res 下载 raw jar 里的 guard so → dlopen → 调 JNI_OnLoad，
//      拦截 RegisterNatives 捕获 DexNative 的全部 native 函数指针；
//    · 跑 getLoader 预热（解壳同款序列，decrypt 依赖它的内部初始化状态）；
//    · 在 0.0.0.0:<GUARD_PORT> 起行式 TCP 服务（宿主经 hostfwd 直连，桥进程是客户端）：
//        DECRYPT/ENCRYPT/MD5/NOXSIGN/CALC/PROXY  →  OK <b64> / OK3 <st> <mime> <body> / ERR <msg>
//    · so 运行中反向调 JNI（AlertDialog/Bitmap/SharedPreferences/File/流…）由本文件的
//      富 JNI 桩承接：对话框/二维码序列化成 JSON 经 /report?ev=ui 上行宿主渲染，
//      用户操作经控制口 UIR 命令下发 → 本模块回调 so 注册的 native listener。
//
//  线程模型：所有 so 调用固定在 guard 线程（so 内部状态可能线程相关）；
//  主循环（ctrlloop）只把 GLOAD/UIR 命令置标志 + 写唤醒管道。
//  ⚠ 本文件被 harness4.c #include（依赖其 JObj/JField/数组/tcp 工具/g_env_ptr 等），
//    必须在 ctrlloop.c 之后（用到 g_ctrl_port）。
// ═══════════════════════════════════════════════════════════════════════

#include <sys/socket.h>
#include <arpa/inet.h>
#include <setjmp.h>
#include <signal.h>

#define GUARD_MAGIC 0x47554152UL   // "GUAR"
#define GID_MAGIC   0x474D4944UL   // "GMID"

// ── Guard 对象模型：全指针带标签（string/class/obj 统一 GObj），可安全判型 ──
typedef struct GObj {
    unsigned long magic;
    int kind;                  // 1=string 2=object 3=class
    char cls[96];
    JField f[MAX_FIELDS];
    int n;
} GObj;

// 方法/字段 ID 载体（带 magic 防野指针）
typedef struct { unsigned long magic; char cls[96]; char name[64]; char sig[80]; } GId;

// RegisterNatives 捕获表
#define MAX_NATIVES 64
typedef struct { char cls[96]; char name[64]; char sig[80]; void *fn; } GNative;
static GNative g_gnat[MAX_NATIVES];
static int g_gnatn = 0;

// 类池（jclass = GObj kind=3，按名 intern）
#define MAX_GCLASS 48
static GObj g_gcls[MAX_GCLASS];
static int g_ngcls = 0;

#define MAX_GOBJ 128
static GObj g_gobjs[MAX_GOBJ];
static int g_ngobj = 0;

// 首选项（桥推送的登录态 Cookie 等；so 经 JNI 读）
#define MAX_GPREFS 48
static struct { char key[64]; char val[512]; int used; } g_gprefs[MAX_GPREFS];

static JavaVM *g_guard_vm = NULL;     // 复用 harness4 的迷你 JavaVM
static struct JNINativeInterface *g_guard_iface_ptr = NULL;
static JNIEnv g_guard_cell = NULL;    // 值 = &iface（JNIEnv 在 C 模式下本身是指针类型）
static JNIEnv *g_guard_env = NULL;    // 传给 native 的指针（指向 g_guard_cell）
static void *g_guard_so = NULL;
static char g_guard_jar[80] = "";     // 资源解析用的 jar hash（/res?jar=<h>）
static int g_guard_ready = 0;
static int g_guard_port = 0;
static int g_gwake[2] = {-1, -1};     // 主循环 → guard 线程唤醒管道

// 主循环下发的命令（guard 线程消费）
static volatile int g_gload_req = 0;
static char g_gload_jar[80] = "";
static volatile int g_uir_flag = 0;
static volatile int g_uir_seq = 0, g_uir_which = 0;

// 对话框注册表（UIR 回调寻址）
#define MAX_GDIALOG 8
typedef struct {
    int seq, active;
    GObj *dialog;
    char title[256], message[512];
    char items[12][192]; int nitems;
    GObj *itemsL, *posL, *negL, *neuL, *cancelL, *dismissL;
    GObj *qr;
    char posText[64], negText[64], neuText[64];
} GDialog;
static GDialog g_gdialog[MAX_GDIALOG];
static int g_gseq = 1;

// ── SIGSEGV 守门：so 野调用不让整个 guest 陪葬 ──
static sigjmp_buf g_gjmp;
static volatile int g_gguard = 0;
static unsigned long g_guard_base = 0;   // guard so 基址（崩溃点归一化用）
static void g_segv_handler(int sig, siginfo_t *si, void *ucv) {
    ucontext_t *uc = (ucontext_t *)ucv;
    if (g_gguard) {
        unsigned long pc = (unsigned long)uc->uc_mcontext.pc;
        printf("[guard] ⚠ SIGSEGV/BUS sig=%d addr=%p PC=0x%lx（so 内偏移 0x%lx）LR=0x%lx\n",
               sig, si ? si->si_addr : NULL, pc,
               g_guard_base ? pc - g_guard_base : pc,
               (unsigned long)uc->uc_mcontext.regs[30]);
        for (int i = 0; i < 8; i++)
            printf("[guard]    x%d=0x%lx\n", i, (unsigned long)uc->uc_mcontext.regs[i]);
        printf("[guard] ⚠（已隔离）\n");
        siglongjmp(g_gjmp, 1);
    }
    _exit(139);
}

// so 调用统一包 SIGSEGV 守门
#define GUARD_PROTECT_CALL(body) do { \
    struct sigaction sa, old; memset(&sa, 0, sizeof sa); \
    sa.sa_sigaction = g_segv_handler; sa.sa_flags = SA_SIGINFO | SA_NODEFER; \
    sigaction(SIGSEGV, &sa, &old); sigaction(SIGBUS, &sa, &old); \
    g_gguard = 1; int gbad = sigsetjmp(g_gjmp, 1); \
    if (!gbad) { body; } \
    else printf("[guard] ⚠ so 调用 SIGSEGV/BUS（已隔离）\n"); \
    g_gguard = 0; sigaction(SIGSEGV, &old, NULL); sigaction(SIGBUS, &old, NULL); \
} while (0)

// ═══════════ 基础工具 ═══════════

static int is_gobj(void *p) {
    return p && ((GObj *)p)->magic == GUARD_MAGIC && ((GObj *)p)->kind >= 1 && ((GObj *)p)->kind <= 3;
}
static int is_gid(void *p) { return p && ((GId *)p)->magic == GID_MAGIC; }
static GObj *g_ctx_stub(void);   // 前置声明（定义在 op 区）
static GObj *g_ctx_from_init = NULL;   // InitOrigin.init(Context) 记录的上下文

static GObj *gobj_new(const char *cls, int kind) {
    GObj *o;
    if (g_ngobj < MAX_GOBJ) {
        o = &g_gobjs[g_ngobj++];
        memset(o, 0, sizeof *o);
    } else {
        // ⚠ 池满后绝不能 memset 复用：so 手里的旧引用会被覆盖成新对象（实测 DexClassLoader
        //   桩被字符串覆盖 → loadClass 分派失效）。改为堆分配，泄漏量可控（VM 内 tmpfs）。
        o = (GObj *)calloc(1, sizeof(GObj));
        if (!o) { printf("[gcall] ⚠ 对象堆分配失败\n"); o = &g_gobjs[0]; memset(o, 0, sizeof *o); }
    }
    o->magic = GUARD_MAGIC; o->kind = kind;
    snprintf(o->cls, sizeof o->cls, "%s", cls ? cls : "java/lang/Object");
    return o;
}

static void gput_field(GObj *o, const char *name, int kind, long iv, void *ov) {
    if (!o || !name) return;
    for (int i = 0; i < o->n; i++)
        if (!strcmp(o->f[i].name, name)) { o->f[i].kind = kind; o->f[i].i = iv; o->f[i].o = ov; return; }
    if (o->n >= MAX_FIELDS) { printf("[gcall] ⚠ 字段池满 %s\n", name); return; }
    JField *nf = &o->f[o->n++];
    snprintf(nf->name, sizeof nf->name, "%s", name);
    nf->kind = kind; nf->i = iv; nf->o = ov;
}
static void *gget_optr(GObj *o, const char *name) {
    if (!o || !name) return NULL;
    for (int i = 0; i < o->n; i++)
        if (!strcmp(o->f[i].name, name)) return o->f[i].o;
    return NULL;
}
static long gget_iv(GObj *o, const char *name) {
    if (!o || !name) return 0;
    for (int i = 0; i < o->n; i++)
        if (!strcmp(o->f[i].name, name)) return o->f[i].i;
    return 0;
}

static GObj *gstr(const char *s) {
    GObj *o = gobj_new("java/lang/String", 1);
    gput_field(o, "value", 2, 0, strdup(s ? s : ""));
    return o;
}
static const char *gstr_chars(jstring js) {
    if (!js) return "";
    if (is_gobj(js)) {
        GObj *o = (GObj *)js;
        if (o->kind == 1) { const char *v = (const char *)gget_optr(o, "value"); return v ? v : ""; }
        return "";
    }
    return (const char *)js;   // 兼容裸 char*
}

static GObj *gclass_intern(const char *name) {
    for (int i = 0; i < g_ngcls; i++)
        if (!strcmp(g_gcls[i].cls, name)) return &g_gcls[i];
    if (g_ngcls >= MAX_GCLASS) return &g_gcls[0];
    GObj *c = &g_gcls[g_ngcls++];
    memset(c, 0, sizeof *c);
    c->magic = GUARD_MAGIC; c->kind = 3;
    snprintf(c->cls, sizeof c->cls, "%s", name ? name : "?");
    return c;
}

static GId *gmid_new(const char *cls, const char *name, const char *sig) {
    GId *m = (GId *)calloc(1, sizeof(GId));
    m->magic = GID_MAGIC;
    snprintf(m->cls, sizeof m->cls, "%s", cls ? cls : "");
    snprintf(m->name, sizeof m->name, "%s", name ? name : "");
    snprintf(m->sig, sizeof m->sig, "%s", sig ? sig : "");
    return m;
}

static GNative *gnat_find_any(const char *name) {
    for (int i = 0; i < g_gnatn; i++)
        if (!strcmp(g_gnat[i].name, name)) return &g_gnat[i];
    return NULL;
}
static GNative *gnat_match(const char *cls, const char *name, const char *sig) {
    for (int i = 0; i < g_gnatn; i++)
        if (!strcmp(g_gnat[i].name, name) && !strcmp(g_gnat[i].sig, sig) &&
            (cls[0] == 0 || !strcmp(g_gnat[i].cls, cls)))
            return &g_gnat[i];
    return NULL;
}

// 字节数组（复用 thunder 池）
static JByteArray *gbytes(const unsigned char *d, int n) {
    JByteArray *a = (JByteArray *)my_NewByteArray(NULL, n);
    if (a && a->b && d && n > 0) memcpy(a->b, d, (size_t)n);
    return a;
}

// File/流桩统一落在 /data/guard 下（guest 真实 FS：tmpfs 可写）
static void gpath_abs(char *out, int n, const char *p) {
    if (!p || !p[0]) { snprintf(out, n, "/data/guard"); return; }
    if (p[0] == '/') snprintf(out, n, "%s", p);
    else snprintf(out, n, "/data/guard/%s", p);
    for (char *q = out; *q; q++) if (*q == '\\') *q = '/';
}
static GObj *gfile_new(const char *path) {
    char abs[512]; gpath_abs(abs, sizeof abs, path);
    GObj *o = gobj_new("java/io/File", 2);
    gput_field(o, "path", 2, 0, strdup(abs));
    return o;
}
static const char *gfile_path(GObj *o) {
    const char *p = (const char *)gget_optr(o, "path");
    return p ? p : "/data/guard";
}
static GObj *guard_dir_new(const char *sub) {
    char abs[512];
    snprintf(abs, sizeof abs, "/data/guard/%s", sub);
    mkdir("/data", 0777);          // ⚠ /data 在 initramfs ramfs 里不存在，须逐级建
    mkdir("/data/guard", 0777);
    mkdir(abs, 0777);
    return gfile_new(abs);
}

// 首选项存取
static const char *gpref_get(const char *k) {
    for (int i = 0; i < MAX_GPREFS; i++)
        if (g_gprefs[i].used && !strcmp(g_gprefs[i].key, k)) return g_gprefs[i].val;
    return NULL;
}
static void gpref_put(const char *k, const char *v) {
    if (!k || !k[0]) return;
    for (int i = 0; i < MAX_GPREFS; i++) {
        if (!g_gprefs[i].used || !strcmp(g_gprefs[i].key, k)) {
            snprintf(g_gprefs[i].key, sizeof g_gprefs[i].key, "%s", k);
            snprintf(g_gprefs[i].val, sizeof g_gprefs[i].val, "%s", v ? v : "");
            g_gprefs[i].used = 1;
            return;
        }
    }
    printf("[gcall] ⚠ prefs 满，丢弃 %s\n", k);
}
static void editor_flush(GObj *ed) {
    if (!ed) return;
    for (int i = 0; i < ed->n; i++)
        if (ed->f[i].o && ed->f[i].name[0] && strcmp(ed->f[i].name, "value"))
            gpref_put(ed->f[i].name, (const char *)ed->f[i].o);
}

// base64（标准字母表，URL-safe 输入兼容）
static const char *B64C = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
static int b64_dec(const char *in, unsigned char *out, int outcap) {
    int tbl[256];
    for (int i = 0; i < 256; i++) tbl[i] = -1;
    for (int i = 0; i < 64; i++) tbl[(unsigned char)B64C[i]] = i;
    tbl[(unsigned char)'-'] = 62; tbl[(unsigned char)'_'] = 62;
    int n = 0, acc = 0, bits = 0;
    for (const char *p = in; *p && *p != '\n' && *p != '\r'; p++) {
        if (*p == '=') break;
        int v = tbl[(unsigned char)*p];
        if (v < 0) continue;
        acc = (acc << 6) | v; bits += 6;
        if (bits >= 8) { bits -= 8; if (n < outcap) out[n++] = (unsigned char)((acc >> bits) & 0xFF); else return -1; }
    }
    return n;
}
static int b64_enc(const unsigned char *in, int n, char *out, int outcap) {
    int j = 0;
    for (int i = 0; i < n && j + 4 < outcap; i += 3) {
        int b0 = in[i], b1 = i + 1 < n ? in[i + 1] : 0, b2 = i + 2 < n ? in[i + 2] : 0;
        out[j++] = B64C[b0 >> 2];
        out[j++] = B64C[((b0 & 3) << 4) | (b1 >> 4)];
        out[j++] = (i + 1 < n) ? B64C[((b1 & 15) << 2) | (b2 >> 6)] : '=';
        out[j++] = (i + 2 < n) ? B64C[b2 & 63] : '=';
    }
    out[j] = 0;
    return j;
}

// ── 宿主 HTTP：大载荷版（/res 资源与 /report UI 事件）──
// 读整个 body 到新 malloc 的缓冲（上限 64MB，返回体独立可 free）。返回 0 成功。
static int ghttp_get_mem(const char *ip, int port, const char *path,
                         unsigned char **out, int *outlen) {
    *out = NULL; *outlen = 0;
    int fd = tcp_connect_ip(ip, port);
    if (fd < 0) return -1;
    char head[512];
    snprintf(head, sizeof head, "GET %s HTTP/1.0\r\nHost: %s:%d\r\nConnection: close\r\n\r\n",
             path, ip, port);
    if (write(fd, head, strlen(head)) < 0) { close(fd); return -1; }
    int cap = 1 << 20, n = 0, hdrend = -1;
    unsigned char *buf = (unsigned char *)malloc((size_t)cap);
    if (!buf) { close(fd); return -1; }
    for (;;) {
        if (n >= cap - 1) {
            if (cap >= (64 << 20)) { printf("[guard] ⚠ 载荷超 64MB 截断\n"); break; }
            cap *= 2;
            unsigned char *nb = (unsigned char *)realloc(buf, (size_t)cap);
            if (!nb) break;
            buf = nb;
        }
        int r = (int)read(fd, buf + n, (size_t)(cap - 1 - n));
        if (r <= 0) break;
        n += r;
        if (hdrend < 0) {
            for (int i = 0; i + 3 < n; i++)
                if (buf[i] == 13 && buf[i + 1] == 10 && buf[i + 2] == 13 && buf[i + 3] == 10) { hdrend = i + 4; break; }
        }
    }
    close(fd);
    if (hdrend < 0 || n <= hdrend) { free(buf); return -1; }
    buf[n] = 0;
    if (strncmp((char *)buf, "HTTP/1.0 200", 12) != 0 && strncmp((char *)buf, "HTTP/1.1 200", 12) != 0) {
        printf("[guard] 宿主响应非 200: %.60s\n", buf);
        free(buf);
        return -1;
    }
    int blen = n - hdrend;
    unsigned char *body = (unsigned char *)malloc((size_t)blen + 1);
    if (!body) { free(buf); return -1; }
    memcpy(body, buf + hdrend, (size_t)blen);
    body[blen] = 0;
    free(buf);
    *out = body;
    *outlen = blen;
    return 0;
}

// UI/状态事件上行（msg 大：QR 矩阵等），动态缓冲
static void guard_report_big(const char *ev, const char *msg) {
    if (!g_ctrl_port) return;
    size_t el = strlen(msg) * 3 + 64;
    char *enc = (char *)malloc(el);
    if (!enc) return;
    url_encode(msg, enc, (int)el);
    size_t pl = strlen(ev) + strlen(enc) + 32;
    char *path = (char *)malloc(pl);
    if (!path) { free(enc); return; }
    snprintf(path, pl, "/report?ev=%s&msg=%s", ev, enc);
    static char resp[256];
    if (http_get_body("10.0.2.2", g_ctrl_port, path, resp, sizeof resp) != 0)
        printf("[guard] （上行 %s 失败，宿主控制端没起？）\n", ev);
    free(enc); free(path);
}

// ── JSON 转义拼装 ──
static void json_esc(char **p, const char *s) {
    *(*p)++ = '"';
    for (const unsigned char *q = (const unsigned char *)(s ? s : ""); *q; q++) {
        if (*q == '"' || *q == '\\') { *(*p)++ = '\\'; *(*p)++ = (char)*q; }
        else if (*q < 0x20) { *p += sprintf(*p, "\\u%04x", *q); }
        else *(*p)++ = (char)*q;
    }
    *(*p)++ = '"';
}
static const char *nz(const char *s) { return s ? s : ""; }

// ═══════════ UI 事件：对话框 / 二维码 / Toast（对齐桥侧 UiBridge 事件形态）═══════════

static void dialog_json(GDialog *d, char *out, int cap) {
    char *p = out;
    p += snprintf(p, (size_t)(cap - (p - out)),
                  "{\"ev\":\"ui-dialog\",\"src\":\"qemu\",\"seq\":%d,\"title\":", d->seq);
    json_esc(&p, d->title);
    p += snprintf(p, (size_t)(cap - (p - out)), ",\"message\":");
    json_esc(&p, d->message);
    if (d->nitems > 0) {
        p += snprintf(p, (size_t)(cap - (p - out)), ",\"items\":[");
        for (int i = 0; i < d->nitems; i++) {
            if (i) *p++ = ',';
            json_esc(&p, d->items[i]);
        }
        *p++ = ']';
    }
    if (d->posText[0]) { p += snprintf(p, (size_t)(cap - (p - out)), ",\"positive\":"); json_esc(&p, d->posText); }
    if (d->negText[0]) { p += snprintf(p, (size_t)(cap - (p - out)), ",\"negative\":"); json_esc(&p, d->negText); }
    if (d->qr) {
        long w = gget_iv(d->qr, "w"), h = gget_iv(d->qr, "h");
        unsigned int *px = (unsigned int *)gget_optr(d->qr, "px");
        if (px && w > 0 && h > 0 && w * h <= (1 << 20)) {
            p += snprintf(p, (size_t)(cap - (p - out)), ",\"qr\":{\"w\":%ld,\"h\":%ld,\"pixels\":[", w, h);
            for (long i = 0; i < w * h; i++) {
                unsigned int v = px[i];
                int black = (((v & 0xFF) < 128) && ((v >> 24) & 0xFF) > 0) ||
                            ((v & 0xFFFFFF) == 0 && ((v >> 24) & 0xFF) > 0);
                if (i) *p++ = ',';
                *p++ = (char)(black ? '1' : '0');
            }
            *p++ = ']'; *p++ = '}';
        }
    }
    *p++ = '}'; *p = 0;
}

static GDialog *dialog_alloc(void) {
    for (int i = 0; i < MAX_GDIALOG; i++)
        if (!g_gdialog[i].active) {
            GDialog *d = &g_gdialog[i];
            memset(d, 0, sizeof *d);
            d->seq = g_gseq++;
            d->active = 1;
            return d;
        }
    printf("[gcall] ⚠ 对话框池满\n");
    return NULL;
}

static GDialog *dialog_by_seq(int seq) {
    for (int i = 0; i < MAX_GDIALOG; i++)
        if (g_gdialog[i].active && g_gdialog[i].seq == seq) return &g_gdialog[i];
    return NULL;
}

static void dialog_emit(GDialog *d) {
    static char buf[1 << 21];
    dialog_json(d, buf, sizeof buf);
    printf("[guard-ui] ↑ ui-dialog seq=%d title=%s items=%d qr=%s\n",
           d->seq, d->title, d->nitems, d->qr ? "有" : "无");
    guard_report_big("ui", buf);
}

static void dialog_dismiss(GDialog *d, int cancelled) {
    if (!d || !d->active) return;
    char buf[160];
    snprintf(buf, sizeof buf, "{\"ev\":\"ui-dismiss\",\"src\":\"qemu\",\"seq\":%d,\"cancelled\":%s}",
             d->seq, cancelled ? "true" : "false");
    guard_report_big("ui", buf);
    d->active = 0;
}

static void emit_toast(const char *text) {
    char buf[1024], *p = buf;
    p += snprintf(p, (size_t)(sizeof buf - (p - buf)), "{\"ev\":\"ui-toast\",\"src\":\"qemu\",\"text\":");
    json_esc(&p, text);
    *p++ = '}'; *p = 0;
    guard_report_big("ui", buf);
}

// ═══════════ JNI 桩（guard env 的槽位实现）═══════════
// 分派顺序：① so 注册的 native（listener 回调/DexNative 自调）② 内建族 ③ 兜底日志。

// ── 资源流（ClassLoader.getResourceAsStream → 宿主 /res）──
typedef struct { unsigned char *data; int len, pos; } GRes;

static GObj *guard_get_res_stream(const char *name) {
    char enc[512], path[768];
    url_encode(name, enc, sizeof enc);
    snprintf(path, sizeof path, "/res?jar=%s&name=%s", g_guard_jar, enc);
    unsigned char *body = NULL; int blen = 0;
    if (ghttp_get_mem("10.0.2.2", g_ctrl_port, path, &body, &blen) != 0) {
        printf("[gcall] getResourceAsStream(%s) → 宿主无此条目\n", name);
        return NULL;
    }
    GRes *rs = (GRes *)malloc(sizeof(GRes));
    rs->data = body; rs->len = blen; rs->pos = 0;
    GObj *o = gobj_new("java/io/InputStream", 2);
    gput_field(o, "rs", 2, 0, rs);
    printf("[gcall] getResourceAsStream(%s) → %d 字节\n", name, blen);
    return o;
}

static jstring g_NewStringUTF(JNIEnv *e, const char *utf) { (void)e; return (jstring)gstr(utf); }
static const char *g_GetStringUTFChars(JNIEnv *e, jstring s, jboolean *c) {
    (void)e; if (c) *c = JNI_FALSE;
    return gstr_chars(s);
}
static void g_ReleaseStringUTFChars(JNIEnv *e, jstring s, const char *c) { (void)e; (void)s; (void)c; }
static jsize g_GetStringUTFLength(JNIEnv *e, jstring s) { (void)e; return (jsize)strlen(gstr_chars(s)); }

static jclass g_FindClass(JNIEnv *e, const char *name) {
    (void)e;
    printf("[gcall] FindClass(%s)\n", name);
    return (jclass)gclass_intern(name);
}
static jclass g_GetObjectClass(JNIEnv *e, jobject o) {
    (void)e;
    if (is_gobj(o)) return (jclass)gclass_intern(((GObj *)o)->cls);
    if (is_byte_array(o)) return (jclass)gclass_intern("[B");
    if (is_int_array(o))  return (jclass)gclass_intern("[I");
    if (is_obj_array(o))  return (jclass)gclass_intern("[Ljava/lang/Object;");
    printf("[gcall] ⚠ GetObjectClass(未知指针 %p)\n", o);
    return (jclass)gclass_intern("java/lang/Object");
}

static jmethodID g_GetMethodID(JNIEnv *e, jclass c, const char *n, const char *s) {
    (void)e; (void)c;
    return (jmethodID)gmid_new(is_gobj(c) ? ((GObj *)c)->cls : "", n, s);
}
static jfieldID g_GetFieldID(JNIEnv *e, jclass c, const char *n, const char *s) {
    (void)e; (void)c;
    return (jfieldID)gmid_new(is_gobj(c) ? ((GObj *)c)->cls : "", n, s);
}

// ── 对象构造 ──
static GObj *guard_new_by_class(const char *cls, va_list ap) {
    if (!strcmp(cls, "java/io/File")) {
        void *a1 = va_arg(ap, void *);
        void *a2 = va_arg(ap, void *);
        if (a2 && is_gobj(a2)) return gfile_new(gstr_chars((jstring)a2));   // File(File,String)/File(String,String)
        return gfile_new(a1 && is_gobj(a1) ? gstr_chars((jstring)a1) : "");
    }
    if (!strcmp(cls, "java/io/FileInputStream")) {
        void *a1 = va_arg(ap, void *);
        const char *p = a1 && is_gobj(a1) ? gstr_chars((jstring)a1) : "";
        GObj *o = gobj_new("java/io/FileInputStream", 2);
        char abs[512]; gpath_abs(abs, sizeof abs, p);
        gput_field(o, "path", 2, 0, strdup(abs));
        printf("[gcall] new FileInputStream(%s)\n", abs);
        return o;
    }
    if (!strcmp(cls, "java/io/FileOutputStream")) {
        void *a1 = va_arg(ap, void *);
        const char *p = a1 && is_gobj(a1) ? gstr_chars((jstring)a1) : "";
        GObj *o = gobj_new("java/io/FileOutputStream", 2);
        char abs[512]; gpath_abs(abs, sizeof abs, p);
        char dir[512]; snprintf(dir, sizeof dir, "%s", abs);
        char *sl = strrchr(dir, '/');
        if (sl) { *sl = 0; mkdir(dir, 0777); }
        gput_field(o, "path", 2, 0, strdup(abs));
        printf("[gcall] new FileOutputStream(%s)\n", abs);
        return o;
    }
    if (!strcmp(cls, "dalvik/system/DexClassLoader")) {
        void *a1 = va_arg(ap, void *);
        const char *dp = a1 && is_gobj(a1) ? gstr_chars((jstring)a1) : "";
        GObj *o = gobj_new("dalvik/system/DexClassLoader", 2);
        gput_field(o, "dexPath", 2, 0, strdup(dp));
        printf("[gcall] new DexClassLoader(%s)（桩：解壳产物宿主侧已有）\n", dp);
        return o;
    }
    if (!strcmp(cls, "android/app/AlertDialog$Builder")) {
        printf("[gcall] new AlertDialog$Builder\n");
        return gobj_new("android/app/AlertDialog$Builder", 2);
    }
    if (!strncmp(cls, "android/widget/", 15)) {
        printf("[gcall] new %s\n", cls);
        return gobj_new(cls, 2);
    }
    if (!strcmp(cls, "java/io/ByteArrayInputStream")) {
        void *a1 = va_arg(ap, void *);
        GObj *o = gobj_new("java/io/ByteArrayInputStream", 2);
        gput_field(o, "bytes", 2, 0, a1);   // JByteArray 指针，结果捕获时取
        return o;
    }
    if (!strcmp(cls, "android/os/Handler")) return gobj_new("android/os/Handler", 2);
    if (!strcmp(cls, "java/lang/StringBuilder")) {
        GObj *o = gobj_new("java/lang/StringBuilder", 2);
        gput_field(o, "sb", 2, 0, calloc(1, 8192));
        return o;
    }
    printf("[gcall] NewObject(%s) → 通用桩\n", cls);
    return gobj_new(cls, 2);
}

static jobject g_NewObjectV(JNIEnv *e, jclass c, jmethodID m, va_list ap) {
    (void)e; (void)m;
    if (is_gobj(c)) return (jobject)guard_new_by_class(((GObj *)c)->cls, ap);
    return NULL;
}
static jobject g_NewObject(JNIEnv *e, jclass c, jmethodID m, ...) {
    va_list ap; va_start(ap, m);
    jobject r = g_NewObjectV(e, c, m, ap);
    va_end(ap);
    return r;
}
static jobject g_NewObjectA(JNIEnv *e, jclass c, jmethodID m, const jvalue *a) {
    (void)e; (void)a; (void)m;
    printf("[gcall] NewObjectA(%s) → 通用桩\n", is_gobj(c) ? ((GObj *)c)->cls : "?");
    return is_gobj(c) ? (jobject)gobj_new(((GObj *)c)->cls, 2) : NULL;
}

// ── 通用可变参取参：按 sig 提取 ──
typedef struct { char t; void *p; long i; } GArg;
static int gargs_parse(const char *sig, va_list ap, GArg *args, int max) {
    int n = 0;
    const char *p = sig;
    if (*p == '(') p++;
    while (*p && *p != ')' && n < max) {
        if (*p == 'L' || *p == '[') {
            args[n].t = (*p == 'L') ? 'o' : 'a';
            args[n].p = va_arg(ap, void *);
            args[n].i = 0;
            n++;
            while (*p && *p != ';' && *p != ')') p++;
            if (*p == ';') p++; else break;
        } else if (*p == 'Z' || *p == 'B' || *p == 'C' || *p == 'S' || *p == 'I' || *p == 'F') {
            args[n].t = 'i'; args[n].p = NULL; args[n].i = va_arg(ap, int); n++; p++;
        } else if (*p == 'J' || *p == 'D') {
            args[n].t = 'l'; args[n].p = NULL; args[n].i = (long)va_arg(ap, long long); n++; p++;
        } else p++;
    }
    return n;
}
#define GARGS(sig, ap, arr) GArg arr[6]; { va_list apc; va_copy(apc, ap); gargs_parse(sig, apc, arr, 6); va_end(apc); }

// ── so 注册的 native 直调 ──
static jobject gnat_call_obj(GNative *n, jobject thiz, const char *sig, va_list ap) {
    JNIEnv *env = g_guard_env;
    void *a1 = NULL, *a2 = NULL, *a3 = NULL;
    if (!strcmp(sig, "(Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;)Ljava/lang/String;")) {
        a1 = va_arg(ap, void *); a2 = va_arg(ap, void *); a3 = va_arg(ap, void *);
        typedef jobject (*fn3)(JNIEnv *, jobject, void *, void *, void *);
        return ((fn3)n->fn)(env, thiz, a1, a2, a3);
    }
    if (!strcmp(sig, "(Ljava/lang/Object;Ljava/lang/Object;)Ljava/lang/Object;")) {
        a1 = va_arg(ap, void *); a2 = va_arg(ap, void *);
        typedef jobject (*fn2)(JNIEnv *, jobject, void *, void *);
        return ((fn2)n->fn)(env, thiz, a1, a2);
    }
    if (!strcmp(sig, "(Ljava/lang/String;)Ljava/lang/String;") ||
        !strcmp(sig, "(Ljava/lang/Object;)Ljava/lang/Object;") ||
        !strcmp(sig, "([I)[I")) {
        a1 = va_arg(ap, void *);
        typedef jobject (*fn1)(JNIEnv *, jobject, void *);
        return ((fn1)n->fn)(env, thiz, a1);
    }
    printf("[guard] ⚠ 未适配的 native 签名 %s.%s%s\n", n->cls, n->name, sig);
    return NULL;
}
static void gnat_call_void(GNative *n, jobject thiz, const char *sig, va_list ap) {
    JNIEnv *env = g_guard_env;
    if (!strcmp(sig, "()V")) {
        typedef void (*fn0)(JNIEnv *, jobject);
        ((fn0)n->fn)(env, thiz);
        return;
    }
    if (!strcmp(sig, "(Landroid/content/DialogInterface;I)V")) {
        void *a1 = va_arg(ap, void *); int a2 = va_arg(ap, int);
        typedef void (*fn2)(JNIEnv *, jobject, void *, int);
        ((fn2)n->fn)(env, thiz, a1, a2);
        return;
    }
    if (!strcmp(sig, "(Landroid/content/DialogInterface;)V")) {
        void *a1 = va_arg(ap, void *);
        typedef void (*fn1)(JNIEnv *, jobject, void *);
        ((fn1)n->fn)(env, thiz, a1);
        return;
    }
    printf("[guard] ⚠ 未适配的 void native 签名 %s.%s%s\n", n->cls, n->name, sig);
}

// runnable/监听器统一执行入口
static void guard_run_void(GObj *receiver, const char *name, const char *sig, jobject a1, int a2) {
    GNative *nat = gnat_match(receiver->cls, name, sig);
    if (!nat) {
        printf("[gcall] ⚠ 回调 %s.%s%s 无注册 native（桩无法执行）\n", receiver->cls, name, sig);
        return;
    }
    printf("[gcall] → 回调 native %s.%s%s\n", receiver->cls, name, sig);
    JNIEnv *env = g_guard_env;
    GUARD_PROTECT_CALL({
        if (!strcmp(sig, "()V")) {
            typedef void (*fn0)(JNIEnv *, jobject);
            ((fn0)nat->fn)(env, (jobject)receiver);
        } else if (!strcmp(sig, "(Landroid/content/DialogInterface;I)V")) {
            typedef void (*fn2)(JNIEnv *, jobject, void *, int);
            ((fn2)nat->fn)(env, (jobject)receiver, a1, a2);
        } else if (!strcmp(sig, "(Landroid/content/DialogInterface;)V")) {
            typedef void (*fn1)(JNIEnv *, jobject, void *);
            ((fn1)nat->fn)(env, (jobject)receiver, a1);
        }
    });
}

static void guard_run_runnable(void *r) {
    if (r && is_gobj(r)) guard_run_void((GObj *)r, "run", "()V", NULL, 0);
}

// ── CallObjectMethod 核心 ──
static jobject guard_call_obj_core(jobject obj, GId *m, const char *sig, va_list ap) {
    GARGS(sig, ap, a)
    const char *name = m->name, *cls = m->cls;

    // ① native 注册表（listener 回调 / so 自调静态 native）
    GNative *nat = gnat_match(cls, name, sig);
    if (nat) {
        printf("[gcall] → native %s.%s%s\n", cls, name, sig);
        return gnat_call_obj(nat, obj, sig, ap);
    }

    // ⓪ loadClass 静态风格（obj 可为 NULL：so 对类/无接收者调 loadClass，2026-09-24 实测）
    if (!strcmp(m->name, "loadClass") && strstr(m->sig, "Ljava/lang/Class;")) {
        // ⚠ 返回类 token（不能 null）：so 拿它继续调静态方法（如 InitOrigin.context()
        //   拿 SharedPreferences 判登录态）。返回 null 会让 proxyInvoke 直接失败。
        const char *cn = (a[0].t == 'o' || a[0].t == 'a') && a[0].p ? gstr_chars((jstring)a[0].p) : "?";
        printf("[gcall] loadClass(%s) → 类桩\n", cn);
        return (jobject)gclass_intern(cn);
    }

    if (obj && is_gobj(obj)) {
        GObj *o = (GObj *)obj;

        // ② Map 族（proxyInvoke 的参数 HashMap / Bundle）
        if ((strstr(cls, "Map") || strstr(cls, "Bundle")) && o->kind == 2) {
            if (!strcmp(sig, "(Ljava/lang/Object;)Ljava/lang/Object;")) {
                const char *k = a[0].p ? gstr_chars((jstring)a[0].p) : "";
                const char *v = (const char *)gget_optr(o, k);
                if (!strcmp(name, "get")) {
                    if (v) return (jobject)gstr(v);
                    printf("[gcall] Map.get(%s) → null\n", k);
                    return NULL;
                }
            }
            if (!strcmp(name, "getOrDefault") &&
                !strcmp(sig, "(Ljava/lang/Object;Ljava/lang/Object;)Ljava/lang/Object;")) {
                const char *k = a[0].p ? gstr_chars((jstring)a[0].p) : "";
                const char *v = (const char *)gget_optr(o, k);
                return (jobject)gstr(v ? v : (a[1].p ? gstr_chars((jstring)a[1].p) : ""));
            }
        }

        // ③ Context 族（目录/包名/ClassLoader/SharedPreferences）
        if (!strcmp(name, "getFilesDir") || !strcmp(name, "getCacheDir") || !strcmp(name, "getCodeCacheDir"))
            return (jobject)guard_dir_new(
                !strcmp(name, "getFilesDir") ? "files" :
                !strcmp(name, "getCacheDir") ? "cache" : "code_cache");
        if (!strcmp(name, "getExternalFilesDir")) return (jobject)guard_dir_new("files");
        if (!strcmp(name, "getDir") && !strcmp(sig, "(Ljava/lang/String;I)Ljava/io/File;"))
            return (jobject)gfile_new(gstr_chars((jstring)a[0].p));
        if (!strcmp(name, "getPackageName")) return (jobject)gstr("com.catclaw.video");
        if (!strcmp(name, "getApplicationContext") || !strcmp(name, "getBaseContext")) return obj;
        if (!strcmp(name, "getClassLoader"))
            return (jobject)gobj_new("java/lang/ClassLoader", 2);
        if (!strcmp(name, "getSharedPreferences") &&
            !strcmp(sig, "(Ljava/lang/String;I)Landroid/content/SharedPreferences;")) {
            printf("[gcall] Context.getSharedPreferences(%s, %ld)\n", gstr_chars((jstring)a[0].p), a[1].i);
            return (jobject)gobj_new("android/content/SharedPreferences", 2);
        }

        // ④ SharedPreferences / Editor
        if (!strcmp(name, "getString") &&
            !strcmp(sig, "(Ljava/lang/String;Ljava/lang/String;)Ljava/lang/String;")) {
            const char *k = gstr_chars((jstring)a[0].p);
            const char *v = gpref_get(k);
            printf("[gcall] prefs.getString(%s) → %s\n", k, v ? "有值" : "缺省");
            return (jobject)gstr(v ? v : gstr_chars((jstring)a[1].p));
        }
        if (!strcmp(name, "edit"))
            return (jobject)gobj_new("android/content/SharedPreferences$Editor", 2);
        if (!strcmp(name, "putString") &&
            !strcmp(sig, "(Ljava/lang/String;Ljava/lang/String;)Landroid/content/SharedPreferences$Editor;")) {
            const char *k = gstr_chars((jstring)a[0].p);
            gput_field(o, k, 2, 0, a[1].p ? (void *)strdup(gstr_chars((jstring)a[1].p)) : NULL);
            printf("[gcall] editor.putString(%s, %s)\n", k, a[1].p ? gstr_chars((jstring)a[1].p) : "null");
            return obj;
        }

        // ⑤ File
        if (!strcmp(cls, "java/io/File")) {
            if (!strcmp(name, "getAbsolutePath") || !strcmp(name, "getPath") || !strcmp(name, "toString"))
                return (jobject)gstr(gfile_path(o));
            if (!strcmp(name, "getName")) {
                const char *p = gfile_path(o);
                const char *b = strrchr(p, '/');
                return (jobject)gstr(b ? b + 1 : p);
            }
            if (!strcmp(name, "getParent")) {
                char t[512]; snprintf(t, sizeof t, "%s", gfile_path(o));
                char *s = strrchr(t, '/');
                if (s) *s = 0;
                return (jobject)gstr(t);
            }
        }

        // ⑥ ClassLoader / DexClassLoader
        if (!strcmp(name, "getResourceAsStream") &&
            !strcmp(sig, "(Ljava/lang/String;)Ljava/io/InputStream;"))
            return (jobject)guard_get_res_stream(gstr_chars((jstring)a[0].p));
        if (!strcmp(name, "loadClass") && strstr(sig, "Ljava/lang/Class;")) {
            // ⚠ 返回类 token（不能 null）：so 拿它继续调静态方法（如 InitOrigin.context()
            //   拿 SharedPreferences 判登录态）。返回 null 会让 proxyInvoke 直接失败
            //   （2026-09-24 实测）。
            const char *cn = a[0].p ? gstr_chars((jstring)a[0].p) : "?";
            printf("[gcall] ClassLoader.loadClass(%s) → 类桩\n", cn);
            return (jobject)gclass_intern(cn);
        }

        // ⑦ AlertDialog$Builder 链式
        if (strstr(cls, "AlertDialog$Builder")) {
            if (!strcmp(name, "setTitle") || !strcmp(name, "setMessage") ||
                !strcmp(name, "setPositiveButton") || !strcmp(name, "setNegativeButton") ||
                !strcmp(name, "setNeutralButton")) {
                const char *tx = a[0].p && is_gobj(a[0].p) ? gstr_chars((jstring)a[0].p) : "";
                if (!strcmp(name, "setTitle")) gput_field(o, "title", 2, 0, strdup(tx));
                if (!strcmp(name, "setMessage")) gput_field(o, "message", 2, 0, strdup(tx));
                if (!strcmp(name, "setPositiveButton")) { gput_field(o, "posText", 2, 0, strdup(tx)); gput_field(o, "posL", 2, 0, a[1].p); }
                if (!strcmp(name, "setNegativeButton")) { gput_field(o, "negText", 2, 0, strdup(tx)); gput_field(o, "negL", 2, 0, a[1].p); }
                if (!strcmp(name, "setNeutralButton")) { gput_field(o, "neuText", 2, 0, strdup(tx)); gput_field(o, "neuL", 2, 0, a[1].p); }
                printf("[gcall] Builder.%s(%s)\n", name, tx);
                return obj;
            }
            if (!strcmp(name, "setItems") && a[1].p) {
                void *arr = a[0].p;
                if (arr && is_obj_array(arr)) {
                    int cnt = ((JObjArray *)arr)->n;
                    gput_field(o, "nitems", 0, cnt, NULL);
                    for (int i = 0; i < cnt && i < 12; i++) {
                        void *el = ((JObjArray *)arr)->v[i];
                        char key[24]; snprintf(key, sizeof key, "item%d", i);
                        gput_field(o, key, 2, 0, el && is_gobj(el) ? (void *)strdup(gstr_chars((jstring)el)) : strdup(""));
                    }
                }
                gput_field(o, "itemsL", 2, 0, a[1].p);
                printf("[gcall] Builder.setItems\n");
                return obj;
            }
            if (!strcmp(name, "setCancelable")) { gput_field(o, "cancelable", 0, a[0].i, NULL); return obj; }
            if (!strcmp(name, "setOnCancelListener")) { gput_field(o, "cancelL", 2, 0, a[0].p); return obj; }
            if (!strcmp(name, "setOnDismissListener")) { gput_field(o, "dismissL", 2, 0, a[0].p); return obj; }
            if (!strcmp(name, "setView")) {
                gput_field(o, "view", 2, 0, a[0].p);
                if (a[0].p && is_gobj(a[0].p)) {
                    void *bm = gget_optr((GObj *)a[0].p, "bm");
                    if (bm) gput_field(o, "qr", 2, 0, bm);
                }
                return obj;
            }
            if (!strcmp(name, "create") || !strcmp(name, "show")) {
                GDialog *d = dialog_alloc();
                if (d) {
                    d->dialog = gobj_new("android/app/AlertDialog", 2);
                    gput_field(d->dialog, "seq", 0, d->seq, NULL);
                    snprintf(d->title, sizeof d->title, "%s", nz((const char *)gget_optr(o, "title")));
                    snprintf(d->message, sizeof d->message, "%s", nz((const char *)gget_optr(o, "message")));
                    snprintf(d->posText, sizeof d->posText, "%s", nz((const char *)gget_optr(o, "posText")));
                    snprintf(d->negText, sizeof d->negText, "%s", nz((const char *)gget_optr(o, "negText")));
                    snprintf(d->neuText, sizeof d->neuText, "%s", nz((const char *)gget_optr(o, "neuText")));
                    d->itemsL = (GObj *)gget_optr(o, "itemsL");
                    d->posL = (GObj *)gget_optr(o, "posL");
                    d->negL = (GObj *)gget_optr(o, "negL");
                    d->neuL = (GObj *)gget_optr(o, "neuL");
                    d->cancelL = (GObj *)gget_optr(o, "cancelL");
                    d->dismissL = (GObj *)gget_optr(o, "dismissL");
                    d->qr = (GObj *)gget_optr(o, "qr");
                    d->nitems = (int)gget_iv(o, "nitems");
                    for (int i = 0; i < d->nitems && i < 12; i++) {
                        char key[24]; snprintf(key, sizeof key, "item%d", i);
                        const char *it = (const char *)gget_optr(o, key);
                        snprintf(d->items[i], sizeof d->items[i], "%s", nz(it));
                    }
                    gput_field(d->dialog, "dlg", 2, 0, d);
                    dialog_emit(d);
                    return (jobject)d->dialog;
                }
                return NULL;
            }
            return obj;   // 其余 Builder 链式方法：返回自身
        }

        // ⑧ ImageView/TextView
        if (!strcmp(name, "setImageBitmap")) { gput_field(o, "bm", 2, 0, a[0].p); return NULL; }
        if (!strcmp(name, "setText")) {
            gput_field(o, "text", 2, 0, a[0].p ? (void *)strdup(gstr_chars((jstring)a[0].p)) : strdup(""));
            return NULL;
        }

        // ⑨ Handler.post → 立即执行 runnable
        if ((!strcmp(name, "post") || !strcmp(name, "postDelayed")) && !strcmp(o->cls, "android/os/Handler")) {
            guard_run_runnable(a[0].p);
            return (jobject)gstr("true");
        }

        // ⑩ String 实例方法
        if (o->kind == 1) {
            const char *s = gstr_chars((jstring)obj);
            if (!strcmp(name, "getBytes")) return (jobject)gbytes((const unsigned char *)s, (int)strlen(s));
            if (!strcmp(name, "trim")) {
                while (*s == ' ') s++;
                const char *e = s + strlen(s);
                while (e > s && e[-1] == ' ') e--;
                char *t = (char *)malloc((size_t)(e - s) + 1);
                memcpy(t, s, (size_t)(e - s)); t[e - s] = 0;
                return (jobject)gstr(t);
            }
            if (!strcmp(name, "substring")) {
                int len = (int)strlen(s);
                int b = (int)a[0].i;
                if (!strcmp(sig, "(I)Ljava/lang/String;")) {
                    if (b < 0 || b > len) b = 0;
                    return (jobject)gstr(s + b);
                }
                if (!strcmp(sig, "(II)Ljava/lang/String;")) {
                    int e2 = (int)a[1].i;
                    if (b < 0) b = 0;
                    if (e2 > len) e2 = len;
                    if (e2 < b) e2 = b;
                    char *t = (char *)malloc((size_t)(e2 - b) + 1);
                    memcpy(t, s + b, (size_t)(e2 - b)); t[e2 - b] = 0;
                    return (jobject)gstr(t);
                }
            }
            if (!strcmp(name, "toLowerCase")) {
                char *t = strdup(s);
                for (char *q = t; *q; q++) if (*q >= 'A' && *q <= 'Z') *q += 32;
                return (jobject)gstr(t);
            }
            if (!strcmp(name, "toUpperCase")) {
                char *t = strdup(s);
                for (char *q = t; *q; q++) if (*q >= 'a' && *q <= 'z') *q -= 32;
                return (jobject)gstr(t);
            }
        }

        if (!strcmp(name, "getClass")) return (jobject)g_GetObjectClass(NULL, obj);

        // InitOrigin 家族实例调用（so 拿类桩继续调）：context()/classLoader() 等
        if (strstr(o->cls, "InitOrigin")) {
            if (!strcmp(name, "context") || !strcmp(name, "getContext")) {
                printf("[gcall] InitOrigin.context() → Context 桩\n");
                return (jobject)g_ctx_stub();
            }
            if (!strcmp(name, "classLoader"))
                return (jobject)gobj_new("java/lang/ClassLoader", 2);
        }
    }

    printf("[gcall] ⚠ UNHANDLED CallObjectMethod %s.%s%s → null (obj=%p magic=%lu kind=%d)\n",
           cls, name, sig, obj,
           obj && is_gobj(obj) ? ((GObj *)obj)->magic : 0,
           obj && is_gobj(obj) ? ((GObj *)obj)->kind : -1);
    return NULL;
}

static jobject g_CallObjectMethodV(JNIEnv *e, jobject o, jmethodID m, va_list ap) {
    (void)e;
    if (!is_gid(m)) return NULL;
    return guard_call_obj_core(o, (GId *)m, ((GId *)m)->sig, ap);
}
static jobject g_CallObjectMethod(JNIEnv *e, jobject o, jmethodID m, ...) {
    va_list ap; va_start(ap, m);
    jobject r = g_CallObjectMethodV(e, o, m, ap);
    va_end(ap);
    return r;
}
static jobject g_CallObjectMethodA(JNIEnv *e, jobject o, jmethodID m, const jvalue *a) {
    (void)e; (void)o; (void)m; (void)a;
    printf("[gcall] ⚠ UNHANDLED CallObjectMethodA（A 变体未实现）\n");
    return NULL;
}

static jobject g_CallStaticObjectMethodV(JNIEnv *e, jclass c, jmethodID m, va_list ap) {
    (void)e;
    if (!is_gid(m)) return NULL;
    GId *mid = (GId *)m;
    const char *cls = is_gobj(c) ? ((GObj *)c)->cls : mid->cls;
    const char *name = mid->name, *sig = mid->sig;
    GARGS(sig, ap, a)
    GNative *nat = gnat_match(cls, name, sig);
    if (nat) {
        printf("[gcall] → static native %s.%s%s\n", cls, name, sig);
        return gnat_call_obj(nat, (jobject)c, sig, ap);
    }
    if (!strcmp(name, "valueOf") && strstr(cls, "Integer")) {
        GObj *o = gobj_new("java/lang/Integer", 2);
        gput_field(o, "v", 0, a[0].i, NULL);
        return (jobject)o;
    }
    if (!strcmp(name, "valueOf") && a[0].p && is_gobj(a[0].p)) return (jobject)a[0].p;
    if (!strcmp(cls, "android/graphics/Bitmap") && !strcmp(name, "createBitmap") &&
        !strcmp(sig, "(IILandroid/graphics/Bitmap$Config;)Landroid/graphics/Bitmap;")) {
        long w = a[0].i, h = a[1].i;
        if (w <= 0 || h <= 0 || w * h > (1 << 20)) {
            printf("[gcall] ⚠ createBitmap(%ld,%ld) 尺寸异常\n", w, h);
            return NULL;
        }
        GObj *bm = gobj_new("android/graphics/Bitmap", 2);
        gput_field(bm, "w", 0, w, NULL);
        gput_field(bm, "h", 0, h, NULL);
        gput_field(bm, "px", 2, 0, calloc(1, (size_t)(w * h * 4)));
        printf("[gcall] Bitmap.createBitmap(%ldx%ld)\n", w, h);
        return (jobject)bm;
    }
    if (!strcmp(cls, "android/graphics/Bitmap") && !strcmp(name, "createBitmap") && a[0].p && is_gobj(a[0].p)) {
        GObj *src = (GObj *)a[0].p;
        long w = gget_iv(src, "w"), h = gget_iv(src, "h");
        unsigned int *sp = (unsigned int *)gget_optr(src, "px");
        GObj *bm = gobj_new("android/graphics/Bitmap", 2);
        gput_field(bm, "w", 0, w, NULL);
        gput_field(bm, "h", 0, h, NULL);
        unsigned int *px = (unsigned int *)calloc(1, (size_t)(w * h * 4 + 4));
        if (sp) memcpy(px, sp, (size_t)(w * h * 4));
        gput_field(bm, "px", 2, 0, px);
        return (jobject)bm;
    }
    if (!strcmp(cls, "android/widget/Toast") && !strcmp(name, "makeText")) {
        GObj *t = gobj_new("android/widget/Toast", 2);
        gput_field(t, "text", 2, 0, a[0].p && is_gobj(a[0].p) ? (void *)strdup(gstr_chars((jstring)a[0].p)) : strdup(""));
        return (jobject)t;
    }
    if (!strcmp(cls, "android/os/Looper") && !strcmp(name, "getMainLooper"))
        return (jobject)gobj_new("android/os/Looper", 2);
    if (!strcmp(cls, "com/github/catvod/spider/Init") && !strcmp(name, "classLoader"))
        return (jobject)gobj_new("java/lang/ClassLoader", 2);
    // InitOrigin 家族（真实类静态门面）：so 的 proxyInvoke 会 loadClass 后调这些拿上下文
    if (strstr(cls, "InitOrigin") || !strcmp(cls, "com/github/catvod/spider/Init")) {
        if (!strcmp(name, "context") || !strcmp(name, "getContext") || !strcmp(name, "application")) {
            printf("[gcall] %s.%s() → Context 桩\n", cls, name);
            return (jobject)g_ctx_stub();
        }
        if (!strcmp(name, "classLoader"))
            return (jobject)gobj_new("java/lang/ClassLoader", 2);
    }
    printf("[gcall] ⚠ UNHANDLED CallStaticObjectMethod %s.%s%s → null\n", cls, name, sig);
    return NULL;
}
static jobject g_CallStaticObjectMethod(JNIEnv *e, jclass c, jmethodID m, ...) {
    va_list ap; va_start(ap, m);
    jobject r = g_CallStaticObjectMethodV(e, c, m, ap);
    va_end(ap);
    return r;
}
static jobject g_CallStaticObjectMethodA(JNIEnv *e, jclass c, jmethodID m, const jvalue *a) {
    (void)e; (void)c; (void)m; (void)a;
    printf("[gcall] ⚠ UNHANDLED CallStaticObjectMethodA\n");
    return NULL;
}

// ── CallVoidMethod ──
static void guard_call_void_core(jobject obj, GId *m, const char *sig, va_list ap) {
    GARGS(sig, ap, a)
    const char *name = m->name, *cls = m->cls;
    GNative *nat = gnat_match(cls, name, sig);
    if (nat) {
        printf("[gcall] → void native %s.%s%s\n", cls, name, sig);
        gnat_call_void(nat, obj, sig, ap);
        return;
    }
    if (obj && is_gobj(obj)) {
        GObj *o = (GObj *)obj;
        // 输出流写
        if (!strcmp(name, "write") && !strcmp(sig, "([BII)V")) {
            JByteArray *ba = (JByteArray *)a[0].p;
            int off = (int)a[1].i, len = (int)a[2].i;
            FILE *f = (FILE *)gget_optr(o, "fh");
            if (!f) {
                const char *p = (const char *)gget_optr(o, "path");
                if (!p) { printf("[gcall] ⚠ write 到无路径流\n"); return; }
                f = fopen(p, "wb");
                if (!f) { printf("[gcall] ⚠ 打开 %s 失败\n", p); return; }
                gput_field(o, "fh", 2, 0, f);
            }
            if (ba && ba->b && f) fwrite(ba->b + off, 1, (size_t)len, f);
            printf("[gcall] OutputStream.write(%d 字节)\n", len);
            return;
        }
        if (!strcmp(name, "close")) {
            FILE *f = (FILE *)gget_optr(o, "fh");
            if (f) { fclose(f); gput_field(o, "fh", 2, 0, NULL); }
            return;
        }
        // Dialog.dismiss/cancel
        if (strstr(o->cls, "Dialog") && (!strcmp(name, "dismiss") || !strcmp(name, "cancel"))) {
            GDialog *d = dialog_by_seq((int)gget_iv(o, "seq"));
            if (d) dialog_dismiss(d, !strcmp(name, "cancel"));
            return;
        }
        if (!strcmp(name, "setImageBitmap")) { gput_field(o, "bm", 2, 0, a[0].p); return; }
        if (!strcmp(name, "setPixel") && !strcmp(sig, "(III)V")) {
            unsigned int *px = (unsigned int *)gget_optr(o, "px");
            long w = gget_iv(o, "w"), h = gget_iv(o, "h");
            long x = a[0].i, y = a[1].i;
            if (px && x >= 0 && y >= 0 && x < w && y < h) px[y * w + x] = (unsigned int)a[2].i;
            return;
        }
        if (!strcmp(name, "setPixels") && a[0].p && is_int_array(a[0].p)) {
            unsigned int *px = (unsigned int *)gget_optr(o, "px");
            JIntArray *src = (JIntArray *)a[0].p;
            long w = gget_iv(o, "w"), h = gget_iv(o, "h");
            if (px && src) {
                for (int i = 0; i < src->n && i < (int)(w * h); i++) px[i] = (unsigned int)src->v[i];
            }
            return;
        }
        if (!strcmp(name, "apply") && strstr(o->cls, "Editor")) { editor_flush(o); return; }
        // InitOrigin 家族（so 拿类桩当实例调）：init 存上下文供后续 getSharedPreferences
        if (strstr(o->cls, "InitOrigin")) {
            if (!strcmp(name, "init") && a[0].p && is_gobj(a[0].p)) {
                g_ctx_from_init = (GObj *)a[0].p;
                printf("[gcall] InitOrigin.init(Context) → 记录上下文\n");
                return;
            }
            if (!strcmp(name, "setClass")) { printf("[gcall] InitOrigin.setClass ✓\n"); return; }
        }
        if (!strcmp(name, "show") && !strcmp(o->cls, "android/widget/Toast")) {
            emit_toast((const char *)(gget_optr(o, "text") ?: ""));
            return;
        }
        if (!strcmp(name, "recycle")) return;
        if (strstr(o->cls, "StringBuilder") && (!strcmp(name, "append") || !strcmp(name, "setLength"))) {
            char *sb = (char *)gget_optr(o, "sb");
            if (sb && !strcmp(name, "append") && a[0].p && is_gobj(a[0].p))
                strncat(sb, gstr_chars((jstring)a[0].p), 8000);
            if (sb && !strcmp(name, "setLength")) sb[0] = 0;
            return;
        }
    }
    printf("[gcall] ⚠ UNHANDLED CallVoidMethod %s.%s%s\n", cls, name, sig);
}
static void g_CallVoidMethodV(JNIEnv *e, jobject o, jmethodID m, va_list ap) {
    (void)e;
    if (!is_gid(m)) return;
    guard_call_void_core(o, (GId *)m, ((GId *)m)->sig, ap);
}
static void g_CallVoidMethod(JNIEnv *e, jobject o, jmethodID m, ...) {
    va_list ap; va_start(ap, m);
    g_CallVoidMethodV(e, o, m, ap);
    va_end(ap);
}
static void g_CallVoidMethodA(JNIEnv *e, jobject o, jmethodID m, const jvalue *a) {
    (void)e; (void)o; (void)m; (void)a;
    printf("[gcall] ⚠ UNHANDLED CallVoidMethodA\n");
}

// ── CallIntMethod ──
static jint guard_call_int_core(jobject obj, GId *m, const char *sig, va_list ap) {
    GARGS(sig, ap, a)
    const char *name = m->name, *cls = m->cls;
    if (obj && is_gobj(obj)) {
        GObj *o = (GObj *)obj;
        if (!strcmp(name, "read") && !strcmp(sig, "([B)I")) {
            GRes *rs = (GRes *)gget_optr(o, "rs");
            if (rs) {
                JByteArray *ba = (JByteArray *)a[0].p;
                if (!ba) return -1;
                int remain = rs->len - rs->pos;
                if (remain <= 0) return -1;
                int n2 = ba->n < remain ? ba->n : remain;
                memcpy(ba->b, rs->data + rs->pos, (size_t)n2);
                rs->pos += n2;
                return n2;
            }
            // FileInputStream：真实读
            const char *p = (const char *)gget_optr(o, "path");
            if (p) {
                FILE *f = (FILE *)gget_optr(o, "fh");
                if (!f) { f = fopen(p, "rb"); if (!f) return -1; gput_field(o, "fh", 2, 0, f); }
                JByteArray *ba = (JByteArray *)a[0].p;
                if (!ba) return -1;
                int n2 = (int)fread(ba->b, 1, (size_t)ba->n, f);
                return n2;
            }
            return -1;
        }
        if (!strcmp(name, "available")) {
            GRes *rs = (GRes *)gget_optr(o, "rs");
            return rs ? rs->len - rs->pos : 0;
        }
        if (!strcmp(name, "getWidth")) return (jint)gget_iv(o, "w");
        if (!strcmp(name, "getHeight")) return (jint)gget_iv(o, "h");
        if (!strcmp(name, "length") && o->kind == 1) return (jint)strlen(gstr_chars((jstring)obj));
        if (!strcmp(name, "intValue")) return (jint)gget_iv(o, "v");
        if (!strcmp(name, "getInt") && !strcmp(sig, "(Ljava/lang/String;I)I")) {
            const char *v = gpref_get(a[0].p ? gstr_chars((jstring)a[0].p) : "");
            return v ? (jint)atoi(v) : (jint)a[1].i;
        }
    }
    if (!strcmp(name, "size") && (strstr(cls, "Map") || strstr(cls, "prefs"))) {
        GObj *o = (GObj *)obj;
        int cnt = 0;
        if (is_gobj(o)) {
            for (int i = 0; i < o->n; i++)
                if (strcmp(o->f[i].name, "value")) cnt++;
        }
        return cnt;
    }
    printf("[gcall] ⚠ UNHANDLED CallIntMethod %s.%s%s → 0\n", cls, name, sig);
    return 0;
}
static jint g_CallIntMethodV(JNIEnv *e, jobject o, jmethodID m, va_list ap) {
    (void)e;
    if (!is_gid(m)) return 0;
    return guard_call_int_core(o, (GId *)m, ((GId *)m)->sig, ap);
}
static jint g_CallIntMethod(JNIEnv *e, jobject o, jmethodID m, ...) {
    va_list ap; va_start(ap, m);
    jint r = g_CallIntMethodV(e, o, m, ap);
    va_end(ap);
    return r;
}
static jint g_CallIntMethodA(JNIEnv *e, jobject o, jmethodID m, const jvalue *a) {
    (void)e; (void)o; (void)m; (void)a;
    printf("[gcall] ⚠ UNHANDLED CallIntMethodA\n");
    return 0;
}

// ── CallBooleanMethod / CallLongMethod ──
static jboolean guard_call_bool_core(jobject obj, GId *m, const char *sig, va_list ap) {
    GARGS(sig, ap, a)
    const char *name = m->name, *cls = m->cls;
    if (obj && is_gobj(obj)) {
        GObj *o = (GObj *)obj;
        if (!strcmp(cls, "java/io/File")) {
            const char *p = gfile_path(o);
            struct stat sb;
            if (!strcmp(name, "exists")) return stat(p, &sb) == 0 ? JNI_TRUE : JNI_FALSE;
            if (!strcmp(name, "isFile")) return (stat(p, &sb) == 0 && S_ISREG(sb.st_mode)) ? JNI_TRUE : JNI_FALSE;
            if (!strcmp(name, "isDirectory")) return (stat(p, &sb) == 0 && S_ISDIR(sb.st_mode)) ? JNI_TRUE : JNI_FALSE;
            if (!strcmp(name, "mkdir")) return mkdir(p, 0777) == 0 ? JNI_TRUE : JNI_FALSE;
            if (!strcmp(name, "mkdirs")) {
                char t[512]; snprintf(t, sizeof t, "%s", p);
                for (char *q = t + 1; *q; q++)
                    if (*q == '/') { *q = 0; mkdir(t, 0777); *q = '/'; }
                return (mkdir(t, 0777) == 0 || stat(t, &sb) == 0) ? JNI_TRUE : JNI_FALSE;
            }
            if (!strcmp(name, "delete")) return unlink(p) == 0 ? JNI_TRUE : JNI_FALSE;
            if (!strcmp(name, "canRead")) return access(p, R_OK) == 0 ? JNI_TRUE : JNI_FALSE;
            if (!strcmp(name, "canWrite")) return access(p, W_OK) == 0 ? JNI_TRUE : JNI_FALSE;
            if (!strcmp(name, "createNewFile")) {
                int fd = open(p, O_CREAT | O_EXCL | O_WRONLY, 0666);
                if (fd >= 0) { close(fd); return JNI_TRUE; }
                return errno == EEXIST ? JNI_TRUE : JNI_FALSE;
            }
            if (!strcmp(name, "setReadOnly")) return chmod(p, 0444) == 0 ? JNI_TRUE : JNI_FALSE;
        }
        if (!strcmp(name, "containsKey") && (strstr(cls, "Map") || strstr(cls, "prefs")))
            return gget_optr(o, a[0].p ? gstr_chars((jstring)a[0].p) : "") ? JNI_TRUE : JNI_FALSE;
        if (!strcmp(name, "contains") && o->kind == 1) {
            const char *needle = a[0].p ? gstr_chars((jstring)a[0].p) : "";
            return strstr(gstr_chars((jstring)obj), needle) ? JNI_TRUE : JNI_FALSE;
        }
        if (!strcmp(name, "startsWith") && o->kind == 1) {
            const char *pre = a[0].p ? gstr_chars((jstring)a[0].p) : "";
            return strncmp(gstr_chars((jstring)obj), pre, strlen(pre)) == 0 ? JNI_TRUE : JNI_FALSE;
        }
        if (!strcmp(name, "equals") && o->kind == 1)
            return !strcmp(gstr_chars((jstring)obj), a[0].p ? gstr_chars((jstring)a[0].p) : "") ? JNI_TRUE : JNI_FALSE;
        if (!strcmp(name, "isEmpty") && o->kind == 1)
            return gstr_chars((jstring)obj)[0] == 0 ? JNI_TRUE : JNI_FALSE;
        if ((!strcmp(name, "commit") || !strcmp(name, "apply")) && strstr(o->cls, "Editor")) {
            editor_flush(o);
            return JNI_TRUE;
        }
        if (!strcmp(name, "booleanValue")) return gget_iv(o, "v") != 0 ? JNI_TRUE : JNI_FALSE;
        if (!strcmp(name, "post") || !strcmp(name, "postDelayed")) {
            guard_run_runnable(a[0].p);
            return JNI_TRUE;
        }
    }
    printf("[gcall] ⚠ UNHANDLED CallBooleanMethod %s.%s%s → false\n", cls, name, sig);
    return JNI_FALSE;
}
static jboolean g_CallBooleanMethodV(JNIEnv *e, jobject o, jmethodID m, va_list ap) {
    (void)e;
    if (!is_gid(m)) return JNI_FALSE;
    return guard_call_bool_core(o, (GId *)m, ((GId *)m)->sig, ap);
}
static jboolean g_CallBooleanMethod(JNIEnv *e, jobject o, jmethodID m, ...) {
    va_list ap; va_start(ap, m);
    jboolean r = g_CallBooleanMethodV(e, o, m, ap);
    va_end(ap);
    return r;
}
static jlong g_CallLongMethodV(JNIEnv *e, jobject o, jmethodID m, va_list ap) {
    (void)e; (void)ap;
    if (!is_gid(m)) return 0;
    GId *mid = (GId *)m;
    if (o && is_gobj(o) && !strcmp(mid->cls, "java/io/File")) {
        struct stat sb;
        if (!strcmp(mid->name, "length")) return stat(gfile_path((GObj *)o), &sb) == 0 ? (jlong)sb.st_size : 0;
        if (!strcmp(mid->name, "lastModified"))
            return stat(gfile_path((GObj *)o), &sb) == 0 ? (jlong)sb.st_mtime * 1000 : 0;
    }
    printf("[gcall] ⚠ UNHANDLED CallLongMethod %s.%s%s → 0\n", mid->cls, mid->name, mid->sig);
    return 0;
}
static jlong g_CallLongMethod(JNIEnv *e, jobject o, jmethodID m, ...) {
    va_list ap; va_start(ap, m);
    jlong r = g_CallLongMethodV(e, o, m, ap);
    va_end(ap);
    return r;
}

// ── 字段访问 ──
static jobject g_GetObjectField(JNIEnv *e, jobject o, jfieldID f) {
    (void)e;
    if (!is_gobj(o) || !is_gid(f)) return NULL;
    void *v = gget_optr((GObj *)o, ((GId *)f)->name);
    const char *sg = ((GId *)f)->sig;
    // SetObjectField 对 String 签名存的是 strdup 的裸 char*
    if (v && !is_gobj(v) && sg && strstr(sg, "Ljava/lang/String;"))
        return (jobject)gstr((const char *)v);
    return (jobject)v;
}
static void g_SetObjectField(JNIEnv *e, jobject o, jfieldID f, jobject v) {
    (void)e;
    if (!is_gobj(o) || !is_gid(f)) return;
    const char *sig = ((GId *)f)->sig;
    void *store = (void *)v;
    if (v && is_gobj(v) && sig && strstr(sig, "Ljava/lang/String;") && ((GObj *)v)->kind == 1)
        store = (void *)strdup(gstr_chars((jstring)v));
    gput_field((GObj *)o, ((GId *)f)->name, 2, 0, store);
}
static jint g_GetIntField(JNIEnv *e, jobject o, jfieldID f) {
    (void)e;
    if (!is_gobj(o) || !is_gid(f)) return 0;
    return (jint)gget_iv((GObj *)o, ((GId *)f)->name);
}
static void g_SetIntField(JNIEnv *e, jobject o, jfieldID f, jint v) {
    (void)e;
    if (!is_gobj(o) || !is_gid(f)) return;
    gput_field((GObj *)o, ((GId *)f)->name, 0, v, NULL);
}
static jlong g_GetLongField(JNIEnv *e, jobject o, jfieldID f) {
    (void)e;
    if (!is_gobj(o) || !is_gid(f)) return 0;
    return gget_iv((GObj *)o, ((GId *)f)->name);
}
static void g_SetLongField(JNIEnv *e, jobject o, jfieldID f, jlong v) {
    (void)e;
    if (!is_gobj(o) || !is_gid(f)) return;
    gput_field((GObj *)o, ((GId *)f)->name, 1, (long)v, NULL);
}
static jboolean g_GetBooleanField(JNIEnv *e, jobject o, jfieldID f) {
    (void)e;
    if (!is_gobj(o) || !is_gid(f)) return JNI_FALSE;
    return gget_iv((GObj *)o, ((GId *)f)->name) != 0 ? JNI_TRUE : JNI_FALSE;
}
static void g_SetBooleanField(JNIEnv *e, jobject o, jfieldID f, jboolean v) {
    (void)e;
    if (!is_gobj(o) || !is_gid(f)) return;
    gput_field((GObj *)o, ((GId *)f)->name, 0, v, NULL);
}
static jobject g_GetStaticObjectField(JNIEnv *e, jclass c, jfieldID f) {
    (void)e;
    const char *name = is_gid(f) ? ((GId *)f)->name : "";
    const char *cls = is_gobj(c) ? ((GObj *)c)->cls : (is_gid(f) ? ((GId *)f)->cls : "");
    if (strstr(cls, "Bitmap$Config")) return (jobject)gclass_intern("android/graphics/Bitmap$Config");
    printf("[gcall] GetStaticObjectField(%s.%s) → null\n", cls, name);
    return NULL;
}
static jint g_GetStaticIntField(JNIEnv *e, jclass c, jfieldID f) {
    (void)e;
    const char *name = is_gid(f) ? ((GId *)f)->name : "";
    const char *cls = is_gobj(c) ? ((GObj *)c)->cls : (is_gid(f) ? ((GId *)f)->cls : "");
    if (!strcmp(cls, "android/os/Build$VERSION") && !strcmp(name, "SDK_INT")) return 28;
    printf("[gcall] GetStaticIntField(%s.%s) → 0\n", cls, name);
    return 0;
}

// ── 引用/异常/杂项 ──
static jobject g_NewGlobalRef(JNIEnv *e, jobject o) { (void)e; return o; }
static void g_DeleteGlobalRef(JNIEnv *e, jobject o) { (void)e; (void)o; }
static jobject g_NewLocalRef(JNIEnv *e, jobject o) { (void)e; return o; }
static jboolean g_IsSameObject(JNIEnv *e, jobject a, jobject b) { (void)e; return a == b ? JNI_TRUE : JNI_FALSE; }
static jint g_RegisterNatives(JNIEnv *e, jclass c, const JNINativeMethod *m, jint n) {
    (void)e;
    const char *cls = is_gobj(c) ? ((GObj *)c)->cls : "?";
    printf("[guard] RegisterNatives(%s) × %d\n", cls, n);
    for (int i = 0; i < n && g_gnatn < MAX_NATIVES; i++) {
        GNative *gn = &g_gnat[g_gnatn++];
        snprintf(gn->cls, sizeof gn->cls, "%s", cls);
        snprintf(gn->name, sizeof gn->name, "%s", m[i].name ? m[i].name : "");
        snprintf(gn->sig, sizeof gn->sig, "%s", m[i].signature ? m[i].signature : "");
        gn->fn = m[i].fnPtr;
        printf("[guard]   %s%s → %p\n", gn->name, gn->sig, gn->fn);
    }
    return JNI_OK;
}
static jint g_ThrowNew(JNIEnv *e, jclass c, const char *msg) {
    (void)e; (void)c;
    printf("[gcall] ThrowNew(%s)（记录不抛）\n", msg ? msg : "");
    return 0;
}
static jthrowable g_ExceptionOccurred(JNIEnv *e) { (void)e; return NULL; }
static jboolean g_ExceptionCheck(JNIEnv *e) { (void)e; return JNI_FALSE; }
static void g_ExceptionClear(JNIEnv *e) { (void)e; }
static jint g_GetJavaVM(JNIEnv *e, JavaVM **p) { (void)e; if (p) *p = g_guard_vm; return JNI_OK; }
static jint g_MonitorEnter(JNIEnv *e, jobject o) { (void)e; (void)o; return 0; }
static jint g_MonitorExit(JNIEnv *e, jobject o) { (void)e; (void)o; return 0; }
static void *g_GetDirectBufferAddress(JNIEnv *e, jobject b) { (void)e; (void)b; return NULL; }
static jobject g_NewDirectByteBuffer(JNIEnv *e, void *addr, jlong cap) {
    (void)e; (void)addr; (void)cap;
    printf("[gcall] NewDirectByteBuffer → null\n");
    return NULL;
}

// ═══════════ so 调用操作 ═══════════

static void op_one_arg(const char *natname, const char *inb64, char *out, int outcap) {
    GNative *n = gnat_find_any(natname);
    if (!n || strcmp(n->sig, "(Ljava/lang/String;)Ljava/lang/String;") != 0) {
        snprintf(out, outcap, "ERR no native %s", natname);
        return;
    }
    unsigned char in[65536];
    int inlen = b64_dec(inb64, in, sizeof in - 1);
    if (inlen < 0) { snprintf(out, outcap, "ERR b64"); return; }
    in[inlen] = 0;
    jobject r = NULL;
    GUARD_PROTECT_CALL({
        typedef jobject (*fn1)(JNIEnv *, jobject, void *);
        r = ((fn1)n->fn)(g_guard_env, (jobject)gclass_intern(n->cls), gstr((const char *)in));
    });
    if (!r || !is_gobj(r)) { snprintf(out, outcap, "ERR null result"); return; }
    const char *s = ((GObj *)r)->kind == 1 ? gstr_chars((jstring)r) : "";
    static char b64o[196608];
    b64_enc((const unsigned char *)s, (int)strlen(s), b64o, sizeof b64o);
    snprintf(out, outcap, "OK %s", b64o);
}

static void op_noxsign(const char *a64, const char *b64s, const char *c64, char *out, int outcap) {
    GNative *n = gnat_find_any("noxSign");
    if (!n || strcmp(n->sig, "(Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;)Ljava/lang/String;") != 0) {
        snprintf(out, outcap, "ERR no native noxSign");
        return;
    }
    unsigned char a[8192], b[8192], c[8192];
    int al = b64_dec(a64, a, sizeof a - 1), bl = b64_dec(b64s, b, sizeof b - 1), cl = b64_dec(c64, c, sizeof c - 1);
    if (al < 0 || bl < 0 || cl < 0) { snprintf(out, outcap, "ERR b64"); return; }
    a[al] = 0; b[bl] = 0; c[cl] = 0;
    jobject r = NULL;
    GUARD_PROTECT_CALL({
        typedef jobject (*fn3)(JNIEnv *, jobject, void *, void *, void *);
        r = ((fn3)n->fn)(g_guard_env, (jobject)gclass_intern(n->cls),
                         gstr((const char *)a), gstr((const char *)b), gstr((const char *)c));
    });
    if (!r || !is_gobj(r)) { snprintf(out, outcap, "ERR null result"); return; }
    const char *s = ((GObj *)r)->kind == 1 ? gstr_chars((jstring)r) : "";
    static char b64o[65536];
    b64_enc((const unsigned char *)s, (int)strlen(s), b64o, sizeof b64o);
    snprintf(out, outcap, "OK %s", b64o);
}

static void op_calc(const char *inb64, char *out, int outcap) {
    GNative *n = gnat_find_any("calcResult");
    if (!n || strcmp(n->sig, "([I)[I") != 0) { snprintf(out, outcap, "ERR no native calcResult"); return; }
    static int vals[1024];
    int n2 = b64_dec(inb64, (unsigned char *)vals, sizeof vals) / 4;
    if (n2 < 0) { snprintf(out, outcap, "ERR b64"); return; }
    JIntArray *ia = new_int_array(n2 > 0 ? n2 : 1);
    for (int i = 0; i < n2; i++) ia->v[i] = vals[i];
    jobject r = NULL;
    GUARD_PROTECT_CALL({
        typedef jobject (*fnA)(JNIEnv *, jobject, void *);
        r = ((fnA)n->fn)(g_guard_env, (jobject)gclass_intern(n->cls), ia);
    });
    if (!r || !is_int_array(r)) { snprintf(out, outcap, "ERR null result"); return; }
    JIntArray *ra = (JIntArray *)r;
    static char b64o[16384];
    b64_enc((const unsigned char *)ra->v, ra->n * 4, b64o, sizeof b64o);
    snprintf(out, outcap, "OK %s", b64o);
}

// proxyInvoke：<nP> 组 prefs 对 + <nM> 组 map 对（全 b64），OK3 <status> <mimeB64> <bodyB64|->
static void op_proxy(char *args, char *out, int outcap) {
    printf("[guard] op_proxy 进入\n");
    GNative *n = gnat_find_any("proxyInvoke");
    // ⚠ 返回类型是 Object[]（签名带 [），早版硬编码漏了 [ 导致恒 ERR（2026-09-24 实测）
    if (!n || strcmp(n->sig, "(Ljava/lang/Object;Ljava/lang/Object;)[Ljava/lang/Object;") != 0) {
        snprintf(out, outcap, "ERR no native proxyInvoke (sig=%s)", n ? n->sig : "null");
        return;
    }
    char *save = NULL;
    char *npS = strtok_r(args, " \t\r\n", &save);
    int np = npS ? atoi(npS) : 0;
    static char kb[256], vb[8192];
    for (int i = 0; i < np; i++) {
        char *k = strtok_r(NULL, " \t\r\n", &save), *v = strtok_r(NULL, " \t\r\n", &save);
        if (!k || !v) break;
        // ⚠ b64_dec 不写终止符，必须按返回长度截断（否则 static 残留污染键名 → do= 空）
        int kl = b64_dec(k, (unsigned char *)kb, sizeof kb - 1);
        int vl = b64_dec(v, (unsigned char *)vb, sizeof vb - 1);
        if (kl < 0) continue;
        kb[kl] = 0; if (vl >= 0) vb[vl] = 0; else vb[0] = 0;
        gpref_put(kb, vb);
    }
    char *nmS = strtok_r(NULL, " \t\r\n", &save);
    int nm = nmS ? atoi(nmS) : 0;
    GObj *map = gobj_new("java/util/HashMap", 2);
    for (int i = 0; i < nm; i++) {
        char *k = strtok_r(NULL, " \t\r\n", &save), *v = strtok_r(NULL, " \t\r\n", &save);
        if (!k || !v) break;
        int kl = b64_dec(k, (unsigned char *)kb, sizeof kb - 1);
        int vl = b64_dec(v, (unsigned char *)vb, sizeof vb - 1);
        if (kl < 0) continue;
        kb[kl] = 0; if (vl >= 0) vb[vl] = 0; else vb[0] = 0;
        gput_field(map, kb, 2, 0, strdup(vb));
    }
    printf("[guard] proxyInvoke: prefs %d 对, map %d 对 (do=%s)\n", np, nm, nz((const char *)gget_optr(map, "do")));
    jobject r = NULL;
    GUARD_PROTECT_CALL({
        typedef jobject (*fn2)(JNIEnv *, jobject, void *, void *);
        r = ((fn2)n->fn)(g_guard_env, (jobject)gclass_intern(n->cls), NULL, (jobject)map);
    });
    if (!r) { snprintf(out, outcap, "ERR proxyInvoke 返回 null"); return; }

    // 结果捕获：Object[]{status, mime, stream?} 尽力解析
    static unsigned char bodyBuf[262144];
    if (is_obj_array(r)) {
        JObjArray *oa = (JObjArray *)r;
        int status = 200;
        char mime[64] = "text/html";
        int haveBody = 0, bodylen = 0;
        for (int i = 0; i < oa->n; i++) {
            void *el = oa->v[i];
            if (!el) continue;
            if (is_gobj(el)) {
                GObj *g = (GObj *)el;
                if (g->kind == 1) {
                    const char *s = gstr_chars((jstring)el);
                    if (s[0] >= '0' && s[0] <= '9' && strlen(s) <= 4) status = atoi(s);
                    else snprintf(mime, sizeof mime, "%s", s);
                } else if (!strcmp(g->cls, "java/lang/Integer")) {
                    status = (int)gget_iv(g, "v");
                } else {
                    void *bytes = gget_optr(g, "bytes");
                    if (bytes && is_byte_array(bytes)) {
                        JByteArray *ba = (JByteArray *)bytes;
                        bodylen = ba->n < (int)sizeof bodyBuf ? ba->n : (int)sizeof bodyBuf;
                        memcpy(bodyBuf, ba->b, (size_t)bodylen);
                        haveBody = 1;
                    } else if (!strcmp(g->cls, "java/io/InputStream")) {
                        GRes *rs = (GRes *)gget_optr(g, "rs");
                        if (rs) {
                            bodylen = rs->len < (int)sizeof bodyBuf ? rs->len : (int)sizeof bodyBuf;
                            memcpy(bodyBuf, rs->data, (size_t)bodylen);
                            haveBody = 1;
                        }
                    }
                }
            } else if (is_byte_array(el)) {
                JByteArray *ba = (JByteArray *)el;
                bodylen = ba->n < (int)sizeof bodyBuf ? ba->n : (int)sizeof bodyBuf;
                memcpy(bodyBuf, ba->b, (size_t)bodylen);
                haveBody = 1;
            }
        }
        printf("[guard] proxyInvoke 结果: status=%d mime=%s body=%d\n", status, mime, haveBody ? bodylen : -1);
        if (haveBody) {
            static char mb[128], bb[524288];
            b64_enc((const unsigned char *)mime, (int)strlen(mime), mb, sizeof mb);
            b64_enc(bodyBuf, bodylen, bb, sizeof bb);
            snprintf(out, outcap, "OK3 %d %s %s", status, mb, bb);
        } else {
            snprintf(out, outcap, "OK3 %d %s -", status, mime);
        }
        return;
    }
    if (is_gobj(r)) {
        const char *s = ((GObj *)r)->kind == 1 ? gstr_chars((jstring)r) : "";
        static char bb[524288];
        b64_enc((const unsigned char *)s, (int)strlen(s), bb, sizeof bb);
        snprintf(out, outcap, "OK3 200 dGV4dC9odG1s %s", bb);
        return;
    }
    snprintf(out, outcap, "ERR 未能识别的返回类型");
}

// ═══════════ 加载 so（GLOAD，guard 线程执行）═══════════

static GObj *g_ctx_stub(void) { return gobj_new("android/content/Context", 2); }

// 预热：getLoader 触发解壳序列（decrypt 依赖其内部状态，同 unidbg 会话）
static void guard_warmup(void) {
    GNative *gl = gnat_find_any("getLoader");
    if (!gl || strcmp(gl->sig, "(Ljava/lang/Object;)Ljava/lang/Object;") != 0) {
        printf("[guard] ⚠ 无 getLoader native，跳过预热\n");
        return;
    }
    printf("[guard] ── getLoader 预热 ──\n");
    GUARD_PROTECT_CALL({
        typedef jobject (*fn1)(JNIEnv *, jobject, void *);
        ((fn1)gl->fn)(g_guard_env, (jobject)gclass_intern(gl->cls), (jobject)g_ctx_stub());
    });
    printf("[guard] ── 预热完成 ──\n");
}

static void guard_do_load(const char *jarhash) {
    snprintf(g_guard_jar, sizeof g_guard_jar, "%s", jarhash);
    // ⚠ /data 不存在（initramfs ramfs），mkdir 非递归必须逐级建（否则 so 落盘失败）
    mkdir("/data", 0777);
    mkdir("/data/guard", 0777);
    // ① 下载 so（宿主从 raw jar 挑 aarch64 的）
    unsigned char *so = NULL; int solen = 0;
    char path[256];
    snprintf(path, sizeof path, "/res?jar=%s&name=__so__", g_guard_jar);
    printf("[guard] 开始拉取 so：%s（控制口 %d）\n", path, g_ctrl_port);
    if (ghttp_get_mem("10.0.2.2", g_ctrl_port, path, &so, &solen) != 0) {
        printf("[guard] ✗ 从宿主取 guard so 失败\n");
        guard_report_big("guard", "error: 从宿主取 guard so 失败（jar 未注册？）");
        return;
    }
    FILE *f = fopen("/data/guard/guard.so", "wb");
    if (!f || fwrite(so, 1, (size_t)solen, f) != (size_t)solen) {
        if (f) fclose(f);
        free(so);
        guard_report_big("guard", "error: so 落盘失败");
        return;
    }
    fclose(f);
    free(so);
    printf("[guard] so 已落盘（%d 字节）\n", solen);

    // ② dlopen + JNI_OnLoad
    g_guard_so = dlopen("/data/guard/guard.so", RTLD_NOW);
    if (!g_guard_so) {
        char m[256];
        snprintf(m, sizeof m, "error: dlopen 失败: %s", dlerror());
        guard_report_big("guard", m);
        return;
    }
    printf("[guard] guard so 已加载\n");
    typedef jint (*fn_onload)(JavaVM *, void *);
    fn_onload onload = (fn_onload)dlsym(g_guard_so, "JNI_OnLoad");
    if (!onload) { guard_report_big("guard", "error: 无 JNI_OnLoad"); return; }
    {   // 崩溃归一化用的基址
        Dl_info di; memset(&di, 0, sizeof di);
        if (dladdr((void *)onload, &di) && di.dli_fbase)
            g_guard_base = (unsigned long)(uintptr_t)di.dli_fbase;
        printf("[guard] so 基址 = 0x%lx\n", g_guard_base);
    }
    jint vr = 0;
    GUARD_PROTECT_CALL({ vr = onload(g_guard_vm, NULL); });
    printf("[guard] JNI_OnLoad → 0x%x，捕获 native %d 个\n", vr, g_gnatn);
    if (g_gnatn == 0)
        printf("[guard] ⚠ JNI_OnLoad 未注册 natives（可能延迟注册）——继续 getLoader 预热观察\n");

    // ③ 预热（解壳序列）：natives 可能在这里才注册
    guard_warmup();

    if (g_gnatn == 0) {
        guard_report_big("guard", "error: RegisterNatives 未捕获（JNI_OnLoad 与 getLoader 均未触发）");
        return;
    }

    g_guard_ready = 1;
    char m[128];
    snprintf(m, sizeof m, "ready: natives=%d", g_gnatn);
    guard_report_big("guard", m);
}

// ═══════════ UIR：用户操作 → so 的 native listener ═══════════

static void guard_do_uir(int seq, int which) {
    GDialog *d = dialog_by_seq(seq);
    if (!d) { printf("[guard-ui] UIR seq=%d 无挂起对话框\n", seq); return; }
    GObj *L = which >= 0 ? d->itemsL : (which == -1 ? d->posL : which == -2 ? d->negL : d->neuL);
    printf("[guard-ui] UIR seq=%d which=%d listener=%s\n", seq, which,
           L && is_gobj(L) ? ((GObj *)L)->cls : "(无)");
    if (L && is_gobj(L))
        guard_run_void(L, "onClick", "(Landroid/content/DialogInterface;I)V", (jobject)d->dialog, which);
    // dismiss 监听（Android 语义：点击后对话框关闭）
    if (d->dismissL && is_gobj(d->dismissL))
        guard_run_void(d->dismissL, "onDismiss", "(Landroid/content/DialogInterface;)V", (jobject)d->dialog, 0);
    dialog_dismiss(d, 0);
}

// ═══════════ guard 服务线程 ═══════════

static void guard_handle_conn(int c) {
    static char req[1 << 20], resp[1 << 21];
    int n = 0;
    while (n < (int)sizeof req - 1) {
        int r = (int)read(c, req + n, 1);   // 逐字节读行（行式协议，量小无所谓）
        if (r <= 0) { close(c); return; }
        if (req[n] == '\n') break;
        n++;
    }
    req[n] = 0;
    while (n > 0 && req[n - 1] == '\r') req[--n] = 0;
    printf("[guard] ← %.100s%s\n", req, n > 100 ? "…" : "");

    char *save = NULL;
    char *op = strtok_r(req, " \t", &save);
    if (!op) { snprintf(resp, sizeof resp, "ERR empty"); goto done; }

    if (!strcmp(op, "PING")) snprintf(resp, sizeof resp, "OK %s", g_guard_ready ? "guard-ready" : "guard-loading");
    else if (!strcmp(op, "DECRYPT")) { char *a = save; while (a && *a == ' ') a++; if (a && a[0]) op_one_arg("decrypt", a, resp, sizeof resp); else snprintf(resp, sizeof resp, "ERR arg"); }
    else if (!strcmp(op, "ENCRYPT")) { char *a = save; while (a && *a == ' ') a++; if (a && a[0]) op_one_arg("encrypt", a, resp, sizeof resp); else snprintf(resp, sizeof resp, "ERR arg"); }
    else if (!strcmp(op, "MD5"))     { char *a = save; while (a && *a == ' ') a++; if (a && a[0]) op_one_arg("native_ting_md5", a, resp, sizeof resp); else snprintf(resp, sizeof resp, "ERR arg"); }
    else if (!strcmp(op, "NOXSIGN")) {
        char *a = strtok_r(NULL, " \t\r\n", &save), *b = strtok_r(NULL, " \t\r\n", &save), *cc = strtok_r(NULL, " \t\r\n", &save);
        if (a && b && cc) op_noxsign(a, b, cc, resp, sizeof resp);
        else snprintf(resp, sizeof resp, "ERR args");
    }
    else if (!strcmp(op, "CALC")) { char *a = save; while (a && *a == ' ') a++; if (a && a[0]) op_calc(a, resp, sizeof resp); else snprintf(resp, sizeof resp, "ERR arg"); }
    else if (!strcmp(op, "PROXY")) {
        printf("[guard] PROXY op: ready=%d arg0=%.20s\n", g_guard_ready, save && save[0] ? save : "(空)");
        if (g_guard_ready && save && save[0]) op_proxy(save, resp, sizeof resp);
        else snprintf(resp, sizeof resp, "ERR %s", g_guard_ready ? "arg" : "guard 未就绪");
    }
    else snprintf(resp, sizeof resp, "ERR unknown op %s", op);

done: {
        printf("[guard] → %.100s%s\n", resp, strlen(resp) > 100 ? "…" : "");
        size_t L = strlen(resp);
        size_t w = 0;
        while (w < L) {
            ssize_t k = write(c, resp + w, L - w);
            if (k <= 0) break;
            w += (size_t)k;
        }
        ssize_t k2 = write(c, "\n", 1);
        (void)k2;
    }
    close(c);
}

static void guard_pump_cmds(void) {
    if (g_gload_req) {
        g_gload_req = 0;
        char jar[80];
        snprintf(jar, sizeof jar, "%s", g_gload_jar);
        guard_do_load(jar);
    }
    if (g_uir_flag) {
        g_uir_flag = 0;
        guard_do_uir(g_uir_seq, g_uir_which);
    }
}

static void *guard_entry(void *arg) {
    (void)arg;
    g_tls_env = g_guard_iface_ptr;            // 本线程所有 so 调用走 guard env
    struct sigaction sa;
    memset(&sa, 0, sizeof sa);
    sa.sa_sigaction = g_segv_handler;
    sa.sa_flags = SA_SIGINFO | SA_NODEFER;
    sigaction(SIGSEGV, &sa, NULL);
    sigaction(SIGBUS, &sa, NULL);

    int ls = socket(AF_INET, SOCK_STREAM, 0);
    if (ls < 0) { printf("[guard] ✗ socket 失败\n"); return NULL; }
    int one = 1;
    setsockopt(ls, SOL_SOCKET, SO_REUSEADDR, &one, sizeof one);
    struct sockaddr_in a;
    memset(&a, 0, sizeof a);
    a.sin_family = AF_INET;
    a.sin_port = htons((unsigned short)g_guard_port);
    a.sin_addr.s_addr = htonl(INADDR_ANY);
    if (bind(ls, (struct sockaddr *)&a, sizeof a) != 0 || listen(ls, 8) != 0) {
        printf("[guard] ✗ 监听 %d 失败 errno=%d\n", g_guard_port, errno);
        return NULL;
    }
    printf("[guard] ✅ 解密服务监听 0.0.0.0:%d（等待宿主 GLOAD + 桥接入）\n", g_guard_port);
    fflush(stdout);

    for (;;) {
        fd_set rs;
        FD_ZERO(&rs);
        FD_SET(ls, &rs);
        int mx = ls;
        if (g_gwake[0] >= 0) { FD_SET(g_gwake[0], &rs); if (g_gwake[0] > mx) mx = g_gwake[0]; }
        int r = select(mx + 1, &rs, NULL, NULL, NULL);
        if (r < 0) { usleep(50000); continue; }
        if (g_gwake[0] >= 0 && FD_ISSET(g_gwake[0], &rs)) {
            char drain[64];
            while (read(g_gwake[0], drain, sizeof drain) > 0) { }
            guard_pump_cmds();
        }
        if (FD_ISSET(ls, &rs)) {
            int c = accept(ls, NULL, NULL);
            if (c >= 0) guard_handle_conn(c);
        }
    }
    return NULL;
}

// 主循环钩子（ctrlloop.c 调用）
void guard_on_cmd(const char *cmd) {
    if (!strncmp(cmd, "GLOAD ", 6)) {
        snprintf(g_gload_jar, sizeof g_gload_jar, "%s", cmd + 6);
        char *nl = strchr(g_gload_jar, '\n');
        if (nl) *nl = 0;
        g_gload_req = 1;
        if (g_gwake[1] >= 0) {
            ssize_t k = write(g_gwake[1], "L", 1);
            (void)k;
        }
        printf("[guard] 收到 GLOAD %s（guard 线程处理）\n", g_gload_jar);
    } else if (!strncmp(cmd, "UIR ", 4)) {
        int seq = 0, which = 0;
        if (sscanf(cmd + 4, "%d %d", &seq, &which) == 2) {
            g_uir_seq = seq;
            g_uir_which = which;
            g_uir_flag = 1;
            if (g_gwake[1] >= 0) {
                ssize_t k = write(g_gwake[1], "U", 1);
                (void)k;
            }
        }
    }
}

int guard_init(void) {
    const char *p = getenv("GUARD_PORT");
    g_guard_port = p ? atoi(p) : 0;
    if (g_guard_port <= 0) {
        printf("[guard] 未设 GUARD_PORT，guard 模块关闭\n");
        return 0;
    }

    // guard env 装配：在 thunder 槽位基础上覆盖 guard 专属实现
    static struct JNINativeInterface iface;
    memset(&iface, 0, sizeof(iface));
    iface = *((struct JNINativeInterface *)g_env_ptr);   // 先拷 thunder 的已实现槽位（数组等）
    iface.GetVersion = my_GetVersion;
    iface.FindClass = g_FindClass;
    iface.GetObjectClass = g_GetObjectClass;
    iface.GetMethodID = g_GetMethodID;
    iface.GetStaticMethodID = g_GetMethodID;
    iface.GetFieldID = g_GetFieldID;
    iface.GetStaticFieldID = g_GetFieldID;
    iface.NewStringUTF = g_NewStringUTF;
    iface.GetStringUTFChars = g_GetStringUTFChars;
    iface.ReleaseStringUTFChars = g_ReleaseStringUTFChars;
    iface.GetStringUTFLength = g_GetStringUTFLength;
    iface.NewObject = g_NewObject;
    iface.NewObjectV = g_NewObjectV;
    iface.NewObjectA = g_NewObjectA;
    iface.CallObjectMethod = g_CallObjectMethod;
    iface.CallObjectMethodV = g_CallObjectMethodV;
    iface.CallObjectMethodA = g_CallObjectMethodA;
    iface.CallStaticObjectMethod = g_CallStaticObjectMethod;
    iface.CallStaticObjectMethodV = g_CallStaticObjectMethodV;
    iface.CallStaticObjectMethodA = g_CallStaticObjectMethodA;
    iface.CallVoidMethod = g_CallVoidMethod;
    iface.CallVoidMethodV = g_CallVoidMethodV;
    iface.CallVoidMethodA = g_CallVoidMethodA;
    iface.CallStaticVoidMethod = g_CallVoidMethod;
    iface.CallStaticVoidMethodV = g_CallVoidMethodV;
    iface.CallStaticVoidMethodA = g_CallVoidMethodA;
    iface.CallIntMethod = g_CallIntMethod;
    iface.CallIntMethodV = g_CallIntMethodV;
    iface.CallIntMethodA = g_CallIntMethodA;
    iface.CallBooleanMethod = g_CallBooleanMethod;
    iface.CallBooleanMethodV = g_CallBooleanMethodV;
    iface.CallLongMethod = g_CallLongMethod;
    iface.CallLongMethodV = g_CallLongMethodV;
    iface.GetObjectField = g_GetObjectField;
    iface.SetObjectField = g_SetObjectField;
    iface.GetIntField = g_GetIntField;
    iface.SetIntField = g_SetIntField;
    iface.GetLongField = g_GetLongField;
    iface.SetLongField = g_SetLongField;
    iface.GetBooleanField = g_GetBooleanField;
    iface.SetBooleanField = g_SetBooleanField;
    iface.GetStaticObjectField = g_GetStaticObjectField;
    iface.GetStaticIntField = g_GetStaticIntField;
    iface.RegisterNatives = g_RegisterNatives;
    iface.NewGlobalRef = g_NewGlobalRef;
    iface.DeleteGlobalRef = g_DeleteGlobalRef;
    iface.NewLocalRef = g_NewLocalRef;
    iface.DeleteLocalRef = g_DeleteGlobalRef;
    iface.IsSameObject = g_IsSameObject;
    iface.ThrowNew = g_ThrowNew;
    iface.ExceptionOccurred = g_ExceptionOccurred;
    iface.ExceptionCheck = g_ExceptionCheck;
    iface.ExceptionClear = g_ExceptionClear;
    iface.GetJavaVM = g_GetJavaVM;
    iface.MonitorEnter = g_MonitorEnter;
    iface.MonitorExit = g_MonitorExit;
    iface.GetDirectBufferAddress = g_GetDirectBufferAddress;
    iface.NewDirectByteBuffer = g_NewDirectByteBuffer;
    // 剩余空槽位装陷阱（谁被调用就能从日志看到槽位名）
    {
        void **tbl = (void **)&iface;
        for (int i = 0; i < JNI_SLOT_COUNT; i++)
            if (!tbl[i]) tbl[i] = JNI_TRAPS[i];
    }
    g_guard_iface_ptr = &iface;
    g_guard_cell = (JNIEnv)g_guard_iface_ptr;
    g_guard_env = &g_guard_cell;
    g_guard_vm = &g_vm;

    if (pipe(g_gwake) != 0) { g_gwake[0] = g_gwake[1] = -1; }
    else {
        // ⚠ 读端必须非阻塞：drain 循环 `while (read(...) > 0)` 在管道空时会**永久阻塞**
        //   （2026-09-24 实测：guard 线程卡死在 drain，GLOAD/PROXY 全部无人处理）
        int fl = fcntl(g_gwake[0], F_GETFL, 0);
        fcntl(g_gwake[0], F_SETFL, fl | O_NONBLOCK);
    }
    pthread_t th;
    if (pthread_create(&th, NULL, guard_entry, NULL) != 0) {
        printf("[guard] ✗ guard 线程起不来\n");
        return 1;
    }
    printf("[guard] 模块就绪（端口 %d）\n", g_guard_port);
    return 0;
}
