package android.view;

public class ViewTreeObserver { 
    // ── 自动补齐：jar 引用到但桩缺失的成员（android-33 签名，空实现）──
    public void addOnGlobalFocusChangeListener(android.view.ViewTreeObserver.OnGlobalFocusChangeListener p0) { }

    public interface OnGlobalFocusChangeListener { void onGlobalFocusChanged(android.view.View oldFocus, android.view.View newFocus); }
    public interface OnGlobalLayoutListener { void onGlobalLayout(); }
    public interface OnScrollChangedListener { void onScrollChanged(); }
}