package android.graphics;

/**
 * 记录式画布。
 *
 * <p><b>为什么需要</b>（2026-09-30 网盘扫码链路）：壳的二维码不是 ImageView + Bitmap，
 * 而是一个自定义 View（混淆后叫 {@code m}）在 {@code onDraw(Canvas)} 里逐格
 * {@code drawRect} 画出来的。桩以前把所有 draw* 都写成空实现 ⇒ 对话框弹到宿主、
 * 但里面永远是空的（日志：{@code 视图树 m qr=无} + {@code qr 兜底未命中}）。
 * 这里把几何绘制落到 {@link Bitmap} 的像素缓冲上 —— 这是**源自己画**的落地处：
 * 壳若 `new Canvas(bmp)` + `drawRect` 再把 bmp 交给 ImageView，桩取到的就是真点阵。
 * ⚠ 2026-10-01 删掉的是桩反过来驱动 View 的 onDraw 离屏重绘（{@code View.renderInto} +
 * huntQr 的第 ③ 层）：那等于替每个订阅源画 UI，不做。保留这一半是因为它是 Android 的
 * 基础渲染原语，与源无关。</p>
 *
 * <p>取舍：只记「位置 + 颜色」，不做抗锯齿/变换/裁剪（{@code translate} 例外，
 * 因为 zxing 一族常先 translate 再画）；文本不落像素——宿主按 message 显示文字。</p>
 */
public class Canvas {

    private Bitmap target;
    private int tx, ty;                 // 累计平移
    private final java.util.Deque<int[]> stack = new java.util.ArrayDeque<>();

    public Canvas() { }

    public Canvas(android.graphics.Bitmap p0) { this.target = p0; }

    public void setBitmap(android.graphics.Bitmap b) { this.target = b; }

    public int getWidth() { return target == null ? 0 : target.getWidth(); }

    public int getHeight() { return target == null ? 0 : target.getHeight(); }

    public int save() { stack.push(new int[]{tx, ty}); return 0; }

    public void restore() {
        int[] s = stack.peek() == null ? null : stack.pop();
        if (s != null) { tx = s[0]; ty = s[1]; }
    }

    public void restoreToCount(int p0) { restore(); }

    public void translate(float dx, float dy) { tx += (int) dx; ty += (int) dy; }

    public void scale(float sx, float sy) { }

    public void rotate(float degrees) { }

    public boolean clipRect(float p0, float p1, float p2, float p3) { return false; }

    public boolean clipRect(int p0, int p1, int p2, int p3) { return false; }

    public void drawColor(int color) { if (target != null) target.blit(0, 0, getWidth(), getHeight(), color); }

    public void drawRGB(int r, int g, int b) { drawColor(0xFF000000 | (r << 16) | (g << 8) | b); }

    public void drawARGB(int a, int r, int g, int b) { drawColor((a << 24) | (r << 16) | (g << 8) | b); }

    public void drawRect(float left, float top, float right, float bottom, android.graphics.Paint p) {
        blitRect(left, top, right, bottom, p);
    }

    public void drawRect(android.graphics.Rect r, android.graphics.Paint p) {
        if (r != null) blitRect(r.left, r.top, r.right, r.bottom, p);
    }

    public void drawRoundRect(android.graphics.RectF r, float rx, float ry, android.graphics.Paint p) {
        if (r != null) blitRect(r.left, r.top, r.right, r.bottom, p);
    }

    /** 圆按外接方框记：二维码里没有圆，而头像/图标只要有个形状就能被宿主看见。 */
    public void drawCircle(float cx, float cy, float radius, android.graphics.Paint p) {
        blitRect(cx - radius, cy - radius, cx + radius, cy + radius, p);
    }

    public void drawLine(float start_x, float start_y, float stop_x, float stop_y, android.graphics.Paint paint) {
        if (target == null) return;
        int c = color(paint);
        int w = Math.max(1, (int) (paint == null ? 1f : paint.getStrokeWidth()));
        float dx = stop_x - start_x, dy = stop_y - start_y;
        int steps = (int) Math.max(Math.abs(dx), Math.abs(dy));
        if (steps <= 0) { target.blit((int) start_x + tx, (int) start_y + ty, w, w, c); return; }
        for (int i = 0; i <= steps; i++) {
            int x = (int) (start_x + dx * i / steps) + tx;
            int y = (int) (start_y + dy * i / steps) + ty;
            target.blit(x, y, w, w, c);
        }
    }

    public void drawPoint(float x, float y, android.graphics.Paint p) {
        if (target != null) target.blit((int) x + tx, (int) y + ty, 1, 1, color(p));
    }

    public void drawPoints(float[] pts, int offset, int count, android.graphics.Paint p) {
        if (pts == null || target == null) return;
        for (int i = 0; i + 1 < count; i += 2)
            target.blit((int) pts[offset + i] + tx, (int) pts[offset + i + 1] + ty, 1, 1, color(p));
    }

    public void drawPoints(float[] pts, android.graphics.Paint p) {
        drawPoints(pts, 0, pts == null ? 0 : pts.length, p);
    }

    /** 把源位图贴进来（zxing 有些实现先建码图再 drawBitmap）。 */
    public void drawBitmap(android.graphics.Bitmap src, float left, float top, android.graphics.Paint p) {
        if (target == null || src == null) return;
        int[] row = new int[Math.max(src.getWidth(), 1)];
        for (int y = 0; y < src.getHeight(); y++) {
            src.getPixels(row, 0, src.getWidth(), 0, y, src.getWidth(), 1);
            target.blit((int) left + tx, (int) top + y + ty, src.getWidth(), 1, row);
        }
    }

    public void drawBitmap(android.graphics.Bitmap src, android.graphics.Matrix matrix, android.graphics.Paint p) {
        drawBitmap(src, 0f, 0f, p);
    }

    public void drawBitmap(android.graphics.Bitmap src, int[] colors, int offset, int stride,
                           float x, float y, boolean hasAlpha, android.graphics.Paint p) {
        drawBitmap(src, x, y, p);
    }

    public void drawBitmap(android.graphics.Bitmap src, android.graphics.Rect srcRect,
                           android.graphics.Rect dstRect, android.graphics.Paint p) {
        if (src == null || dstRect == null) return;
        drawBitmap(src, dstRect.left, dstRect.top, p);
    }

    public void drawBitmap(android.graphics.Bitmap src, android.graphics.Rect srcRect,
                           android.graphics.RectF dstRect, android.graphics.Paint p) {
        if (src == null || dstRect == null) return;
        drawBitmap(src, dstRect.left, dstRect.top, p);
    }

    // 文本不落到像素：宿主对话框按 message/标题显示文字，画进位图只会糊掉二维码。
    public void drawText(java.lang.String p0, float p1, float p2, android.graphics.Paint p3) { }

    public void drawText(java.lang.CharSequence p0, int p1, int p2, float p3, float p4, android.graphics.Paint p5) { }

    public void drawText(char[] p0, int p1, int p2, float p3, float p4, android.graphics.Paint p5) { }

    public void drawTextOnPath(java.lang.String p0, android.graphics.Path p1, float p2, float p3,
                               android.graphics.Paint p4) { }

    public void drawArc(android.graphics.RectF oval, float startAngle, float sweepAngle, boolean useCenter,
                        android.graphics.Paint p) {
        if (oval != null) blitRect(oval.left, oval.top, oval.right, oval.bottom, p);
    }

    public void drawPaint(android.graphics.Paint p) {
        if (p != null) drawColor(p.getColor());
    }

    private static int color(android.graphics.Paint p) { return p == null ? 0xFF000000 : p.getColor(); }

    private void blitRect(float l, float t, float r, float b, android.graphics.Paint p) {
        if (target == null) return;
        int x0 = (int) Math.min(l, r) + tx, x1 = (int) Math.max(l, r) + tx;
        int y0 = (int) Math.min(t, b) + ty, y1 = (int) Math.max(t, b) + ty;
        // 零面积（zxing 常见「一格宽 0」的浮点误差）也要画得出来，否则整张码会全白
        int w = Math.max(1, x1 - x0), h = Math.max(1, y1 - y0);
        target.blit(x0, y0, w, h, color(p));
    }
}
