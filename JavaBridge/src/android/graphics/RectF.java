package android.graphics;

/** android.graphics.RectF 桩（jar 的 Canvas 绘制路径会引用）。 */
public class RectF {
    public float left, top, right, bottom;
    public RectF() { }
    public RectF(float l, float t, float r, float b) { left = l; top = t; right = r; bottom = b; }
    public RectF(Rect r) { }
    public void set(float l, float t, float r, float b) { left = l; top = t; right = r; bottom = b; }
    public void set(RectF r) { if (r != null) set(r.left, r.top, r.right, r.bottom); }
    public void setEmpty() { left = top = right = bottom = 0f; }
    public boolean isEmpty() { return left >= right || top >= bottom; }
    public float width() { return right - left; }
    public float height() { return bottom - top; }
    public float centerX() { return (left + right) * 0.5f; }
    public float centerY() { return (top + bottom) * 0.5f; }
    public void offset(float dx, float dy) { left += dx; right += dx; top += dy; bottom += dy; }
    public void inset(float dx, float dy) { left += dx; top += dy; right -= dx; bottom -= dy; }
    public boolean contains(float x, float y) { return left < right && top < bottom && x >= left && x < right && y >= top && y < bottom; }
    public void round(Rect dst) { }
    public void roundOut(Rect dst) { }
}
