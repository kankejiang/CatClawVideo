package android.graphics;

public class Bitmap {

    /** 位图配置（真实为 enum，保持一致以便 spider 的 switch/compare 通过）。 */
    public enum Config {
        ALPHA_8, RGB_565, ARGB_4444, ARGB_8888, RGBA_F16, HARDWARE
    }

    public int getWidth() { return 0; }
    public int getHeight() { return 0; }
    public Config getConfig() { return Config.ARGB_8888; }
    public boolean isRecycled() { return false; }
    public void recycle() { }
    public boolean compress(CompressFormat format, int quality, java.io.OutputStream stream) { return false; }
    public static Bitmap createBitmap(int width, int height, Config config) { return new Bitmap(); }

    public enum CompressFormat { JPEG, PNG, WEBP }
}