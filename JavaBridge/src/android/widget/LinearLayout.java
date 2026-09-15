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
    public void addView(View v) { }
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
