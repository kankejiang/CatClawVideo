package android.widget;

/**
 * <code>android.widget.AbsoluteLayout</code> 桩 —— 真机 WebView 的直接父类（标记父类）。
 *
 * <p>真机链：{@code WebView → AbsoluteLayout → ViewGroup}（API 33 里 WebView 仍挂 AbsoluteLayout）。
 * 桩以前写成 {@code WebView → FrameLayout} —— 少一层且分支不对：壳里凡是把 WebView 当
 * AbsoluteLayout/ViewGroup 用的代码要按真机那条链才过校验。{@link ViewGroup} 已带 addView 等
 * 成员，所以不需要 FrameLayout 那一支。</p>
 */
public class AbsoluteLayout extends android.view.ViewGroup {

    public AbsoluteLayout() { }

    public AbsoluteLayout(android.content.Context c) { super(c); }

    public AbsoluteLayout(android.content.Context c, android.util.AttributeSet attrs) { super(c, attrs); }
}
