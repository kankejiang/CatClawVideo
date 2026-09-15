package android.widget;

import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.drawable.Drawable;
import android.util.AttributeSet;
import android.view.View;

public class ImageView extends View {
    public ImageView() { super(); }
    public ImageView(Context c) { super(c); }
    public ImageView(Context c, AttributeSet attrs) { super(c, attrs); }

    public void setImageBitmap(Bitmap bm) { }
    public void setImageDrawable(Drawable drawable) { }
    public void setImageResource(int resId) { }
    public void setScaleType(ScaleType scaleType) { }
    public ScaleType getScaleType() { return ScaleType.FIT_CENTER; }
    public void setAdjustViewBounds(boolean adjustViewBounds) { }

    /** 缩放模式（真实为 enum）。 */
    public enum ScaleType {
        MATRIX, FIT_XY, FIT_START, FIT_CENTER, FIT_END, CENTER, CENTER_CROP, CENTER_INSIDE
    }
}