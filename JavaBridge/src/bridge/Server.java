package bridge;

import android.content.Context;
import com.github.catvod.crawler.SpiderApi;
import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.File;
import java.io.InputStreamReader;
import java.lang.reflect.Method;
import java.net.URL;
import java.net.URLClassLoader;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;

/**
 * TVBox spider jar 桌面桥（常驻进程，stdin/stdout 每行一条 JSON）。
 * 由 CatClawVideo（.NET）启动：classpath 含 vendor/deps/* 与 converted/*.jar。
 *
 * 协议：
 * → {"id":1,"op":"load","site":"xiaoya","className":"AListSh","ext":"..."}  → {"id":1,"ok":true}
 * → {"id":2,"op":"call","site":"xiaoya","method":"categoryContent","args":["电影","1"]}
 * ← {"id":2,"ok":true,"result":"{...协议 JSON...}"}
 * → {"op":"exit"}
 */
public class Server {

    private static final java.util.concurrent.ConcurrentHashMap<String, Object> SPIDERS = new java.util.concurrent.ConcurrentHashMap<>();
    /** jar 集合 -> 类加载器。按 jar 集合分桶，而不是全局单例。 */
    private static final HashMap<String, URLClassLoader> LOADERS = new HashMap<>();
    private static final Object LOCK = new Object();

    /**
     * 懒构建：把 C# 侧传入的转换后 jar 挂到 URLClassLoader。
     *
     * <p>必须按 jar 集合分别缓存。早期实现把加载器放在一个全局字段里、第二次调用直接返回，
     * 于是<b>只有第一个站点用到的那个 jar 生效</b>：订阅里 10 个 jar 站跨 5 个不同的 jar，
     * 后续站点一律报「jar 中找不到爬虫类」，看起来像站点坏了。</p>
     */
    private static URLClassLoader loaderFor(org.json.JSONArray jars) throws Exception {
        if (jars == null || jars.length() == 0) throw new IllegalStateException("no jars provided");
        URL[] urls = new URL[jars.length()];
        StringBuilder keyBuilder = new StringBuilder();
        for (int i = 0; i < jars.length(); i++) {
            File f = new File(jars.getString(i));
            if (!f.isFile()) throw new java.io.FileNotFoundException(f.getAbsolutePath());
            urls[i] = f.toURI().toURL();
            keyBuilder.append(f.getAbsolutePath()).append('\n');
        }
        String key = keyBuilder.toString();
        URLClassLoader cached = LOADERS.get(key);
        if (cached != null) return cached;
        URLClassLoader created = new SleepPatchingLoader(urls, Server.class.getClassLoader());
        LOADERS.put(key, created);
        return created;
    }

    public static void main(String[] args) throws Exception {
        // 把 AES/*/PKCS7Padding 别名到 PKCS5Padding（标准 JVM 不提供 PKCS7 命名，Android 提供）
        // → 否则爬虫的接口加解密直接失败（NoSuchAlgorithmException → aes decrypt fail）。见 Pkcs7Provider。
        Pkcs7Provider.install();
        // 强制 stdout/stderr 为 UTF-8（JVM 默认跟随 Windows 控制台代码页 GBK，中文会坏）
        System.setOut(new java.io.PrintStream(new java.io.FileOutputStream(java.io.FileDescriptor.out), true, "UTF-8"));
        System.setErr(new java.io.PrintStream(new java.io.FileOutputStream(java.io.FileDescriptor.err), true, "UTF-8"));
        // data 目录：spider 的 Context 文件操作都落在 App 约定的桥工作目录
        System.setProperty("data.dir", new File("data").getAbsolutePath());
        startParentWatchdog();
        BufferedReader in = new BufferedReader(new InputStreamReader(System.in, StandardCharsets.UTF_8));
        // ⚠ proxy op 必须与 call 并行：call(detailContent) 在主循环同步执行期间，spider 内部
        //   会同步发 HTTP 请求（do=config）→ 宿主 → proxy op。若在主循环排队，call 不返回
        //   proxy 就轮不到 → 死锁到 HTTP 超时 → spider 拿错误文本喂 Gson 炸（2026-09-24 实测）。
        //   协议响应按 id 匹配（宿主 RoundTripAsync），异步乱序输出安全。
        java.util.concurrent.ExecutorService proxyPool = java.util.concurrent.Executors.newFixedThreadPool(4);
        String line;
        while ((line = in.readLine()) != null) {
            if (line.isBlank()) continue;
            String out;
            long id;
            boolean async;
            final JSONObject req;
            try {
                req = new JSONObject(line);
                String op = req.optString("op");
                if ("exit".equals(op)) break;
                // 宿主回传对话框用户操作（无响应回执；线程池里回调 jar listener——
                // 回调可能继续弹下一个框/发网络请求）
                if ("ui-result".equals(op)) {
                    final JSONObject freq = req;
                    proxyPool.submit(() -> UiBridge.dispatchResult(freq));
                    continue;
                }
                id = req.optLong("id", -1);
                async = "proxy".equals(op);
            } catch (Exception e) {
                System.out.println(new JSONObject().put("id", -1).put("ok", false)
                        .put("error", "bad request: " + e).toString());
                continue;
            }
            final long fid = id;
            final String fop = req.optString("op");
            if (async) {
                proxyPool.submit(() -> {
                    String o;
                    try {
                        Object result = proxy(req.optString("site"), req.optJSONObject("query"), req.optString("outFile"));
                        o = new JSONObject().put("id", fid).put("ok", true)
                                .put("result", result == null ? JSONObject.NULL : result).toString();
                    } catch (Throwable t) {
                        Throwable c = t;
                        while (c.getCause() != null) c = c.getCause();
                        o = new JSONObject().put("id", fid).put("ok", false)
                                .put("error", c.getClass().getSimpleName() + ": " + c.getMessage()).toString();
                    }
                    System.out.println(o);
                });
                continue;
            }
            try {
                Object result = switch (fop) {
                    case "load" -> loadFull(req.optString("site"), req.optString("className"), req.optString("ext"), req.optJSONArray("jars"),
                            req.optString("shellJar", null), req.optString("rawJar", null), req.optString("realJar", null),
                            req.optInt("guardPort", 0));
                    case "call" -> call(req.optString("site"), req.optString("method"), req.optJSONArray("args"));
                    case "ping" -> "pong";
                    case "get-prefs" -> {
                        // 网盘登录态读取：jar 的 proxyInput/do=xx 推送把 Cookie 写 SharedPreferences.DATA，
                        // 宿主「已登录+启用中」对话框按它渲染状态（key 含 quark/uc/baidu/ali 等）
                        org.json.JSONArray arr = new org.json.JSONArray();
                        for (var e : android.content.PrefsStore.snapshot().entrySet()) {
                            Object v = e.getValue();
                            arr.put(new JSONObject().put("key", e.getKey())
                                    .put("value", v == null ? "" : v.toString()));
                        }
                        yield arr.toString();
                    }
                    default -> {
                        // 端口下发：桥内无 JNI（Android 走 TvBoxCompatBridge.SetProxyPort），走协议直写静态字段
                        if ("setProxyPort".equals(fop)) {
                            com.github.catvod.crawler.SpiderApi.setHostProxyPort(req.optInt("port"));
                            yield "ok";
                        }
                        throw new IllegalArgumentException("unknown op: " + fop);
                    }
                };
                out = new JSONObject().put("id", id).put("ok", true)
                        .put("result", result == null ? JSONObject.NULL : result).toString();
            } catch (Throwable t) {
                    Throwable c = t;
                    while (c.getCause() != null) c = c.getCause();
                    StringBuilder sb = new StringBuilder(c.getClass().getSimpleName() + ": " + c.getMessage());
                    for (int i = 0; i < Math.min(8, c.getStackTrace().length); i++)
                        sb.append(" | ").append(c.getStackTrace()[i]);
                    out = new JSONObject().put("id", id).put("ok", false)
                            .put("error", sb.toString()).toString();
                }
            System.out.println(out);
            System.out.flush();
        }
    }

    private static String load(String site, String className, String ext, org.json.JSONArray jars) throws Exception {
        return loadFull(site, className, ext, jars, null, null, null, 0);
    }

    /** 壳框架加载：shellJar=壳 dex 转换产物；rawJar=原始 Guard jar（assets/*.so 解密引擎）；realJar=解壳产物；
     *  guardPort=Guard QEMU 解密服务端口（0=未启用 → 解密走 unidbg 会话）。 */
    /**
     * 父进程看门狗：宿主被强杀时自行了断，不留孤儿 JVM。
     *
     * <p>为什么三条退出路径都要有：{@code op=exit} 只有宿主正常收尾才发得出；stdin 的 EOF
     * 也靠不住 —— Windows 上管道写句柄会被宿主另开的子进程（QEMU VM）继承走，父进程死了
     * 句柄还活着，{@code readLine()} 永远等不到 null；job object 在宿主自己已属于某个 job 时
     * {@code AssignProcessToJobObject} 直接失败（实测 win32=5）。2026-09-25 一次开发会话
     * 因此攒下 17 个 {@code java.exe}，其中一个把 {@code bridge.jar} 映射住，重打包必失败。</p>
     */
    private static void startParentWatchdog() {
        long ppid;
        try {
            ppid = Long.parseLong(System.getProperty("catclaw.ppid", "0"));
        } catch (Throwable t) {
            return;
        }
        if (ppid <= 0L || ppid == ProcessHandle.current().pid()) return;
        Thread t = new Thread(() -> {
            while (true) {
                try {
                    Thread.sleep(2000);
                } catch (InterruptedException e) {
                    return;
                }
                if (ProcessHandle.of(ppid).isPresent()) continue;
                System.err.println("[srv] 宿主 pid=" + ppid + " 已消失，桥自行退出");
                for (String name : android.content.PrefsStore.names()) {
                    try { android.content.PrefsStore.flush(name); } catch (Throwable ignored) { }
                }
                // halt 而不是 exit：proxy 线程池里有非守护线程，exit 可能挂住不返回
                Runtime.getRuntime().halt(0);
                return;
            }
        }, "ppid-watchdog");
        t.setDaemon(true);
        t.start();
    }

    private static String loadFull(String site, String className, String ext, org.json.JSONArray jars,
                                   String shellJar, String rawJar, String realJar, int guardPort) throws Exception {
        synchronized (LOCK) {
            if (SPIDERS.containsKey(site)) return "loaded";

            // Guard QEMU 解密通道（2026-09-24 用户拍板架构）：解密/签名/proxyInvoke 走 Guard VM
            // 里的 ftyguard so（ARM）；就绪后 unidbg 不再预热（首次回落时才懒起，见 GuardSession）
            if (guardPort > 0) QemuGuardChannel.setPort(guardPort);

            // 壳框架模式：shellJar/rawJar/realJar 由宿主下发（Guard 源）——壳实例跑在独立 loader，
            // DexNative 解密优先走 QEMU 通道（guardPort>0 时），unidbg 会话懒加载兜底
            if (shellJar != null && !shellJar.isEmpty()) {
                if (rawJar != null && !rawJar.isEmpty()) {
                    if (guardPort <= 0) GuardSession.ensureSession(new File(rawJar));
                    // QEMU 优先时不预热，但路径要记下：VM 连不上时 requireSession 的懒建兜底才有得建
                    else GuardSession.noteJar(new File(rawJar));
                }
                if (realJar != null && !realJar.isEmpty()) {
                    ClassLoader realLoader = GuardSession.setRealLoader(realJar, null);
                    // ⚠ realLoader 的 InitOrigin/Init 也要注入：真实类的解密器（merge.Ku.N）调
                    //   InitOrigin.context().getSharedPreferences——不注入则壳框架触碰真实类静态
                    //   时直接 NPE（2026-09-24 实测 Ku.N 堆栈）
                    Context realCtx = new android.app.Application();
                    for (String holder : new String[]{
                            "com.github.catvod.spider.InitOrigin",
                            "com.github.catvod.spider.Init",
                            "com.github.catvod.spider.merge.InitOrigin"}) {
                        injectStaticContext(realLoader, holder, realCtx);
                    }
                }
                URLClassLoader shellLoader = new SleepPatchingLoader(
                        new URL[]{new File(shellJar).toURI().toURL()}, Server.class.getClassLoader());
                Class<?> cls = shellLoader.loadClass("com.github.catvod.spider." + className);
                Object instance = cls.getDeclaredConstructor().newInstance();
                try { cls.getField("siteKey").set(instance, site); } catch (Throwable ignored) { }
                try {
                    cls.getMethod("initApi", com.github.catvod.crawler.SpiderApi.class)
                            .invoke(instance, new com.github.catvod.crawler.SpiderApi());
                } catch (NoSuchMethodException ignored) { }

                // ⚠ 壳框架自身的公共类（merge.InitOrigin/merge.Z 等）在 **shellLoader** 里——
                //   不注入 Context 的话壳的静态初始化直接 NPE（merge.Ku.N，2026-09-24 实测）。
                //   与真实类路径同一套注入序列：Application 桩 → InitOrigin/Init/merge.InitOrigin
                //   → siteKey → initApi → init。
                Context appContext = new android.app.Application();
                try {
                    Class<?> initCls = shellLoader.loadClass("com.github.catvod.spider.Init");
                    initCls.getMethod("init", Context.class).invoke(null, appContext);
                } catch (Throwable ignored) { }
                for (String holder : new String[]{
                        "com.github.catvod.spider.InitOrigin",
                        "com.github.catvod.spider.Init",
                        "com.github.catvod.spider.merge.InitOrigin"}) {
                    injectStaticContext(shellLoader, holder, appContext);
                }

                // ═══════════ TVBox ProtectedInitJar 对等实现（2026-09-24）═══════════
                // 壳 jar 的 Init 有三个**非静态**字段：
                //   ClassLoader oOo0oOo0Oo0oO0Oo / dalvik.system.DexClassLoader oOoOoOo0oOo0o0oO
                //   android.app.Application oOoOoOoOoOoOoO0o
                // TVBox 的 ProtectedInitJar.init() 会拿 Init.get() 单例，把 App 与
                // DexNative.getLoader() 返回的 DexClassLoader 分别反射注入进去；
                // **不注入则 Init.loader()/classLoader() 返回 null，jar 内部 loadClass 全废。**
                // 我们此前只注入了 static 上下文，漏了这一步。
                bindInitSingleton(shellLoader, appContext, realJar);
                try {
                    cls.getMethod("init", Context.class, String.class)
                            .invoke(instance, new Context(), ext == null ? "" : ext);
                } catch (NoSuchMethodException ignored) { }
                for (String holder : new String[]{
                        "com.github.catvod.spider.InitOrigin",
                        "com.github.catvod.spider.merge.InitOrigin"}) {
                    injectStaticContext(shellLoader, holder, appContext);
                }

                SPIDERS.put(site, instance);
                System.err.println("[srv] 壳框架已加载: " + site + " (" + className + ")");
                return "loaded";
            }

            URLClassLoader loader = loaderFor(jars);

            // ⚠⚠ 必须在**实例化之前**注入 TVBox 公共类的 Context/Application。
            //   实测 2026-09-16：csp_S_zpsGuard（JPan/ZPan 等一批站点）的构造函数与 init(ext)
            //   里就会调 InitOrigin.context().getSharedPreferences(...)；晚注入（原来放在本方法末尾）
            //   → 抛 "Cannot invoke android.content.Context.getSharedPreferences(String,int) because
            //   the return value of InitOrigin.context() is null" → **整站 load 失败**。
            //   用户可见症状极具误导性：搜索「0 个结果」，而日志里只有一句英文 NPE。
            Context appContext = new android.app.Application();
            try {
                Class<?> initCls = loader.loadClass("com.github.catvod.spider.Init");
                initCls.getMethod("init", Context.class).invoke(null, appContext);
            } catch (Throwable ignored) { }
            for (String holder : new String[]{
                    "com.github.catvod.spider.InitOrigin",
                    "com.github.catvod.spider.Init",
                    "com.github.catvod.spider.merge.InitOrigin"}) {
                injectStaticContext(loader, holder, appContext);
            }

            Class<?> cls = null;
            for (String p : new String[]{"com.github.catvod.spider.", "com.github.catvod.crawler.", ""}) {
                try { cls = loader.loadClass(p + className); break; } catch (ClassNotFoundException ignored) { }
            }
            if (cls == null) throw new ClassNotFoundException(className);

            Object instance = cls.getDeclaredConstructor().newInstance();

            // 与 TVBox JarLoader.getSpider 的调用序列一致：siteKey -> initApi -> init。
            // siteKey 供爬虫读取自身站点 key；initApi 注入 SpiderApi（XBPQ 等爬虫会覆盖它并调用
            // super.initApi，且把实例存进字段后用于日志/端口，不注入则后续 NPE）。
            try {
                cls.getField("siteKey").set(instance, site);
            } catch (Throwable ignored) { }
            try {
                cls.getMethod("initApi", SpiderApi.class).invoke(instance, new SpiderApi());
            } catch (NoSuchMethodException ignored) { }

            try {
                cls.getMethod("init", Context.class, String.class).invoke(instance, new Context(), ext == null ? "" : ext);
            } catch (NoSuchMethodException ignored) { }

            // 兜底再注入一次（有的爬虫在 init 里才把公共类换掉/重建单例）
            for (String holder : new String[]{"com.github.catvod.spider.InitOrigin", "com.github.catvod.spider.merge.InitOrigin"}) {
                injectStaticContext(loader, holder, appContext);
            }

            SPIDERS.put(site, instance);
            return "loaded";
        }
    }

    /**
     * 把宿主桩 Context 注入 TVBox 公共类的静态 context：
     * ① 静态字段（<c>context</c> / <c>mContext</c>，多数分支就是 public static Context context）；
     * ② 静态 init(Context) / init(Context, String)。
     * 类不存在或注入失败都静默忽略（不同年代的 jar 公共类名/签名不一致，注入是**尽力而为**）。
     */
    /**
     * TVBox {@code ProtectedInitJar.init()} 的对等实现：把 App 与 DexClassLoader 反射注入
     * 壳框架 {@code Init} 的**非静态**字段。
     *
     * <p>壳 jar 的 Init 结构（javap 实测）：
     * <pre>
     *   private ClassLoader                  oOo0oOo0Oo0oO0Oo;
     *   private dalvik.system.DexClassLoader oOoOoOo0oOo0o0oO;   ← bindDexLoader 的目标
     *   private android.app.Application      oOoOoOoOoOoOoO0o;   ← bindContext 的目标
     *   public static Init get();  public static DexClassLoader loader();
     * </pre>
     * 不注入 ⇒ {@code Init.loader() / classLoader()} 返回 <b>null</b> ⇒ jar 内部 loadClass 全废。</p>
     *
     * @param loader  {@code Init} 所在 ClassLoader（壳 jar 的 loader）
     * @param ctx     注入用的 Application 桩
     * @param realJar 解壳产物（.jar），造 {@code dalvik.system.DexClassLoader} 时指向它；空则跳过 loader 注入
     */
    private static void bindInitSingleton(ClassLoader loader, Context ctx, String realJar) {
        Object init;
        Class<?> initCls;
        try {
            initCls = loader.loadClass("com.github.catvod.spider.Init");
            init = initCls.getMethod("get").invoke(null);
        } catch (Throwable t) {
            System.err.println("[srv] Init.get() 不可用，跳过单例注入: " + t);
            return;
        }
        if (init == null) {
            System.err.println("[srv] Init.get() 返回 null，跳过单例注入");
            return;
        }

        Object dexLoader = null;
        if (realJar != null && !realJar.isEmpty() && new File(realJar).isFile()) {
            try {
                dexLoader = new dalvik.system.DexClassLoader(realJar, loader);
            } catch (Throwable t) {
                System.err.println("[srv] 造 DexClassLoader 失败: " + t.getMessage());
            }
        }

        int ctxN = 0, dalN = 0;
        for (Class<?> t = initCls; t != null && t != Object.class; t = t.getSuperclass()) {
            for (java.lang.reflect.Field f : t.getDeclaredFields()) {
                if (java.lang.reflect.Modifier.isStatic(f.getModifiers())) continue;
                try {
                    f.setAccessible(true);
                    if (dexLoader != null && dalvik.system.DexClassLoader.class.isAssignableFrom(f.getType())) {
                        f.set(init, dexLoader);
                        dalN++;
                    } else if (Context.class.isAssignableFrom(f.getType())) {
                        f.set(init, ctx);
                        ctxN++;
                    }
                } catch (Throwable ignored) { }
            }
        }

        // TVBox 对加固包固定调这两个（其他 fork 的壳有；本壳 jar 无此方法 → 防御性调用即可）
        try {
            initCls.getMethod("replaceCloudDiskNames").invoke(null);
            System.err.println("[srv] Init.replaceCloudDiskNames() ✓");
        } catch (Throwable ignored) { }
        try {
            initCls.getMethod("startGoProxy", Context.class).invoke(null, ctx);
            System.err.println("[srv] Init.startGoProxy(ctx) ✓");
        } catch (Throwable ignored) { }

        System.err.println("[srv] Init 单例注入完成: Application×" + ctxN + "  DexClassLoader×" + dalN
                + (dexLoader != null ? "（" + new File(realJar).getName() + "）" : "（无 realJar，跳过）"));
    }

    private static void injectStaticContext(ClassLoader loader, String className, Context ctx) {
        try {
            Class<?> c = loader.loadClass(className);
            // ⚠ 必须注入 **Application**（不是裸 Context）：TVBox 的 InitOrigin 里
            //   `public static Application context()` 返回的就是 Application 实例，
            //   init(Context) 内部会强转成 Application —— 传裸 Context 会被 ClassCastException
            //   吞掉，context() 依旧返回 null（实测 2026-09-16：传 Context 无效，改 Application 后 NPE 消失）。
            //   ctx 由调用方传入（同一个实例注入到所有公共类，避免各自 new 出一堆孤岛）。
            for (String fn : new String[]{"context", "mContext", "appContext", "N"}) {
                try {
                    java.lang.reflect.Field f = c.getDeclaredField(fn);
                    f.setAccessible(true);
                    if (java.lang.reflect.Modifier.isStatic(f.getModifiers()) && f.get(null) == null) {
                        f.set(null, ctx);
                    }
                } catch (Throwable ignored) { }
            }
            for (java.lang.reflect.Method m : c.getDeclaredMethods()) {
                if (!"init".equals(m.getName()) || !java.lang.reflect.Modifier.isStatic(m.getModifiers())) continue;
                Class<?>[] ps = m.getParameterTypes();
                try {
                    m.setAccessible(true);
                    if (ps.length == 1 && ps[0] == Context.class) {
                        m.invoke(null, ctx);
                    } else if (ps.length == 2 && ps[0] == Context.class && ps[1] == String.class) {
                        m.invoke(null, ctx, "");
                    }
                } catch (Throwable ignored) { }
            }

            // ⚠ InitOrigin.i 是 <clinit> 里 `Class.forName("android.app.ActivityThread")` 存下来的
            //   Class<?>，它被 getActivity()/context() 直接用来 getDeclaredMethod("currentActivityThread")
            //   → 拿不到就直接 NPE（Cannot invoke "java.lang.Class.getDeclaredMethod(...)" because
            //   "com.github.catvod.spider.InitOrigin.i" is null，实测 2026-09-16）。
            //   桥里已有 android.app.ActivityThread 桩；这里再显式调一次 setClass 兜底，
            //   不依赖 jar 里那个被混淆的类名字符串解出来正好等于我们的桩名。
            try {
                java.lang.reflect.Method setClass = c.getMethod("setClass", Class.class);
                setClass.invoke(null, Class.forName("android.app.ActivityThread", false, Server.class.getClassLoader()));
            } catch (Throwable ignored) { }
        } catch (Throwable ignored) { }
    }

    private static String call(String site, String method, org.json.JSONArray args) throws Exception {
        // 慢调用留痕（>300ms 打 stderr → 宿主日志的 [jvm] stderr 行）：
        // 「播放页加载很慢」要能一眼看出是爬虫自身耗时还是宿主侧排队（实测 2026-09-16 需要此数据）
        final long t0 = System.currentTimeMillis();
        synchronized (LOCK) {
            for (int i = 0; i < args.length(); i++) {
                String a = args.optString(i);
                StringBuilder cp = new StringBuilder();
                a.chars().forEach(c -> cp.append(Integer.toHexString(c)).append(' '));
                System.err.println("[srv] arg[" + i + "] len=" + a.length() + " cps=" + cp);
            }
            Object instance = SPIDERS.get(site);
            if (instance == null) throw new IllegalStateException("site not loaded: " + site);
            Class<?> cls = instance.getClass();
            Object result;
            switch (method) {
                case "homeContent" -> result = cls.getMethod("homeContent", boolean.class)
                        .invoke(instance, args != null && args.length() > 0 && args.optBoolean(0));
                case "homeVideoContent" -> result = cls.getMethod("homeVideoContent").invoke(instance);
                case "categoryContent" -> result = cls.getMethod("categoryContent", String.class, String.class, boolean.class, HashMap.class)
                        .invoke(instance, args.optString(0), args.optString(1), false, new HashMap<String, String>());
                case "detailContent" -> {
                    List<String> ids = new ArrayList<>();
                    ids.add(args.optString(0));
                    result = cls.getMethod("detailContent", List.class).invoke(instance, ids);
                }
                case "searchContent" -> {
                    // 3 参（pg 为 String）优先，回退 2 参
                    try {
                        result = cls.getMethod("searchContent", String.class, boolean.class, String.class)
                                .invoke(instance, args.optString(0), false, args.optString(1));
                    } catch (NoSuchMethodException e) {
                        result = cls.getMethod("searchContent", String.class, boolean.class)
                                .invoke(instance, args.optString(0), false);
                    }
                }
                case "playerContent" -> result = cls.getMethod("playerContent", String.class, String.class, List.class)
                        .invoke(instance, args.optString(0), args.optString(1), new ArrayList<String>());
                // TVBox SourceViewModel.doAction 对等：卡片自带 action JSON（如网盘「登入自己网盘」）
                // → spider.action(String)。缺这条时宿主只能走 detailContent 兜底，
                // jar 里由 action 建出的原生对话框/扫码（Pan.showInputQRCode）永远到不了 UI 层。
                case "action" -> result = cls.getMethod("action", String.class)
                        .invoke(instance, args.optString(0));
                default -> throw new IllegalArgumentException("unknown method: " + method);
            }
            var out = result == null ? "{}" : result.toString();
            var dt = System.currentTimeMillis() - t0;
            if (dt > 300) {
                System.err.println("[srv] " + site + "." + method + " 耗时 " + dt + "ms，结果 " + out.length() + " 字节");
            }
            return out;
        }
    }

    /**
     * 回调爬虫的 {@code proxy(Map)}（TVBox {@code ApiConfig.proxyLocal} → {@code spider.proxy(param)} 语义）。
     *
     * <p>宿主本地 <c>/proxy?do=config|danmu|ck…</c> 的响应体必须由<b>爬虫自己</b>产生：
     * Guard 系网盘源（csp_MDriveGuard 等）的「云盘配置」JSON、荐片的 do=ck 握手都走这里。
     * 不转发的话爬虫拿到「missing url」文本 → 内部 Gson 解析炸
     * （{@code Expected BEGIN_OBJECT but was STRING}，Android 真机 2026-09-24 同因同果）。</p>
     *
     * <p>响应体写 {@code outFile}（stdout 每行一条 JSON，body 走文件避免转义/体积问题）；
     * result = {@code "<status>|<mime>|<len>"}。对齐 Android 端 SpiderProxyBridge.proxyToFile。</p>
     */
    private static String proxy(String site, org.json.JSONObject query, String outPath) throws Exception {
        final long t0 = System.currentTimeMillis();
        // ⚠ 绝不能持 LOCK：call(detailContent) 持锁执行中会同步回调本方法（spider 内部
        //   HTTP 请求 do=config → 宿主 → 本 op）——同一把锁 = 死锁到 HTTP 超时，spider
        //   拿错误文本喂 Gson 炸（Expected BEGIN_OBJECT but was STRING，2026-09-24 实测）。
        //   SPIDERS 已是 ConcurrentHashMap：load/call 互斥照旧（LOCK），proxy 无锁读。
        Object instance = SPIDERS.get(site);
        if (instance == null) throw new IllegalStateException("site not loaded: " + site);
        if (query == null) throw new IllegalArgumentException("proxy: missing query");

        HashMap<String, String> param = new HashMap<>();
        java.util.Iterator<String> keys = query.keys();
        while (keys.hasNext()) {
            String k = keys.next();
            param.put(k, query.optString(k));
        }

        // ① 实例方法：沿类层次找 proxy(Map)（爬虫可能覆写成 HashMap 形参，不能硬套 Map.class）。
        //    基类桩的 proxy 默认转 proxyLocal → null；爬虫没覆写时 rs 为 null，走 ②。
        Object[] rs = invokeProxyMethod(instance, param);

        // ② Guard 系网盘源平台分发：do=<平台> → Cloud_<平台>.proxy(Map)——
        //    夸父(夸克)/优汐(UC)/嘟嘟(百度)/阿狸(阿里) 的登录页/扫码/启停/推送
        //    都在各平台 Cloud 类的静态 proxy 里（真实类，可直接调）。
        //    TVBox 语义：ApiConfig.proxyLocal 按请求 do 分发到对应平台处理器。
        if (rs == null) {
            String doVal = param.getOrDefault("do", "");
            if (doVal.length() > 0 && doVal.matches("[a-zA-Z_0-9]+")) {
                try {
                    ClassLoader loader = instance.getClass().getClassLoader();
                    Class<?> clz = loader.loadClass("com.github.catvod.spider.Cloud_" + doVal);
                    java.lang.reflect.Method m = clz.getMethod("proxy", java.util.Map.class);
                    Object r = m.invoke(null, param);
                    if (r instanceof Object[] arr) rs = arr;
                } catch (ClassNotFoundException ignored) {
                } catch (Throwable t) {
                    Throwable root = t;
                    while (root.getCause() != null) root = root.getCause();
                    System.err.println("[srv] Cloud_" + doVal + ".proxy 异常: "
                            + root.getClass().getSimpleName() + ": " + root.getMessage());
                }
            }
        }

        // ③ Guard 系网盘源变体（Pan 家族）：无参静态 proxyInput()，返回契约与 proxy(Map) 相同
        if (rs == null) {
            try {
                java.lang.reflect.Method m = instance.getClass().getMethod("proxyInput");
                Object r = m.invoke(null);
                if (r instanceof Object[] arr) rs = arr;
            } catch (NoSuchMethodException ignored) {
            } catch (Throwable t) {
                Throwable root = t;
                while (root.getCause() != null) root = root.getCause();
                System.err.println("[srv] " + instance.getClass().getSimpleName()
                        + ".proxyInput 异常: " + root.getClass().getSimpleName() + ": " + root.getMessage());
            }
        }

        // ③ 静态回退：jar 内 com.github.catvod.spider.Proxy.proxy(Map)（TVBox JarLoader.invokeProxy
        //    语义；荐片 do=ck 握手由它处理）
        if (rs == null) {
            ClassLoader loader = instance.getClass().getClassLoader();
            try {
                Class<?> clz = loader.loadClass("com.github.catvod.spider.Proxy");
                java.lang.reflect.Method m = clz.getMethod("proxy", java.util.Map.class);
                Object r = m.invoke(null, param);
                if (!(r instanceof Object[] arr)) {
                    throw new IllegalStateException("Proxy.proxy() 返回 "
                            + (r == null ? "null" : r.getClass().getName()));
                }
                rs = arr;
            } catch (ClassNotFoundException e) {
                throw new IllegalStateException("该 jar 未提供 com.github.catvod.spider.Proxy");
            }
        }

            int status = rs.length > 0 && rs[0] instanceof Number n ? n.intValue() : 200;
            String mime = rs.length > 1 && rs[1] != null ? rs[1].toString() : "application/octet-stream";
            long written = 0;
            if (rs.length > 2 && rs[2] instanceof java.io.InputStream in) {
                java.io.File f = new java.io.File(outPath);
                java.io.File parent = f.getParentFile();
                if (parent != null && !parent.exists()) parent.mkdirs();
                try (java.io.FileOutputStream fout = new java.io.FileOutputStream(f)) {
                    byte[] buf = new byte[64 * 1024];
                    int n;
                    while ((n = in.read(buf)) > 0) {
                        fout.write(buf, 0, n);
                        written += n;
                    }
                } finally {
                    try { in.close(); } catch (Exception ignored) { }
                }
            }
            var dt = System.currentTimeMillis() - t0;
            System.err.println("[srv] " + site + ".proxy do=" + param.getOrDefault("do", "?")
                    + " → " + status + " " + mime + " " + written + "B，耗时 " + dt + "ms");
            return status + "|" + mime + "|" + written;
    }

    /** 沿类层次找名为 proxy、单 Map 形参的方法并调用；返回 Object[] 或 null（未覆写/返回空/异常）。 */
    private static Object[] invokeProxyMethod(Object spider, HashMap<String, String> param) {
        for (Class<?> c = spider.getClass(); c != null && c != Object.class; c = c.getSuperclass()) {
            for (java.lang.reflect.Method m : c.getDeclaredMethods()) {
                if (!"proxy".equals(m.getName()) || m.getParameterTypes().length != 1
                        || !java.util.Map.class.isAssignableFrom(m.getParameterTypes()[0])) continue;
                try {
                    m.setAccessible(true);
                    Object r = m.invoke(spider, param);
                    return r instanceof Object[] arr ? arr : null;
                } catch (Throwable t) {
                    Throwable root = t;
                    while (root.getCause() != null) root = root.getCause();
                    System.err.println("[srv] " + spider.getClass().getSimpleName()
                            + ".proxy 异常: " + root.getClass().getSimpleName() + ": " + root.getMessage());
                    return null;
                }
            }
        }
        return null;
    }
}
