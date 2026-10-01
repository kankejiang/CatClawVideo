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
    private long bornMs;    // 创建时刻（兜底扫描按新鲜度筛）

    /**
     * 最近创建的位图（有界）。用途：jar 用**自定义 View** 画二维码时不走
     * {@code ImageView.setImageBitmap}，UiBridge 在视图树里找不到图 —— 这时按
     * 「近 15s 内、正方形、够大、有墨」挑一张兜底上行（2026-09-30 实测：
     * 视图树只有一个自定义 View 节点、qr=无）。
     */
    private static final java.util.List<Bitmap> RECENT =
            java.util.Collections.synchronizedList(new java.util.ArrayList<>());
    private static final int RECENT_MAX = 12;
    private static int created;   // createBitmap 调用计数（诊断日志限流）

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

    /**
     * 记录式画布用的实心块：越界自动裁掉，全透明（argb 全 0）不覆盖已有像素。
     * 与 {@link #setPixel}/{@link #setPixels} 同一份缓冲，所以「自定义 View 在 onDraw 里
     * drawRect 画的二维码」和「ImageView 收到的位图」走同一条上行路（UiBridge.qrJson）。
     */
    public void blit(int x, int y, int bw, int bh, int color) {
        if (pixels == null || bw <= 0 || bh <= 0 || (color >>> 24) == 0) return;
        int x0 = Math.max(x, 0), y0 = Math.max(y, 0);
        int x1 = Math.min(x + bw, w), y1 = Math.min(y + bh, h);
        if (x0 >= x1 || y0 >= y1) return;   // 完全越出画布：Arrays.fill 对 from>to 会抛 IllegalArgumentException
        for (int row = y0; row < y1; row++)
            java.util.Arrays.fill(pixels, row * w + x0, row * w + x1, color);
    }

    /** 逐行贴一段像素（{@code Canvas.drawBitmap} 用）。 */
    public void blit(int x, int y, int bw, int bh, int[] src) {
        if (pixels == null || src == null || bw <= 0 || bh <= 0) return;
        for (int row = 0; row < bh; row++) {
            int dstY = y + row;
            if (dstY < 0 || dstY >= h) continue;
            for (int col = 0; col < bw; col++) {
                int dstX = x + col;
                int si = row * bw + col;
                if (dstX < 0 || dstX >= w || si >= src.length) continue;
                int c = src[si];
                if ((c >>> 24) != 0) pixels[dstY * w + dstX] = c;
            }
        }
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
        if ((width == height && width >= 64) || width >= 200 || height >= 200)
            System.err.println("[ui] Bitmap.setPixels " + width + "x" + height + " stride=" + stride);
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

    /**
     * 真的编码并写流。旧实现直接 {@code return false} —— 壳的扫码链路正好用它：
     * zxing 生成 720×720 位图（实测 {@code Bitmap.createBitmap 720x720} + {@code setPixels}）
     * 后 {@code compress(PNG, out)} 把图写进登录页/文件，桩返回 false ⇒ 码“生成了但没出去”，
     * 宿主既没有对话框也没有图（2026-10-01「点了扫码登录只进推送页」的定位过程）。
     * <p>guest 里没有 {@code java.awt}，所以手写 PNG：8bit 真彩 RGB、每行 filter 0、
     * {@link java.util.zip.Deflater} 出 IDAT、{@link java.util.zip.CRC32} 出分块 CRC。</p>
     */
    public boolean compress(CompressFormat format, int quality, java.io.OutputStream stream) {
        if (pixels == null || stream == null) return false;
        try {
            byte[] png = encodePng();
            stream.write(png);
            stream.flush();
            System.err.println("[ui] Bitmap.compress PNG " + w + "x" + h + " → " + png.length + "B");
            return true;
        } catch (Throwable t) {
            System.err.println("[ui] Bitmap.compress 失败: " + t);
            return false;
        }
    }

    private byte[] encodePng() throws java.io.IOException {
        java.io.ByteArrayOutputStream idat = new java.io.ByteArrayOutputStream();
        int stride = w * 3;
        byte[] raw = new byte[h * (stride + 1)];
        for (int y = 0; y < h; y++) {
            int row = y * (stride + 1);
            raw[row] = 0;
            for (int x = 0; x < w; x++) {
                int c = pixels[y * w + x];
                raw[row + 1 + x * 3] = (byte) (c >> 16);
                raw[row + 2 + x * 3] = (byte) (c >> 8);
                raw[row + 3 + x * 3] = (byte) c;
            }
        }
        java.util.zip.Deflater def = new java.util.zip.Deflater(6);
        try {
            def.setInput(raw);
            def.finish();
            byte[] buf = new byte[16384];
            while (!def.finished()) idat.write(buf, 0, def.deflate(buf));
        } finally {
            def.end();
        }
        java.io.ByteArrayOutputStream out = new java.io.ByteArrayOutputStream();
        out.write(new byte[]{(byte) 0x89, 'P', 'N', 'G', '\r', '\n', 0x1A, '\n'});
        byte[] ihdr = new byte[]{(byte) (w >> 24), (byte) (w >> 16), (byte) (w >> 8), (byte) w,
                (byte) (h >> 24), (byte) (h >> 16), (byte) (h >> 8), (byte) h,
                8, 2, 0, 0, 0};            // bitdepth 8, colortype 2=真彩 RGB
        chunk(out, "IHDR", ihdr);
        chunk(out, "IDAT", idat.toByteArray());
        chunk(out, "IEND", new byte[0]);
        return out.toByteArray();
    }

    private static void chunk(java.io.ByteArrayOutputStream out, String type, byte[] data)
            throws java.io.IOException {
        byte[] t = type.getBytes("US-ASCII");
        int n = data.length;
        out.write(new byte[]{(byte) (n >> 24), (byte) (n >> 16), (byte) (n >> 8), (byte) n});
        int mark = out.size();
        out.write(t);
        out.write(data);
        java.util.zip.CRC32 crc = new java.util.zip.CRC32();
        crc.update(out.toByteArray(), mark, t.length + n);
        long c = crc.getValue();
        out.write(new byte[]{(byte) (c >> 24), (byte) (c >> 16), (byte) (c >> 8), (byte) c});
    }

    /**
     * 是否把新建位图记进「近 15s 候选池」。
     * <p>渲染层（AlertDialog 驱动壳的 onDraw 到录制画布）必须临时关掉它：那张画布自己也是
     * 480×480 正方形，兜底层会把它当成壳的二维码捞出来上行 —— 2026-10-01 实测第二轮回读命中
     * 「qr 兜底命中 480x480」，上行的是我们全白的画布（宿主拿到一张空白二维码）。</p>
     */
    public static boolean trackRecent = true;

    public static Bitmap createBitmap(int width, int height, Config config) {
        Bitmap b = new Bitmap();
        if (width > 0 && height > 0 && (long) width * height <= MAX_PIXELS) {
            b.w = width;
            b.h = height;
            b.pixels = new int[width * height];
            java.util.Arrays.fill(b.pixels, 0xFFFFFFFF);   // 默认白底（二维码白底黑点）
        }
        b.bornMs = System.currentTimeMillis();
        // 诊断留痕（限流 40 条）：扫码链路到底建没建位图、建了多大，一眼可见
        if (trackRecent && created++ < 80) {
            System.err.println("[ui] Bitmap.createBitmap " + width + "x" + height + " " + config
                    + (width == height ? " square" : "") + (width >= 200 || height >= 200 ? " large" : ""));
        }
        if (trackRecent) {
            synchronized (RECENT) {
                RECENT.add(b);
                while (RECENT.size() > RECENT_MAX) RECENT.remove(0);
            }
        }
        return b;
    }

    /** 位图是否有「墨」：既有明显暗点又有明显亮点（纯色图不要，二维码一定有）。 */
    public boolean hasInk() {
        if (pixels == null) return false;
        boolean dark = false, light = false;
        for (int i = 0; i < pixels.length; i++) {
            int v = pixels[i] & 0xFF;
            if (v < 120) dark = true; else if (v > 200) light = true;
            if (dark && light) return true;
        }
        return false;
    }

    /**
     * 二维码兜底候选：近 15s 内创建、正方形、≥100 的最新一张（不查墨——
     * jar 的 zxing 循环先 createBitmap(白) 再 setPixels(黑码)，创建瞬间全白）。
     */
    public static Bitmap recentQrCandidate() {
        long now = System.currentTimeMillis();
        synchronized (RECENT) {
            for (int i = RECENT.size() - 1; i >= 0; i--) {
                Bitmap b = RECENT.get(i);
                if (b == null || b.pixels == null) continue;
                if (now - b.bornMs > 15000L) continue;
                if (b.w < 100 || b.w != b.h) continue;
                return b;
            }
        }
        return null;
    }

    /** UiBridge 提取像素快照（未捕获/已回收返回 null）。 */
    public int[] snapshotPixels() { return pixels; }
}
