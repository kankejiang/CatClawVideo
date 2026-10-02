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

    // ── call 并行化（搜索提速的核心，2026-09-26）──
    // 此前主循环单线程 + synchronized(LOCK) 全局锁：一个 18s 的慢搜索把队列里所有快源堵死，
    // 搜 20 个源 = 串行耗时之和（TVBox 本机多线程并发调不同 spider，没有这层瓶颈）。
    // 改为：call/load 提交线程池，call 按站点加锁 —— 同站点串行（jar 实例非线程安全），
    // 不同站点并行；loadFull 仍持全局 LOCK（load 是重活且 LOADERS 非线程安全）。
    // 响应按 id 乱序回写（宿主 RoundTripAsync 按 id 匹配，proxy op 当年已因同一理由异步化）。
    private static final java.util.concurrent.ConcurrentHashMap<String, java.util.concurrent.locks.ReentrantLock> SITE_LOCKS =
            new java.util.concurrent.ConcurrentHashMap<>();
    /** stdout 写回互斥：call/load 异步化后多线程 println 会交错破坏行协议。 */
    private static final Object OUT_LOCK = new Object();
    private static java.util.concurrent.ExecutorService CALL_POOL;

    /** 行协议写回（唯一出口）：多线程下保证一行一条完整 JSON。 */
    private static void emit(String line) {
        synchronized (OUT_LOCK) {
            System.out.println(line);
            System.out.flush();
        }
    }

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

    /**
     * ART 侧挑「含 dex 的那个 jar」：优先原始壳 jar（它自己会解出真 dex），其次任一入口带 dex。
     *
     * <p>判据是 zip 里有没有 {@code classes.dex}（或整个文件本身就是 dex）—— 纯 {@code .class}
     * 的 java 源在 ART 里永远加载不了，那种源继续走宿主的 JVM 桥。</p>
     */
    private static String firstDexJar(String rawJar, String shellJar, String realJar, org.json.JSONArray jars) {
        java.util.List<String> cand = new java.util.ArrayList<>();
        for (String p : new String[]{rawJar, shellJar, realJar})
            if (p != null && !p.isEmpty()) cand.add(p);
        if (jars != null)
            for (int i = 0; i < jars.length(); i++) {
                String p = jars.optString(i, null);
                if (p != null && !p.isEmpty()) cand.add(p);
            }
        for (String p : cand) if (hasDex(p)) return p;
        return null;
    }

    private static boolean hasDex(String path) {
        // http(s) 引用没法就地验货，交给 Art.materialize 取回后再说
        if (path.startsWith("http://") || path.startsWith("https://")) return true;
        try {
            File f = new File(path);
            if (!f.isFile()) return false;
            byte[] head = new byte[3];
            try (java.io.FileInputStream in = new java.io.FileInputStream(f)) {
                if (in.read(head) == 3 && head[0] == 'd' && head[1] == 'e' && head[2] == 'x') return true;
            }
            try (java.util.zip.ZipFile z = new java.util.zip.ZipFile(f)) {
                for (java.util.Enumeration<? extends java.util.zip.ZipEntry> e = z.entries(); e.hasMoreElements(); )
                    if (e.nextElement().getName().endsWith(".dex")) return true;
            }
        } catch (Exception ignored) { }
        return false;
    }

    public static void main(String[] args) throws Exception {
        // 静默线程死亡的最后一道眼：网盘登录保存链若在某线程悄悄抛异常被壳吞掉，这里抓不到；
        // 但**未捕获**的异常（保存线程被炸死）一定会从这里过 —— 配合 stackdump 定位（2026-10-02）。
        Thread.setDefaultUncaughtExceptionHandler((t, e) -> {
            try {
                System.err.println("[srv] 未捕获异常 thread=" + t.getName() + " : " + e);
                for (StackTraceElement f : e.getStackTrace()) System.err.println("    at " + f);
            } catch (Throwable ignored) { }
        });
        // Guard 资源控制口：guest 内 harness 的 GLOAD 下发与 so/dex 条目服务（仅 spider guest 用）。
        // 桌面直跑模式该端口被占用时静默失败不影响主流程。
        try { bridge.GuardCtrl.start(); } catch (Throwable ig) { }

        // 把 AES/*/PKCS7 别名到 PKCS5（标准 JVM 不提供 PKCS7 命名）→ 否则爬虫的接口加解密直接失败。
        // ART 里不装：conscrypt 自带的 BC 原生就认 PKCS7Padding，我们的包装反而会盖掉真实现。
        if (!Art.onArt()) Pkcs7Provider.install();
        // guest 模式（QEMU 里的 ART）由 GuestMain 把标准流换成 socket，此时不能被这里覆盖回去
        if (System.getProperty("bridge.keepStdio") == null) {
            // 强制 stdout/stderr 为 UTF-8（JVM 默认跟随 Windows 控制台代码页 GBK，中文会坏）
            System.setOut(new java.io.PrintStream(new java.io.FileOutputStream(java.io.FileDescriptor.out), true, "UTF-8"));
            System.setErr(new java.io.PrintStream(new java.io.FileOutputStream(java.io.FileDescriptor.err), true, "UTF-8"));
        }
        // data 目录：spider 的 Context 文件操作都落在 App 约定的桥工作目录（guest 侧自己预设则不覆盖）
        if (System.getProperty("data.dir") == null)
            System.setProperty("data.dir", new File("data").getAbsolutePath());
        startParentWatchdog();
        startDataWatcher();
        startAdbd();
        startSurfaceFlinger();
        BufferedReader in = new BufferedReader(new InputStreamReader(System.in, StandardCharsets.UTF_8));
        // ⚠ proxy op 必须与 call 并行：call(detailContent) 在主循环同步执行期间，spider 内部
        //   会同步发 HTTP 请求（do=config）→ 宿主 → proxy op。若在主循环排队，call 不返回
        //   proxy 就轮不到 → 死锁到 HTTP 超时 → spider 拿错误文本喂 Gson 炸（2026-09-24 实测）。
        //   协议响应按 id 匹配（宿主 RoundTripAsync），异步乱序输出安全。
        java.util.concurrent.ExecutorService proxyPool = java.util.concurrent.Executors.newFixedThreadPool(4);
        // call/load 的执行池：daemon —— op=exit 时主循环退出，未完成的调用随 JVM 一起收
        // ⚠ 必须是**可扩**池，不能固定 4 条（2026-09-30 实测）：壳的网盘调用没有读超时，
        //   一条 hang 死的 playerContent 会**永久**占住一条池线程；固定 4 条时
        //   「点 4 次没登录的盘」就把整条桥（连别的站点）一起堵死。
        //   改成 cached 后毒化范围收敛到那一个站（配合 call 里的 tryLock 10s 立刻回错误）。
        CALL_POOL = java.util.concurrent.Executors.newCachedThreadPool(r -> {
            Thread t = new Thread(r, "bridge-call");
            t.setDaemon(true);
            return t;
        });
        OpWatchdog.start();   // op 挂死看门狗（>45s dump 全线程栈到 stderr）
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
            OpWatchdog.begin(fid, fop);
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
                    emit(o);
                });
                continue;
            }
            if ("call".equals(fop) || "load".equals(fop)) {
                // call/load 重活异步执行（见 SITE_LOCKS 注释）：慢源不再堵快源，
                // 搜索/切站的多源并发真正并行。轻 op（ping/probe/prefs…）保持同步直答。
                final JSONObject freq = req;
                CALL_POOL.submit(() -> {
                    OpWatchdog.begin(fid, fop);   // 主循环随后的 OP_START 清零会盖掉 begin，任务内重设
                    String o;
                    try {
                        Object result = "call".equals(fop)
                                ? call(freq.optString("site"), freq.optString("method"), freq.optJSONArray("args"))
                                : loadFull(freq.optString("site"), freq.optString("className"), freq.optString("ext"),
                                        freq.optJSONArray("jars"), freq.optString("shellJar", null),
                                        freq.optString("rawJar", null), freq.optString("realJar", null),
                                        freq.optInt("guardPort", 0), freq.optBoolean("force", false));
                        o = new JSONObject().put("id", fid).put("ok", true)
                                .put("result", result == null ? JSONObject.NULL : result).toString();
                    } catch (Throwable t) {
                        Throwable c = t;
                        while (c.getCause() != null) c = c.getCause();
                        StringBuilder sb = new StringBuilder(c.getClass().getSimpleName() + ": " + c.getMessage());
                        for (int i = 0; i < Math.min(8, c.getStackTrace().length); i++)
                            sb.append(" | ").append(c.getStackTrace()[i]);
                        o = new JSONObject().put("id", fid).put("ok", false).put("error", sb.toString()).toString();
                    }
                    emit(o);
                    OP_START.set(0);   // 任务完成：清看门狗（主循环侧的清零发生在提交时，不反映真实完成）
                });
                continue;
            }
            try {
                Object result = switch (fop) {
                    case "load" -> loadFull(req.optString("site"), req.optString("className"), req.optString("ext"), req.optJSONArray("jars"),
                            req.optString("shellJar", null), req.optString("rawJar", null), req.optString("realJar", null),
                            req.optInt("guardPort", 0), req.optBoolean("force", false));
                    case "call" -> call(req.optString("site"), req.optString("method"), req.optJSONArray("args"));
                    case "writefile" -> {
                        // 宿主回灌持久化文件（网盘 Cookie 的 sharedb）：guest /data 是 tmpfs，
                        // VM 冷启即清，握手后把上次会话保存的文件写回来。路径白名单防任意写。
                        String p = req.optString("path", "");
                        if (!p.startsWith("/data/cache/sharedb/") || p.contains(".."))
                            throw new IllegalArgumentException("writefile: path 不在白名单");
                        byte[] data = java.util.Base64.getDecoder().decode(req.optString("data", ""));
                        java.io.File f = new java.io.File(p);
                        java.io.File parent = f.getParentFile();
                        if (parent != null && !parent.exists()) parent.mkdirs();
                        try (java.io.FileOutputStream fo = new java.io.FileOutputStream(f)) { fo.write(data); }
                        yield "written " + data.length + "B";
                    }
                    case "stackdump" -> {
                        // 自打线程栈（guest 里没有 tombstoned/debuggerd，卡死时这是唯一的眼睛）：
                        // 全部线程的 name + 状态 + 顶层 12 帧进 stderr（→ 宿主 DiagLog）。
                        StringBuilder sb = new StringBuilder("\n=== stackdump ===\n");
                        for (java.util.Map.Entry<Thread, StackTraceElement[]> e : Thread.getAllStackTraces().entrySet()) {
                            Thread th = e.getKey();
                            sb.append("── ").append(th.getName()).append(" state=").append(th.getState())
                              .append(" daemon=").append(th.isDaemon()).append('\n');
                            StackTraceElement[] st = e.getValue();
                            for (int i = 0; i < Math.min(12, st.length); i++) sb.append("    at ").append(st[i]).append('\n');
                        }
                        System.err.print(sb.toString());
                        yield "dumped " + Thread.getAllStackTraces().size() + " threads";
                    }
                    case "ping" -> "pong";
                    case "cookies" -> {
                        // 2026-10-02 网盘登录态排障（CATCLAW_BRIDGE_DEBUG 泵用，只读）：
                        // ① java.net.CookieHandler 默认层 ② android.webkit 真框架 CookieManager
                        // ③ 已装载 spider 类的静态 String 字段 —— 三处一起看，
                        //    判定扫码确认后「新鲜的登录态到底落在哪/哪一层丢了」。
                        StringBuilder sb = new StringBuilder();
                        java.net.CookieHandler h = java.net.CookieHandler.getDefault();
                        sb.append("java.net.CookieHandler=").append(h == null ? "null" : h.getClass().getName()).append('\n');
                        if (h instanceof java.net.CookieManager cm) {
                            try {
                                for (java.net.HttpCookie c : cm.getCookieStore().getCookies()) {
                                    String v = c.getValue();
                                    sb.append("  [jnet] ").append(c.getDomain()).append("  ").append(c.getName())
                                      .append("  ").append(v == null ? -1 : v.length()).append("B\n");
                                }
                            } catch (Throwable t) { sb.append("  [jnet] store 读取失败: ").append(t).append('\n'); }
                        }
                        try {
                            Object wk = Class.forName("android.webkit.CookieManager").getMethod("getInstance").invoke(null);
                            for (String u : new String[]{"https://uop.quark.cn/", "https://pan.quark.cn/"}) {
                                try {
                                    Object c = wk.getClass().getMethod("getCookie", String.class).invoke(wk, u);
                                    sb.append("  [webkit] getCookie(").append(u).append(") → ")
                                      .append(c == null ? "null" : (c.toString().length() + "B: " + c.toString())).append('\n');
                                } catch (Throwable t) { sb.append("  [webkit] ").append(u).append(" 探测失败: ").append(t).append('\n'); }
                            }
                        } catch (Throwable t) {
                            Throwable c = t;
                            while (c.getCause() != null) c = c.getCause();
                            sb.append("  [webkit] getInstance 失败: ").append(t.getClass().getSimpleName())
                              .append(" / root=").append(c.getClass().getName()).append(": ").append(c.getMessage()).append('\n');
                        }
                        // 壳类的静态 String 字段（登录态常驻处）：Quark / FishConfig 两个名字都试
                        java.util.LinkedHashSet<ClassLoader> loaders = new java.util.LinkedHashSet<>();
                        for (Object sp : SPIDERS.values()) {
                            try { loaders.add(sp.getClass().getClassLoader()); } catch (Throwable ignored) { }
                        }
                        for (ClassLoader cl : loaders) {
                            for (String cn : new String[]{"com.github.catvod.spider.Quark", "com.github.catvod.spider.FishConfig"}) {
                                try {
                                    Class<?> c = Class.forName(cn, false, cl);
                                    for (java.lang.reflect.Field f : c.getDeclaredFields()) {
                                        if (!java.lang.reflect.Modifier.isStatic(f.getModifiers())) continue;
                                        try {
                                            f.setAccessible(true);
                                            Object v = f.get(null);
                                            if (v instanceof String s && !s.isEmpty())
                                                sb.append("  [static] ").append(cn).append('.').append(f.getName())
                                                  .append(" = ").append(s.length()).append("B: ")
                                                  .append(s, 0, Math.min(60, s.length())).append("...\n");
                                        } catch (Throwable ignored) { }
                                    }
                                } catch (Throwable ignored) { }
                            }
                        }
                        yield sb.toString();
                    }
                    case "guard-encrypt" -> {
                        // guest 内 Guard 加密：凭据回写 spUtils 前的逆向操作（同 Rc.KJ 体系）
                        String data = req.optString("data", "");
                        String enc = guardEncryptViaSpider(data);
                        yield enc == null ? JSONObject.NULL : enc;
                    }
                    case "guard-decrypt" -> {
                        // guest 内 Guard 解密：经已装载 spider 的类加载器调其解密入口
                        // （merge.Rc.KJ → HideUtils.decrypt → 壳内转译 SO）。FishConfig 的
                        // quark_scan 等流程经 10.0.2.2:18481 → 宿主 18481 监听 → 本 op。
                        String data = req.optString("data", "");
                        String dec = guardDecryptViaSpider(data);
                        yield dec == null ? JSONObject.NULL : dec;
                    }
                    case "probe" -> {
                        // 「guest 里这个类名到底是谁的」一次性问清：boot classpath 前置有没有生效、
                        // 桥的 UI 捕获层有没有被真框架顶掉（2026-09-26 网盘对话框上不来，两种原因表现一样）。
                        StringBuilder sb = new StringBuilder();
                        for (String n : new String[]{"android.app.AlertDialog", "android.app.Activity",
                                "android.app.Application", "android.widget.TextView", "android.content.Context"}) {
                            try {
                                Class<?> k = Class.forName(n);
                                String loc = "?";
                                try {
                                    loc = String.valueOf(k.getProtectionDomain().getCodeSource().getLocation());
                                } catch (Throwable ignored) { }
                                sb.append(n).append('←').append(loc).append(' ');
                            } catch (Throwable e) { sb.append(n).append("=!").append(e.getClass().getSimpleName()).append(' '); }
                        }
                        // 桥桩独有的成员（真框架 Dialog 没有 fillSpec）—— 前置生效与否的直接判据
                        try {
                            Class.forName("android.app.Dialog").getDeclaredMethod("fillSpec");
                            sb.append("Dialog.fillSpec=有(桩生效)");
                        } catch (Throwable e) { sb.append("Dialog.fillSpec=无(真框架)"); }
                        // 类型身份问题只能问运行时：谁解析到的、父链里有没有 ContextWrapper。
                        // （Rebo 的 VerifyError 说「register 是 Activity 但期望 ContextWrapper」，
                        //  真机上 Activity 就是 ContextWrapper 的子类 —— 说明要么解析到了别的东西，
                        //  要么这个类型压根没解析成功。）
                        for (String n : new String[]{"android.app.Activity", "android.content.ContextWrapper",
                                "android.app.Application$ActivityLifecycleCallbacks"}) {
                            try {
                                Class<?> k = Class.forName(n, false,
                                        SPIDERS.isEmpty() ? Server.class.getClassLoader()
                                                : SPIDERS.values().iterator().next().getClass().getClassLoader());
                                StringBuilder chain = new StringBuilder();
                                for (Class<?> c = k; c != null && chain.length() < 160; c = c.getSuperclass())
                                    chain.append(c.getSimpleName()).append('>');
                                sb.append(" | 类型 ").append(n).append(" 父链=").append(chain)
                                  .append(" 接口数=").append(k.getInterfaces().length);
                            } catch (Throwable e) { sb.append(" | 类型 ").append(n).append(" 解析失败 ").append(e); }
                        }
                        try {
                            Object a = bridge.Art.app();
                            sb.append("app=").append(a.getClass().getName())
                              .append(" isActivity=").append(a instanceof android.app.Activity);
                        } catch (Throwable e) { sb.append("app=!").append(e); }
                        yield sb.toString();
                    }
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
                    case "readfile" -> {
                        // 只读取证（CATCLAW_BRIDGE_DEBUG 泵才有意义）：把 guest 里的一个文件原样带回宿主，
                        // 用来取壳运行时才解出的 /data/cache/sharedb/config.db —— 里面才有
                        // `do=quark&type=<词>` 的词表（宿主侧回显探针已确认键名是 type，值全被拒）。
                        // 安全边界：只允许读普通文件、有体积上限、不带写路径的能力。
                        java.io.File rf = new java.io.File(req.optString("path"));
                        if (rf.isDirectory()) {
                            // 只读取证也用来**定位**文件（壳解出的 dex 名字带哈希/按需生成）
                            org.json.JSONArray ls = new org.json.JSONArray();
                            java.io.File[] kids = rf.listFiles();
                            if (kids != null) {
                                java.util.Arrays.sort(kids);
                                for (java.io.File k : kids)
                                    ls.put(new org.json.JSONObject()
                                            .put("n", k.getName())
                                            .put("dir", k.isDirectory())
                                            .put("len", k.length()));
                            }
                            yield ls.toString();
                        }
                        if (!rf.isFile()) throw new IllegalArgumentException("不是普通文件: " + rf);
                        long len = rf.length();
                        long roff = Math.max(0L, req.optLong("off", 0L));
                        long cap = Math.max(1L, req.optLong("cap", 24L * 1024 * 1024));
                        // 一律按窗口搬：cap 就是"这一次最多多少字节"，大文件从 off=0 分几次读就行
                        if (roff >= len) throw new IllegalArgumentException("off 超过文件长度 " + len);
                        try (java.io.RandomAccessFile ra = new java.io.RandomAccessFile(rf, "r")) {
                            ra.seek(roff);
                            int n = (int) Math.min(len - roff, cap);
                            byte[] part = new byte[n];
                            ra.readFully(part);
                            yield java.util.Base64.getEncoder().encodeToString(part);
                        }
                    }
                    case "qrtext" -> {
                        // 「爬虫给 URL、宿主出码」这条契约通道的受控自检（调试泵专用，与任何源无关）：
                        // 造一个只含 URL 文本的对话框走完整生产管线（show → 取不到位图 → qrText 上行
                        // → 宿主 QrPng 出码）。必须经 Art.stubFirst() 反射：直接 new android.app.*
                        // 会解析到 guest 框架里的真类（boot 命名空间），那样一条事件都不会发。
                        ClassLoader sf = bridge.Art.stubFirst();
                        String url = req.optString("url", "https://su.quark.cn/selftest?token=CTRLQRTEXT77");
                        boolean late = req.optBoolean("async", false);
                        Object r;
                        try {
                            r = sf.loadClass("android.app.AlertDialog")
                                    .getMethod("selftest", String.class, boolean.class)
                                    .invoke(null, url, late);
                        } catch (Throwable t) {
                            Throwable c = t.getCause() != null ? t.getCause() : t;
                            yield "{\"error\":\"" + c.getClass().getName() + ": " + c.getMessage() + "\"}";
                        }
                        yield String.valueOf(r);
                    }
                    case "dumpstr" -> {
                        // 反射取证（只读，CATCLAW_BRIDGE_DEBUG 泵用）：把壳在运行时才解出的那份 dex
                        // （config.db）里某个类的**编码字符串表**整本解出来。
                        // 不猜词：拿一段已知明文（jar 自己回给我们的报错句 "Unknown quark proxy type: "）
                        // 在表里定位，用首字符反推 key（减/异或各试一次），命中就整表解码 ——
                        // 那张表里的兄弟常量正是 switch 比对的 type 取值。
                        String needle = req.optString("needle", "Unknown quark proxy type: ");
                        String[] clss = req.optString("cls",
                                "com.github.catvod.spider.ProxyOrigin,com.github.catvod.spider.Quark,"
                                        + "com.github.catvod.spider.merge.A.b0").split(",");
                        java.util.LinkedHashSet<ClassLoader> loaders = new java.util.LinkedHashSet<>();
                        for (Object sp : SPIDERS.values()) {
                            for (Class<?> c = sp.getClass(); c != null; c = c.getSuperclass()) {
                                if (c.getClassLoader() != null) loaders.add(c.getClassLoader());
                            }
                        }
                        StringBuilder dbg = new StringBuilder();
                        org.json.JSONArray toks = new org.json.JSONArray();
                        for (ClassLoader cl : loaders) {
                            dbg.append("loaders=").append(loaders.size()).append(' ');
                            for (String cn : clss) {
                                Class<?> target;
                                try { target = Class.forName(cn.trim(), false, cl); }
                                catch (Throwable t) { continue; }
                                for (java.lang.reflect.Field f : target.getDeclaredFields()) {
                                    if (!java.lang.reflect.Modifier.isStatic(f.getModifiers())
                                            || !f.getType().isArray()) continue;
                                    long[] t2;
                                    try {
                                        f.setAccessible(true);
                                        Object arr = f.get(null);
                                        if (arr instanceof short[] s) {
                                            t2 = new long[s.length];
                                            for (int i = 0; i < s.length; i++) t2[i] = s[i];
                                        } else if (arr instanceof char[] s) {
                                            t2 = new long[s.length];
                                            for (int i = 0; i < s.length; i++) t2[i] = s[i];
                                        } else if (arr instanceof int[] s) {
                                            t2 = new long[s.length];
                                            for (int i = 0; i < s.length; i++) t2[i] = s[i];
                                        } else if (arr instanceof byte[] s) {
                                            t2 = new long[s.length];
                                            for (int i = 0; i < s.length; i++) t2[i] = s[i];
                                        } else continue;
                                    } catch (Throwable t) { continue; }
                                    int nl = needle.length();
                                    for (int i = 0; i + nl <= t2.length; i++) {
                                        for (int mode = 0; mode < 2; mode++) {
                                            long key = mode == 0 ? (t2[i] - needle.charAt(0)) : (t2[i] ^ needle.charAt(0));
                                            boolean ok = true;
                                            for (int j = 1; j < nl; j++) {
                                                int c = mode == 0 ? (int) ((t2[i + j] - key) & 0xFFFF)
                                                        : (int) ((t2[i + j] ^ key) & 0xFFFF);
                                                if (c != needle.charAt(j)) { ok = false; break; }
                                            }
                                            if (!ok) continue;
                                            dbg.append("HIT ").append(target.getName()).append('.').append(f.getName())
                                                    .append(" mode=").append(mode == 0 ? "sub" : "xor")
                                                    .append(" key=0x").append(Long.toHexString(key))
                                                    .append(" @").append(i).append(" len=").append(t2.length).append("; ");
                                            StringBuilder cur = new StringBuilder();
                                            for (int j = 0; j < t2.length; j++) {
                                                int c = mode == 0 ? (int) ((t2[j] - key) & 0xFFFF)
                                                        : (int) ((t2[j] ^ key) & 0xFFFF);
                                                if (c >= 32 && c < 127) cur.append((char) c);
                                                else {
                                                    if (cur.length() >= 2 && toks.length() < 900) toks.put(cur.toString());
                                                    cur.setLength(0);
                                                }
                                            }
                                            if (cur.length() >= 2 && toks.length() < 900) toks.put(cur.toString());
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                        yield new JSONObject().put("note", dbg.toString()).put("tokens", toks).toString();
                    }
                    case "netstat" -> {
                        // guest 监听端口盘点（/proc/net/tcp{,6}，st=0A 即 LISTEN）：
                        // 诊断壳的流中转服务（6678）是否真的活着。
                        // 2026-09-27：宿主侧曾见空输出——改用 FileReader 逐行（procfs size=0，
                        // readAllLines 在某些实现上直接返回空）并把异常打进结果，不再静默吞。
                        StringBuilder sb = new StringBuilder();
                        for (String f : new String[]{"/proc/net/tcp", "/proc/net/tcp6"}) {
                            try (java.io.BufferedReader br = new java.io.BufferedReader(new java.io.FileReader(f))) {
                                String ln;
                                while ((ln = br.readLine()) != null) {
                                    String[] p = ln.trim().split("\\s+");
                                    if (p.length > 3 && "0A".equals(p[3]))
                                        sb.append(Integer.parseInt(p[1].split(":")[1], 16)).append(' ');
                                }
                            } catch (Throwable e) { sb.append('!').append(f).append('=').append(e).append(' '); }
                        }
                        yield sb.toString();
                    }
                    case "ls" -> {
                        // 诊断（2026-10-01）：列目录（递归、带大小/mtime）。起因 —— Guard 网盘源的
                        // 登录态靠 refresh_token，而 prefs 里没有这个键；令牌必然是**文件**，
                        // 落在 <data.dir>（guest 的 /data/catclaw 是 initramfs，VM 冷启即清）。
                        // GUI 与日志都看不到这个目录，只能从桥里列。纯 java.io（不 fork）。
                        String dir = req.optString("dir", System.getProperty("data.dir", "/data/catclaw"));
                        int depth = req.optInt("depth", 3);
                        StringBuilder sb = new StringBuilder("[ls] " + dir + "\n");
                        walk(new java.io.File(dir), depth, sb, "");
                        yield sb.toString();
                    }
                    case "cat" -> {
                        // 诊断：读小文件。文本直出；b64=1 时给 base64（SQLite/二进制用）。默认上限 16KB。
                        String path = req.optString("path", "");
                        int max = req.optInt("max", 16384);
                        java.io.File f = new java.io.File(path);
                        if (!f.isFile()) yield "不是文件: " + path;
                        byte[] all = java.nio.file.Files.readAllBytes(f.toPath());
                        int n = Math.min(all.length, max);
                        byte[] part = java.util.Arrays.copyOf(all, n);
                        String body = req.optBoolean("b64", false)
                                ? java.util.Base64.getEncoder().encodeToString(part)
                                : new String(part, "UTF-8");
                        yield "[" + path + " " + all.length + "B，显示前 " + n + "B]\n" + body;
                    }
                    case "fetch" -> {
                        // 通用 guest 内探针（2026-09-27 壳流服务 6678 排查）：原始 socket 直连
                        // host:port，发一条 HTTP/1.0 GET，把「连上没 / 收到多少字节 / 原始回复」
                        // 原样回传。宿主的「unexpected end of stream」到底是「对端秒 FIN」还是
                        // 「有数据但非 HTTP」，一次照清楚；也可复现壳的端口探测行为。
                        String host = req.optString("host", "127.0.0.1");
                        int port = req.optInt("port", 0);
                        String path = req.optString("path", "/");
                        String range = req.optString("range", null);
                        int max = req.optInt("max", 2048);
                        int tmo = req.optInt("timeout", 6000);
                        /* 2026-09-27 播放链路排查：okhttp（Art.java）请求 6678 报 unexpected end of stream
                         * 而裸 HTTP/1.0 探针成功——把「请求特征」做成参数，一网打尽：
                         * http=1.1 / keepalive=1 / gzip=1 / ua=<字符串> / connhang=<毫秒>（发完挂住不读，
                         * 模拟「连上不发/慢发」） */
                        String httpVer = req.optString("http", "1.0");
                        String ua = req.optString("ua", "CatClawProbe/1.0");
                        boolean keepalive = req.optBoolean("keepalive", false);
                        boolean gzip = req.optBoolean("gzip", false);
                        StringBuilder fsb = new StringBuilder();
                        java.net.Socket sk = null;
                        try {
                            sk = new java.net.Socket();
                            sk.connect(new java.net.InetSocketAddress(host, port), 4000);
                            sk.setSoTimeout(tmo);
                            fsb.append("connected\n");
                            StringBuilder rq = new StringBuilder();
                            rq.append("GET ").append(path).append(" HTTP/").append(httpVer).append("\r\n")
                              .append("Host: ").append(host).append(':').append(port).append("\r\n");
                            if (keepalive) rq.append("Connection: Keep-Alive\r\n");
                            if (gzip) rq.append("Accept-Encoding: gzip\r\n");
                            if (range != null && range.length() > 0)
                                rq.append("Range: ").append(range).append("\r\n");
                            rq.append("User-Agent: ").append(ua).append("\r\n\r\n");
                            java.io.OutputStream so = sk.getOutputStream();
                            so.write(rq.toString().getBytes("UTF-8"));
                            so.flush();
                            fsb.append("sent ").append(rq.length()).append("B\n");
                            byte[] buf = new byte[max];
                            int n = 0;
                            try {
                                int r;
                                while (n < max && (r = sk.getInputStream().read(buf, n, max - n)) > 0) n += r;
                            } catch (java.net.SocketTimeoutException te) {
                                fsb.append("read-timeout\n");
                            }
                            fsb.append("recv ").append(n).append("B: ");
                            for (int i = 0; i < n; i++) {
                                int b = buf[i] & 0xff;
                                if (b >= 32 && b < 127) fsb.append((char) b);
                                else if (b == 10) fsb.append("\\n");
                                else if (b == 13) fsb.append("\\r");
                                else fsb.append("\\x")
                                        .append(Character.forDigit((b >> 4) & 15, 16))
                                        .append(Character.forDigit(b & 15, 16));
                            }
                        } catch (Throwable e) {
                            fsb.append("EX ").append(e.getClass().getName()).append(": ").append(e.getMessage());
                        } finally {
                            if (sk != null) try { sk.close(); } catch (Throwable ignored) { }
                        }
                        yield fsb.toString();
                    }
                    case "prefsput" -> {
                        // 宿主回灌 guest 偏好（guest 的 /data 是 tmpfs，VM 冷启即清，见 PrefsStore.flush
                        // 的 prefs-sync 上行）：写回 shared_prefs/<name>.xml。PrefsStore 按名惰性读盘，
                        // jar 首次访问该 name 时自然载入 —— 回灌必须发生在任何 spider 代码运行之前。
                        String pn = req.optString("name", "default");
                        String xml = req.optString("xml", "");
                        if (pn.endsWith(".xml")) pn = pn.substring(0, pn.length() - 4);
                        int slash = pn.lastIndexOf('/');
                        if (slash >= 0) pn = pn.substring(slash + 1);
                        if (pn.isEmpty()) pn = "default";
                        java.io.File dir = new java.io.File(System.getProperty("data.dir", "data"), "shared_prefs");
                        if (!dir.isDirectory()) dir.mkdirs();
                        java.nio.file.Files.write(new java.io.File(dir, pn + ".xml").toPath(),
                                xml.getBytes(java.nio.charset.StandardCharsets.UTF_8));
                        System.err.println("[prefs] 宿主回灌 " + pn + "（" + xml.length() + "B）");
                        yield "ok";
                    }
                    default -> {
                        // 端口下发：桥内无 JNI（Android 走 TvBoxCompatBridge.SetProxyPort），走协议直写静态字段
                        if ("setProxyPort".equals(fop)) {
                            com.github.catvod.crawler.SpiderApi.setHostProxyPort(req.optInt("port"));
                            bridge.Art.setStubHostProxyPort(req.optInt("port"));   // 桩命名空间副本（ui_stub.dex）
                            // guest 里 127.0.0.1 是它自己的回环，爬虫的 proxy:// 回调必须就地转发给宿主
                            if (Art.onArt()) Art.serveProxy(req.optInt("port"));
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
            emit(out);
            OP_START.set(0);   // op 已完成：看门狗清零（避免误报下一轮 dump）
        }
    }

    private static String load(String site, String className, String ext, org.json.JSONArray jars) throws Exception {
        return loadFull(site, className, ext, jars, null, null, null, 0, false);
    }

    /** 壳框架加载：shellJar=壳 dex 转换产物；rawJar=原始 Guard jar（assets/*.so 解密引擎）；realJar=解壳产物；
     *  guardPort=Guard QEMU 解密服务端口（0=未启用 → ARM 调用直接报错，没有第二引擎）。 */
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
        if (ppid <= 0L || ppid == myPidSafe()) return;
        Thread t = new Thread(() -> {
            while (true) {
                try {
                    Thread.sleep(2000);
                } catch (InterruptedException e) {
                    return;
                }
                if (pidAlive(ppid)) continue;
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

    /**
     * {@code ProcessHandle} 是 JDK9 类，ART guest 里没有 —— 解析会抛
     * {@code NoClassDefFoundError}，看门狗不该因此把桥带崩（2026-09-30）。取不到 pid 就返回 -1
     * （不会与任何真实 ppid 相等 → 看门狗按"宿主还在"处理）。
     */
    private static long myPidSafe() {
        try {
            return ProcessHandle.current().pid();
        } catch (Throwable t) {
            return -1L;
        }
    }

    /** 同上：查不到进程存活信息时一律当"活着"（宁可留下孤儿，也不能误杀自己）。 */
    private static boolean pidAlive(long pid) {
        try {
            return ProcessHandle.of(pid).isPresent();
        } catch (Throwable t) {
            return true;
        }
    }

    private static String loadFull(String site, String className, String ext, org.json.JSONArray jars,
                                   String shellJar, String rawJar, String realJar, int guardPort,
                                   boolean force) throws Exception {
        synchronized (LOCK) {
            if (SPIDERS.containsKey(site)) {
                if (!force) return "loaded";
                // force 重装载（2026-09-27 端口抢占修复的桥侧配套）：Guard 家族的多个源共享壳内部的
                // 约定流服务端口（6678），任何同 jar 兄弟源被装载都会把先装载者的流服务顶掉——
                // 表现为其 playerContent 返回的 6678 地址请求全 0B（handler NPE）。宿主在「播放
                // Guard 家族源且期间有同 jar 站点被装载」时以 force 重装载本源：新 DexClassLoader
                // → 新类 → 壳静态/服务状态重建 → 本源重新占回端口。实测新 site key 重装载 3.4s 可
                // 100% 抢回（docs 交接 §6.8）。
                SPIDERS.remove(site);
                // force 重装载同时**换掉站点锁**（2026-09-30）：一条 hang 死的网盘调用会永久持有旧锁，
                // 不换锁的话新实例照样被上一代的僵尸调用堵死 ⇒ 实测症状是「点一次无响应的线路，
                // 这个网盘源整个会话都废了，只能重启应用」。换锁后那条僵尸线程只握着旧对象
                // （它自己慢慢结束或永久泄漏，但不再挡路），新调用用新锁，站点当场复活。
                SITE_LOCKS.remove(site);
                System.err.println("[srv] force 重装载: " + site + " (" + className + ")");
            }

            // guest（QEMU 里的真 ART）：壳 jar 由 ART 直接吃，ARM native 就地执行，
            // 既不需要 Guard 解密通道，也不需要 dex2jar 产物（2026-09-25 P3 实测 336ms 出 homeContent）。
            if (Art.onArt()) {
                String jar = firstDexJar(rawJar, shellJar, realJar, jars);
                if (jar == null) throw new IllegalStateException("ART guest 只能装载 dex 型 jar（纯 class 的 java 源请留在宿主 JVM）: site=" + site);
                SPIDERS.put(site, Art.loadSpider(site, jar, className, ext));
                System.err.println("[srv] ART 已装载: " + site + " ← " + jar);
                return "loaded";
            }

            // Guard QEMU 通道（2026-09-25 定案）：解密/签名/计算/proxyInvoke 一律由 Guard VM 里的
            // ftyguard so（ARM）执行 —— 运行时已经没有 unidbg 这条备选路径，端口没下来就是没就绪。
            if (guardPort > 0) QemuGuardChannel.setPort(guardPort);

            // 壳框架模式：shellJar/rawJar/realJar 由宿主下发（Guard 源）——壳实例跑在独立 loader
            if (shellJar != null && !shellJar.isEmpty()) {
                if (rawJar != null && !rawJar.isEmpty() && guardPort <= 0)
                    throw new IllegalStateException("Guard 源需要 QEMU guest 执行 ARM native，但宿主没下发 guardPort"
                            + "（Guard VM 未就绪）");
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

    /** 一个 op 的处理开始时间（0=空闲）：看门狗据此发现挂死的装载/调用。 */
    private static final java.util.concurrent.atomic.AtomicLong OP_START = new java.util.concurrent.atomic.AtomicLong(0);
    private static final java.util.Set<Long> OP_DUMPED = java.util.concurrent.ConcurrentHashMap.newKeySet();

    /** op 处理超时看门狗：>45s 未完成的 op 把全部线程栈 dump 到 stderr（挂死现场一次，换 id 重置）。 */
    private static final class OpWatchdog {
        static void begin(long id, String op) {
            OP_DUMPED.clear();
            OP_START.set(System.currentTimeMillis());
        }

        static void start() {
            Thread t = new Thread(() -> {
                while (true) {
                    long st = OP_START.get();
                    if (st > 0 && System.currentTimeMillis() - st > 45_000 && OP_DUMPED.add(st)) {
                        System.err.println("[watchdog] op 已处理超 45s，全线程栈：");
                        for (var e : Thread.getAllStackTraces().entrySet()) {
                            if (e.getValue().length == 0) continue;
                            StringBuilder sb = new StringBuilder("[watchdog] 「").append(e.getKey().getName()).append("」");
                            for (StackTraceElement f : e.getValue()) sb.append("\n    at ").append(f);
                            System.err.println(sb);
                        }
                        OP_START.set(System.currentTimeMillis());   // 45s 后再 dump 一轮（看现场变化）
                    }
                    try { Thread.sleep(5000); } catch (InterruptedException ignored) { return; }
                }
            }, "op-watchdog");
            t.setDaemon(true);
            t.start();
        }
    }

    static void injectStaticContext(ClassLoader loader, String className, Object ctx) {

        try {
            Class<?> c = loader.loadClass(className);
            // ⚠ 必须注入 **Application**（不是裸 Context）：TVBox 的 InitOrigin 里
            //   `public static Application context()` 返回的就是 Application 实例，
            //   init(Context) 内部会强转成 Application —— 传裸 Context 会被 ClassCastException
            //   吞掉，context() 依旧返回 null（实测 2026-09-16：传 Context 无效，改 Application 后 NPE 消失）。
            //   ctx 由调用方传入（同一个实例注入到所有公共类，避免各自 new 出一堆孤岛）。
            //   guest 桩命名空间：ctx 是桩 Application（Object 传递）；参数类型按**名字**匹配
            //   —— Class 身份跨命名空间不同，==Context.class 会失配（2026-09-26）。
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
                    if (ps.length == 1 && "android.content.Context".equals(ps[0].getName())) {
                        m.invoke(null, ctx);
                    } else if (ps.length == 2 && "android.content.Context".equals(ps[0].getName()) && ps[1] == String.class) {
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
        // 「播放页加载很慢」要能一眼看出是爬虫自身耗时还是宿主侧排队（实测 2026-09-16 需要此数据）。
        // ⚠ 曾在锁内对每个参数逐字符 hexdump（[srv] arg[i] cps=…）——那是排查「内嵌 JSON id
        //   被引号污染」的临时手段：它对每个调用都做 O(2n) 字符串构造并占着全局锁 + stderr
        //   通道，长参数（筛选 ext / 网盘 JSON id）时肉眼可见地拖慢每一次调用，已移除。
        //   需要看参数原文时在此处临时加回（必须随 initrd/gb.dex 重编才进 guest）。
        final long t0 = System.currentTimeMillis();
        // ⚠ 站点锁必须 tryLock，不能 synchronized（2026-09-30 实测「点一次没登录的盘，
        //   整个网盘源以后再也不响应」）：壳里的网盘调用**没有读超时**，一条 hang 死的
        //   playerContent 会一直持有这把锁；synchronized 让后续调用无限排队 ⇒
        //   宿主每条都撞 300s 看门狗（实测 homeContent/categoryContent 连着全部 300s 无响应），
        //   整站被永久毒化，而且用户看到的是「点了没反应」。
        //   tryLock 只排队 10s：拿不到锁立刻回**说人话**的错误，别的站不受影响。
        final java.util.concurrent.locks.ReentrantLock siteLock =
                SITE_LOCKS.computeIfAbsent(site, k -> new java.util.concurrent.locks.ReentrantLock());
        if (!siteLock.tryLock(10, java.util.concurrent.TimeUnit.SECONDS))
            throw new IllegalStateException("站点 " + site + " 上一次调用仍未结束"
                    + "（网盘线路无响应，通常是那个盘没登录）——请稍后重试或换线路");
        try {
            Object instance = SPIDERS.get(site);
            if (instance == null) throw new IllegalStateException("site not loaded: " + site);
            Class<?> cls = instance.getClass();
            Object result;
            switch (method) {
                case "homeContent" -> result = cls.getMethod("homeContent", boolean.class)
                        .invoke(instance, args != null && args.length() > 0 && args.optBoolean(0));
                case "homeVideoContent" -> result = cls.getMethod("homeVideoContent").invoke(instance);
                case "categoryContent" -> {
                    // 宿主发 [tid, pg, {筛选键→值}]：extend 非空即 filter=true（TVBox
                    // GridFilterDialog 同一判定）。以前这里硬编码 false+空表，筛选器在桌面端整条空转。
                    HashMap<String, String> ext = extend(args);
                    result = cls.getMethod("categoryContent", String.class, String.class, boolean.class, HashMap.class)
                            .invoke(instance, args.optString(0), args.optString(1), !ext.isEmpty(), ext);
                }
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
                // spider 型直播源（TVBox ApiConfig 把非本机 api 包成 proxy?do=live&type=txt&ext=<b64>
                // → 宿主解出真实地址后交给 liveContent，回 TXT/M3U 频道表）。
                // 缺这条时桌面端整类直播源只能报「爬虫类型不支持 liveContent」。
                case "liveContent" -> result = cls.getMethod("liveContent", String.class)
                        .invoke(instance, args.optString(0));
                default -> throw new IllegalArgumentException("unknown method: " + method);
            }
            var out = result == null ? "{}" : result.toString();
            var dt = System.currentTimeMillis() - t0;
            if (dt > 300) {
                System.err.println("[srv] " + site + "." + method + " 耗时 " + dt + "ms，结果 " + out.length() + " 字节");
            }
            // 诊断：playerContent/detailContent 的应答原文（排查「宿主解析不出播放地址」类问题 ——
            // 3671B 这类非标准形态光看字节数猜不出来，2026-09-26 seed 站实测）
            if (("playerContent".equals(method) || "detailContent".equals(method)) && out.length() > 2 && out.length() < 40000) {
                System.err.println("[srv-resp] " + site + "." + method + " ← " + out);
            }
            // 网盘 Cookie 持久化：spider 刚跑完，sharedb（config.db 等）可能被写——变化就推给宿主
            sharedbSync();
            return out;
        } finally {
            siteLock.unlock();
        }
    }

    /** sharedb 上次同步状态（内容键）：变了才上行，避免每个 call 都刷事件。 */
    private static volatile String sharedbKey;

    /**
     * 网盘 Cookie 等持久化文件的同步（2026-10-02）：{@code /data/cache/sharedb/config.db}
     * 是 FishConfig 存夸克/GitHub Cookie 的地方，而 guest /data 是 tmpfs——VM 冷启即清，
     * 登录态跟着丢（用户实测「重启后登入状态消失」）。宿主偏好链（prefs-sync）只覆盖
     * SharedPreferences XML，管不到这里 ⇒ 每个 call 结束后 stat 一遍，变化就把整个目录
     * 内容上行（ev=file-sync），宿主落 guest-prefs\sharedb\，下次握手回灌。
     */
    private static void sharedbSync() {
        try {
            java.io.File dir = new java.io.File("/data/cache/sharedb");
            java.io.File[] fs = dir.listFiles();
            StringBuilder key = new StringBuilder();
            org.json.JSONArray files = new org.json.JSONArray();
            if (fs != null) {
                java.util.Arrays.sort(fs, java.util.Comparator.comparing(java.io.File::getName));
                for (java.io.File f : fs) {
                    if (!f.isFile()) continue;
                    key.append(f.getName()).append(':').append(f.length()).append(':').append(f.lastModified()).append(';');
                    byte[] data = java.nio.file.Files.readAllBytes(f.toPath());
                    files.put(new org.json.JSONObject()
                            .put("name", f.getName())
                            .put("b64", java.util.Base64.getEncoder().encodeToString(data)));
                }
            }
            String k = key.toString();
            if (k.equals(sharedbKey)) return;
            sharedbKey = k;
            if (files.length() > 0) {
                UiBridge.emit(new org.json.JSONObject().put("ev", "file-sync").put("files", files));
                System.err.println("[srv] sharedb 变化 → 上行 " + files.length() + " 个文件（网盘 Cookie 持久化）");
            }
        } catch (Throwable t) {
            System.err.println("[srv] sharedb 同步失败: " + t);
        }
    }

    /** categoryContent 的第 3 参：宿主发的 <c>{筛选键: 值}</c> 对象 → 爬虫要的 HashMap。缺省给空表。 */
    private static HashMap<String, String> extend(org.json.JSONArray args) {
        HashMap<String, String> m = new HashMap<>();
        org.json.JSONObject o = args.optJSONObject(2);
        if (o == null) return m;
        for (String k : o.keySet()) m.put(k, o.optString(k));
        return m;
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

        // 四步分派与宿主 JRE 时代的 proxy op 完全同源（抽成 proxyDispatch 两边共用）：
        // guest 里以前只接了第 ④ 步，于是网盘的 do=config/input/quark 全部 502
        // （2026-09-26 实测：只有 do=ck 有货）—— 那三步才是 Pan/Cloud_ 家族真正答题的地方。
        Object[] rs = proxyDispatch(instance, param);
        if (rs == null) throw new IllegalStateException("proxy 无人应答 do=" + param.get("do"));

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

    /**
     * proxy 的四步分派（TVBox {@code ApiConfig.proxyLocal} + {@code JarLoader.invokeProxy} 合体语义）：
     * ① 爬虫实例的 {@code proxy(Map)}；② {@code Cloud_<do>.proxy(Map)}（夸父/优汐/嘟嘟/阿狸）；
     * ③ {@code proxyInput()}（Pan 家族的 Cookie 粘贴页）；④ jar 内静态 {@code Proxy.proxy(Map)}（荐片 do=ck 握手）。
     * <p>宿主 JRE 的 proxy op 与 ART guest 里的 /proxy 服务（{@code bridge.Art.serveOne}）共用它 ——
     * 少一步就有半类网盘交互哑掉。</p>
     */
    static Object[] proxyDispatch(Object instance, HashMap<String, String> param) throws Exception {
        // ① 实例方法：沿类层次找 proxy(Map)（爬虫可能覆写成 HashMap 形参，不能硬套 Map.class）。
        //    基类桩的 proxy 默认转 proxyLocal → null；爬虫没覆写时 rs 为 null，走 ②。
        // 壳类（BaseSpiderGuard 家族）把真爬虫藏在字段里：不先掏出来，① 与 ③ 永远找不到
        // —— 宿主 JRE 时代 SPIDERS 里存的是解壳后的真类，ART 里存的是壳，两边形状不一样。
        Object target = guardTarget(instance);
        Object[] rs = invokeProxyMethod(target, param);
        // 诊断探针（2026-10-01）：记「四步里哪一步应答」。夸克「正在获取账号信息…」卡住时，
        // 宿主只看到请求进了分派、看不到是 ①壳 proxy 还是 ③proxyInput（Cookie 粘贴页 HTML）
        // 应答的 —— 拿到 JSON 还是 HTML 决定了插件能不能解析出账号信息，必须打出来。
        String hit = rs != null ? "①壳实例 proxy" : null;

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
                    if (rs != null) hit = "②Cloud_" + doVal + ".proxy";
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
                java.lang.reflect.Method m = target.getClass().getMethod("proxyInput");
                Object r = m.invoke(null);
                if (r instanceof Object[] arr) rs = arr;
                if (rs != null) hit = "③" + target.getClass().getSimpleName() + ".proxyInput";
            } catch (NoSuchMethodException ignored) {
            } catch (Throwable t) {
                Throwable root = t;
                while (root.getCause() != null) root = root.getCause();
                System.err.println("[srv] " + target.getClass().getSimpleName()
                        + ".proxyInput 异常: " + root.getClass().getSimpleName() + ": " + root.getMessage());
            }
        }

        // ④ 壳自带的静态 Proxy.proxy(Map)（真机 JarLoader.invokeProxy 语义；荐片 do=ck 握手归它）
        if (rs == null) rs = jarProxy(instance, param);
        if (rs != null && hit == null) hit = "④壳静态 Proxy.proxy";
        String doVal = param.getOrDefault("do", "");
        if (doVal.length() > 0)
            System.err.println("[probe] do=" + doVal + " type=" + param.getOrDefault("type", "")
                    + " site=" + param.getOrDefault("site", "") + " 命中=" + (hit == null ? "四步都不接" : hit));
        return rs;
    }

    /**
     * 数据目录观察者（诊断，2026-10-01）：每 20s 走一遍 {@code <data.dir>}，**清单有变化时**
     * 把全量清单打到 stderr（→ guest console → 宿主日志）。
     *
     * <p>为什么需要：Guard 网盘源的登录态靠 {@code refresh_token}，而插件写过的所有 prefs 键里
     * **没有**这个键 —— 令牌是**文件**。guest 的 {@code /data/catclaw} 落在 initramfs（VM 冷启即清），
     * 宿主只同步了 {@code shared_prefs/*.xml}，于是 cookie 活着（能播）、令牌死掉（界面显示未登录）。
     * 要证明「令牌文件落在哪个子目录」，GUI 和日志都看不到，只能让 guest 自己报出来。</p>
     */
    private static volatile boolean DATA_WATCH_STARTED = false;

    /**
     * B1.0（2026-10-01）：把 <b>adbd</b> 拉起来，让宿主能 {@code adb connect} 进 guest 直接排障
     * （看目录 / 推拉文件 / logcat / screencap），不必再"加桩 → 重建镜像"。
     *
     * <p><b>为什么由桥（Java）而不是 /init 拉起</b>：我们 guest 没有真正的 init property_service，
     * 属性是 {@code /proppreload.so}（LD_PRELOAD）假装的；artlaunch 带着它启动，**子进程继承该环境**。
     * 于是 adbd 通过 proppreload 导出的 {@code Java_android_os_SystemProperties_native_1*} JNI 符号
     * 读到我们这里 set 的属性。shell 里没有 setprop，所以走 Java 这条路。</p>
     *
     * <p><b>为什么必须是 x86-64 动态链接那份 adbd</b>：镜像根 {@code /bin/adbd} 是 ARM aarch64
     * <b>静态</b>版 —— 静态链接会让 LD_PRELOAD 失效、属性读不到；正确来源是
     * {@code system/apex/com.android.adbd/bin/adbd}（x86-64 PIE）。</p>
     *
     * <p>可用 {@code -Dbridge.adbd=0} 关闭。</p>
     */
    private static void startAdbd() {
        if ("0".equals(System.getProperty("bridge.adbd"))) return;
        Thread t = new Thread(() -> {
            try {
                try { new java.io.File("/data/misc/adb").mkdirs(); } catch (Throwable ignored) { }
                // 单实例守卫（2026-10-03）：init 侧（v9 段）已经拉起 adbd，桥侧再起一个的话，
                // 两个 adbd 抢 5555 —— 输家 bind 失败却不退出，陷入错误重试循环：
                // 实测恒定吃 ~2 核 CPU、其错误日志经 liblog→logdw 灌爆 fakelogd（1 核）→
                // 串口 ~5500 中断/s（每次都是 VM exit）→ 桥的 pump 线程再吃 ~55% ⇒
                // 整个 guest 空闲时白烧 ~4 核（qemu 宿主进程 560% 的主因）。
                // 与 startSurfaceFlinger 的守卫同款：扫 /proc/*/cmdline，纯文件读，零 fork。
                for (int w = 0; w < 30; w++) {
                    if (adbdAlreadyRunning()) {
                        System.err.println("[adbd] 已有实例（init 侧）→ 桥侧不再拉起（双实例会抢 5555 空转）");
                        return;
                    }
                    Thread.sleep(500);
                }
                setProp("service.adb.tcp.port", "5555");
                setProp("ro.adb.secure", "0");
                // B1.1 排障用：允许 `adb root` 拿到 guest 内的 root shell（要看 /dev、挂载、/data 里的东西）
                setProp("service.adb.root", "1");
                setProp("ro.debuggable", "1");
                java.io.File bin = new java.io.File("/system/bin/adbd");
                if (!bin.isFile()) {
                    System.err.println("[adbd] 缺 " + bin + "（镜像里没注入？见 .zwork/rebuild_gb.cmd）");
                    return;
                }
                Process p = Runtime.getRuntime().exec(new String[]{"/system/bin/adbd"});
                System.err.println("[adbd] 已拉起（宿主 adb connect 127.0.0.1:<hostfwd→5555>）");
                pump(p.getInputStream(), "[adbd] ");
                pump(p.getErrorStream(), "[adbd!] ");
            } catch (Throwable e) {
                System.err.println("[adbd] 启动失败: " + e);
            }
        }, "adbd-launch");
        t.setDaemon(true);
        t.start();
    }

    /** 反射设属性：src/ 是"桩环境"（编译期无 android.jar），而真类就在 BCP 上。 */
    private static void setProp(String k, String v) {
        try {
            Class<?> sp = Class.forName("android.os.SystemProperties");
            sp.getMethod("set", String.class, String.class).invoke(null, k, v);
            System.err.println("[adbd] prop " + k + "=" + v);
        } catch (Throwable e) {
            System.err.println("[adbd] prop " + k + " 设置失败: " + e);
        }
    }

    /**
     * B1.1：拉起 <b>SurfaceFlinger</b>。必须由本进程（Java）而不是 init 脚本拉起，原因有二：
     * <ol>
     *   <li><b>HIDL 的 ready 属性</b>：hwservicemanager 上线后，客户端靠属性
     *       {@code hwservicemanager.ready} 判断"注册表可用了"。我们的属性服务是 proppreload 假装的，
     *       没人 set 这个属性 ⇒ SF 的 HIDL 客户端会一直打
     *       「Waited for hwservicemanager.ready for a second, waiting another...」（实测）。</li>
     *   <li><b>必须 root</b>：{@code /dev/binder} 是 0600 root；用 adb shell(uid=shell) 手推只会得到
     *       「Binder driver could not be opened」的假象（实测）。</li>
     * </ol>
     * 依赖 init 段先起好 servicemanager / hwservicemanager / weston 与 /dev/dri。
     * {@code -Dbridge.sf=0} 可关。
     */
    private static void startSurfaceFlinger() {
        if ("0".equals(System.getProperty("bridge.sf"))) return;
        Thread t = new Thread(() -> {
            try {
                // 单实例守卫（2026-10-02）：composer HAL 是**单客户端** —— init 侧已拉起 SF 后，
                // 桥侧再起的每一个都崩在 HwcComposer "failed to create composer client"（SIGABRT），
                // 形成多实例假象且刷屏。桥 fork 可能被 proppreload 拦截（error=11），不能派 pidof
                // 子进程 ⇒ 直接扫 /proc/*/cmdline（纯文件读，零 fork）。
                for (int w = 0; w < 60; w++) {
                    if (sfAlreadyRunning()) {
                        System.err.println("[sf] SurfaceFlinger 已有实例 → 桥侧不再拉起（composer HAL 单客户端）");
                        return;
                    }
                    if (new java.io.File("/tmp/hal_ready").exists()) break;
                    Thread.sleep(500);
                }
                if (sfAlreadyRunning()) {
                    System.err.println("[sf] SurfaceFlinger 已有实例 → 桥侧不再拉起（composer HAL 单客户端）");
                    return;
                }
                setProp("hwservicemanager.ready", "true");
                // 图形栈属性：HAL 变体与 DRM 设备（值取自 108 上跑通的 Waydroid 配置）
                setProp("ro.hardware.hwcomposer", "waydroid");
                setProp("ro.hardware.gralloc", "minigbm_gbm_mesa");
                setProp("gralloc.gbm.device", "/dev/dri/renderD128");
                // 无 GL 环境：让 RenderEngine 走 Skia CPU（否则 SF 会去找 EGL 驱动）
                setProp("debug.renderengine.backend", "skiacpu");
                try {
                    new java.io.File("/run/user/0").mkdirs();
                } catch (Throwable ignored) { }
                ProcessBuilder pb = new ProcessBuilder("/system/bin/surfaceflinger");
                // libpropfix 必须排在 /proppreload.so **前面**：它只回答环境变量里声明的那几个
                // "跨进程约定"属性，其余用 dlsym(RTLD_NEXT) 转发给 proppreload（每进程一份的编译期表）。
                // 没有它，SF 的 HIDL 客户端会死等 hwservicemanager.ready（实测）——
                // 因为桥里 set 的属性，别的进程根本看不到。
                pb.environment().put("LD_PRELOAD",
                        // libllvmstub：libgallium_dri.so 里 llvmpipe 用到 llvm:: 符号，bionic 加载时会全部解析；
                        // 我们跑 swrast(softpipe) 不会调用它们，所以只提供地址的桩就够了（真 LLVM 未压缩 105MB）。
                        "/system/lib64/libpropfix.so:/proppreload.so");
                pb.environment().put("PROPFIX",
                        "hwservicemanager.ready=true"
                        + ";ro.hardware.hwcomposer=waydroid"
                        + ";ro.hardware.gralloc=minigbm_gbm_mesa"
                        + ";ro.hardware.egl=angle"
                        + ";ro.hardware.vulkan=lvp"
                        + ";gralloc.gbm.device=/dev/dri/renderD128"
                        + ";debug.renderengine.backend=skiagl"
                        // softpipe 只提供标准 EGL 配置；不关掉这两个，SF 会去找广色域/HDR 配置并报
                        // "no suitable EGLConfig found, giving up"（实测）。
                        + ";ro.surface_flinger.has_wide_color_display=false"
                        + ";ro.surface_flinger.has_HDR_display=false"
                        + ";ro.surface_flinger.use_color_management=false");
                // mesa 的软件光栅化：本 guest 没有 GPU，iris 起不来，强制软件路径。
                // kms_swrast（2026-10-02 二轮）：swrast/llvmpipe 的 gbm 分配是哑元（同指针），
                // 随后在 dri_gbm 里对 SCANOUT|LINEAR 分配 SEGV；kms_swrast 走 DRM dumb 真分配。
                pb.environment().put("GALLIUM_DRIVER", "kms_swrast");
                // mesa/EGL 自己的调试输出（定位"驱动为什么没起来"）
                pb.environment().put("MESA_DEBUG", "1");
                pb.environment().put("LIBGL_DEBUG", "verbose");
                pb.environment().put("EGL_LOG_LEVEL", "debug");
                pb.environment().put("MESA_LOADER_DEBUG", "1");
                pb.environment().put("LIBGL_ALWAYS_SOFTWARE", "1");
                pb.environment().put("MESA_LOADER_DRIVER_OVERRIDE", "llvmpipe");
                // 诊断开关：让 libpropfix 把 SF 的每一次属性读取打到控制台
                // （EGL 找不到实现时，靠它看 libEGL 到底问哪个键、拿到什么值）。
                // 临时无条件开启做一次诊断；查清后改回按环境变量开关。
                pb.environment().put("PROPFIX_DEBUG", "1");
                // 崩溃现场：装 SIGSEGV/SIGABRT 处理器打印信号与回溯。
                // 为什么需要：SF 在 RenderEngine 之后崩，而 guest 里没有 tombstoned ⇒
                // crash_dump64 拿不到 tombstone，致命信号与回溯在日志里完全看不到。
                pb.environment().put("PROPFIX_CRASH", "1");
                pb.environment().put("LD_LIBRARY_PATH", "/vendor/lib64/egl:/vendor/lib64:/system/lib64:/system/lib64/egl");
                pb.environment().put("WAYLAND_DISPLAY", "wayland-0");
                pb.environment().put("XDG_RUNTIME_DIR", "/run/user/0");
                pb.redirectErrorStream(true);
                // 等 HAL 服务注册完成的哨兵（init 在服务块末尾写）——实测 SF 与 HAL 注册存在竞争：
                // 抢跑时会撞上间歇性的 "gralloc-mapper is missing" / libEGL 找不到实现。
                for (int w = 0; w < 120 && !new java.io.File("/tmp/hal_ready").exists(); w++) {
                    Thread.sleep(500);
                }
                // 再重试最多 3 次：即使某个组件仍慢一拍，重试也能吃掉（SF 崩在启动早期，重启成本低）
                for (int attempt = 1; attempt <= 3; attempt++) {
                    if (sfAlreadyRunning()) {
                        System.err.println("[sf] SurfaceFlinger 已有实例 → 停止重试（composer HAL 单客户端）");
                        return;
                    }
                    Process p = pb.start();
                    System.err.println("[sf] 已拉起 surfaceflinger（第 " + attempt + " 次）pid=" + p.hashCode());
                    // ⚠ 必须**无条件** pump：早先只在"存活"时 pump ⇒ SF 若 6s 内退出，
                    //   它的全部输出（含 shim 打的诊断：getRawServiceInternal/dlopen/EGL）都被丢掉 ✗
                    //   —— 这正是此前"拦截器 0 条输出"的假象来源。
                    //   pump 读到流关闭（进程退出）才返回；SF 活着就一直读（守护线程，正合需要）。
                    pump(p.getInputStream(), "[sf] ");
                    if (p.isAlive()) break;
                    System.err.println("[sf] 第 " + attempt + " 次启动后已退出（进程不在），准备重试");
                }
            } catch (Throwable e) {
                System.err.println("[sf] 启动失败: " + e);
            }
        }, "sf-launch");
        t.setDaemon(true);
        t.start();
    }

    /**
     * guest 里是否已有存活的 surfaceflinger（扫 /proc 各 pid 的 cmdline，零 fork ——
     * 桥的 fork 会被 proppreload 拦截，不能派 pidof 子进程）。
     */
    /** 见 startAdbd 的单实例守卫：扫 /proc 下每个 pid 的 cmdline，出现 adbd 即认为已有实例。 */
    private static boolean adbdAlreadyRunning() {
        String[] list;
        try { list = new java.io.File("/proc").list(); } catch (Throwable e) { return false; }
        if (list == null) return false;
        for (String name : list) {
            int pid;
            try { pid = Integer.parseInt(name); } catch (NumberFormatException e) { continue; }
            if (pid <= 1) continue;
            try (java.io.InputStream in = new java.io.FileInputStream("/proc/" + name + "/cmdline")) {
                byte[] buf = new byte[128];
                int n = in.read(buf);
                if (n <= 0) continue;
                String cmd = new String(buf, 0, n, java.nio.charset.StandardCharsets.UTF_8);
                // cmdline 以 NUL 结尾（"/system/bin/adbd\0"），equals 永不命中 ⇒ 用 startsWith
                if (cmd.startsWith("/system/bin/adbd")) return true;
            } catch (Throwable ignored) { }
        }
        return false;
    }

    private static boolean sfAlreadyRunning() {
        String[] list;
        try { list = new java.io.File("/proc").list(); } catch (Throwable e) { return false; }
        if (list == null) return false;
        for (String name : list) {
            int pid;
            try { pid = Integer.parseInt(name); } catch (NumberFormatException e) { continue; }
            if (pid <= 1) continue;
            try (java.io.InputStream in = new java.io.FileInputStream("/proc/" + name + "/cmdline")) {
                byte[] buf = new byte[128];
                int n = in.read(buf);
                if (n <= 0) continue;
                String cmd = new String(buf, 0, n, java.nio.charset.StandardCharsets.UTF_8);
                if (cmd.contains("surfaceflinger")) return true;
            } catch (Throwable ignored) { }
        }
        return false;
    }

    /** 把子进程输出转发到控制台（排障用；每个流一个守护线程）。 */
    private static void pump(java.io.InputStream in, String prefix) {
        Thread t = new Thread(() -> {
            try (java.io.BufferedReader r = new java.io.BufferedReader(
                    new java.io.InputStreamReader(in, java.nio.charset.StandardCharsets.UTF_8))) {
                String line;
                while ((line = r.readLine()) != null) {
                    // adbd 的 wifi 传输分支每 ~200ms 刷一行 "Waiting for persist.adb.tls_server.enable=1"
                    // （我们用的是 5555 上的 TCP 传输，这行纯噪音，会把控制台日志淹掉）→ 过滤。
                    if (line.contains("adb_wifi.cpp")) continue;
                    System.err.println(prefix + line);
                }
            } catch (Throwable ignored) { }
        }, "pump");
        t.setDaemon(true);
        t.start();
    }

    private static void startDataWatcher() {
        if (DATA_WATCH_STARTED) return;
        DATA_WATCH_STARTED = true;
        Thread t = new Thread(() -> {
            // 启动即打印 /data 真实目录树（深度 2）：一次就能判明壳用的是我们的桩路径
            // （/data/catclaw）还是真框架路径（/data/user/0/<pkg>、/data/data/<pkg>）。
            try {
                StringBuilder tree = new StringBuilder("[datatree] /data 深度2:" + (char) 10);
                walkData(new java.io.File("/data"), 2, tree, "  ");
                System.err.println(tree);
            } catch (Throwable ignored) { }
            String last = null;
            while (true) {
                try { Thread.sleep(20000); } catch (InterruptedException e) { return; }
                try {
                    // 三个根一起看：<data.dir>（桥的 Context 文件操作）、/data/fishso（插件解出来的
                    // 原生库，v7=ARM32 / v8=ARM64 就看这里）、/data/cache（壳的 sharedb 缓存）。
                    // 2026-10-01：壳可能绕过我们的 Context 桩、用真框架的 ContextImpl / 真 SharedPreferences
                    // 把登录态写到 /data/data/... 去（那条路既不在同步范围、也不在原先的观察范围）。
                    String[] roots = { System.getProperty("data.dir", "/data/catclaw"), "/data/fishso", "/data/cache",
                            // 真框架的 ContextImpl 走 /data/user/0/<pkg> 与 /data/data/<pkg> —— 原先没观察，
                            // 而「灌了真机 cookie 仍显示未登录」强烈提示壳的网盘 prefs 落在那边。
                            "/data/data", "/data/user", "/data/user_de", "/data/misc", "/data/system",
                            "/data/local", "/data/local/tmp" };
                    StringBuilder sb = new StringBuilder();
                    for (String r : roots) {
                        java.io.File root = new java.io.File(r);
                        if (!root.isDirectory()) continue;
                        sb.append('[').append(r).append("]\n");
                        walkData(root, 3, sb, "  ");
                    }
                    String sig = sb.toString();
                    if (!sig.equals(last)) {
                        last = sig;
                        System.err.println("[datawatch] 数据面清单变化:\n" + sig);
                    }
                } catch (Throwable ignored) { }
            }
        }, "datawatch");
        t.setDaemon(true);
        t.start();
    }

    /** 列 &lt;data.dir&gt;；跳过 art/inbox 与 art/lib（大且可再取），其余全列（目录/文件 + 大小）。 */
    static void walkData(java.io.File f, int depth, StringBuilder sb, String indent) {
        if (depth <= 0 || sb.length() > 8000) return;
        java.io.File[] kids = f.listFiles();
        if (kids == null) return;
        java.util.Arrays.sort(kids, (a, b) -> a.getName().compareTo(b.getName()));
        for (java.io.File k : kids) {
            if (sb.length() > 8000) return;
            String n = k.getName();
            boolean skip = k.isDirectory() && ("inbox".equals(n) || "lib".equals(n));
            sb.append(indent).append(k.isDirectory() ? "d " : "f ").append(n);
            if (k.isDirectory()) sb.append(skip ? "/  (跳过)" : "/");
            else sb.append("  ").append(k.length()).append("B  mtime=").append(k.lastModified());
            sb.append('\n');
            if (k.isDirectory() && !skip) walkData(k, depth - 1, sb, indent + "  ");
        }
    }

    /** 给 guest 内的 /proxy 服务取已装载的爬虫实例（站点键 → 实例）。 */
    static Object spiderOf(String site) { return SPIDERS.get(site); }

    /**
     * 诊断用的递归列目录（见 {@code op=ls}）：带 {@code d/f}、大小、mtime；限层数与总输出长度，
     * 免得把 initramfs 整个刷回来。纯 java.io —— guest 里不依赖 fork/exec。
     */
    static void walk(java.io.File f, int depth, StringBuilder sb, String indent) {
        if (depth <= 0 || sb.length() > 24000) return;
        java.io.File[] kids = f.listFiles();
        if (kids == null) { sb.append(indent).append(f.getName()).append("  [不可读或不存在]\n"); return; }
        java.util.Arrays.sort(kids, java.util.Comparator.comparing(java.io.File::getName));
        for (java.io.File k : kids) {
            if (sb.length() > 24000) return;
            sb.append(indent).append(k.isDirectory() ? "d " : "f ").append(k.getName());
            if (k.isDirectory()) sb.append('/');
            else sb.append("  ").append(k.length()).append("B  ").append(k.lastModified());
            sb.append('\n');
            if (k.isDirectory()) walk(k, depth - 1, sb, indent + "  ");
        }
    }

    /**
     * guest 内 Guard 解密：经任意已装载 spider 的类加载器调其解密包装
     * （merge.Rc.KJ(String)——内部 cn.yq=true 时走壳内转译 SO 的 HideUtils.decrypt）。
     * FishConfig 的 quark_scan/解密链路探测 10.0.2.2:18481（Guard 服务约定口），
     * 宿主 18481 监听把请求转到本方法——x86 guest 无独立 Guard VM 的替代供给。
     */
    static String guardEncryptViaSpider(String data) {
        // SO 的 encrypt 入口：Rc 体系同族（encrypt 走 HideUtils.encrypt/转译 SO）
        for (Object spider : SPIDERS.values()) {
            try {
                ClassLoader cl = spider.getClass().getClassLoader();
                for (String cn : new String[]{"com.github.catvod.spider.merge.Rc"}) {
                    try {
                        Class<?> rc = cl.loadClass(cn);
                        for (Method m : rc.getDeclaredMethods()) {
                            if (java.lang.reflect.Modifier.isStatic(m.getModifiers()) && m.getParameterCount() == 1
                                    && m.getParameterTypes()[0] == String.class
                                    && m.getReturnType() == String.class
                                    && (m.getName().equals("encrypt") || m.getName().equals("JM") || m.getName().equals("KJ"))) {
                                m.setAccessible(true);
                                Object r = m.invoke(null, data);
                                if (r instanceof String str && !str.isEmpty()) return str;
                            }
                        }
                    } catch (ClassNotFoundException ignored) { }
                }
            } catch (Throwable ignored) { }
        }
        return null;
    }

    static String guardDecryptViaSpider(String data) {
        for (Object spider : SPIDERS.values()) {
            try {
                ClassLoader cl = spider.getClass().getClassLoader();
                Class<?> rc = cl.loadClass("com.github.catvod.spider.merge.Rc");
                Method m = rc.getDeclaredMethod("KJ", String.class);
                m.setAccessible(true);
                Object r = m.invoke(null, data);
                if (r instanceof String str && !str.isEmpty()) return str;
                System.err.println("[guard-decrypt] " + spider.getClass().getSimpleName() + ".KJ 返回空（data=" + data.length() + "B）");
            } catch (Throwable t) {
                System.err.println("[guard-decrypt] " + spider.getClass().getSimpleName() + ".KJ 异常: " + t);
            }
        }
        System.err.println("[guard-decrypt] 解密失败：SPIDERS=" + SPIDERS.size() + "，无可用的 Rc.KJ");
        return null;
    }

    /**
     * jar 内 {@code com.github.catvod.spider.Proxy.proxy(Map)}：类不存在或返回不是 Object[] 时回 null，
     * 让调用方继续往下找（与 {@link #proxyDispatch} 的"没人应答就下一步"一致）。
     */
    static Object[] jarProxy(Object instance, java.util.Map<String, String> param) {
        try {
            ClassLoader loader = instance.getClass().getClassLoader();
            Class<?> clz = loader.loadClass("com.github.catvod.spider.Proxy");
            java.lang.reflect.Method m = clz.getMethod("proxy", java.util.Map.class);
            return m.invoke(null, param) instanceof Object[] arr ? arr : null;
        } catch (ClassNotFoundException e) {
            return null;
        } catch (Throwable t) {
            Throwable root = t;
            while (root.getCause() != null) root = root.getCause();
            System.err.println("[srv] 静态 Proxy.proxy 异常: " + root.getClass().getSimpleName()
                    + ": " + root.getMessage());
            return null;
        }
    }

    /**
     * 壳 → 真爬虫：{@code BaseSpiderGuard} 的实现对象在它自己的 crawler 字段里（{@code Init.getSpider(name)}
     * 造出来的），实例方法 {@code proxy(Map)} 与静态 {@code proxyInput()} 都长在那一层上。
     * 找不到内层就原样返回（普通 jar 的爬虫本来就是真身）。
     */
    static Object guardTarget(Object wrapper) {
        for (Class<?> c = wrapper.getClass(); c != null && c != Object.class; c = c.getSuperclass()) {
            for (java.lang.reflect.Field f : c.getDeclaredFields()) {
                if (java.lang.reflect.Modifier.isStatic(f.getModifiers())) continue;
                if (!f.getType().getName().startsWith("com.github.catvod")) continue;
                try {
                    f.setAccessible(true);
                    Object v = f.get(wrapper);
                    if (v != null && v != wrapper) return v;
                } catch (Throwable ignored) { }
            }
        }
        return wrapper;
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
