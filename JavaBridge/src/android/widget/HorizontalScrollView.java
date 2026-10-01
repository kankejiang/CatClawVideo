package android.widget;

import android.content.Context;
import android.util.AttributeSet;

public class HorizontalScrollView extends FrameLayout {
    public HorizontalScrollView() { super(); }
    public HorizontalScrollView(Context c) { super(c); }
    public HorizontalScrollView(Context c, AttributeSet attrs) { super(c, attrs); }

    // ── 自动补齐：jar 引用到但桩缺失的成员（android-33 签名）──
    // ⚠ addView 必须转交 super，否则子节点被静默丢掉（同 ScrollView，2026-10-01 扫码链路实测）。
    @Override public void addView(android.view.View p0) { super.addView(p0); }
    public void smoothScrollTo(int p0, int p1) { }
}