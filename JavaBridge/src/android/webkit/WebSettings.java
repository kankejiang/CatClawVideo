package android.webkit;

/**
 * WebSettings 桩（2026-10-01 <b>取证版</b>）。
 *
 * <p>spider 里常被 {@code getSettings().setJavaScriptEnabled(true)} 之类调用。
 * 本轮把每次配置都打到 guest 控制台：配置项本身就是壳对 WebView 的**能力要求清单**
 * （要 JS、要 DOM storage、要 cookie、要 UA…），后面实现「WebView → 宿主 WebView2」桥时
 * 要按这份清单对齐宿主侧设置。</p>
 */
public class WebSettings {

    /** 所属 WebView 的编号（无参构造时为 0，保持 API 兼容）。 */
    private final int owner;

    public WebSettings() { this(0); }

    WebSettings(int owner) { this.owner = owner; }

    private void log(String what) {
        System.err.println("[websettings#" + owner + "] " + what);
    }

    public void setJavaScriptEnabled(boolean flag) { log("setJavaScriptEnabled(" + flag + ")"); }
    public void setDomStorageEnabled(boolean flag) { log("setDomStorageEnabled(" + flag + ")"); }
    public void setDatabaseEnabled(boolean flag) { log("setDatabaseEnabled(" + flag + ")"); }
    public void setSupportZoom(boolean support) { log("setSupportZoom(" + support + ")"); }
    public void setBuiltInZoomControls(boolean enabled) { log("setBuiltInZoomControls(" + enabled + ")"); }
    public void setDisplayZoomControls(boolean enabled) { log("setDisplayZoomControls(" + enabled + ")"); }
    public void setUseWideViewPort(boolean use) { log("setUseWideViewPort(" + use + ")"); }
    public void setLoadWithOverviewMode(boolean overview) { log("setLoadWithOverviewMode(" + overview + ")"); }
    public void setAllowFileAccess(boolean allow) { log("setAllowFileAccess(" + allow + ")"); }
    public void setBlockNetworkImage(boolean flag) { log("setBlockNetworkImage(" + flag + ")"); }
    public void setJavaScriptCanOpenWindowsAutomatically(boolean flag) { log("setJavaScriptCanOpenWindowsAutomatically(" + flag + ")"); }
    public void setUserAgentString(String ua) { log("setUserAgentString(" + ua + ")"); }
    public String getUserAgentString() { log("getUserAgentString()"); return ""; }
    public void setCacheMode(int mode) { log("setCacheMode(" + mode + ")"); }
    public int getCacheMode() { log("getCacheMode() → 0"); return 0; }
    public void setTextZoom(int textZoom) { log("setTextZoom(" + textZoom + ")"); }
    public void setDefaultTextEncodingName(String encoding) { log("setDefaultTextEncodingName(" + encoding + ")"); }

    // ── 自动补齐：jar 引用到但桩缺失的成员（android-33 签名，空实现）──
    public void setAllowContentAccess(boolean p0) { log("setAllowContentAccess(" + p0 + ")"); }
    public void setBlockNetworkLoads(boolean p0) { log("setBlockNetworkLoads(" + p0 + ")"); }
}
