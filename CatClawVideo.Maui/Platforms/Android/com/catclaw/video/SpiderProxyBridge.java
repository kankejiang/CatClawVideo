package com.catclaw.video;

import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.lang.reflect.Method;
import java.util.HashMap;
import java.util.Map;

/**
 * 宿主本地 HTTP 服务器 → 爬虫 {@code proxy(Map)} 的回调桥。
 *
 * <p>TVBox 的对应实现是 {@code ApiConfig.proxyLocal} → {@code spider.proxy(param)}：
 * 本地服务器收到 {@code /proxy?do=…} 后，响应体必须由**爬虫自己**生成
 * （荐片的 {@code do=ck} 握手就是这条）。</p>
 *
 * <p><b>为什么走临时文件而不是直接返回 byte[]</b>：C# 侧只能用反射调用（没有绑定类），
 * 而 {@code Method.Invoke} 的静态返回类型是 {@code Java.Lang.Object}，
 * 既拿不到 Java 数组（CS0039/CS8121），也接不住基本类型数组。返回一个字符串头 + 落盘文件
 * 可以完全绕开 JNI 封送问题，且对任意体积的响应体都成立。</p>
 *
 * <p>返回格式：{@code "<status>|<mime>|<bodyBytes>"}；失败返回 {@code "ERR:<原因>"}。</p>
 */
public final class SpiderProxyBridge {

    private SpiderProxyBridge() {
    }

    /**
     * 调用 spider 实例的 {@code proxy(Map)} 并把响应体写入 outPath。
     *
     * @param spider  爬虫实例（Spider 子类，可为 null）
     * @param loader  该 jar 的 ClassLoader（用于回退到 {@code com.github.catvod.spider.Proxy}）
     * @param param   查询参数（do / siteKey / url / …）
     * @param outPath 响应体落盘路径（无响应体则不创建）
     */
    public static String proxyToFile(Object spider, ClassLoader loader,
                                     HashMap<String, String> param, String outPath) {
        // ① 先试实例方法（TVBox ApiConfig.proxyLocal 的第一步）
        if (spider != null) {
            try {
                Method proxy = findProxy(spider.getClass());
                if (proxy != null) {
                    Object result = proxy.invoke(spider, param);
                    if (result instanceof Object[]) {
                        return writeResult((Object[]) result, outPath, "spider.proxy");
                    }
                    // 实例 proxy 返回 null → 落到静态回退（TVBox 同款顺序）
                }
            } catch (Throwable ignored) {
                // 继续走回退
            }
        }

        // ② 回退到 jar 里的静态入口（TVBox JarLoader.invokeProxy：
        //    loadClass("com.github.catvod.spider.Proxy").getMethod("proxy", Map.class) 静态调用）
        //    荐片的 /proxy?do=ck 握手就是由它处理的。
        if (loader != null) {
            try {
                Class<?> clz = loader.loadClass("com.github.catvod.spider.Proxy");
                Method m = clz.getMethod("proxy", Map.class);
                Object result = m.invoke(null, param);
                if (result instanceof Object[]) {
                    return writeResult((Object[]) result, outPath, "Proxy.proxy(static)");
                }
                return "ERR:com.github.catvod.spider.Proxy.proxy() 返回 "
                        + (result == null ? "null" : result.getClass().getName());
            } catch (ClassNotFoundException e) {
                return "ERR:该 jar 未提供 com.github.catvod.spider.Proxy";
            } catch (Throwable t) {
                return "ERR:" + rootCause(t).getClass().getSimpleName() + ": " + rootCause(t).getMessage();
            }
        }

        return "ERR:没有可用的 proxy 实现（实例 proxy 返回空且无 ClassLoader）";
    }

    /** 把 Object[]{int status, String mime, InputStream body} 落盘并返回 "<status>|<mime>|<bytes>" */
    private static String writeResult(Object[] rs, String outPath, String via) throws Exception {
        if (rs.length < 2) {
            return "ERR:" + via + " 返回的 Object[] 长度 " + rs.length + " < 2";
        }
        int status = rs[0] instanceof Number ? ((Number) rs[0]).intValue() : 200;
        String mime = rs[1] == null ? "application/octet-stream" : rs[1].toString();

        InputStream in = rs.length > 2 && rs[2] instanceof InputStream ? (InputStream) rs[2] : null;
        long written = 0;
        if (in != null) {
            File f = new File(outPath);
            File parent = f.getParentFile();
            if (parent != null && !parent.exists()) {
                //noinspection ResultOfMethodCallIgnored
                parent.mkdirs();
            }
            try (FileOutputStream out = new FileOutputStream(f)) {
                byte[] buf = new byte[64 * 1024];
                int n;
                while ((n = in.read(buf)) > 0) {
                    out.write(buf, 0, n);
                    written += n;
                }
            } finally {
                try {
                    in.close();
                } catch (Exception ignored) {
                }
            }
        }
        // via 只用于排障，不参与格式
        return status + "|" + mime + "|" + written;
    }

    private static Throwable rootCause(Throwable t) {
        Throwable root = t;
        while (root.getCause() != null) {
            root = root.getCause();
        }
        return root;
    }

    /** 按名 + 形参个数查找 proxy（爬虫可能覆写成 HashMap 形参，不能硬套 Map.class） */
    private static Method findProxy(Class<?> cls) {
        for (Class<?> c = cls; c != null && c != Object.class; c = c.getSuperclass()) {
            for (Method m : c.getDeclaredMethods()) {
                if (m.getName().equals("proxy") && m.getParameterTypes().length == 1
                        && Map.class.isAssignableFrom(m.getParameterTypes()[0])) {
                    m.setAccessible(true);
                    return m;
                }
            }
        }
        return null;
    }
}
