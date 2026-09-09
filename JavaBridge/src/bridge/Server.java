package bridge;

import android.content.Context;
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
    private static URLClassLoader loader;
    private static final Object LOCK = new Object();

    /** 懒构建：把 C# 侧传入的转换后 jar 挂到 URLClassLoader */
    private static void ensureLoader(org.json.JSONArray jars) throws Exception {
        if (loader != null) return;
        if (jars == null || jars.length() == 0) throw new IllegalStateException("no jars provided");
        URL[] urls = new URL[jars.length()];
        for (int i = 0; i < jars.length(); i++) {
            File f = new File(jars.getString(i));
            if (!f.isFile()) throw new java.io.FileNotFoundException(f.getAbsolutePath());
            urls[i] = f.toURI().toURL();
        }
        loader = new URLClassLoader(urls, Server.class.getClassLoader());
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
            ensureLoader(jars);

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

            try {
                cls.getMethod("init", Context.class, String.class).invoke(instance, new Context(), ext == null ? "" : ext);
            } catch (NoSuchMethodException ignored) { }

            SPIDERS.put(site, instance);
            return "loaded";
        }
    }

    private static String call(String site, String method, org.json.JSONArray args) throws Exception {
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
            return result == null ? "{}" : result.toString();
        }
    }
}
