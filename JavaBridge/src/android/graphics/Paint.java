package android.graphics;

public class Paint { 
    // ── 自动补齐：jar 引用到但桩缺失的成员（android-33 签名，空实现）──
    public Paint() { }
    public Paint(int p0) { }
    public Paint(android.graphics.Paint p0) { }
    public void setFakeBoldText(boolean p0) { }
    public void setStyle(android.graphics.Paint.Style p0) { }
    public void setColor(int p0) { }
    public void setColor(long p0) { }
    public void setAlpha(int p0) { }
    public void setStrokeWidth(float p0) { }
    public void setStrokeCap(android.graphics.Paint.Cap p0) { }
    public void setStrokeJoin(android.graphics.Paint.Join p0) { }
    public android.graphics.ColorFilter setColorFilter(android.graphics.ColorFilter p0) { return null; }
    public void setShadowLayer(float p0, float p1, float p2, int p3) { }
    public void setShadowLayer(float p0, float p1, float p2, long p3) { }
    public float measureText(java.lang.String p0) { return 0f; }
    public void getTextBounds(java.lang.String p0, int p1, int p2, android.graphics.Rect p3) { }
    public void getTextBounds(java.lang.CharSequence p0, int p1, int p2, android.graphics.Rect p3) { }
    public void getTextBounds(char[] p0, int p1, int p2, android.graphics.Rect p3) { }

    // ── 第二批补齐（2026-09-30 扫码 QR 视图链路）：这些是真机 Paint 上极常见的调用，
    //    少一个就是 NoSuchMethodError，而且常发生在壳的后台线程里（只打日志、不上抛），
    //    表现成「对话框弹了但里面是空的」。签名全部对照 android-33。
    public void setAntiAlias(boolean p0) { }
    public void setDither(boolean p0) { }
    public void setFilterBitmap(boolean p0) { }
    public void setLinearText(boolean p0) { }
    public void setSubpixelText(boolean p0) { }
    public void setUnderlineText(boolean p0) { }
    public void setStrikeThruText(boolean p0) { }
    public void setFlags(int p0) { }
    public int getFlags() { return 0; }
    public void reset() { }
    public void setTextSize(float p0) { }
    public float getTextSize() { return 12f; }
    public void setTypeface(android.graphics.Typeface p0) { }
    public android.graphics.Typeface getTypeface() { return null; }
    public int getColor() { return 0xFF000000; }
    public float getStrokeWidth() { return 0f; }
    public float getFontMetrics() { return 0f; }
    public float getFontMetricsInt() { return 0f; }
    public int getFontMetricsInt(android.graphics.Paint.FontMetricsInt fm) { return 0; }
    public float measureText(CharSequence p0, int start, int end) { return 0f; }
    public float measureText(char[] p0, int start, int length) { return 0f; }
    public float measureText(java.lang.String p0, int start, int end) { return 0f; }

    public static class FontMetricsInt { public int ascent, bottom, leading, top; }

    public enum Style { FILL, STROKE, FILL_AND_STROKE }
    public enum Cap { BUTT, ROUND, SQUARE }
    public enum Join { MITER, ROUND, BEVEL }
    public static class FontMetrics { public float ascent, descent, top, bottom, leading; }
}