package android.content.res;

import android.graphics.Bitmap;
import android.graphics.drawable.Drawable;
import android.util.DisplayMetrics;

/**
 * <code>android.content.res.Resources</code> 桩——Guard 壳框架弹对话框必需。
 *
 * <p>原来的实现是空类：{@code Pan.showInputQRCode()} 一开就
 * {@code NoSuchMethodError: android.content.res.Resources android.content.Context.getResources()}
 * ——扫码登录整条链在第一步就断掉（2026-09-24 实测，JVM 试验台复现）。</p>
 *
 * <p>语义取舍：资源 id 在这套桌面环境里没有真表，因此
 * <b>字符串一律空串、尺寸一律 0、颜色给不透明黑、drawable/bitmap 给 null</b>。
 * 壳框架对「取不到」本来就有分支（它自己也会先判 null），所以给空值比抛异常安全；
 * 标识类查询（getIdentifier）返回 0 = 「找不到」，与 Android 语义一致。
 * 只有 {@code getResourceName} 一族按 Android 契约抛 {@link NotFoundException}。</p>
 */
public class Resources {

    private static final DisplayMetrics METRICS = new DisplayMetrics();

    public Resources() { }

    // ── 字符串 / 文本 ──
    public String getString(int resId) { return ""; }
    public String getString(int resId, Object... formatArgs) { return ""; }
    public CharSequence getText(int resId) { return ""; }
    public CharSequence getText(int resId, CharSequence def) { return def == null ? "" : def; }
    public CharSequence getQuantityText(int id, int quantity) { return ""; }
    public String getQuantityString(int id, int quantity) { return ""; }
    public String getQuantityString(int id, int quantity, Object... formatArgs) { return ""; }
    public String[] getStringArray(int resId) { return new String[0]; }

    // ── 标量 / 数组 ──
    public int[] getIntArray(int resId) { return new int[0]; }
    public int getInteger(int resId) { return 0; }
    public boolean getBoolean(int resId) { return false; }

    // ── 颜色 / 尺寸 ──
    public int getColor(int resId) { return 0xFF000000; }
    public int getColor(int resId, Theme theme) { return 0xFF000000; }
    public ColorStateList getColorStateList(int resId) { return new ColorStateList(); }
    public ColorStateList getColorStateList(int resId, Theme theme) { return new ColorStateList(); }
    public float getDimension(int resId) { return 0f; }
    public int getDimensionPixelSize(int resId) { return 0; }
    public int getDimensionPixelOffset(int resId) { return 0; }
    public float getFraction(int id, int base, int pbase) { return 0f; }

    // ── 图形 ──
    public Drawable getDrawable(int resId) { return null; }
    public Drawable getDrawable(int resId, Theme theme) { return null; }
    public Drawable getDrawableForDensity(int resId, int density) { return null; }
    public Bitmap getBitmap(int resId) { return null; }

    // ── 环境 ──
    public DisplayMetrics getDisplayMetrics() { return METRICS; }
    public Configuration getConfiguration() { return new Configuration(); }
    public Theme newTheme() { return new Theme(); }
    public void updateConfiguration(Configuration config, DisplayMetrics metrics) { }
    public void flushLayoutCache() { }

    // ── 标识查询（0 = 找不到，与 Android 一致）──
    public int getIdentifier(String name, String defType, String defPackage) { return 0; }
    public String getResourceName(int resId) throws NotFoundException { throw new NotFoundException("res 0x" + Integer.toHexString(resId)); }
    public String getResourceEntryName(int resId) throws NotFoundException { return ""; }
    public String getResourcePackageName(int resId) throws NotFoundException { return ""; }
    public String getResourceTypeName(int resId) throws NotFoundException { return ""; }

    /** Android 里是 Resources 的内部异常类，壳框架会 catch 它。 */
    public static class NotFoundException extends RuntimeException {
        public NotFoundException(String name) { super(name); }
        public NotFoundException() { super(); }
        public NotFoundException(String name, Exception cause) { super(name, cause); }
    }

    /** Theme 桩：壳框架弹窗时会套主题取色（桌面无主题表，一律给默认值）。 */
    public static class Theme {
        public Theme() { }
        public void applyStyle(int resId, boolean force) { }
        public void setTo(Theme other) { }
        public Drawable getDrawable(int resId) { return null; }
        public int getColor(int resId) { return 0xFF000000; }
        public ColorStateList getColorStateList(int resId) { return new ColorStateList(); }
        public float getDimension(int resId) { return 0f; }
        public int getDimensionPixelSize(int resId) { return 0; }
    }
}
