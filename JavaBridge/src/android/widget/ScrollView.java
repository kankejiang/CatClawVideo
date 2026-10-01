package android.widget;

import android.content.Context;
import android.util.AttributeSet;

public class ScrollView extends FrameLayout {
    public ScrollView() { super(); }
    public ScrollView(Context c) { super(c); }
    public ScrollView(Context c, AttributeSet attrs) { super(c, attrs); }

    // ── 自动补齐：jar 引用到但桩缺失的成员（android-33 签名）──
    // ⚠ addView/onMeasure 必须转交 super：桩生成器给这三个 addView 重载填过**空实现**，
    //   子类调用点描述符写死 ScrollView.addView(View) ⇒ 命中空覆写 ⇒ 子节点被静默丢掉。
    //   扫码二维码正是这种结构（2026-10-01 实测 `视图状态 …m extends ScrollView {}` 子节点数 0，
    //   驱动 onDraw 得到 着色像素=0/230400 —— 码在子 View 里，而我们连子 View 都没有）。
    //   onMeasure 同理：空覆写会让 measure() 记不到尺寸。
    @Override public void addView(android.view.View p0) { super.addView(p0); }
    @Override public void addView(android.view.View p0, int p1) { super.addView(p0, p1); }
    @Override public void addView(android.view.View p0, android.view.ViewGroup.LayoutParams p1) { super.addView(p0, p1); }
    public void setFillViewport(boolean p0) { }
    public void requestLayout() { super.requestLayout(); }
    public void scrollTo(int p0, int p1) { }
}