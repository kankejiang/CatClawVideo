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

    public enum Style { FILL, STROKE, FILL_AND_STROKE }
    public enum Cap { BUTT, ROUND, SQUARE }
    public enum Join { MITER, ROUND, BEVEL }
    public static class FontMetrics { public float ascent, descent, top, bottom, leading; }
}