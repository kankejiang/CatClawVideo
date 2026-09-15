package android.webkit;

/** CookieManager 桩：spider 常做 setCookie/getCookie（PC 桥无 cookie 存储，返回 null 可观测降级）。 */
public class CookieManager {

    private static final CookieManager INSTANCE = new CookieManager();

    public static CookieManager getInstance() { return INSTANCE; }

    public void setAcceptCookie(boolean accept) { }
    public boolean acceptCookie() { return true; }
    public void setAcceptThirdPartyCookies(WebView webview, boolean accept) { }
    public void setCookie(String url, String value) { }
    public void setCookie(String url, String value, ValueCallback<Boolean> callback) { }
    public String getCookie(String url) { return null; }
    public boolean hasCookies() { return false; }
    public void removeAllCookie() { }
    public void removeSessionCookie() { }
    public void removeExpiredCookie() { }
    public void flush() { }

    public interface ValueCallback<T> { void onReceiveValue(T value); }
}
