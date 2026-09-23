package com.github.catvod.crawler;

import android.util.Base64;

import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import com.google.gson.JsonPrimitive;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;

/**
 * Host-provided helper injected into every spider through {@link Spider#initApi(SpiderApi)}.
 *
 * <p>Spider jars are compiled against this exact class and reference it in field descriptors,
 * so a missing {@code SpiderApi} makes the whole jar unresolvable: class verification of the
 * spider fails with {@code ClassNotFoundException: com.github.catvod.crawler.SpiderApi} before
 * a single method runs. {@code XBPQ} (the "饭太硬 / 海阔" family jar, used by hundreds of
 * subscriptions) is the most common consumer - it stores the instance in a field and calls
 * {@code getPort()} from its own {@code initApi} override.</p>
 *
 * <p>Ported from the TVBox reference implementation
 * ({@code app/src/main/java/com/github/catvod/crawler/SpiderApi.java}) with the TVBox-internal
 * dependencies removed, because they have no equivalent in this host:</p>
 * <ul>
 *   <li>{@code ControlManager} (the local proxy server) - {@code getAddress()}/{@code getPort()}
 *       return an empty string, so spiders fall back to their direct-URL code paths.
 *       Consequence: {@code proxy://} style results are not served; see {@code webParse}.</li>
 *   <li>{@code App} / {@code Activity} - {@code getScreenOrientation()} returns the
 *       sensor-landscape constant directly instead of querying the current activity.</li>
 *   <li>{@code com.github.catvod.net.OkHttp} - {@code multiReq} is reimplemented on plain
 *       {@link HttpURLConnection} so no OkHttp classes are required at compile time.</li>
 * </ul>
 */
public class SpiderApi {

    private static final int CONNECT_TIMEOUT_MS = 15000;
    private static final int READ_TIMEOUT_MS = 20000;

    /** {@code ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE} - inlined to stay dependency-free. */
    private static final int SCREEN_ORIENTATION_SENSOR_LANDSCAPE = 6;

    /**
     * 宿主本地 proxy（SpiderProxyServer）实际监听的端口，由宿主启动后通过 JNI 写入
     * （TvBoxCompatBridge.SetProxyPort）。Guard 系网盘源（csp_MyDriveGuard 等）用
     * {@code getAddress}/{@code getPort} 拼「云盘配置」页 URL，返回空会导致
     * 配置入口失效（URL 拼不出 / 探测走错端口）。0 = proxy 未就绪。
     */
    public static volatile int hostProxyPort = 0;

    /**
     * 当前前台 Activity，由宿主生命周期上报（TvBoxCompatBridge.ReportActivity）。
     * Guard 系 jar 需要一个能弹对话框的 Activity——拿到它才能渲染
     * 「已登录+启用中」云盘配置界面（TVBox 里由 App.getCurrentActivity() 提供）。
     */
    public static volatile android.app.Activity currentActivity = null;

    /** JNI 桥：宿主写 proxy 端口（JNIEnv 无直接静态字段写入的友好重载，走 setter 稳妥）。 */
    public static void setHostProxyPort(int port) {
        hostProxyPort = port;
    }

    /** JNI 桥：宿主写当前前台 Activity。 */
    public static void setCurrentActivity(android.app.Activity activity) {
        currentActivity = activity;
    }

    /** Host proxy base URL（127.0.0.1 回环；proxy 未就绪返回空串）。 */
    public String getAddress(boolean local) {
        int port = hostProxyPort;
        return port > 0 ? "http://127.0.0.1:" + port : "";
    }

    /** Host proxy 端口号字符串；proxy 未就绪返回空串。 */
    public String getPort() {
        int port = hostProxyPort;
        return port > 0 ? String.valueOf(port) : "";
    }

    public void log(String msg) {
        try {
            SpiderDebug.log(msg);
        } catch (Throwable ignored) {
        }
    }

    public int getScreenOrientation() {
        android.app.Activity activity = currentActivity;
        if (activity != null) {
            try {
                int orientation = activity.getResources().getConfiguration().orientation;
                if (orientation == android.content.res.Configuration.ORIENTATION_PORTRAIT) {
                    return android.content.pm.ActivityInfo.SCREEN_ORIENTATION_PORTRAIT;
                }
                return android.content.pm.ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE;
            } catch (Throwable ignored) {
            }
        }
        return SCREEN_ORIENTATION_SENSOR_LANDSCAPE;
    }

    /**
     * Batch HTTP utility used by newer spiders to fetch many detail pages at once.
     * Mirrors TVBox's contract: a JSON array of {@code {url, method, headers, data, postType}}
     * requests in, a JSON array of responses out (JSON-parsed when the body is JSON,
     * otherwise a plain string).
     */
    public String multiReq(JsonArray array) {
        try {
            if (array == null || array.size() == 0) return "";
            ExecutorService executor = Executors.newFixedThreadPool(Math.min(array.size(), 6));
            List<Future<String>> futures = new ArrayList<>();
            for (JsonElement element : array) {
                if (!element.isJsonObject()) continue;
                final JsonObject obj = element.getAsJsonObject();
                futures.add(executor.submit(() -> request(obj)));
            }
            JsonArray result = new JsonArray();
            for (Future<String> future : futures) result.add(toResult(future.get()));
            executor.shutdown();
            return result.toString();
        } catch (Throwable th) {
            return "";
        }
    }

    /**
     * Builds the {@code proxy://go=SuperParse&...} URL TVBox hands to its own WebView parser.
     * This host does not run that proxy, so the string is returned for compatibility only.
     */
    public String webParse(String url, String flag) {
        try {
            if (url == null || url.isEmpty()) return "";
            String encoded = Base64.encodeToString(url.getBytes("UTF-8"), Base64.DEFAULT | Base64.URL_SAFE | Base64.NO_WRAP);
            return "proxy://go=SuperParse&flag=" + (flag == null ? "" : flag) + "&url=" + encoded;
        } catch (Throwable th) {
            return "";
        }
    }

    private static String request(JsonObject obj) {
        HttpURLConnection conn = null;
        try {
            String url = string(obj, "url");
            if (url.isEmpty()) return "";
            boolean post = "POST".equalsIgnoreCase(string(obj, "method"));

            conn = (HttpURLConnection) new URL(url).openConnection();
            conn.setConnectTimeout(CONNECT_TIMEOUT_MS);
            conn.setReadTimeout(READ_TIMEOUT_MS);
            conn.setInstanceFollowRedirects(true);
            conn.setRequestMethod(post ? "POST" : "GET");
            conn.setRequestProperty("User-Agent", "okhttp/3.12.11");
            conn.setRequestProperty("Accept-Encoding", "identity");

            JsonElement headers = obj.get("headers");
            if (headers != null && headers.isJsonObject()) {
                for (Map.Entry<String, JsonElement> entry : headers.getAsJsonObject().entrySet()) {
                    conn.setRequestProperty(entry.getKey(), entry.getValue().getAsString());
                }
            }

            if (post) {
                byte[] payload = body(obj);
                conn.setDoOutput(true);
                conn.setRequestProperty("Content-Type", "form".equalsIgnoreCase(string(obj, "postType"))
                        ? "application/x-www-form-urlencoded"
                        : "application/json; charset=utf-8");
                conn.setFixedLengthStreamingMode(payload.length);
                try (OutputStream os = conn.getOutputStream()) {
                    os.write(payload);
                }
            }

            int code = conn.getResponseCode();
            InputStream is = code >= 400 ? conn.getErrorStream() : conn.getInputStream();
            return is == null ? "" : readAll(is);
        } catch (Throwable th) {
            return "";
        } finally {
            if (conn != null) conn.disconnect();
        }
    }

    private static byte[] body(JsonObject obj) throws Exception {
        JsonElement data = obj.get("data");
        if (data == null || data.isJsonNull()) return new byte[0];
        if ("form".equalsIgnoreCase(string(obj, "postType")) && data.isJsonObject()) {
            StringBuilder sb = new StringBuilder();
            for (Map.Entry<String, JsonElement> entry : data.getAsJsonObject().entrySet()) {
                if (sb.length() > 0) sb.append('&');
                sb.append(URLEncoder.encode(entry.getKey(), "UTF-8"))
                        .append('=')
                        .append(URLEncoder.encode(entry.getValue().getAsString(), "UTF-8"));
            }
            return sb.toString().getBytes(StandardCharsets.UTF_8);
        }
        String raw = data.isJsonPrimitive() ? data.getAsString() : data.toString();
        return raw.getBytes(StandardCharsets.UTF_8);
    }

    private static JsonElement toResult(String text) {
        if (text == null) return new JsonPrimitive("");
        try {
            String trim = text.trim();
            if (trim.startsWith("{") || trim.startsWith("[")) return JsonParser.parseString(trim);
        } catch (Throwable ignored) {
        }
        return new JsonPrimitive(text);
    }

    private static String readAll(InputStream is) throws Exception {
        try (InputStream in = is; ByteArrayOutputStream bos = new ByteArrayOutputStream()) {
            byte[] buf = new byte[8192];
            int n;
            while ((n = in.read(buf)) > 0) bos.write(buf, 0, n);
            return new String(bos.toByteArray(), StandardCharsets.UTF_8);
        }
    }

    private static String string(JsonObject obj, String key) {
        JsonElement element = obj.get(key);
        return element == null || element.isJsonNull() ? "" : element.getAsString();
    }
}
