package android.graphics;

/**
 * <code>android.graphics.Bitmap</code> 桩——二维码捕获版。
 *
 * <p>TVBox 系 jar 生成登录二维码的方式：zxing/自研编码器把内容编码成像素矩阵，
 * 经 createBitmap + setPixel(s) 写入 Bitmap。桩按尺寸记录像素（上限防呆），
 * UiBridge 把黑白矩阵上行、宿主重建图像展示——用户手机扫码，与 TVBox 交互一致。</p>
 */
public class Bitmap {

    /** 位图配置（真实为 enum，保持一致以便 spider 的 switch/compare 通过）。 */
    public enum Config {
        ALPHA_8, RGB_565, ARGB_4444, ARGB_8888, RGBA_F16, HARDWARE
    }

    public enum CompressFormat { JPEG, PNG, WEBP }

    /** 像素捕获上限（4M 像素 ~16MB int[]；二维码远小于此）。 */
    private static final int MAX_PIXELS = 4_000_000;

    private int w, h;
    private int[] pixels;   // ARGB，按行优先；null = 未捕获（尺寸 0 或超限）

    public int getWidth() { return w; }
    public int getHeight() { return h; }
    public Config getConfig() { return Config.ARGB_8888; }
    public boolean isRecycled() { return false; }
    public void recycle() { pixels = null; }

    public int getRowBytes() { return w * 4; }
    public int getByteCount() { return w * h * 4; }

    public void setPixel(int x, int y, int color) {
        if (pixels != null && x >= 0 && y >= 0 && x < w && y < h) pixels[y * w + x] = color;
    }

    public int getPixel(int x, int y) {
        return pixels != null && x >= 0 && y >= 0 && x < w && y < h ? pixels[y * w + x] : 0;
    }

    public void setPixels(int[] p, int offset, int stride, int x, int y, int width, int height) {
        if (pixels == null || p == null) return;
        for (int row = 0; row < height; row++) {
            int dstY = y + row;
            if (dstY < 0 || dstY >= h) continue;
            for (int col = 0; col < width; col++) {
                int dstX = x + col;
                if (dstX < 0 || dstX >= w) continue;
                pixels[dstY * w + dstX] = p[offset + row * stride + col];
            }
        }
    }

    public void getPixels(int[] p, int offset, int stride, int x, int y, int width, int height) {
        if (pixels == null || p == null) return;
        for (int row = 0; row < height; row++) {
            int srcY = y + row;
            for (int col = 0; col < width; col++) {
                int srcX = x + col;
                p[offset + row * stride + col] =
                    (srcX >= 0 && srcY >= 0 && srcX < w && srcY < h) ? pixels[srcY * w + srcX] : 0;
            }
        }
    }

    public boolean compress(CompressFormat format, int quality, java.io.OutputStream stream) { return false; }

    public static Bitmap createBitmap(int width, int height, Config config) {
        Bitmap b = new Bitmap();
        if (width > 0 && height > 0 && (long) width * height <= MAX_PIXELS) {
            b.w = width;
            b.h = height;
            b.pixels = new int[width * height];
            java.util.Arrays.fill(b.pixels, 0xFFFFFFFF);   // 默认白底（二维码白底黑点）
        }
        return b;
    }

    /** UiBridge 提取像素快照（未捕获/已回收返回 null）。 */
    public int[] snapshotPixels() { return pixels; }
}
