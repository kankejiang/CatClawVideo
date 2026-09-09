package android.util;
public final class Log {
    public static int d(String tag, String msg) { System.err.println("[Log.d] " + tag + ": " + msg); return 0; }
    public static int i(String tag, String msg) { System.err.println("[Log.i] " + tag + ": " + msg); return 0; }
    public static int w(String tag, String msg) { System.err.println("[Log.w] " + tag + ": " + msg); return 0; }
    public static int e(String tag, String msg) { System.err.println("[Log.e] " + tag + ": " + msg); return 0; }
    public static int e(String tag, String msg, Throwable t) { System.err.println("[Log.e] " + tag + ": " + msg + " / " + t); return 0; }
    public static String getStackTraceString(Throwable t) { return String.valueOf(t); }
    public static boolean isLoggable(String tag, int level) { return false; }
}
