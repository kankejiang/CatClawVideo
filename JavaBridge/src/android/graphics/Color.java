package android.graphics;

/** Color 桩：常量与颜色换算按真实语义实现（spider 里常用来拼色值）。 */
public class Color {

    public static final int BLACK = 0xFF000000;
    public static final int DKGRAY = 0xFF444444;
    public static final int GRAY = 0xFF888888;
    public static final int LTGRAY = 0xFFCCCCCC;
    public static final int WHITE = 0xFFFFFFFF;
    public static final int RED = 0xFFFF0000;
    public static final int GREEN = 0xFF00FF00;
    public static final int BLUE = 0xFF0000FF;
    public static final int YELLOW = 0xFFFFFF00;
    public static final int CYAN = 0xFF00FFFF;
    public static final int MAGENTA = 0xFFFF00FF;
    public static final int TRANSPARENT = 0;

    public static int alpha(int color) { return color >>> 24; }
    public static int red(int color) { return (color >> 16) & 0xFF; }
    public static int green(int color) { return (color >> 8) & 0xFF; }
    public static int blue(int color) { return color & 0xFF; }
    public static int rgb(int red, int green, int blue) { return 0xFF000000 | (red << 16) | (green << 8) | blue; }
    public static int argb(int alpha, int red, int green, int blue) {
        return (alpha << 24) | (red << 16) | (green << 8) | blue;
    }
    public static int parseColor(String colorString) { return parse(colorString); }

    /** 支持 #RGB / #ARGB / #RRGGBB / #AARRGGBB（与真实 parseColor 的十六进制分支一致）。 */
    public static int parse(String colorString) {
        if (colorString == null || colorString.isEmpty() || colorString.charAt(0) != '#') {
            throw new IllegalArgumentException("Unknown color: " + colorString);
        }
        String hex = colorString.substring(1);
        long color;
        try { color = Long.parseLong(hex, 16); }
        catch (NumberFormatException e) { throw new IllegalArgumentException("Unknown color: " + colorString); }
        switch (hex.length()) {
            case 3: {   // #RGB
                long r = (color >> 8) & 0xF, g = (color >> 4) & 0xF, b = color & 0xF;
                return (int) (0xFF000000L | (r << 20) | (r << 16) | (g << 12) | (g << 8) | (b << 4) | b);
            }
            case 4: {   // #ARGB
                long a = (color >> 12) & 0xF, r = (color >> 8) & 0xF, g = (color >> 4) & 0xF, b = color & 0xF;
                return (int) ((a << 28) | (a << 24) | (r << 20) | (r << 16) | (g << 12) | (g << 8) | (b << 4) | b);
            }
            case 6: return (int) (color | 0xFF000000L);   // #RRGGBB
            case 8: return (int) color;                   // #AARRGGBB
            default: throw new IllegalArgumentException("Unknown color: " + colorString);
        }
    }
}
