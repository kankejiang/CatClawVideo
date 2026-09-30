package android.graphics;

/** android.graphics.Matrix 桩。 */
public class Matrix {
    public Matrix() { }
    public void reset() { }
    public void set(Matrix src) { }
    public boolean postTranslate(float dx, float dy) { return true; }
    public boolean postScale(float sx, float sy) { return true; }
    public boolean postRotate(float degrees) { return true; }
    public boolean preTranslate(float dx, float dy) { return true; }
    public boolean preScale(float sx, float sy) { return true; }
    public boolean preRotate(float degrees) { return true; }
    public boolean setRectToRect(RectF src, RectF dst, ScaleToFit stf) { return true; }
    public boolean mapRect(RectF dst, RectF src) { return true; }
    public boolean isIdentity() { return true; }
    public enum ScaleToFit { FILL, START, CENTER, END }
}
