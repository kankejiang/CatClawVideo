package android.webkit;

import android.graphics.Bitmap;

/** WebChromeClient 桩。 */
public class WebChromeClient {
    public WebChromeClient() { }

    public void onProgressChanged(WebView view, int newProgress) { }
    public void onReceivedTitle(WebView view, String title) { }
    public void onReceivedIcon(WebView view, Bitmap icon) { }
    public boolean onJsAlert(WebView view, String url, String message, JsResult result) { return false; }
    public boolean onJsConfirm(WebView view, String url, String message, JsResult result) { return false; }
    public boolean onJsPrompt(WebView view, String url, String message, String defaultValue, JsPromptResult result) { return false; }
    public boolean onCreateWindow(WebView view, boolean isDialog, boolean isUserGesture, android.os.Message resultMsg) { return false; }
    public void onCloseWindow(WebView window) { }

    public interface JsResult {
        void confirm();
        void cancel();
    }
    public interface JsPromptResult extends JsResult {
        void confirm(String result);
    }
}
