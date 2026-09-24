package android.graphics;

/**
 * <code>android.graphics.Typeface</code> 桩：Guard 壳框架给对话框文字设字体时用
 * （{@code TextView.setTypeface(Typeface, int)}，2026-09-24 实测缺它断链）。
 * 桌面只关心「别抛异常」，样式位保留下来但不参与渲染。
 */
public class Typeface {

    public static final int NORMAL = 0;
    public static final int BOLD = 1;
    public static final int ITALIC = 2;
    public static final int BOLD_ITALIC = 3;

    public static final Typeface DEFAULT = new Typeface("sans-serif", NORMAL);
    public static final Typeface DEFAULT_BOLD = new Typeface("sans-serif", BOLD);
    public static final Typeface SANS_SERIF = new Typeface("sans-serif", NORMAL);
    public static final Typeface SERIF = new Typeface("serif", NORMAL);
    public static final Typeface MONOSPACE = new Typeface("monospace", NORMAL);

    private final String family;
    private final int style;

    public Typeface(String family, int style) {
        this.family = family;
        this.style = style;
    }

    public int getStyle() { return style; }

    public boolean isBold() { return (style & BOLD) != 0; }

    public boolean isItalic() { return (style & ITALIC) != 0; }

    @Override public String toString() { return "Typeface{" + family + ", style=" + style + "}"; }

    public static Typeface create(String familyName, int style) { return new Typeface(familyName, style); }

    public static Typeface create(Typeface family, int style) {
        return new Typeface(family == null ? "sans-serif" : family.family, style);
    }

    /** 字体文件在桌面环境不存在，返回 null（调用方通常已有 null 分支）。 */
    public static Typeface createFromFile(String path) { return null; }

    public static Typeface createFromFile(java.io.File path) { return null; }

    public static Typeface defaultFromStyle(int style) {
        return style == BOLD ? DEFAULT_BOLD : DEFAULT;
    }
}
