package android.widget;

import android.content.Context;
import android.util.AttributeSet;

public class ScrollView extends FrameLayout {
    public ScrollView() { super(); }
    public ScrollView(Context c) { super(c); }
    public ScrollView(Context c, AttributeSet attrs) { super(c, attrs); }

    // ── 自动补齐：jar 引用到但桩缺失的成员（android-33 签名，空实现）──
    public void addView(android.view.View p0) { }
    public void addView(android.view.View p0, int p1) { }
    public void addView(android.view.View p0, android.view.ViewGroup.LayoutParams p1) { }
    public void setFillViewport(boolean p0) { }
    public void onMeasure(int p0, int p1) { }
    public void requestLayout() { }
    public void scrollTo(int p0, int p1) { }
}