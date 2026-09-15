package android.view;

import android.util.DisplayMetrics;

/** Display 桩：PC 上没有真实屏幕信息，给一组稳定的默认值（spider 多用于换算比例）。 */
public final class Display {

    public static final int DEFAULT_DISPLAY = 0;
    public static final int ROTATION_0 = 0;
    public static final int ROTATION_90 = 1;
    public static final int ROTATION_180 = 2;
    public static final int ROTATION_270 = 3;

    private final int width;
    private final int height;

    public Display() { this(1080, 1920); }
    public Display(int width, int height) { this.width = width; this.height = height; }

    public int getWidth() { return width; }
    public int getHeight() { return height; }
    public int getRotation() { return ROTATION_0; }
    public int getDisplayId() { return DEFAULT_DISPLAY; }
    public boolean isValid() { return true; }

    public void getSize(android.graphics.Point outSize) {
        if (outSize != null) outSize.set(width, height);
    }
    public void getRealSize(android.graphics.Point outSize) {
        if (outSize != null) outSize.set(width, height);
    }
    public void getMetrics(DisplayMetrics outMetrics) {
        if (outMetrics == null) return;
        outMetrics.widthPixels = width;
        outMetrics.heightPixels = height;
        outMetrics.density = 1.0f;
    }
    public DisplayMetrics getMetrics() {
        DisplayMetrics m = new DisplayMetrics();
        getMetrics(m);
        return m;
    }
}
