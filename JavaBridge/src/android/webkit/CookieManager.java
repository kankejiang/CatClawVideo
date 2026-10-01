package android.webkit;

/**
 * CookieManager 桩：spider 常做 setCookie/getCookie。
 *
 * <p>⚠️ 2026-10-01 网盘登录态排障：本桩**把 cookie 丢掉了**（setCookie 空实现、getCookie 恒 null），
 * 而 guest 的 {@code framework.jar} 里其实有真的 {@code android.webkit.CookieManager}
 * （只是被本桩在 boot classpath 前置挡住）。若壳用 CookieManager 保存网盘登录态，就会出现
 * 「扫码成功 → 重启未登录」。本桩现在对每次读写都打 {@code [cookie]} 留痕，
 * 用一轮扫码即可判定它是"壳在用但我们丢了"，还是"壳压根没走这条"。</p>
 */
public class CookieManager {

    private static final CookieManager INSTANCE = new CookieManager();

    public static CookieManager getInstance() { return INSTANCE; }

    public void setAcceptCookie(boolean accept) { }

    public boolean acceptCookie() { return true; }

    public void setAcceptThirdPartyCookies(WebView webview, boolean accept) { }

    public void setCookie(String url, String value) {
        System.err.println("[cookie] setCookie(" + host(url) + ") 值 " + (value == null ? "null" : value.length() + "B")
                + " → **丢弃**（桩无存储）");
    }

    public void setCookie(String url, String value, ValueCallback<Boolean> callback) {
        System.err.println("[cookie] setCookie(" + host(url) + ", 回调) 值 "
                + (value == null ? "null" : value.length() + "B") + " → **丢弃**（桩无存储）");
        if (callback != null) callback.onReceiveValue(Boolean.FALSE);
    }

    public String getCookie(String url) {
        System.err.println("[cookie] getCookie(" + host(url) + ") → null（桩无存储）");
        return null;
    }

    public boolean hasCookies() {
        System.err.println("[cookie] hasCookies() → false（桩无存储）");
        return false;
    }

    public void removeAllCookie() { System.err.println("[cookie] removeAllCookie()"); }

    public void removeSessionCookie() { System.err.println("[cookie] removeSessionCookie()"); }

    public void removeExpiredCookie() { }

    public void flush() { System.err.println("[cookie] flush()"); }

    /** 日志只记 host（URL 的 query/path 可能带会话信息）。 */
    private static String host(String url) {
        try {
            java.net.URI u = java.net.URI.create(url);
            return u.getHost() == null ? "?" : u.getHost();
        } catch (Throwable t) {
            return "?";
        }
    }

    public interface ValueCallback<T> { void onReceiveValue(T value); }
}
