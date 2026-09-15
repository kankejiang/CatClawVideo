package android.webkit;

import android.content.Context;
import android.util.AttributeSet;
import android.view.ViewGroup;
import android.widget.FrameLayout;

/** WebView 桩。
 *  <para>⚠ 只在 PC 桥里用于「让 spider 的类能加载」——没有任何真实渲染/JS 能力。
 *  spider 若真的依赖 WebView 出结果，会拿到空值而不是崩溃（可观测、可降级）。</para> */
public class WebView extends FrameLayout {

    public WebView() { super(); }
    public WebView(Context c) { super(c); }
    public WebView(Context c, AttributeSet attrs) { super(c, attrs); }

    public void setWebViewClient(WebViewClient client) { }
    public void setWebChromeClient(WebChromeClient client) { }
    public WebViewClient getWebViewClient() { return null; }
    public WebSettings getSettings() { return new WebSettings(); }
    public void loadUrl(String url) { }
    public void loadUrl(String url, java.util.Map<String, String> additionalHttpHeaders) { }
    public void loadData(String data, String mimeType, String encoding) { }
    public void loadDataWithBaseURL(String baseUrl, String data, String mimeType, String encoding, String historyUrl) { }
    public void postUrl(String url, byte[] postData) { }
    public void reload() { }
    public void stopLoading() { }
    public void destroy() { }
    public void clearCache(boolean includeDiskFiles) { }
    public void clearHistory() { }
    public void addJavascriptInterface(Object object, String name) { }
    public void removeJavascriptInterface(String name) { }
    public void evaluateJavascript(String script, ValueCallback<String> resultCallback) { }
    public void setInitialScale(int scaleInPercent) { }
    public void setBackgroundColor(int color) { }
    public void setLayerType(int layerType, android.graphics.Paint paint) { }
    public String getUrl() { return null; }
    public String getTitle() { return null; }
    public boolean canGoBack() { return false; }
    public boolean canGoForward() { return false; }
    public void goBack() { }
    public void goForward() { }

    public interface ValueCallback<T> { void onReceiveValue(T value); }
}
