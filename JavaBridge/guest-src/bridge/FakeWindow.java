package bridge;

/**
 * 无头 guest 的 {@code android.view.Window} 伪实现。
 *
 * <p>为什么需要它：Guard 壳的 {@code ProxyOrigin.getan()} 拿到 Activity 后第一件事就是
 * {@code getWindow()}（2026-09-29 实测 NPE：
 * {@code Attempt to invoke virtual method 'android.view.Window android.app.Activity.getWindow()' on a null object}，
 * 调用栈 {@code WoGG.playerContent → Pan.playerContent → ProxyOrigin.getan}）。
 * guest 里没有 WMS/真窗口，只能给一个「什么都不做」的窗口：全部抽象方法空实现，
 * 且每次被调都打一行日志 —— 这样既能让壳的 UI 支路走通，也留下去看它到底想要什么的证据。</p>
 *
 * <p>方法集直接取自 guest 自带 framework.jar（Android 13 / API 33）里
 * {@code android.view.Window} 的 53 个抽象方法，签名必须与之逐字一致，
 * 否则运行时 {@code AbstractMethodError}。</p>
 */
public final class FakeWindow extends android.view.Window {

    private android.view.View mDecor;

    public FakeWindow(android.content.Context context) {
        super(context);
    }

    private static void log(String m) {
        System.err.println("[fakewin] " + m);
    }

    private android.view.View decor() {
        if (mDecor == null) {
            try {
                mDecor = new android.view.View(getContext());
            } catch (Throwable t) {
                log("decor 构造失败: " + t);
            }
        }
        return mDecor;
    }

    private android.view.LayoutInflater inflater() {
        try {
            return android.view.LayoutInflater.from(getContext());
        } catch (Throwable t) {
            log("getLayoutInflater 失败: " + t);
            return null;
        }
    }

    public void addContentView(android.view.View p0, android.view.ViewGroup.LayoutParams p1) {
        log("addContentView");
    }

    public void alwaysReadCloseOnTouchAttr() {
        log("alwaysReadCloseOnTouchAttr");
    }

    public void clearContentView() {
        log("clearContentView");
    }

    public void closeAllPanels() {
        log("closeAllPanels");
    }

    public void closePanel(int p0) {
        log("closePanel");
    }

    public android.view.View getCurrentFocus() {
        log("getCurrentFocus");
        return null;
    }

    public android.view.View getDecorView() {
        log("getDecorView");
        return decor();
    }

    public android.view.LayoutInflater getLayoutInflater() {
        log("getLayoutInflater");
        return inflater();
    }

    public int getNavigationBarColor() {
        log("getNavigationBarColor");
        return 0;
    }

    public int getStatusBarColor() {
        log("getStatusBarColor");
        return 0;
    }

    public int getVolumeControlStream() {
        log("getVolumeControlStream");
        return 0;
    }

    public void invalidatePanelMenu(int p0) {
        log("invalidatePanelMenu");
    }

    public boolean isFloating() {
        log("isFloating");
        return false;
    }

    public boolean isShortcutKey(int p0, android.view.KeyEvent p1) {
        log("isShortcutKey");
        return false;
    }

    public void onActive() {
        log("onActive");
    }

    public void onConfigurationChanged(android.content.res.Configuration p0) {
        log("onConfigurationChanged");
    }

    public void onMultiWindowModeChanged() {
        log("onMultiWindowModeChanged");
    }

    public void onPictureInPictureModeChanged(boolean p0) {
        log("onPictureInPictureModeChanged");
    }

    public void openPanel(int p0, android.view.KeyEvent p1) {
        log("openPanel");
    }

    public android.view.View peekDecorView() {
        log("peekDecorView");
        return decor();
    }

    public boolean performContextMenuIdentifierAction(int p0, int p1) {
        log("performContextMenuIdentifierAction");
        return false;
    }

    public boolean performPanelIdentifierAction(int p0, int p1, int p2) {
        log("performPanelIdentifierAction");
        return false;
    }

    public boolean performPanelShortcut(int p0, int p1, android.view.KeyEvent p2, int p3) {
        log("performPanelShortcut");
        return false;
    }

    public void reportActivityRelaunched() {
        log("reportActivityRelaunched");
    }

    public void restoreHierarchyState(android.os.Bundle p0) {
        log("restoreHierarchyState");
    }

    public android.os.Bundle saveHierarchyState() {
        log("saveHierarchyState");
        return new android.os.Bundle();
    }

    public void setBackgroundDrawable(android.graphics.drawable.Drawable p0) {
        log("setBackgroundDrawable");
    }

    public void setChildDrawable(int p0, android.graphics.drawable.Drawable p1) {
        log("setChildDrawable");
    }

    public void setChildInt(int p0, int p1) {
        log("setChildInt");
    }

    public void setContentView(int p0) {
        log("setContentView");
    }

    public void setContentView(android.view.View p0) {
        log("setContentView");
    }

    public void setContentView(android.view.View p0, android.view.ViewGroup.LayoutParams p1) {
        log("setContentView");
    }

    public void setDecorCaptionShade(int p0) {
        log("setDecorCaptionShade");
    }

    public void setFeatureDrawable(int p0, android.graphics.drawable.Drawable p1) {
        log("setFeatureDrawable");
    }

    public void setFeatureDrawableAlpha(int p0, int p1) {
        log("setFeatureDrawableAlpha");
    }

    public void setFeatureDrawableResource(int p0, int p1) {
        log("setFeatureDrawableResource");
    }

    public void setFeatureDrawableUri(int p0, android.net.Uri p1) {
        log("setFeatureDrawableUri");
    }

    public void setFeatureInt(int p0, int p1) {
        log("setFeatureInt");
    }

    public void setNavigationBarColor(int p0) {
        log("setNavigationBarColor");
    }

    public void setResizingCaptionDrawable(android.graphics.drawable.Drawable p0) {
        log("setResizingCaptionDrawable");
    }

    public void setStatusBarColor(int p0) {
        log("setStatusBarColor");
    }

    public void setTitle(java.lang.CharSequence p0) {
        log("setTitle");
    }

    public void setTitleColor(int p0) {
        log("setTitleColor");
    }

    public void setVolumeControlStream(int p0) {
        log("setVolumeControlStream");
    }

    public boolean superDispatchGenericMotionEvent(android.view.MotionEvent p0) {
        log("superDispatchGenericMotionEvent");
        return false;
    }

    public boolean superDispatchKeyEvent(android.view.KeyEvent p0) {
        log("superDispatchKeyEvent");
        return false;
    }

    public boolean superDispatchKeyShortcutEvent(android.view.KeyEvent p0) {
        log("superDispatchKeyShortcutEvent");
        return false;
    }

    public boolean superDispatchTouchEvent(android.view.MotionEvent p0) {
        log("superDispatchTouchEvent");
        return false;
    }

    public boolean superDispatchTrackballEvent(android.view.MotionEvent p0) {
        log("superDispatchTrackballEvent");
        return false;
    }

    public void takeInputQueue(android.view.InputQueue.Callback p0) {
        log("takeInputQueue");
    }

    public void takeKeyEvents(boolean p0) {
        log("takeKeyEvents");
    }

    public void takeSurface(android.view.SurfaceHolder.Callback2 p0) {
        log("takeSurface");
    }

    public void togglePanel(int p0, android.view.KeyEvent p1) {
        log("togglePanel");
    }
}
