package android.widget;

import android.content.Context;
import android.content.res.ColorStateList;
import android.graphics.Paint;
import android.graphics.Typeface;
import android.graphics.drawable.Drawable;
import android.text.TextUtils;
import android.util.AttributeSet;
import android.view.View;

/**
 * <code>android.widget.TextView</code> 桩。
 *
 * <p>⚠️ 参数类型必须与 Android 真实签名<b>逐字符一致</b>：jar 是预编译的，调用点写的是
 * 精确描述符（如 {@code setTypeface(Landroid/graphics/Typeface;I)V}），用 {@code Object}
 * 兜底并不能匹配，只会把 NoSuchMethodError 推后。2026-09-24 实测：
 * {@code Pan.tF(...)} 卡在 {@code setTypeface(Typeface,int)}。</p>
 */
public class TextView extends View {

    /** 文本对齐常量（按名引用，缺了 NoSuchFieldError）。 */
    public static final int TEXT_ALIGNMENT_INHERIT = 0;
    public static final int TEXT_ALIGNMENT_GRAVITY = 1;
    public static final int TEXT_ALIGNMENT_TEXT_START = 2;
    public static final int TEXT_ALIGNMENT_TEXT_END = 3;
    public static final int TEXT_ALIGNMENT_CENTER = 4;
    public static final int TEXT_ALIGNMENT_VIEW_START = 5;
    public static final int TEXT_ALIGNMENT_VIEW_END = 6;

    private CharSequence text = "";
    private float textSize = 14f;
    private int textColor = 0xFF000000;
    private Typeface typeface;

    public TextView() { super(); }
    public TextView(Context c) { super(c); }
    public TextView(Context c, AttributeSet attrs) { super(c, attrs); }

    // ── 文本 ──
    public void setText(CharSequence t) { text = t == null ? "" : t; }
    public void setText(int resId) { text = ""; }
    public void setText(char[] t, int start, int len) { text = new String(t, start, len); }
    public CharSequence getText() { return text; }
    public void append(CharSequence t) { text = String.valueOf(text) + (t == null ? "" : t); }
    public int length() { return String.valueOf(text).length(); }
    public void setHint(CharSequence hint) { }
    public CharSequence getHint() { return ""; }
    public void setHintTextColor(int color) { }
    public void setHighlightColor(int color) { }

    // ── 字号 / 颜色 / 字体 ──
    public void setTextSize(float size) { textSize = size; }
    public void setTextSize(int unit, float size) { textSize = size; }
    public float getTextSize() { return textSize; }
    public void setTextColor(int color) { textColor = color; }
    public void setTextColor(int color, int alpha) { textColor = color; }
    public void setTextColor(ColorStateList colors) { }
    public int getCurrentTextColor() { return textColor; }
    public void setTypeface(Typeface tf) { typeface = tf; }
    public void setTypeface(Typeface tf, int style) { typeface = tf; }
    public Typeface getTypeface() { return typeface; }
    public void setTextAppearance(Context context, int resId) { }
    public void setAllCaps(boolean allCaps) { }
    public void setPaintFlags(int flags) { }
    public Paint getPaint() { return new Paint(); }
    public void setShadowLayer(float radius, float dx, float dy, int color) { }
    public void setLetterSpacing(float letterSpacing) { }
    public void setIncludeFontPadding(boolean includepad) { }
    public void setLineSpacing(float add, float mult) { }

    // ── 行 / 排版 ──
    public void setGravity(int gravity) { }
    public int getGravity() { return 0; }
    public void setGravity(int gravity, int textAlignment) { }
    public void setSingleLine() { }
    public void setSingleLine(boolean singleLine) { }
    public void setMaxLines(int maxLines) { }
    public int getMaxLines() { return Integer.MAX_VALUE; }
    public void setLines(int lines) { }
    public void setMinLines(int minlines) { }
    public void setMaxWidth(int maxPixels) { }
    public void setMinWidth(int minPixels) { }
    public void setMinHeight(int minPixels) { }
    public void setMaxHeight(int maxPixels) { }
    public void setHorizontallyScrolling(boolean whether) { }
    public void setEllipsize(TextUtils.TruncateAt where) { }
    public void setMarqueeRepeatLimit(int marqueeLimit) { }
    public void setTextIsSelectable(boolean selectable) { }
    public void setSelectAllOnFocus(boolean selectAllOnFocus) { }
    public void setInputType(int type) { }
    public void setImeOptions(int imeOptions) { }
    public void setFilters(Object[] filters) { }
    public void setEnabled(boolean enabled) { }

    // ── 内嵌图标 ──
    public void setCompoundDrawables(Drawable left, Drawable top, Drawable right, Drawable bottom) { }
    public void setCompoundDrawablesWithIntrinsicBounds(int l, int t, int r, int b) { }
    public void setCompoundDrawablePadding(int pad) { }
}
