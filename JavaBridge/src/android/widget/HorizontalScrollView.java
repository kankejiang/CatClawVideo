package android.widget;

import android.content.Context;
import android.util.AttributeSet;

public class HorizontalScrollView extends FrameLayout {
    public HorizontalScrollView() { super(); }
    public HorizontalScrollView(Context c) { super(c); }
    public HorizontalScrollView(Context c, AttributeSet attrs) { super(c, attrs); }

    // ── 自动补齐：jar 引用到但桩缺失的成员（android-33 签名，空实现）──
    public void addView(android.view.View p0) { }
    public void smoothScrollTo(int p0, int p1) { }
}