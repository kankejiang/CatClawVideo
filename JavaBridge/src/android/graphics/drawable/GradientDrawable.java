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
    private float cornerRadius;
    private int strokeColor; private int strokeWidth;
    private int shape = RECTANGLE;

    public GradientDrawable() { }

    public GradientDrawable(Orientation orientation, int[] colors) { }

    /** ⚠ jar 实测会调（网盘排序对话框的按钮底色），缺了直接 NoSuchMethodError。 */
    public void setColor(int argb) { this.color = argb; }

    public void setColor(android.content.res.ColorStateList colorStateList) { }

    public void setColors(int[] colors) { }

    /** 圆角真存（A 路线树序列化还原卡片观感）。 */
    public void setCornerRadius(float radius) { this.cornerRadius = radius; }
    public float getCornerRadius() { return cornerRadius; }

    public void setCornerRadii(float[] radii) { }

    /** 描边真存（禁用态/边框样式）。 */
    public void setStroke(int width, int color) { this.strokeWidth = width; this.strokeColor = color; }

    public void setStroke(int width, int color, float dashWidth, float dashGap) {
        this.strokeWidth = width; this.strokeColor = color;
    }

    public void setGradientType(int gradient) { }

    public void setShape(int shape) { }

    public void setGradientRadius(float gradientRadius) { }

    public void setSize(int width, int height) { }

    public void setUseLevel(boolean useLevel) { }

    public int getColor() { return color; }

    // ── 自动补齐：jar 引用到但桩缺失的成员（android-33 签名，空实现）──
    public void setStroke(int p0, android.content.res.ColorStateList p1) { }
}