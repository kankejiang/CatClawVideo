package android.webkit;

import java.util.Map;

/** WebResourceRequest 桩（WebViewClient.shouldOverrideUrlLoading 的新版重载参数）。 */
public interface WebResourceRequest {
    android.net.Uri getUrl();
    boolean isForMainFrame();
    boolean isRedirect();
    boolean hasGesture();
    String getMethod();
    Map<String, String> getRequestHeaders();
}
