package android.view;

import android.content.Context;
import android.util.AttributeSet;

public class ViewGroup extends View {

    public ViewGroup() { super(); }
    public ViewGroup(Context c) { super(c); }
    public ViewGroup(Context c, AttributeSet attrs) { super(c, attrs); }

    public void addView(View child) { }
    public void addView(View child, int index) { }
    public void addView(View child, LayoutParams params) { }
    public void removeView(View view) { }
    public void removeAllViews() { }
    public int getChildCount() { return 0; }
    public View getChildAt(int index) { return null; }
    public void setPadding(int l, int t, int r, int b) { }
    public void setClipToPadding(boolean clip) { }
    public void setDescendantFocusability(int focusability) { }
    public void setOnHierarchyChangeListener(Object listener) { }

    /** 子 View 的布局参数（spider 里常以 new ViewGroup.LayoutParams(w,h) 出现）。 */
    public static class LayoutParams {
        public static final int MATCH_PARENT = -1;
        public static final int WRAP_CONTENT = -2;
        public static final int FILL_PARENT = -1;

        public int width;
        public int height;

        public LayoutParams(int w, int h) { this.width = w; this.height = h; }
        public LayoutParams(LayoutParams source) { this.width = source.width; this.height = source.height; }
    }

    public static class MarginLayoutParams extends LayoutParams {
        public int leftMargin;
        public int topMargin;
        public int rightMargin;
        public int bottomMargin;

        public MarginLayoutParams(int w, int h) { super(w, h); }
        public MarginLayoutParams(LayoutParams source) { super(source); }
        public void setMargins(int l, int t, int r, int b) {
            this.leftMargin = l; this.topMargin = t; this.rightMargin = r; this.bottomMargin = b;
        }
    }

    public interface OnHierarchyChangeListener {
        void onChildViewAdded(View parent, View child);
        void onChildViewRemoved(View parent, View child);
    }
}
