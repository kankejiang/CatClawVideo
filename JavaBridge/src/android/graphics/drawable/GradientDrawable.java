package android.graphics.drawable;

public class GradientDrawable extends Drawable {

    public static final int RECTANGLE = 0;
    public static final int OVAL = 1;
    public static final int LINE = 2;
    public static final int RING = 3;

    public static final int LINEAR_GRADIENT = 0;
    public static final int RADIAL_GRADIENT = 1;
    public static final int SWEEP_GRADIENT = 2;

    public enum Orientation {
        TOP_BOTTOM, TR_BL, RIGHT_LEFT, BR_TL, BOTTOM_TOP, BL_TR, LEFT_RIGHT, TL_BR
    }

    private int color;

    public GradientDrawable() { }

    public GradientDrawable(Orientation orientation, int[] colors) { }

    /** ⚠ jar 实测会调（网盘排序对话框的按钮底色），缺了直接 NoSuchMethodError。 */
    public void setColor(int argb) { this.color = argb; }

    public void setColor(android.content.res.ColorStateList colorStateList) { }

    public void setColors(int[] colors) { }

    public void setCornerRadius(float radius) { }

    public void setCornerRadii(float[] radii) { }

    public void setStroke(int width, int color) { }

    public void setStroke(int width, int color, float dashWidth, float dashGap) { }

    public void setGradientType(int gradient) { }

    public void setShape(int shape) { }

    public void setGradientRadius(float gradientRadius) { }

    public void setSize(int width, int height) { }

    public void setUseLevel(boolean useLevel) { }

    public int getColor() { return color; }
}