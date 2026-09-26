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
        ClassLoader sys = Art.class.getClassLoader();
        dalvik.system.DexClassLoader loader =
                new dalvik.system.DexClassLoader(jarPath, cache, dir("lib").getAbsolutePath(), sys);
        System.err.println("[art] DexClassLoader " + f.getName() + " 就绪");
        Class<?> cls = loader.loadClass("com.github.catvod.spider." + className);
        bindInit(loader, app());
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
        long t0 = System.currentTimeMillis();
        try {
            cls.getMethod("init", Context.class, String.class).invoke(sp, app(), ext == null ? "" : ext);
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
    static void bindInit(ClassLoader loader, Context ctx) {
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
        if (init != null && setField(clz, init, ctx) && bindDexLoader(clz, init, loader, ctx)) {
            System.err.println("[art] Init 单例已按 protected jar 序列注入");
            return;
        }
        try {
            clz.getMethod("init", Context.class).invoke(null, ctx);
            System.err.println("[art] Init.init(Context) 完成");
        } catch (Throwable t) {
            System.err.println("[art] Init.init(Context) 失败: " + t);
        }
    }

    private static boolean setField(Class<?> clz, Object target, Context ctx) {
        try {
            Field c = clz.getDeclaredField("c");         // TVBox 里试的第一个名字
            c.setAccessible(true);
            c.set(target, ctx);
            return true;
        } catch (Throwable ignored) { }
        for (Field fd : clz.getDeclaredFields()) {
            try {
                if (Modifier.isStatic(fd.getModifiers()) || !Context.class.isAssignableFrom(fd.getType())) continue;
                fd.setAccessible(true);
                fd.set(target, ctx);
                return true;
            } catch (Throwable ignored) { }
        }
        return false;
    }

    private static boolean bindDexLoader(Class<?> clz, Object init, ClassLoader loader, Context ctx) {
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
