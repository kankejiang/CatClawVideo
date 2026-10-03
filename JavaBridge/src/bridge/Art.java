package bridge;

import android.content.Context;
import android.content.SharedPreferences;

import java.io.File;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.util.HashMap;
import java.util.Map;

/**
 * 桥跑在 <b>真 ART（QEMU guest）</b> 里时用到的那一套：假 Application + DexClassLoader + 照搬 TVBox
 * {@code JarLoader.load}/{@code ProtectedInitJar.init} 的装载序列。
 *
 * <p>2026-09-25 P3 实测（{@code D:\Code\.shot\p3/run_all8.log}）：guest 里
 * {@code DexClassLoader(raw.jar) → Init.init(app) → new MyDriveGuard() → homeContent} 全通，
 * 壳自己的 arm64 {@code ftyguard_v8.so} 由 {@code System.load} 加载、native 解出 1MB 载荷后
 * <b>自己 new DexClassLoader</b>；{@code encrypt/decrypt} 2–4ms，{@code homeContent} 336ms。
 * 所以 dex2jar / converted 产物 / 手写 {@code DexNative} 替身这条链在 guest 里整体退役。</p>
 *
 * <p>与 TVBox 的一处刻意偏差：<b>不做</b> {@code ProtectedInitJar.check()} 那个 dex 扫描。
 * 它存在的唯一理由是"别去调 protected jar 的 {@code Init.init(Context)}，那里面有
 * {@code Process.killProcess}"。我们改成<b>先走</b> protected 注入序列（{@code Init.get()} +
 * {@code DexNative.getLoader(app)} + 反射灌字段，全程不执行壳的 init 字节码），
 * 注入不上才退回 {@code Init.init(ctx)} —— 同一效果，且不可能被反调试打死。</p>
 */
public final class Art {

    private Art() { }

    /** ART 里 {@code java.vm.name} 恒为 "Dalvik"；桌面 JRE 是 "OpenJDK 64-Bit Server VM"。 */
    public static boolean onArt() {
        return "Dalvik".equals(System.getProperty("java.vm.name"));
    }

    static File dir(String name) {
        File f = new File(new File(System.getProperty("data.dir", "data"), "art"), name);
        f.mkdirs();
        return f;
    }

    private static volatile App sApp;

    public static Context app() {
        if (sApp == null) {
            synchronized (Art.class) {
                if (sApp == null) sApp = new App();
            }
        }
        return sApp;
    }

    /**
     * ui_stub.dex 的「桩优先」加载器：dex 里的类（android/** 桩）自取，其余走 parent（boot）。
     * <p>boot classpath 覆盖是已证死路（mk_art_initrd.py 顶部注释：前置 -Xbootclasspath 也
     * 抢不回 android.app.Dialog 的解析），唯一可行点是在<b>jar 源自己的 loader 链</b>上换
     * 命名空间 —— 爬虫代码的 android.* 解析到 JRE 同款桩，Dialog/Toast 的 UI 事件才能上行。
     * DexFile.loadClass(name, this) 是 ART（Android 9）公开的按 dex 定义类原语。</p>
     */
    private static volatile ClassLoader sStubFirst;

    static ClassLoader stubFirst() {
        if (sStubFirst != null) return sStubFirst;
        synchronized (Art.class) {
            if (sStubFirst != null) return sStubFirst;
            ClassLoader boot = Art.class.getClassLoader();
            File f = new File("/ui_stub.dex");
            if (!f.isFile()) {
                System.err.println("[art] 无 /ui_stub.dex，jar 源走 boot 解析（无 UI 桩）");
                return sStubFirst = boot;
            }
            try {
                dalvik.system.DexFile dex = dalvik.system.DexFile.loadDex(
                        f.getAbsolutePath(), new File(dir("opt"), "ui_stub.odex").getAbsolutePath(), 0);
                sStubFirst = new StubFirstLoader(boot, dex);
                System.err.println("[art] UI 桩优先链就绪（" + f.getAbsolutePath() + "）");
                // 补同步：setProxyPort 可能在本方法之前就被调（宿主一连上桥就下发端口），
                // 那时桩链还不存在、同步被跳过 —— 这里从 boot 副本补写一次
                try {
                    Object cur = boot.loadClass("com.github.catvod.crawler.SpiderApi")
                            .getField("hostProxyPort").get(null);
                    if (cur instanceof Integer) setStubHostProxyPort((Integer) cur);
                } catch (Throwable ignored) { }
            } catch (Throwable t) {
                System.err.println("[art] ui_stub.dex 加载失败（回落 boot）: " + t);
                sStubFirst = boot;
            }
            return sStubFirst;
        }
    }

    /** dex 命中自取（含 boot 重复名 = 桩赢），其余 parent 优先。 */
    static final class StubFirstLoader extends ClassLoader {
        private final dalvik.system.DexFile dex;

        StubFirstLoader(ClassLoader parent, dalvik.system.DexFile dex) {
            super(parent);
            this.dex = dex;
        }

        @Override protected Class<?> loadClass(String name, boolean resolve) throws ClassNotFoundException {
            Class<?> c = findLoadedClass(name);
            if (c == null) {
                try { c = dex.loadClass(name, this); } catch (Throwable ignored) { }
            }
            if (c == null) return super.loadClass(name, resolve);
            if (resolve) resolveClass(c);
            return c;
        }
    }

    /**
     * 爬虫侧 Context：桩链在 → stub 命名空间的 Application（与 jar 代码同命名空间，类型身份一致
     * —— 真 App extends 真 Application，灌进按桩解析的 Context 字段会 ICCE）；否则真 App 桩。
     */
    static Object spiderCtx() {
        ClassLoader sf = stubFirst();
        if (sf != Art.class.getClassLoader()) {
            try {
                return sf.loadClass("android.app.Application").getMethod("getInstance").invoke(null);
            } catch (Throwable t) {
                System.err.println("[art] stub Application 创建失败，回落真 App: " + t);
            }
        }
        return app();
    }

    /**
     * 把宿主下发的 proxy 端口同步进桩命名空间的 SpiderApi 副本（ui_stub.dex 里还有一份 ——
     * 爬虫经 initApi 拿到的是它，只设 boot 副本 getAddress() 就回空串，云盘配置 URL 拼不出来）。
     */
    public static void setStubHostProxyPort(int port) {
        if (sStubFirst == null || sStubFirst == Art.class.getClassLoader()) return;
        try {
            Class<?> apiCls = sStubFirst.loadClass("com.github.catvod.crawler.SpiderApi");
            Field f = apiCls.getField("hostProxyPort");
            f.setInt(null, port);
        } catch (Throwable t) {
            System.err.println("[art] 桩 SpiderApi 端口同步失败: " + t);
        }
    }

    /**
     * 把壳的「Context 反射提供者」喂饱：TVBox 公共类 {@code InitOrigin} 用
     * {@code ActivityThread.currentActivityThread()} / {@code mApplication} 系反射链拿 Context，
     * guest 不是 zygote 起的，那条链在真 framework 里返回 null → 壳 fallback 出
     * {@code mBase=null} 的 ContextWrapper → 弹 UI 第一步
     * {@code getPackageManager()} 就 NPE（2026-09-26 栈：ContextWrapper:96 ← merge.cn.B）。
     *
     * <p>做法：真 ActivityThread 类**不能换**（boot classpath 覆盖是已证死路），
     * 改用 {@code Unsafe.allocateInstance} 绕构造器造一个真类实例（零副作用），
     * 反射填 {@code sCurrentActivityThread}（静态）/ {@code mApplication} /
     * {@code mInitialApplication} = 桥的 App 桩，{@code mActivities} 给空 ArrayMap
     * （壳遍历它取 Activity：拿 null → 走壳自己的无 Activity 降级路径，如 proxy/HTML 配置页，
     * 不去碰真 AlertDialog —— guest 里没有 WMS，真 UI 链走不通）。
     * 任一步失败只告警：行为与注入前一致（壳自己 NPE）。</p>
     */
    public static void injectActivityThread() {
        try {
            System.err.println("[art] inject#1 forName ActivityThread");
            Class<?> at = Class.forName("android.app.ActivityThread");
            Object thread;
            try {
                System.err.println("[art] inject#2 unsafe");
                Class<?> unsafeCls = Class.forName("sun.misc.Unsafe");
                java.lang.reflect.Field uf = unsafeCls.getDeclaredField("theUnsafe");
                uf.setAccessible(true);
                Object unsafe = uf.get(null);   // theUnsafe 是静态字段：get(null) 拿到 Unsafe 实例本身
                java.lang.reflect.Method alloc = unsafeCls.getMethod("allocateInstance", Class.class);
                System.err.println("[art] inject#3 allocateInstance");
                thread = alloc.invoke(unsafe, (Object) at);
                System.err.println("[art] inject#3 ok: " + thread.getClass().getName());
            } catch (Throwable u) {
                System.err.println("[art] inject#3 unsafe 失败: " + u + " / cause=" + rootOf(u).getMessage()
                        + " —— 退回私有构造反射（会拉起 Binder native，guest 预期炸）");
                java.lang.reflect.Constructor<?> c = at.getDeclaredConstructor();
                c.setAccessible(true);
                thread = c.newInstance();
            }
            System.err.println("[art] inject#4 sCurrentActivityThread");
            setStatic(at, "sCurrentActivityThread", thread);
            // 真 ActivityThread（Android 9）没有 mApplication 字段（JRE 桩才有）——逐个容错填，
            // 一个缺失不 abort：mInitialApplication / mAllApplications 才是真类里的应用钩子
            System.err.println("[art] inject#5 mApplication/mInitialApplication");
            trySetInstance(at, thread, "mApplication", app());
            trySetInstance(at, thread, "mInitialApplication", app());
            System.err.println("[art] inject#6 mAllApplications");
            try {
                java.lang.reflect.Field all = at.getDeclaredField("mAllApplications");
                all.setAccessible(true);
                Object list = all.get(thread);
                if (list instanceof java.util.List) {
                    ((java.util.List<?>) list).clear();
                    ((java.util.List<Object>) list).add(app());
                }
            } catch (Throwable ignored) { }
            // mActivities：以前给的是空 ArrayMap —— 壳遍历它取 Activity 永远得 null，
            // 于是 ProxyOrigin.getan() 里 activity.getWindow() 直接 NPE（2026-09-29 实测：
            // WoGG.playerContent → Pan.playerContent → ProxyOrigin.getan）。这里改成
            // 塞一条真实 ActivityClientRecord，activity 指向桥的伪 Activity（真 Activity
            // 子类，getWindow() 返回 FakeWindow），让壳的 UI 支路能走完而不是炸。
            System.err.println("[art] inject#7 mActivities（含伪 Activity）");
            try {
                Object map = Class.forName("android.util.ArrayMap").getDeclaredConstructor().newInstance();
                setInstance(at, thread, "mActivities", map);
                try {
                    Object act = Class.forName("bridge.FakeActivity").getConstructor().newInstance();
                    // 基 Context 直接反射填（不调 attachBaseContext：那条链上有 autofill 注册的 NPE）
                    try {
                        setInstance(Class.forName("android.content.ContextWrapper"), act, "mBase", app());
                        System.err.println("[art]   伪 Activity.mBase = " + app().getClass().getName());
                    } catch (Throwable tb) {
                        System.err.println("[art]   伪 Activity.mBase 填充失败: " + rootOf(tb));
                    }
                    System.err.println("[art] 伪 Activity 已建: " + act.getClass().getName());
                    Class<?> recCls = Class.forName("android.app.ActivityThread$ActivityClientRecord");
                    Object rec = allocateQuiet(recCls);
                    setInstance(recCls, rec, "activity", act);
                    // 不用 android.os.Binder：guest 里没有 binder native 实现，new Binder() 直接
                    // "No implementation found for getNativeBBinderHolder()"（2026-09-29 实测）。
                    // ArrayMap 的键类型在运行时已擦除，用普通对象占位即可 —— 壳遍历的是 values。
                    Object token = new Object();
                    @SuppressWarnings("unchecked")
                    java.util.Map<Object, Object> m = (java.util.Map<Object, Object>) map;
                    m.put(token, rec);
                    System.err.println("[art] mActivities 已注入 1 条 ActivityClientRecord（activity=伪 Activity）");
                } catch (Throwable ta) {
                    System.err.println("[art] 伪 Activity 注入失败（壳仍会取到 null）: " + ta
                            + " / cause=" + rootOf(ta).getMessage());
                    java.io.StringWriter sw = new java.io.StringWriter();
                    rootOf(ta).printStackTrace(new java.io.PrintWriter(sw));
                    System.err.println(sw.toString());
                }
            } catch (Throwable ignored) { }
            System.err.println("[art] ActivityThread 伪实例已注入（sCurrentActivityThread/mApplication/"
                    + "mInitialApplication=App, mActivities=1 条伪 Activity）");
        } catch (Throwable t) {
            Throwable c = rootOf(t);
            System.err.println("[art] ActivityThread 注入失败（壳的 Context 链将维持现状）: "
                    + t.getClass().getName() + " / cause=" + c.getClass().getName() + ": " + c.getMessage());
        }
    }

    private static Throwable rootOf(Throwable t) {
        Throwable c = t;
        while (c.getCause() != null && c.getCause() != c) c = c.getCause();
        return c;
    }

    private static void setStatic(Class<?> clz, String name, Object value) throws Exception {
        java.lang.reflect.Field f = clz.getDeclaredField(name);
        f.setAccessible(true);
        f.set(null, value);
    }

    private static void setInstance(Class<?> clz, Object target, String name, Object value) throws Exception {
        java.lang.reflect.Field f = clz.getDeclaredField(name);
        f.setAccessible(true);
        f.set(target, value);
    }

    /**
     * 用 {@code Unsafe.allocateInstance} 绕过构造器造实例：真 framework 的内部类
     * （如 {@code ActivityThread$ActivityClientRecord}）是隐藏 API，直接反射其构造会被
     * hiddenapi 拦下，而 Unsafe 不走那条检查（与 inject#2 造 ActivityThread 同一招）。
     */
    private static Object allocateQuiet(Class<?> clz) throws Exception {
        Class<?> unsafeCls = Class.forName("sun.misc.Unsafe");
        java.lang.reflect.Field uf = unsafeCls.getDeclaredField("theUnsafe");
        uf.setAccessible(true);
        Object unsafe = uf.get(null);
        java.lang.reflect.Method alloc = unsafeCls.getMethod("allocateInstance", Class.class);
        return alloc.invoke(unsafe, (Object) clz);
    }

    /** 字段缺失/类型不符只告警不抛（真 framework 类与桩的字段集不一致是常态）。 */
    private static void trySetInstance(Class<?> clz, Object target, String name, Object value) {
        try {
            setInstance(clz, target, name, value);
            System.err.println("[art]   " + name + " = " + (value == null ? "null" : value.getClass().getName()));
        } catch (Throwable t) {
            System.err.println("[art]   " + name + " 跳过: " + rootOf(t));
        }
    }

    /**
     * 把宿主给的 jar 引子落成本地文件。宿主 JRE 那条路传的是本地路径，guest 里读不到宿主的盘 ——
     * 所以 ART 模式约定传 URL（走 slirp：guest 看宿主是 10.0.2.2），例如控制口
     * {@code http://10.0.2.2:18090/jar?h=<hash>}。<b>jar 不烧进 initrd</b>：订阅里的源是开放集合。
     *
     * @return guest 本地可读的文件路径；下载失败抛异常（不静默回落，见 android 桩那条教训）
     */
    public static String materialize(String ref) throws java.io.IOException {
        if (ref == null || ref.isEmpty()) throw new IllegalArgumentException("jar 引用为空");
        File local = new File(ref);
        if (local.isFile()) return ref;
        if (!ref.startsWith("http://") && !ref.startsWith("https://"))
            throw new IllegalArgumentException("ART guest 只认本地文件或 http(s) 引用: " + ref);
        File inbox = dir("inbox");
        String name = ref.substring(ref.lastIndexOf('/') + 1);
        int q = name.indexOf('?');
        if (q > 0) name = name.substring(0, q);
        if (name.isEmpty()) name = "jar";
        File out = new File(inbox, (name.isEmpty() ? "jar" : name.replace('#', '_'))
                + "-" + Integer.toHexString(ref.hashCode()) + ".jar");   // 名字带引用哈希：多源各自一份，不互相覆盖
        long t0 = System.currentTimeMillis();
        java.io.InputStream in = new java.net.URL(ref).openStream();
        try {
            java.io.OutputStream os = new java.io.FileOutputStream(out);
            try {
                byte[] buf = new byte[16384];
                int n;
                while ((n = in.read(buf)) > 0) os.write(buf, 0, n);
            } finally { os.close(); }
        } finally { in.close(); }
        System.err.println("[art] jar 已取回 " + out.getName() + " " + out.length() + "B ("
                + (System.currentTimeMillis() - t0) + "ms)");
        return out.getAbsolutePath();
    }

    /** 站点键与爬虫类名 → 站点键：爬虫自建的回调 URL 里 site= 用的是它自己的名字，不一定是宿主给的键。 */
    static final java.util.Map<String, String> KEYS =
            new java.util.concurrent.ConcurrentHashMap<String, String>();
    static final java.util.Set<String> SITES =
            java.util.concurrent.ConcurrentHashMap.newKeySet();
    static final java.util.Set<Integer> PROXY_PORTS =
            java.util.concurrent.ConcurrentHashMap.newKeySet();

    /** TVBox 本地服务的默认端口；壳的 adjustPort 只认 9978..9999，不认宿主下发的那个。 */
    static final int TVBOX_PORT = 9978;

    public static void serveProxy(int port) {
        // 两个都听：宿主下发的端口（走 SpiderApi 桩的 jar），以及 TVBox 的本地服务默认端口 9978
        // （真机上 RemoteServer 就在它上面；解密壳的 adjustPort 从 9978 起逐个探测，找的是一个「应答正确的服务」，
        //  全扫到 9999 失败就退回 -1 —— 2026-09-26 实测：url 直接回显 vid、danmaku 里是 127.0.0.1:-1）。
        listenProxy(port);
        listenProxy(TVBOX_PORT);
    }

    static void listenProxy(final int port) {
        if (port <= 0 || !PROXY_PORTS.add(port)) return;
        Thread t = new Thread(() -> {
            try (final java.net.ServerSocket ss = open(port)) {
                System.err.println("[art] jar 自回调 proxy 服务已起 :" + port + "（通配，宿主隧道与壳都够得着）");
                while (true) {
                    final java.net.Socket s = ss.accept();
                    Thread w = new Thread(() -> serveOne(s, port), "art-proxy");
                    w.setDaemon(true);
                    w.start();
                }
            } catch (Exception e) {
                System.err.println("[art] proxy 服务（端口 " + port + "）结束: " + e);
            }
        }, "art-proxy-" + port);
        t.setDaemon(true);
        t.start();
    }

    static java.net.ServerSocket open(int port) throws java.io.IOException {
        java.net.ServerSocket ss = new java.net.ServerSocket();
        ss.setReuseAddress(true);
        // 绑通配而不是 127.0.0.1：宿主要经 slirp hostfwd 打进来，它拨的是 guest 的 eth0 地址
        // （10.0.2.15），只绑回环的连接会被直接 RST（2026-09-26 实测 WinError 10054）。
        // 对外仍然只有宿主回环能到达（slirp 不放外部入站），所以不额外暴露任何东西。
        ss.bind(new java.net.InetSocketAddress(port), 64);
        return ss;
    }

    static void serveOne(java.net.Socket s, int port) {
        String line = null;
        java.io.OutputStream out = null;
        try (java.net.Socket c = s;
             java.io.BufferedReader rd = new java.io.BufferedReader(
                     new java.io.InputStreamReader(c.getInputStream(), "UTF-8"))) {
            line = rd.readLine();
            if (line == null) return;
            // 收集请求头：壳的流中转要透传 Range（视频拖动靠 206/Content-Range）
            java.util.Map<String, String> reqHeaders = new java.util.HashMap<>();
            for (String h = rd.readLine(); h != null && h.length() > 0; h = rd.readLine()) {
                int c2 = h.indexOf(':');
                if (c2 > 0) reqHeaders.put(h.substring(0, c2).trim().toLowerCase(), h.substring(c2 + 1).trim());
            }
            out = c.getOutputStream();
            String path = line.split(" ")[1];
            // 壳的本地流中转（/proxy/play/<盘>/<文件>，guest 内 6678）：路径式路由，壳拿 Cookie
            // 中转夸克直链。宿主播放器的请求经 ProxyTunnelPort hostfwd 到这，原样透传给壳服务。
            if (path.startsWith("/proxy/play/")) { streamPassThrough(out, line, path, reqHeaders); return; }
            java.util.Map<String, String> q = query(line);
            // 非 /proxy 的（探活、壳发来的 /shutdown）给合法应答即可：壳的 adjustPort 就是在找
            // 「能正常应答的服务」，答不对它把端口记成 -1，播放地址就变成 127.0.0.1:-1。
            if (!line.startsWith("GET /proxy")) { writeText(out, 200, "ok"); return; }
            String want = q.get("site");
            String key = want == null ? null : KEYS.get(want);
            if (key == null && SITES.size() == 1) key = SITES.iterator().next();
            Object sp = key == null ? null : Server.spiderOf(key);
            System.err.println("[art] proxy:" + port + " ← " + line + " → "
                    + (sp == null ? "没有对应爬虫" : "四步分派 " + sp.getClass().getSimpleName()));
            if (sp == null && ("ck".equals(q.get("do")) || "danmu".equals(q.get("do")))) {
                // 壳的两种无 site 回调：
                // ① do=ck —— adjustPort 探测（找「有服务应答」的端口，失败记 -1 → 播放地址必挂）
                // ② do=danmu&url=<json> —— push 型聚合条目（seed 等）的播放解析入口：壳的
                //    ProxyOrigin 解析 url 里的网盘链接 → 起流服务。真机上都由壳自己的服务应答；
                //    回 404 壳就永远拿不到活。逐个壳实例试，谁应答用谁。
                for (String k : SITES) {
                    Object cand = Server.spiderOf(k);
                    if (cand == null) continue;
                    Object[] r = Server.jarProxy(cand, q);
                    if (r == null) r = Server.proxyDispatch(cand, new java.util.HashMap<String, String>(q));
                    if (r != null && r.length >= 3) { System.err.println("[art] " + q.get("do") + " 回调由 " + k + " 应答"); writeResult(out, r); return; }
                }
                writeText(out, 200, "ok");   // 全都不接也回 200：探测要的只是「有服务应答」
                return;
            }
            if (sp == null) { writeText(out, 404, "no spider for site"); return; }
            // 顺序有实测依据：先问壳自带的静态 Proxy（真机 JarLoader.invokeProxy 语义），它不接才走
            // ①②③。反过来会坏 —— 内层爬虫的 proxy(Map) 见谁都回 Cookie 粘贴页，把荐片 init 里的
            // do=ck 握手也换成了 HTML，load 直接 NPE（2026-09-26 一轮改错顺序的回归记录）。
            Object[] r = Server.jarProxy(sp, q);
            Object[] rr = r != null ? r : Server.proxyDispatch(sp, new java.util.HashMap<String, String>(q));
            // 诊断探针（2026-10-01）：网盘交互的应答形状。壳的 ④ 步会回「Cookie 粘贴页 HTML」，
            // 插件按 JSON 解析就永远拿不到账号信息（界面卡在「正在获取账号信息…」）——
            // 只打 do=quark 这一类，且**先读头再拼接回原流**，不能吞掉 body。
            if (q.containsKey("do")) rr = probeBody("do=" + q.get("do") + " type=" + q.get("type"), rr);
            writeResult(out, rr);
        } catch (Throwable e) {
            System.err.println("[art] proxy 服务一条失败: " + e + "  请求=" + line);
            // 必须回话：掐连接在宿主侧是 RemoteDisconnected，WebView 只会显示一片空白，
            // 用户看到的就是「点了没反应」（2026-09-26 网盘兜底页实测）。
            if (out != null) try { writeText(out, 502, String.valueOf(e.getMessage())); } catch (Throwable ignored) { }
        }
    }

    /** 壳的流服务端口（play URL 形如 http://127.0.0.1:6678/proxy/play/...）。 */
    static final int GUEST_STREAM_PORT = 6678;

    /**
     * 诊断探针（2026-10-01）：把 {@code /proxy?do=…} 的应答形状打出来 —— 状态码 / mime /
     * 头部若干字节（**脱敏**）。用于「夸克正在获取账号信息…」这类卡死：宿主只看到请求进了
     * 四步分派，看不到拿到的是账号 JSON 还是 Cookie 粘贴页 HTML。
     *
     * <p>关键实现细节：壳的应答体是 {@link java.io.InputStream}，{@link #writeResult} 会把它
     * 流式写给对端 —— 探针**必须先读头、再把「已读前缀 + 剩余流」拼回去**，否则 body 被吃掉，
     * 诊断本身就变成了故障。</p>
     */
    static Object[] probeBody(String tag, Object[] r) {
        if (r == null || r.length < 3 || !(r[2] instanceof java.io.InputStream)) {
            System.err.println("[probe] " + tag + " → 形状异常（" + (r == null ? "null" : r.length + " 段") + "）");
            return r;
        }
        try {
            java.io.InputStream in = (java.io.InputStream) r[2];
            byte[] pre = new byte[4096];
            int n = 0, k;
            while (n < pre.length && (k = in.read(pre, n, pre.length - n)) > 0) n += k;
            System.err.println("[probe] " + tag + " → " + r[0] + " " + r[1] + " 头 " + n + "B: "
                    + redact(new String(pre, 0, n, "UTF-8")));
            java.io.InputStream back = n < pre.length
                    ? new java.io.ByteArrayInputStream(pre, 0, n)
                    : new java.io.SequenceInputStream(new java.io.ByteArrayInputStream(pre, 0, n), in);
            r[2] = back;
            return r;
        } catch (Throwable t) {
            System.err.println("[probe] " + tag + " 读取失败: " + t);
            return r;
        }
    }

    /**
     * 探针脱敏：cookie/令牌值折叠成 {@code <N>}，长串（≥32 的 URL-safe 串，base64/hex）也折叠，
     * 再压空白并截断到 220 字符。**日志里绝不出现可用凭据**（与「登录 URL 只记长度+host」同规矩）。
     */
    static String redact(String s) {
        if (s == null) return "";
        s = s.replaceAll("(?i)((?:ck|cookie|token|ticket|auth|sign|utdid|st)[\"']?\\s*[:=]\\s*[\"']?)"
                + "[A-Za-z0-9_\\-+./=%]{12,}", "$1<N>");
        s = s.replaceAll("[A-Za-z0-9_\\-]{32,}", "<N>");
        s = s.replaceAll("\\s+", " ").trim();
        return s.length() > 220 ? s.substring(0, 220) + "…" : s;
    }

    /**
     * 单个端口的应答预算。两个数都来自实测：僵尸壳的服务「accept 了但一个字节都不回」（实测 0B 到对端关闭），
     * 而真壳实例的首个请求要现去网盘换直链（实测快的时候 24~337ms，慢的时候 366ms），
     * 2.5s 会误杀慢-but-正确的服务（实测把正确服务判死后剩下的扫描全 0B → 整条 502），所以给到 8s。
     */
    private static final int STREAM_PROBE_MS = 8000;

    /** 上一次真正应答过播放路径的端口。多壳共存时壳自报的端口会说谎（见 streamPassThrough 注释），
     *  这条缓存让「同一次播放的后续 Range 请求」不必再扫一遍。 */
    private static volatile int sStreamPort = 0;

    /** 一次壳流探测的结果：已读完响应头的 socket + 头字节。 */
    private static final class StreamHit {
        java.net.Socket sock;
        java.io.InputStream body;
        byte[] head;
        int port;
        String status;
    }

    /**
     * 壳的流中转透传：宿主播放器的 {@code GET /proxy/play/<盘>/<文件>}（带 Range）转给 guest 内
     * <b>真正持有这个播放会话的那个壳实例</b>，响应头与 body 原样过桥（206/Content-Range 不重构，
     * 播放器 seek 语义不变）。
     * <para><b>为什么不能直接信 URL 里的端口</b>（2026-09-30 「狂怒者：荣誉之战」实测）：一个 guest 里
     * 装着 N 个 Guard 壳，就有 N 个流服务分别占着 6678…6686，而壳的端口发现（{@code adjustPort}）是
     * 「从 6678 往上扫，谁应答就用谁」—— 它扫到的是<b>别的壳</b>的应答，于是 URL 写 6679、自己的服务
     * 在 6686。实测逐端口探同一个播放路径：6679…6685 全部 connect 成功但 {@code recv 0B}，只有 6686
     * 回 {@code HTTP/1.1 200 + 头}。所以这里按「谁对这个路径真的回话」选路，不按谁报的号。</para>
     * <p>次序：① URL 带来的 {@code gp}（宿主改写播放地址时捎上的原端口，单壳时就是对的，省一次扫描）
     * ② 上次应答过的缓存端口 ③ 都不应答就把 66xx 段所有在听的口并行扫一遍（实测 8 口 0.4s）。</p>
     */
    private static void streamPassThrough(java.io.OutputStream o, String reqLine, String path,
            java.util.Map<String, String> reqHeaders) {
        StreamHit hit = null;
        try {
            int gp = GUEST_STREAM_PORT;
            String clean = path;
            int qi = path.indexOf('?');
            if (qi >= 0) {
                clean = path.substring(0, qi);
                for (String kv : path.substring(qi + 1).split("&")) {
                    if (!kv.startsWith("gp=")) continue;
                    try { gp = Integer.parseInt(kv.substring(3).trim()); } catch (NumberFormatException ignored) { }
                }
            }
            String range = reqHeaders.get("range");

            hit = openStream(gp, reqLine, clean, range);
            if (hit == null && sStreamPort > 0 && sStreamPort != gp) hit = openStream(sStreamPort, reqLine, clean, range);
            if (hit == null) hit = sweepStreams(reqLine, clean, range, gp);
            if (hit == null) {
                writeText(o, 502, "guest 里没有壳流服务应答这个播放路径（gp=" + gp + "，66xx 全段扫描无响应）");
                System.err.println("[art] 流透传无人应答 " + reqLine + " gp=" + gp);
                return;
            }
            sStreamPort = hit.port;
            o.write(hit.head);
            if (!reqLine.startsWith("HEAD")) {
                byte[] buf = new byte[65536];
                int n;
                while ((n = hit.body.read(buf)) > 0) o.write(buf, 0, n);
            }
            o.flush();
            System.err.println("[art] 流透传 " + reqLine + " → 端口 " + hit.port + " " + hit.status
                    + (range == null ? "" : " Range=" + range));
        } catch (Throwable e) {
            System.err.println("[art] 流透传失败: " + e + "  " + reqLine);
            try { writeText(o, 502, String.valueOf(e.getMessage())); } catch (Throwable ignored) { }
        } finally {
            if (hit != null) try { hit.sock.close(); } catch (Throwable ignored) { }
        }
    }

    /** guest 里在听的壳流端口（/proc/net/tcp{,6} 的 st=0A），限定 66xx 段。 */
    private static java.util.List<Integer> streamPorts() {
        java.util.List<Integer> out = new java.util.ArrayList<>();
        for (String f : new String[]{"/proc/net/tcp", "/proc/net/tcp6"}) {
            try (java.io.BufferedReader br = new java.io.BufferedReader(new java.io.FileReader(f))) {
                String ln;
                while ((ln = br.readLine()) != null) {
                    String[] p = ln.trim().split("\\s+");
                    if (p.length > 3 && "0A".equals(p[3])) {
                        int port = Integer.parseInt(p[1].split(":")[1], 16);
                        if (port >= 6600 && port <= 6999 && !out.contains(port)) out.add(port);
                    }
                }
            } catch (Throwable ignored) { }
        }
        java.util.Collections.sort(out);
        return out;
    }

    /** 把 66xx 段所有在听的端口并行问一遍「这个播放路径你认不认」，谁先回 HTTP 头用谁。 */
    private static StreamHit sweepStreams(String reqLine, String clean, String range, int gp) {
        final java.util.List<Integer> ports = streamPorts();
        ports.remove(Integer.valueOf(gp));
        ports.remove(Integer.valueOf(sStreamPort));
        if (ports.isEmpty()) return null;
        final java.util.concurrent.BlockingQueue<StreamHit> q =
                new java.util.concurrent.LinkedBlockingQueue<StreamHit>();
        final java.util.List<java.net.Socket> opened =
                java.util.Collections.synchronizedList(new java.util.ArrayList<java.net.Socket>());
        for (final int port : ports) {
            Thread t = new Thread(new Runnable() {
                public void run() {
                    StreamHit h = openStream(port, reqLine, clean, range, opened);
                    if (h != null) q.offer(h);
                }
            });
            t.setDaemon(true);
            t.start();
        }
        StreamHit hit = null;
        long end = System.currentTimeMillis() + STREAM_PROBE_MS + 800;
        try {
            while (hit == null && System.currentTimeMillis() < end) {
                hit = q.poll(Math.max(50L, end - System.currentTimeMillis()),
                        java.util.concurrent.TimeUnit.MILLISECONDS);
            }
        } catch (InterruptedException ignored) { }
        // 落选的连接立刻关：留着就占住壳的 handler 线程，后面的请求更慢
        for (java.net.Socket s : opened) {
            if (hit != null && s == hit.sock) continue;
            try { s.close(); } catch (Throwable ignored) { }
        }
        System.err.println("[art] 流扫描 " + ports + " → " + (hit == null ? "无人应答" : "命中 " + hit.port));
        return hit;
    }

    private static StreamHit openStream(int port, String reqLine, String clean, String range) {
        return openStream(port, reqLine, clean, range, null);
    }

    /** 向一个候选端口发真实播放请求并读响应头；头都不回（0B / 超时 / 非 HTTP）就算它不该这个会话。 */
    private static StreamHit openStream(int port, String reqLine, String clean, String range,
                                        java.util.List<java.net.Socket> track) {
        java.net.Socket s = null;
        try {
            s = new java.net.Socket();
            s.connect(new java.net.InetSocketAddress("127.0.0.1", port), 1500);
            s.setSoTimeout(STREAM_PROBE_MS);
            if (track != null) track.add(s);
            StringBuilder rq = new StringBuilder();
            rq.append(reqLine.startsWith("HEAD") ? "HEAD " : "GET ").append(clean).append(" HTTP/1.1\r\n")
              .append("Host: 127.0.0.1:").append(port).append("\r\n")
              // ⚠ User-Agent 不能省：壳的流 handler 会读 UA 做分支，缺它就在
              //   `String.contains(...)` 上 NPE（实测 13ms 就挂断、一个字节都不回），
              //   表现成「端口在听但没人应答」。旧实现走 HttpURLConnection 时它自带
              //   `User-Agent: Java/17`，所以从没暴露过这个依赖。
              .append("User-Agent: Dalvik/2.1.0 (Linux; U; Android 13; x86_64 Build/TQ2A.230505.002)\r\n")
              .append("Accept: */*\r\nAccept-Encoding: identity\r\nConnection: close\r\n");
            if (range != null) rq.append("Range: ").append(range).append("\r\n");
            rq.append("\r\n");
            java.io.OutputStream os = s.getOutputStream();
            os.write(rq.toString().getBytes("UTF-8"));
            os.flush();
            java.io.InputStream in = s.getInputStream();
            long t0 = System.currentTimeMillis();
            byte[] head = readHead(in);
            if (head == null) {
                // 分不清「没应答」与「应答了但头不合法」，留一条现场（2026-09-30 排查 8s 预算下无一条命中）
                System.err.println("[art] 探测 " + port + " 无 HTTP 头（" + (System.currentTimeMillis() - t0) + "ms）");
                try { s.close(); } catch (Throwable ignored) { }
                return null;
            }
            s.setSoTimeout(0);          // 头到手就别再限时：body 可能因为上游网盘直链慢而断续
            StreamHit h = new StreamHit();
            h.sock = s; h.body = in; h.head = head; h.port = port;
            h.status = new String(head, "UTF-8").split("\r\n")[0];
            return h;
        } catch (Throwable e) {
            System.err.println("[art] 探测 " + port + " 异常 " + e.getClass().getSimpleName() + ": " + e.getMessage());
            if (s != null) try { s.close(); } catch (Throwable ignored) { }
            return null;
        }
    }

    /** 读到 {@code \r\n\r\n} 为止（含）；连一个字节都没有、或不是 HTTP 头，都算「没应答」。 */
    private static byte[] readHead(java.io.InputStream in) throws java.io.IOException {
        java.io.ByteArrayOutputStream b = new java.io.ByteArrayOutputStream();
        int c, matched = 0;
        while ((c = in.read()) != -1) {
            b.write(c);
            char expect = "\r\n\r\n".charAt(matched);
            if (c == expect) {
                if (++matched == 4) break;
            } else {
                matched = c == '\r' ? 1 : 0;
            }
        }
        if (b.size() == 0) return null;
        byte[] head = b.toByteArray();
        return new String(head, "UTF-8").startsWith("HTTP/") ? head : null;
    }

    static java.util.Map<String, String> query(String req) {
        java.util.Map<String, String> map = new java.util.HashMap<>();
        int i = req.indexOf('?');
        String qs = i < 0 ? "" : req.substring(i + 1);
        int sp = qs.indexOf(' ');
        if (sp > 0) qs = qs.substring(0, sp);
        for (String kv : qs.split("&")) {
            int e = kv.indexOf('=');
            if (e <= 0) continue;
            try {
                map.put(java.net.URLDecoder.decode(kv.substring(0, e), "UTF-8"),
                        java.net.URLDecoder.decode(kv.substring(e + 1), "UTF-8"));
            } catch (Exception ignored) { }
        }
        return map;
    }

    static void writeText(java.io.OutputStream o, int status, String msg) throws java.io.IOException {
        byte[] b = msg.getBytes("UTF-8");
        String h = "HTTP/1.1 " + status + (status == 200 ? " OK" : " Not Found")
                + "\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: " + b.length
                + "\r\nConnection: close\r\n\r\n";
        o.write(h.getBytes("UTF-8"));
        o.write(b);
        o.flush();
    }

    /** jar 的 proxy 约定：{@code Object[]{Integer status, String mime, InputStream body[, Map headers]}} */
    static void writeResult(java.io.OutputStream o, Object[] r) throws java.io.IOException {
        if (r == null || r.length < 3) { writeText(o, 502, "proxy 返回形状不对"); return; }
        int status = r[0] instanceof Integer ? (Integer) r[0] : 200;
        String mime = String.valueOf(r[1]);
        java.io.InputStream body = (java.io.InputStream) r[2];
        StringBuilder h = new StringBuilder("HTTP/1.1 " + status + " OK\r\nContent-Type: " + mime + "\r\n");
        if (r.length > 3 && r[3] instanceof java.util.Map)
            for (java.util.Map.Entry<?, ?> e : ((java.util.Map<?, ?>) r[3]).entrySet())
                h.append(e.getKey()).append(": ").append(e.getValue()).append("\r\n");
        if (body != null) h.append("Transfer-Encoding: chunked\r\n");
        h.append("Connection: close\r\n\r\n");
        o.write(h.toString().getBytes("UTF-8"));
        if (body == null) { o.flush(); return; }
        byte[] buf = new byte[8192];
        int n;
        while ((n = body.read(buf)) > 0) {
            o.write((Integer.toHexString(n) + "\r\n").getBytes("US-ASCII"));
            o.write(buf, 0, n);
            o.write("\r\n".getBytes("US-ASCII"));
        }
        o.write("0\r\n\r\n".getBytes("US-ASCII"));
        o.flush();
        body.close();
    }

    /**
     * 照搬 {@code JarLoader.load(key,file)} + {@code invokeInit} + {@code getSpider}。
     *
     * @param jarPath 原始 jar（Guard 壳 jar 或普通 dex jar 都行；<b>纯 .class 的 java jar 不行</b>，
     *                那种源留在宿主 JVM 侧跑 —— 它本来也没有 ARM 原生码）
     * @return 已 {@code init(Context, ext)} 过的 spider
     */
    public static Object loadSpider(String site, String jarPath, String className, String ext) throws Exception {
        jarPath = materialize(jarPath);
        File f = new File(jarPath);
        f.setReadOnly();
        String cache = dir("opt").getAbsolutePath();
        // jar 源的 loader 挂「桩优先」parent：爬虫代码的 android.* 解析到 ui_stub.dex 桩
        // （≈ JRE 桥语义，Dialog/Toast 桩才能把 UI 事件发出来）；boot 覆盖是已证死路，只能换命名空间。
        ClassLoader sys = stubFirst();
        dalvik.system.DexClassLoader loader =
                new dalvik.system.DexClassLoader(jarPath, cache, dir("lib").getAbsolutePath(), sys);
        System.err.println("[art] DexClassLoader " + f.getName() + " 就绪"
                + (sys != Art.class.getClassLoader() ? "（parent=桩优先链）" : ""));
        Class<?> cls = loader.loadClass("com.github.catvod.spider." + className);
        System.err.println("[art] step#3 loadClass ok: " + cls.getName());
        Object ctx = spiderCtx();
        System.err.println("[art] step#4 spiderCtx ok: " + (ctx == null ? "null" : ctx.getClass().getName()));
        bindInit(loader, ctx);
        System.err.println("[art] step#5 bindInit ok");
        // 登记站点键：爬虫自建的回调 URL 里 site= 用的是它自己的类名，两个键都要能找回这个站点。
        // 应答本身走 Server.proxyDispatch（与宿主 JRE 的 proxy op 同源：实例 proxy → Cloud_<do>
        // → proxyInput → 静态 Proxy.proxy）。以前这里只登记第 ④ 步，网盘系 do=input/quark 全 502。
        KEYS.put(site, site);
        KEYS.put(className, site);
        SITES.add(site);
        // 装载即起服务：壳的 adjustPort 要在 9978..9999 里找到一个「会应答」的本地服务，
        // 找不到就把端口记成 -1，于是给播放器的地址变成 http://127.0.0.1:-1/proxy?…（2026-09-26 装机台架实测）。
        // 这一步不能依赖宿主下发 setProxyPort —— 宿主没接本地代理服务时根本不发。
        listenProxy(TVBOX_PORT);
        System.err.println("[art] step#6 proxy 自回调已就位");
        Object sp = cls.getDeclaredConstructor().newInstance();
        System.err.println("[art] step#7 newInstance ok: " + sp.getClass().getName());
        // 与 TVBox JarLoader.getSpider 的调用序列一致：siteKey → initApi → init。
        // initApi 的实例必须取自**桩命名空间**（stub Spider 的 initApi 参数是桩 SpiderApi，
        // 灌 boot 副本会 ICCE），宿主经 setStubHostProxyPort 同步端口到该副本。
        try { cls.getField("siteKey").set(sp, site); } catch (Throwable ignored) { }
        System.err.println("[art] step#8 siteKey ok");
        try {
            Class<?> apiCls = sys.loadClass("com.github.catvod.crawler.SpiderApi");
            Method ia = findMethod(cls, "initApi", 1);
            if (ia != null) ia.invoke(sp, apiCls.getDeclaredConstructor().newInstance());
        } catch (NoSuchMethodException ignored) { }
        System.err.println("[art] step#9 initApi ok");
        long t0 = System.currentTimeMillis();
        try {
            // 参数类型可能解析到桩命名空间（Class 身份与 boot 不同），不能 getMethod(Context.class, ...)
            Method m = findMethod(cls, "init", 2);
            System.err.println("[art] step#10 调 " + className + ".init(Context, ext) —— 崩溃若在此后即壳的 init 触发");
            if (m != null) m.invoke(sp, ctx, ext == null ? "" : ext);
            System.err.println("[art] step#11 init 返回");
        } catch (NoSuchMethodException ignored) { }
        System.err.println("[art] spider " + className + " init 完成 ("
                + (System.currentTimeMillis() - t0) + "ms)");
        return sp;
    }

    /**
     * {@code ProtectedInitJar.init(Init类)} 的对等实现：把 App 灌进 {@code Init} 的 Context 字段、
     * 把 {@code DexNative.getLoader(app)} 返回的 DexClassLoader 灌进它的 DexClassLoader 字段。
     * 任一步不成 → 退回普通 {@code Init.init(Context)}。
     */
    static void bindInit(ClassLoader loader, Object ctx) {
        // InitOrigin 家族的静态 Context 一并注入（merge.Ku 等壳代码走 InitOrigin.context()，
        // 不注入 NPE on getSharedPreferences）—— 与宿主 JRE 的 Server.injectStaticContext 同源。
        // ⚠ 必须在「没有 Init 类就提前 return」之前：解密产物（merge.* 结构）只有 InitOrigin 没有 Init
        for (String holder : new String[]{
                "com.github.catvod.spider.InitOrigin",
                "com.github.catvod.spider.Init",
                "com.github.catvod.spider.merge.InitOrigin"}) {
            Server.injectStaticContext(loader, holder, ctx);
        }
        Class<?> clz;
        try {
            clz = loader.loadClass("com.github.catvod.spider.Init");
        } catch (Throwable t) {
            return;                                     // 这 jar 没有 Init，普通源
        }
        Object init = null;
        try {
            Method get = clz.getMethod("get");
            init = get.invoke(null);
        } catch (Throwable t) {
            // 2026-10-02：主路径从未成功过（68 次全回退）——受保护序列的 Context/DexClassLoader
            // 绑定因此全被跳过，壳的登录保存链很可能死在这。打日志看 get() 到底为什么失败。
            Throwable r = t;
            while (r.getCause() != null) r = r.getCause();
            System.err.println("[art] Init.get() 失败: " + t.getClass().getName()
                    + " / root=" + r.getClass().getName() + ": " + r.getMessage());
        }
        if (init != null) {
            // 对齐真机 ProtectedInitJar.init：bindContext 与 bindDexLoader **两步独立**执行，
            // context 绑不上不代表 loader 绑不上（2026-10-02 实测 FishConfig 卡在 setContextField
            // 静默失败，短路导致 DexNative.getLoader 从未被调用 → 壳的登录保存链死）
            boolean ctxOk = setContextField(clz, init, ctx);
            boolean loaderOk = bindDexLoader(clz, init, loader, ctx);
            if (ctxOk && loaderOk) {
                System.err.println("[art] Init 单例已按 protected jar 序列注入");
            } else {
                System.err.println("[art] Init 序列注入不完整: ctx=" + ctxOk + " loader=" + loaderOk
                        + " —— 回退 Init.init(Context)");
                try {
                    Method m = findMethod(clz, "init", 1);
                    if (m != null) m.invoke(null, ctx);
                    System.err.println("[art] Init.init(Context) 完成");
                } catch (Throwable t) {
                    System.err.println("[art] Init.init(Context) 失败: " + t);
                }
            }
        }
    }

    private static boolean setContextField(Class<?> clz, Object target, Object ctx) {
        StringBuilder cand = new StringBuilder();
        try {
            Field c = clz.getDeclaredField("c");         // TVBox 里试的第一个名字
            c.setAccessible(true);
            c.set(target, ctx);
            return true;
        } catch (Throwable ignored) { }
        for (Class<?> t = clz; t != null && t != Object.class; t = t.getSuperclass()) {
            for (Field fd : t.getDeclaredFields()) {
                try {
                    if (Modifier.isStatic(fd.getModifiers())) continue;
                    // 按名字匹配 Context 系：字段类型可能解析到桩命名空间，与 boot 的 Context 类身份不同，
                    // isAssignableFrom 会误判 false
                    String tn = fd.getType().getName();
                    if (!tn.equals("android.content.Context") && !tn.equals("android.app.Application")
                            && !tn.equals("android.content.ContextWrapper")) {
                        cand.append(tn).append('.').append(fd.getName()).append(' ');
                        continue;
                    }
                    fd.setAccessible(true);
                    fd.set(target, ctx);
                    return true;
                } catch (Throwable ignored) { }
            }
        }
        // 诊断：列出全部候选字段类型，失败一眼可见（2026-10-02 网盘登录态排障）
        System.err.println("[art] setContextField 未命中: Init 类非静态字段 = "
                + (cand.length() == 0 ? "(无)" : cand.toString()));
        return false;
    }

    /** 按名字+参数个数找方法（含父类）：参数类型的 Class 身份跨命名空间时 getMethod 匹配不上。 */
    private static Method findMethod(Class<?> clz, String name, int params) throws NoSuchMethodException {
        for (Class<?> t = clz; t != null && t != Object.class; t = t.getSuperclass()) {
            for (Method m : t.getDeclaredMethods()) {
                if (m.getName().equals(name) && m.getParameterCount() == params) {
                    m.setAccessible(true);
                    return m;
                }
            }
        }
        throw new NoSuchMethodException(clz.getName() + "." + name + "/" + params);
    }

    private static boolean bindDexLoader(Class<?> clz, Object init, ClassLoader loader, Object ctx) {
        Object dexLoader;
        try {
            Class<?> dn = loader.loadClass("com.github.catvod.spider.DexNative");
            dexLoader = dn.getMethod("getLoader", Object.class).invoke(null, ctx);
        } catch (Throwable t) {
            System.err.println("[art] DexNative.getLoader 不可用: " + t);
            return false;
        }
        if (!(dexLoader instanceof dalvik.system.DexClassLoader)) return false;
        boolean bound = false;
        for (Class<?> t = clz; t != null; t = t.getSuperclass()) {
            for (Field fd : t.getDeclaredFields()) {
                try {
                    if (Modifier.isStatic(fd.getModifiers())
                            || !dalvik.system.DexClassLoader.class.isAssignableFrom(fd.getType())) continue;
                    fd.setAccessible(true);
                    fd.set(init, dexLoader);
                    bound = true;
                } catch (Throwable ignored) { }
            }
        }
        System.err.println("[art] DexNative.getLoader → " + dexLoader + " 注入=" + bound);
        return bound;
    }

    /**
     * Unsafe 绕构造器造真类实例（零副作用，字段全默认）；失败返回 null。
     * 供 App 桩造系统服务对象用（真 ContextImpl 的 SystemServiceRegistry 在 guest 里不存在）。
     */
    static Object forged(String cn) {
        try {
            Class<?> c = Class.forName(cn);
            Class<?> unsafeCls = Class.forName("sun.misc.Unsafe");
            java.lang.reflect.Field uf = unsafeCls.getDeclaredField("theUnsafe");
            uf.setAccessible(true);
            Object unsafe = uf.get(null);
            java.lang.reflect.Method alloc = unsafeCls.getMethod("allocateInstance", Class.class);
            return alloc.invoke(unsafe, (Object) c);
        } catch (Throwable t) {
            return null;
        }
    }

    /**
     * 真 {@code ContextWrapper.getCacheDir()} 要 ActivityThread，guest 里没有 ActivityThread，
     * 所以整棵 Context 树靠覆写给出（漏哪个就在哪个上 NPE —— P3 实测 {@code getSharedPreferences} 就是）。
     */
    static final class App extends android.app.Application {
        private final Map<String, SharedPreferences> prefs = new HashMap<>();
        private File cache, files, lib, prefsDir;

        App() {
            cache = dir("cache");
            files = dir("files");
            lib = dir("lib");
            prefsDir = dir("shared_prefs");
        }

        @Override public File getCacheDir() { return cache; }
        @Override public File getCodeCacheDir() { return cache; }
        @Override public File getExternalCacheDir() { return cache; }
        @Override public File getFilesDir() { return files; }
        @Override public File getDataDir() { return files.getParentFile(); }
        @Override public File getNoBackupFilesDir() { return files; }
        @Override public File getDir(String name, int mode) { return new File(files, name); }
        @Override public File getExternalFilesDir(String type) { return files; }
        // 2026-09-25 实测：壳的 merge.Rc.G4 会要 getDatabasePath —— 不覆写就走到
        // ContextWrapper 的 mBase（null）上 NPE（漏哪个 Context 方法就在哪个上炸，全族都一样）。
        @Override public File getDatabasePath(String name) {
            File db = new File(files, "databases");
            db.mkdirs();
            return new File(db, name);
        }
        @Override public String getPackageName() { return "com.catclaw.video"; }
        @Override public Context getApplicationContext() { return this; }
        @Override public ClassLoader getClassLoader() { return Art.class.getClassLoader(); }

        /**
         * guest 里必须覆写：真 {@code ContextWrapper.getPackageManager()} 走 mBase（null）→ NPE，
         * 壳的 UI 工具类（merge.cn.B，2026-09-26 栈证实）第一步就死在这。
         * 返回 {@code GuestPackageManager}（guest-src 单独编译，superclass 按环境解析到真
         * PackageManager）；JRE 桩环境没有真类时回落 super（桩 Context 自带实现，行为不变）。
         */
        @Override public android.content.pm.PackageManager getPackageManager() {
            if (!onArt()) return super.getPackageManager();   // JRE 桩链：桩 Context 自带实现，行为不变
            try {
                return (android.content.pm.PackageManager) Class.forName("bridge.GuestPackageManager")
                        .getDeclaredConstructor().newInstance();
            } catch (Throwable t) {
                System.err.println("[art] GuestPackageManager 不可用，回落 super: " + t);
                return super.getPackageManager();
            }
        }

        @Override public android.content.pm.ApplicationInfo getApplicationInfo() {
            android.content.pm.ApplicationInfo ai = new android.content.pm.ApplicationInfo();
            ai.packageName = getPackageName();
            ai.dataDir = files.getAbsolutePath();
            ai.nativeLibraryDir = lib.getAbsolutePath();
            ai.sourceDir = cache.getAbsolutePath();
            // ABI 面必须补齐（2026-10-01）：插件装载原生库时会读 primaryCpuAbi / secondaryCpuAbi
            // （不设就是 null），加上 Process.is64Bit() 曾恒 false —— 两处一起把它推去挑
            // assets/FishGuard-v7.so（ARM32），在 x86_64 guest 里必然 dlopen 失败。
            // 本 guest 是「x86_64 原生 + arm64 用户态转译」，对外统一报 arm64-v8a（与
            // Build.SUPPORTED_ABIS / ro.product.cpu.abilist64 一致），32 位一律留空。
            ai.primaryCpuAbi = "arm64-v8a";
            ai.secondaryCpuAbi = null;
            return ai;
        }

        @Override public SharedPreferences getSharedPreferences(String name, int mode) {
            synchronized (prefs) {
                SharedPreferences p = prefs.get(name);
                if (p == null) {
                    // PrefsStore 就是桌面那套「按 name 分文件 + Android 同款 XML」，guest 复用同一实现，
                    // 宿主和 guest 读的是同一份 shared_prefs/<name>.xml（cookie/useState 不落两遍）。
                    p = new ArtPrefs(new File(prefsDir, name + ".xml"));
                    prefs.put(name, p);
                }
                return p;
            }
        }

        /**
         * ⚠ 壳的 native 经 JNI 回调 {@code Context.getSystemService}：真 ContextWrapper 走
         * mBase（guest 里是 null）→ 抛 NPE → JNI 调用返回 NULL → 转译执行的 ARM 代码
         * 不判空直接解引用 → **SIGSEGV(fault addr=0)**（§6.6 实锤：崩溃前唯一的信号就是它）。
         *
         * <p>所以这里**绝不返回 null**：按服务名造「真类的 Unsafe 伪实例」（零副作用），
         * 未知服务退化为非 null 占位对象并打日志 —— 日志会指出下一步该补哪个服务。
         * JRE 桩环境（非 ART）行为完全不变。</p>
         */
        @Override public Object getSystemService(String name) {
            if (!onArt()) return super.getSystemService(name);
            try {
                String cn = sysServiceClass(name);
                Object o = cn == null ? null : forged(cn);
                if (o != null)
                    System.err.println("[art] getSystemService(" + name + ") → " + cn + " 伪实例");
                else if (cn != null)
                    System.err.println("[art] getSystemService 造桩失败: " + name + " (" + cn + ")");
                else
                    System.err.println("[art] getSystemService 未支持（占位返回）: " + name);
                return o != null ? o : new Object();
            } catch (Throwable t) {
                System.err.println("[art] getSystemService 异常: " + name + " " + t);
                return new Object();
            }
        }

        /** 常见服务名 → 真系统服务类（用 Unsafe 造实例）；null = 未知，走占位。 */
        private static String sysServiceClass(String name) {
            if (name == null) return null;
            switch (name) {
                case "connectivity":  return "android.net.ConnectivityManager";
                case "wifi":          return "android.net.wifi.WifiManager";
                case "telephony":     return "android.telephony.TelephonyManager";
                case "activity":      return "android.app.ActivityManager";
                case "power":         return "android.os.PowerManager";
                case "storage":       return "android.os.storage.StorageManager";
                case "battery":       return "android.os.BatteryManager";
                case "sensor":        return "android.hardware.SensorManager";
                case "window":        return "android.view.WindowManager";
                case "audio":         return "android.media.AudioManager";
                case "notification":  return "android.app.NotificationManager";
                case "keyguard":      return "android.app.KeyguardManager";
                case "vibrator":      return "android.os.Vibrator";
                case "input_method":  return "android.view.inputmethod.InputMethodManager";
                case "layout_inflater": return "android.view.LayoutInflater";
                default: return null;
            }
        }
    }
}
