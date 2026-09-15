package android.graphics;

import android.os.Parcelable;

/** Rect 桩：真正按几何语义实现（spider 里常用于算尺寸/裁剪判定）。 */
public final class Rect implements Parcelable {

    public int left;
    public int top;
    public int right;
    public int bottom;

    public Rect() { }
    public Rect(int left, int top, int right, int bottom) {
        this.left = left; this.top = top; this.right = right; this.bottom = bottom;
    }
    public Rect(Rect r) { if (r != null) set(r); }

    public void set(int left, int top, int right, int bottom) {
        this.left = left; this.top = top; this.right = right; this.bottom = bottom;
    }
    public void set(Rect src) { if (src != null) set(src.left, src.top, src.right, src.bottom); }
    public void setEmpty() { left = top = right = bottom = 0; }

    public boolean isEmpty() { return left >= right || top >= bottom; }
    public int width() { return right - left; }
    public int height() { return bottom - top; }
    public int centerX() { return (left + right) >> 1; }
    public int centerY() { return (top + bottom) >> 1; }
    public float exactCenterX() { return (left + right) * 0.5f; }
    public float exactCenterY() { return (top + bottom) * 0.5f; }

    public boolean contains(int x, int y) { return left < right && top < bottom && x >= left && x < right && y >= top && y < bottom; }
    public boolean contains(Rect r) {
        return r != null && left < right && top < bottom && left <= r.left && top <= r.top && right > r.right && bottom > r.bottom;
    }
    public boolean intersect(Rect r) {
        if (r == null) return false;
        return left < r.right && r.left < right && top < r.bottom && r.top < bottom;
    }
    public void union(int x, int y) {
        if (x < left) left = x; else if (x > right) right = x;
        if (y < top) top = y; else if (y > bottom) bottom = y;
    }
    public void union(Rect r) {
        if (r == null) return;
        union(r.left, r.top);
        union(r.right, r.bottom);
    }
    public void offset(int dx, int dy) { left += dx; top += dy; right += dx; bottom += dy; }
    public void inset(int dx, int dy) { left += dx; top += dy; right -= dx; bottom -= dy; }

    @Override public boolean equals(Object o) {
        if (!(o instanceof Rect)) return false;
        Rect r = (Rect) o;
        return left == r.left && top == r.top && right == r.right && bottom == r.bottom;
    }
    @Override public int hashCode() {
        int result = left; result = 31 * result + top; result = 31 * result + right; result = 31 * result + bottom;
        return result;
    }
    @Override public String toString() { return "Rect(" + left + ", " + top + " - " + right + ", " + bottom + ")"; }
    public String flattenToString() { return left + " " + top + " " + right + " " + bottom; }
    public String toShortString() { return "[" + left + "," + top + "][" + right + "," + bottom + "]"; }
}
