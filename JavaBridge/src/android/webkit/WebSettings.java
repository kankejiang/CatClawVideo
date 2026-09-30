package android.webkit;

/** WebSettings 桩：spider 里常被 getSettings().setJavaScriptEnabled(true) 之类调用。 */
public class WebSettings {
    public void setJavaScriptEnabled(boolean flag) { }
    public void setDomStorageEnabled(boolean flag) { }
    public void setDatabaseEnabled(boolean flag) { }
    public void setSupportZoom(boolean support) { }
    public void setBuiltInZoomControls(boolean enabled) { }
    public void setDisplayZoomControls(boolean enabled) { }
    public void setUseWideViewPort(boolean use) { }
    public void setLoadWithOverviewMode(boolean overview) { }
    public void setAllowFileAccess(boolean allow) { }
    public void setBlockNetworkImage(boolean flag) { }
    public void setJavaScriptCanOpenWindowsAutomatically(boolean flag) { }
    public void setUserAgentString(String ua) { }
    public String getUserAgentString() { return ""; }
    public void setCacheMode(int mode) { }
    public int getCacheMode() { return 0; }
    public void setTextZoom(int textZoom) { }
    public void setDefaultTextEncodingName(String encoding) { }

    // ── 自动补齐：jar 引用到但桩缺失的成员（android-33 签名，空实现）──
    public void setAllowContentAccess(boolean p0) { }
    public void setBlockNetworkLoads(boolean p0) { }
}
