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
            // mActivities：空 ArrayMap（真类型，new 得起），壳遍历得 null Activity → 走降级
            System.err.println("[art] inject#7 mActivities");
            try {
                Object empty = Class.forName("android.util.ArrayMap").getDeclaredConstructor().newInstance();
                setInstance(at, thread, "mActivities", empty);
            } catch (Throwable ignored) { }
            System.err.println("[art] ActivityThread 伪实例已注入（sCurrentActivityThread/mApplication/"
                    + "mInitialApplication=App, mActivities=空）");
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
            for (String h = rd.readLine(); h != null && h.length() > 0; h = rd.readLine()) { /* 丢掉首部 */ }
            out = c.getOutputStream();
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
            if (sp == null) { writeText(out, 404, "no spider for site"); return; }
            // 顺序有实测依据：先问壳自带的静态 Proxy（真机 JarLoader.invokeProxy 语义），它不接才走
            // ①②③。反过来会坏 —— 内层爬虫的 proxy(Map) 见谁都回 Cookie 粘贴页，把荐片 init 里的
            // do=ck 握手也换成了 HTML，load 直接 NPE（2026-09-26 一轮改错顺序的回归记录）。
            Object[] r = Server.jarProxy(sp, q);
            writeResult(out, r != null ? r : Server.proxyDispatch(sp, new java.util.HashMap<String, String>(q)));
        } catch (Throwable e) {
            System.err.println("[art] proxy 服务一条失败: " + e + "  请求=" + line);
            // 必须回话：掐连接在宿主侧是 RemoteDisconnected，WebView 只会显示一片空白，
            // 用户看到的就是「点了没反应」（2026-09-26 网盘兜底页实测）。
            if (out != null) try { writeText(out, 502, String.valueOf(e.getMessage())); } catch (Throwable ignored) { }
        }
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
        Object ctx = spiderCtx();
        bindInit(loader, ctx);
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
        System.err.println("[art] " + site + "：proxy 自回调已就位");
        Object sp = cls.getDeclaredConstructor().newInstance();
        // 与 TVBox JarLoader.getSpider 的调用序列一致：siteKey → initApi → init。
        // initApi 的实例必须取自**桩命名空间**（stub Spider 的 initApi 参数是桩 SpiderApi，
        // 灌 boot 副本会 ICCE），宿主经 setStubHostProxyPort 同步端口到该副本。
        try { cls.getField("siteKey").set(sp, site); } catch (Throwable ignored) { }
        try {
            Class<?> apiCls = sys.loadClass("com.github.catvod.crawler.SpiderApi");
            Method ia = findMethod(cls, "initApi", 1);
            if (ia != null) ia.invoke(sp, apiCls.getDeclaredConstructor().newInstance());
        } catch (NoSuchMethodException ignored) { }
        long t0 = System.currentTimeMillis();
        try {
            // 参数类型可能解析到桩命名空间（Class 身份与 boot 不同），不能 getMethod(Context.class, ...)
            Method m = findMethod(cls, "init", 2);
            if (m != null) m.invoke(sp, ctx, ext == null ? "" : ext);
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
        } catch (Throwable ignored) { }
        if (init != null && setContextField(clz, init, ctx) && bindDexLoader(clz, init, loader, ctx)) {
            System.err.println("[art] Init 单例已按 protected jar 序列注入");
        } else {
            try {
                Method m = findMethod(clz, "init", 1);
                if (m != null) m.invoke(null, ctx);
                System.err.println("[art] Init.init(Context) 完成");
            } catch (Throwable t) {
                System.err.println("[art] Init.init(Context) 失败: " + t);
            }
        }
    }

    private static boolean setContextField(Class<?> clz, Object target, Object ctx) {
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
                            && !tn.equals("android.content.ContextWrapper")) continue;
                    fd.setAccessible(true);
                    fd.set(target, ctx);
                    return true;
                } catch (Throwable ignored) { }
            }
        }
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
    }
}
