package android.graphics;

import android.os.Parcelable;

/** Point 桩（Display.getSize / getRealSize 的输出参数）。 */
public class Point implements Parcelable {

    public int x;
    public int y;

    public Point() { }
    public Point(int x, int y) { this.x = x; this.y = y; }
    public Point(Point src) { if (src != null) set(src.x, src.y); }

    public void set(int x, int y) { this.x = x; this.y = y; }
    public void set(Point src) { if (src != null) set(src.x, src.y); }
    public void negate() { x = -x; y = -y; }
    public void offset(int dx, int dy) { x += dx; y += dy; }
    public final boolean equals(int x, int y) { return this.x == x && this.y == y; }

    @Override public boolean equals(Object o) {
        return o instanceof Point && x == ((Point) o).x && y == ((Point) o).y;
    }
    @Override public int hashCode() { return 31 * x + y; }
    @Override public String toString() { return "Point(" + x + ", " + y + ")"; }
}
