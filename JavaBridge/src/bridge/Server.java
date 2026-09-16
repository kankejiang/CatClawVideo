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

    private static final HashMap<String, Object> SPIDERS = new HashMap<>();
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
        URLClassLoader created = new URLClassLoader(urls, Server.class.getClassLoader());
        LOADERS.put(key, created);
        return created;
    }

    public static void main(String[] args) throws Exception {
        // 强制 stdout/stderr 为 UTF-8（JVM 默认跟随 Windows 控制台代码页 GBK，中文会坏）
        System.setOut(new java.io.PrintStream(new java.io.FileOutputStream(java.io.FileDescriptor.out), true, "UTF-8"));
        System.setErr(new java.io.PrintStream(new java.io.FileOutputStream(java.io.FileDescriptor.err), true, "UTF-8"));
        // data 目录：spider 的 Context 文件操作都落在 App 约定的桥工作目录
        System.setProperty("data.dir", new File("data").getAbsolutePath());
        BufferedReader in = new BufferedReader(new InputStreamReader(System.in, StandardCharsets.UTF_8));
        String line;
        while ((line = in.readLine()) != null) {
            if (line.isBlank()) continue;
            String out;
            try {
                JSONObject req = new JSONObject(line);
                String op = req.optString("op");
                if ("exit".equals(op)) break;
                long id = req.optLong("id", -1);
                try {
                    Object result = switch (op) {
                        case "load" -> load(req.optString("site"), req.optString("className"), req.optString("ext"), req.optJSONArray("jars"));
                        case "call" -> call(req.optString("site"), req.optString("method"), req.optJSONArray("args"));
                        case "ping" -> "pong";
                        default -> throw new IllegalArgumentException("unknown op: " + op);
                    };
                    out = new JSONObject().put("id", id).put("ok", true)
                            .put("result", result == null ? JSONObject.NULL : result).toString();
                } catch (Throwable t) {
                    Throwable c = t;
                    while (c.getCause() != null) c = c.getCause();
                    out = new JSONObject().put("id", id).put("ok", false)
                            .put("error", c.getClass().getSimpleName() + ": " + c.getMessage()).toString();
                }
            } catch (Exception e) {
                out = new JSONObject().put("id", -1).put("ok", false).put("error", "bad request: " + e).toString();
            }
            System.out.println(out);
            System.out.flush();
        }
    }

    private static String load(String site, String className, String ext, org.json.JSONArray jars) throws Exception {
        synchronized (LOCK) {
            if (SPIDERS.containsKey(site)) return "loaded";
            URLClassLoader loader = loaderFor(jars);

            Class<?> cls = null;
            for (String p : new String[]{"com.github.catvod.spider.", "com.github.catvod.crawler.", ""}) {
                try { cls = loader.loadClass(p + className); break; } catch (ClassNotFoundException ignored) { }
            }
            if (cls == null) throw new ClassNotFoundException(className);

            Object instance = cls.getDeclaredConstructor().newInstance();

            // Guard 框架入口：先初始化 com.github.catvod.spider.Init 单例
            try {
                Class<?> initCls = loader.loadClass("com.github.catvod.spider.Init");
                initCls.getMethod("init", Context.class).invoke(null, new android.app.Application());
            } catch (Exception ignored) { }

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

            // ⚠ TVBox 公共类的**静态 Context**（新版 InitOrigin 是新土豆/影视仓系爬虫的公共入口）：
            //   spider 常用 InitOrigin.context().getSharedPreferences(...) 做缓存/配置，宿主不注入就 NPE。
            //   实测 2026-09-16：新6V 的 playerContent 抛
            //     Cannot invoke "android.content.Context.getSharedPreferences(String,int)"
            //     because the return value of "com.github.catvod.spider.InitOrigin.context()" is null
            //   → 播放地址解析失败 → 播放页只能跨站回退（用户感知「加载很慢」）。
            for (String holder : new String[]{
                    "com.github.catvod.spider.InitOrigin",
                    "com.github.catvod.spider.Init",
                    "com.github.catvod.spider.merge.InitOrigin"}) {
                injectStaticContext(loader, holder);
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
    private static void injectStaticContext(ClassLoader loader, String className) {
        try {
            Class<?> c = loader.loadClass(className);
            // ⚠ 必须注入 **Application**（不是裸 Context）：TVBox 的 InitOrigin 里
            //   `public static Application context()` 返回的就是 Application 实例，
            //   init(Context) 内部会强转成 Application —— 传裸 Context 会被 ClassCastException
            //   吞掉，context() 依旧返回 null（实测 2026-09-16：传 Context 无效，改 Application 后 NPE 消失）。
            Context ctx = new android.app.Application();
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
}
