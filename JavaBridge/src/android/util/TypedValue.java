package android.util;

/**
 * <code>android.util.TypedValue</code> 桩。
 *
 * <p>⚠️ <b>这些方法必须是 static</b>：真实 Android 里它们就是静态方法，jar 以
 * {@code invokestatic} 调用；写成实例方法会抛
 * {@code IncompatibleClassChangeError: Expected static method}
 * （2026-09-24 实测：{@code Pan.showInputQRCode()} 算二维码尺寸时踩到）。</p>
 *
 * <p>桌面无真实密度换算表，按 CSS 惯例近似：dp→px 直接用 value（1:1），其余单位原样返回。
 * 二维码弹窗只关心「多大」，取合理值即可。</p>
 */
public class TypedValue {

    // 单位常量（jar 里常按名引用，缺了会 NoSuchFieldError）
    public static final int COMPLEX_UNIT_PX = 0;
    public static final int COMPLEX_UNIT_DIP = 1;
    public static final int COMPLEX_UNIT_SP = 2;
    public static final int COMPLEX_UNIT_PT = 3;
    public static final int COMPLEX_UNIT_IN = 4;
    public static final int COMPLEX_UNIT_MM = 5;

    public static final int TYPE_NULL = 0x00;
    public static final int TYPE_REFERENCE = 0x01;
    public static final int TYPE_ATTRIBUTE = 0x02;
    public static final int TYPE_STRING = 0x03;
    public static final int TYPE_FLOAT = 0x04;
    public static final int TYPE_DIMENSION = 0x05;
    public static final int TYPE_FRACTION = 0x06;
    public static final int TYPE_INT_DEC = 0x10;
    public static final int TYPE_INT_HEX = 0x11;
    public static final int TYPE_INT_BOOLEAN = 0x12;

    public int type;
    public float data;
    public CharSequence string;
    public int resourceId;

    public TypedValue() { }

    /** 单位换算（1:1 近似；密度链在桌面环境里没有意义）。 */
    public static float applyDimension(int unit, float value, DisplayMetrics metrics) {
        return value;
    }

    /** 新版 Android 的 int 版（jar 可能调到）。 */
    public static int applyDimensionInt(int unit, float value, DisplayMetrics metrics) {
        return (int) value;
    }

    public static float complexToDimension(int data, DisplayMetrics metrics) {
        return complexToFloat(data);
    }

    public static int complexToDimensionPixelSize(int data, DisplayMetrics metrics) {
        return (int) complexToFloat(data);
    }

    public static float complexToFloat(int complex) {
        return (complex & 0xFFFFFF00) * (1.0f / 256.0f);
    }

    public static int complexToInt(int complex) {
        return complex & 0xFFFFFF00;
    }

    public static float complexToFraction(int data, float base, float pbase) {
        return complexToFloat(data);
    }

    public void setTo(TypedValue other) {
        if (other == null) return;
        type = other.type; data = other.data; string = other.string; resourceId = other.resourceId;
    }

    public final CharSequence coerceToString() { return string == null ? "" : string; }

    public final float getFloat() { return data; }

    public static String coerceToString(int type, int data) { return String.valueOf(data); }

    @Override public String toString() { return "TypedValue{t=" + type + " d=" + data + "}"; }
}
