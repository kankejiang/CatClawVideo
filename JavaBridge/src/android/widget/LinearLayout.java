package android.widget;
import android.content.Context;
import android.util.AttributeSet;
import android.view.View;
import android.view.ViewGroup;
public class LinearLayout extends ViewGroup {
    public static final int HORIZONTAL = 0;
    public static final int VERTICAL = 1;
    public LinearLayout() { super(); }
    public LinearLayout(Context c) { super(c); }
    public LinearLayout(Context c, AttributeSet attrs) { super(c, attrs); }
    public void setOrientation(int o) { }

    /**
     * ⚠ 必须转交 {@code super}：jar 的调用点描述符写死 {@code LinearLayout.addView(View)}，
     * 这里留空实现会把 ViewGroup 那个真正存子节点的实现挡掉 —— 网盘对话框的
     * LinearLayout 于是永远是空的，宿主只收到一个没有内容的框（2026-09-24 实测）。
     */
    @Override public void addView(View v) { super.addView(v); }

    public void setGravity(int gravity) { }
    public void setWeightSum(float weightSum) { }

    /** LinearLayout.LayoutParams 桩（真实父类 ViewGroup.MarginLayoutParams）。 */
    public static class LayoutParams extends ViewGroup.MarginLayoutParams {

        public float weight;
        public int gravity = -1;

        public LayoutParams(int width, int height) { super(width, height); }
        public LayoutParams(int width, int height, float weight) { super(width, height); this.weight = weight; }
        public LayoutParams(int width, int height, int gravity) { super(width, height); this.gravity = gravity; }
        public LayoutParams(ViewGroup.LayoutParams source) { super(source); }
        public LayoutParams(LayoutParams source) {
            super(source);
            if (source != null) { this.weight = source.weight; this.gravity = source.gravity; }
        }
    }
}
