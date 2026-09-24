package android.graphics.drawable;

public abstract class Drawable {

    public void setBounds(int left, int top, int right, int bottom) { }
    public void setBounds(android.graphics.Rect bounds) { }
    public android.graphics.Rect getBounds() { return new android.graphics.Rect(); }
    public void setAlpha(int alpha) { }
    public void setColorFilter(android.graphics.ColorFilter cf) { }
    public void setDither(boolean dither) { }
    public void setFilterBitmap(boolean filter) { }
    public boolean isStateful() { return false; }
    public int getOpacity() { return -1; }
    public int getIntrinsicWidth() { return -1; }
    public int getIntrinsicHeight() { return -1; }
    public Drawable mutate() { return this; }
    public boolean setState(int[] stateSet) { return false; }
    public int[] getState() { return new int[0]; }
    public void setTint(int tintColor) { }
    public void setTintList(android.content.res.ColorStateList tint) { }
    public void invalidateSelf() { }
    public void setVisible(boolean visible, boolean restart) { }
}