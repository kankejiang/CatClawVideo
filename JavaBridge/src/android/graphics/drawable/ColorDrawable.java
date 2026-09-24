package android.graphics.drawable;

public class ColorDrawable extends Drawable {

    private int color;

    public ColorDrawable() { }

    /** ⚠ jar 实测会调 {@code new ColorDrawable(int)}（Guard 对话框背景），缺了直接 NoSuchMethodError。 */
    public ColorDrawable(int color) { this.color = color; }

    public void setColor(int color) { this.color = color; }

    public int getColor() { return color; }

    public void setAlpha(int alpha) { }
}