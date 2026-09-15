package android.text.format;

import android.content.Context;

/** Formatter 桩：formatFileSize 按真实语义（1024 进制 + 单位）实现，spider 常用来显示体积。 */
public final class Formatter {

    public static final int FLAG_SHORTER = 1;
    public static final int FLAG_CALCULATE_ROUNDED = 2;

    private static final String[] UNITS = { "B", "KB", "MB", "GB", "TB", "PB" };

    public static String formatFileSize(Context context, long number) {
        return formatFileSize(context, number, 0);
    }

    public static String formatFileSize(Context context, long number, int flags) {
        if (number < 0) return "";
        double value = number;
        int unit = 0;
        while (value >= 1024 && unit < UNITS.length - 1) { value /= 1024; unit++; }
        return unit == 0 ? number + "B" : String.format(java.util.Locale.US, "%.1f%s", value, UNITS[unit]);
    }

    public static String formatIpAddress(int ipv4Address) {
        return (ipv4Address & 0xFF) + "." + ((ipv4Address >> 8) & 0xFF) + "."
                + ((ipv4Address >> 16) & 0xFF) + "." + ((ipv4Address >> 24) & 0xFF);
    }

    public static String formatShortElapsedTime(Context context, long millis) {
        long seconds = millis / 1000;
        long minutes = seconds / 60;
        long hours = minutes / 60;
        if (hours > 0) return hours + ":" + String.format(java.util.Locale.US, "%02d:%02d", minutes % 60, seconds % 60);
        return minutes + ":" + String.format(java.util.Locale.US, "%02d", seconds % 60);
    }
}
