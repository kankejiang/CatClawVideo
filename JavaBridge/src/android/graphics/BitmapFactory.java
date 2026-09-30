package android.graphics;

public class BitmapFactory {
    public static Bitmap decodeByteArray(byte[] data, int offset, int length) { return null; }
    public static Bitmap decodeFile(String pathName) { return null; }

    // ── 自动补齐：jar 引用到但桩缺失的成员（android-33 签名，空实现）──
    public static android.graphics.Bitmap decodeByteArray(byte[] p0, int p1, int p2, android.graphics.BitmapFactory.Options p3) { return null; }

    public static class Options { public boolean inJustDecodeBounds; public int inSampleSize; public android.graphics.Bitmap.Config inPreferredConfig; public boolean inScaled; public int outWidth; public int outHeight; public String outMimeType; }
}