package android.widget;

import android.content.Context;
import android.util.AttributeSet;
import android.view.ViewGroup;

public class FrameLayout extends ViewGroup {
    public FrameLayout() { super(); }
    public FrameLayout(Context c) { super(c); }
    public FrameLayout(Context c, AttributeSet attrs) { super(c, attrs); }

    public void setForegroundGravity(int foregroundGravity) { }
    public void setMeasureAllChildren(boolean measureAll) { }

    /** FrameLayout.LayoutParams 桩（真实父类 ViewGroup.MarginLayoutParams）。
     *  常量取值对齐 android.view.Gravity，避免再引一个 stub。 */
    public static class LayoutParams extends ViewGroup.MarginLayoutParams {

        public static final int UNSPECIFIED_GRAVITY = -1;
        public static final int TOP = 0x30;
        public static final int BOTTOM = 0x50;
        public static final int LEFT = 0x03;
        public static final int RIGHT = 0x05;
        public static final int CENTER_VERTICAL = 0x10;
        public static final int CENTER_HORIZONTAL = 0x01;
        public static final int CENTER = 0x11;
        public static final int START = 0x00800003;
        public static final int END = 0x00800005;

        public int gravity = UNSPECIFIED_GRAVITY;

        public LayoutParams(int width, int height) { super(width, height); }
        public LayoutParams(int width, int height, int gravity) { super(width, height); this.gravity = gravity; }
        public LayoutParams(ViewGroup.LayoutParams source) { super(source); }
        public LayoutParams(LayoutParams source) {
            super(source);
            if (source != null) this.gravity = source.gravity;
        }

        public void setGravity(int gravity) { this.gravity = gravity; }
        public int getGravity() { return gravity; }
    }
}