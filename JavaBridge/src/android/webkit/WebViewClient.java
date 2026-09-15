package android.webkit;

import android.graphics.Bitmap;

/** WebViewClient 桩：spider 常以匿名类 extends 它抓页面（缺类即 ClassNotFoundException）。 */
public class WebViewClient {

    public WebViewClient() { }

    public boolean shouldOverrideUrlLoading(WebView view, String url) { return false; }
    public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) { return false; }
    public void onPageStarted(WebView view, String url, Bitmap favicon) { }
    public void onPageFinished(WebView view, String url) { }
    public void onLoadResource(WebView view, String url) { }
    public void onReceivedError(WebView view, int errorCode, String description, String failingUrl) { }
    public WebResourceResponse shouldInterceptRequest(WebView view, String url) { return null; }
    public WebResourceResponse shouldInterceptRequest(WebView view, WebResourceRequest request) { return null; }
    public void doUpdateVisitedHistory(WebView view, String url, boolean isReload) { }
    public boolean shouldOverrideKeyEvent(WebView view, android.view.KeyEvent event) { return false; }
}
