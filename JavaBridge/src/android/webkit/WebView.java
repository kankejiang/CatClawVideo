package android.webkit;

import android.content.Context;
import android.util.AttributeSet;
import android.view.ViewGroup;
import android.widget.AbsoluteLayout;

import java.util.LinkedHashMap;
import java.util.Map;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * WebView 桩（2026-10-01 <b>取证版</b>）。
 *
 * <p>背景：真机上「一次成功登录」那一分钟，{@code shared_prefs/WebViewChromiumPrefs.xml}
 * 同时被写 —— 说明壳是**用 WebView 完成登录/取账号信息**的。而我们这里是空转桩
 * （{@code loadUrl} 什么都不做），那条路必然卡死，也解释了为什么把真机的 cookie
 * 灌进 guest 仍然停在「正在获取账号信息…」：cookie 只是钥匙，**开锁的 JS 在 WebView 里跑**。</p>
 *
 * <p>本轮只做留痕：把壳对 WebView 的完整契约打到 guest 控制台（URL / JS 脚本 /
 * 注入的 JS 接口名 / 客户端的真实类 / 关键调用的调用栈），据此再实现真正的
 * 「WebView → 宿主 WebView2」桥。行为保持与旧桩一致（全部空实现、不回调），
 * 以便区分「壳等回调」与「壳根本没用 WebView」。</p>
 */
public class WebView extends AbsoluteLayout {

    private static final AtomicInteger SEQ = new AtomicInteger();
    private static final AtomicInteger TRACES = new AtomicInteger();
    private static final int MAX_TRACES = 10;
    private static final int MAX_LEN = 400;

    private final int id = SEQ.incrementAndGet();
    private String lastUrl;
    private String lastTitle;
    private WebViewClient client;
    private WebChromeClient chrome;
    private final Map<String, Object> ifaces = new LinkedHashMap<>();

    public WebView() { super(); trace("new WebView()"); }
    public WebView(Context c) { super(c); trace("new WebView(" + cls(c) + ")"); }
    public WebView(Context c, AttributeSet attrs) { super(c, attrs); trace("new WebView(ctx, attrs)"); }

    private static String cls(Object o) { return o == null ? "null" : o.getClass().getName(); }

    private static String cut(String s) {
        if (s == null) return "null";
        String one = s.replace("\n", "\\n").replace("\r", "");
        return one.length() <= MAX_LEN ? one : one.substring(0, MAX_LEN) + "…(共 " + one.length() + " 字)";
    }

    private void log(String what) {
        System.err.println("[webview#" + id + "] " + what);
    }

    /** 关键调用：除日志外还打一次调用栈（定位壳里是哪一段代码在驱动 WebView）。 */
    private void trace(String what) {
        log(what);
        if (TRACES.incrementAndGet() <= MAX_TRACES) {
            StackTraceElement[] st = new Throwable().getStackTrace();
            StringBuilder sb = new StringBuilder();
            for (int i = 1; i < st.length && i <= 12; i++) {
                sb.append("\n      at ").append(st[i]);
            }
            System.err.println("[webview#" + id + "] 调用栈:" + sb);
        }
    }

    public void setWebViewClient(WebViewClient c) { client = c; trace("setWebViewClient(" + cls(c) + ")"); }
    public void setWebChromeClient(WebChromeClient c) { chrome = c; log("setWebChromeClient(" + cls(c) + ")"); }
    public WebViewClient getWebViewClient() { log("getWebViewClient() → " + cls(client)); return client; }

    public WebSettings getSettings() {
        log("getSettings()");
        return new WebSettings(id);
    }

    public void loadUrl(String url) {
        lastUrl = url;
        trace("loadUrl(" + cut(url) + ")");
    }

    public void loadUrl(String url, Map<String, String> headers) {
        lastUrl = url;
        trace("loadUrl(" + cut(url) + ", headers=" + (headers == null ? "null" : headers.keySet()) + ")");
    }

    public void loadData(String data, String mimeType, String encoding) {
        trace("loadData(mime=" + mimeType + ", enc=" + encoding + ", " + cut(data) + ")");
    }

    public void loadDataWithBaseURL(String baseUrl, String data, String mimeType, String encoding, String historyUrl) {
        lastUrl = baseUrl;
        trace("loadDataWithBaseURL(base=" + cut(baseUrl) + ", mime=" + mimeType + ", enc=" + encoding
                + ", " + cut(data) + ")");
    }

    public void postUrl(String url, byte[] postData) {
        lastUrl = url;
        trace("postUrl(" + cut(url) + ", " + (postData == null ? "null" : postData.length + "B") + ")");
    }

    public void reload() { log("reload()"); }
    public void stopLoading() { log("stopLoading()"); }
    public void destroy() { log("destroy()"); }
    public void clearCache(boolean includeDiskFiles) { log("clearCache(" + includeDiskFiles + ")"); }
    public void clearHistory() { log("clearHistory()"); }

    public void addJavascriptInterface(Object object, String name) {
        ifaces.put(name, object);
        trace("addJavascriptInterface(name=" + name + ", obj=" + cls(object) + ")");
    }

    public void removeJavascriptInterface(String name) {
        ifaces.remove(name);
        log("removeJavascriptInterface(" + name + ")");
    }

    public void evaluateJavascript(String script, ValueCallback<String> resultCallback) {
        trace("evaluateJavascript(cb=" + cls(resultCallback) + ", js=" + cut(script) + ")");
    }

    public void setInitialScale(int scaleInPercent) { log("setInitialScale(" + scaleInPercent + ")"); }
    public void setBackgroundColor(int color) { log("setBackgroundColor(0x" + Integer.toHexString(color) + ")"); }
    public void setLayerType(int layerType, android.graphics.Paint paint) { log("setLayerType(" + layerType + ")"); }

    public String getUrl() { log("getUrl() → " + cut(lastUrl)); return lastUrl; }
    public String getTitle() { log("getTitle() → " + cut(lastTitle)); return lastTitle; }
    public boolean canGoBack() { log("canGoBack() → false"); return false; }
    public boolean canGoForward() { log("canGoForward() → false"); return false; }
    public void goBack() { log("goBack()"); }
    public void goForward() { log("goForward()"); }

    public interface ValueCallback<T> { void onReceiveValue(T value); }
}
