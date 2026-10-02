package android.widget;

import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.drawable.Drawable;
import android.util.AttributeSet;
import android.view.View;

public class ImageView extends View {
    private Bitmap bitmap;

    public ImageView() { super(); }
    public ImageView(Context c) { super(c); }
    public ImageView(Context c, AttributeSet attrs) { super(c, attrs); }

    public void setImageBitmap(Bitmap bm) { bitmap = bm; }
    public Bitmap getImageBitmap() { return bitmap; }
    public void setImageDrawable(Drawable drawable) { }
    public void setImageResource(int resId) { }
    public void setScaleType(ScaleType scaleType) { }
    public ScaleType getScaleType() { return ScaleType.FIT_CENTER; }
    public void setAdjustViewBounds(boolean adjustViewBounds) { }

    /** 缩放模式（真实为 enum）。 */
    public enum ScaleType {
        MATRIX, FIT_XY, FIT_START, FIT_CENTER, FIT_END, CENTER, CENTER_CROP, CENTER_INSIDE
    }

    // ── 自动补齐：jar 引用到但桩缺失的成员（android-33 签名，空实现）──
    public android.graphics.drawable.Drawable getDrawable() { return null; }
    public void setImageMatrix(android.graphics.Matrix p0) { }
    public void setCropToPadding(boolean p0) { }
    public void onDraw(android.graphics.Canvas p0) { }
    @Override public void setVisibility(int p0) { super.setVisibility(p0); }
}