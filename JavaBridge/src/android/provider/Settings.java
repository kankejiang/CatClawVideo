package android.provider;

/**
 * <code>android.provider.Settings</code> 桩。
 *
 * <p>⚠ 2026-10-01 改动：此前 <code>Secure.getString</code> **恒返回 null**。壳会读设备身份
 * （真机 spUtils 里的 <code>hide_appgz_android_id</code> 就是它，16 字符），拿到 null 后
 * 它报给服务端的设备指纹就是残缺的 —— 这是「真机行、虚拟机不行」类问题的常见根因：
 * 服务端认了扫码（toast 还会显示会员），但取账号信息那一步被拒。</p>
 *
 * <p>取值策略：与真机（同一台 Xiaomi Mi 11 / venus）保持一致，且与已同步的 prefs 对得上；
 * 同一进程内多次读取必须稳定（壳会做指纹校验，飘了会重新注册）。</p>
 */
public class Settings {

    /** 真机 ANDROID_ID（与 spUtils.hide_appgz_android_id 一致）。 */
    public static final String CANON_ANDROID_ID = "1c65b4c5f71bf03c";

    private static String value(String name) {
        if (name == null) return null;
        switch (name) {
            case "android_id":                   return CANON_ANDROID_ID;
            case "bluetooth_name":               return "CatClaw";
            case "device_name":                  return "CatClaw";
            case "adb_enabled":                  return "0";
            case "development_settings_enabled": return "0";
            case "time_12_24":                   return "24";
            default:                             return null;
        }
    }

    public static final class Secure {
        public static final String ANDROID_ID = "android_id";
        public static final String BLUETOOTH_NAME = "bluetooth_name";
        public static final String DEVICE_NAME = "device_name";

        public static String getString(android.content.ContentResolver resolver, String name) {
            String v = value(name);
            java.lang.System.err.println("[settings] Secure.getString(" + name + ") → " + v);
            return v;
        }

        public static String getString(android.content.ContentResolver resolver, String name, String def) {
            String v = value(name);
            return v != null ? v : def;
        }

        public static int getInt(android.content.ContentResolver resolver, String name, int def) {
            String v = value(name);
            try { return v == null ? def : Integer.parseInt(v); } catch (Throwable t) { return def; }
        }

        public static long getLong(android.content.ContentResolver resolver, String name, long def) {
            String v = value(name);
            try { return v == null ? def : Long.parseLong(v); } catch (Throwable t) { return def; }
        }

        public static float getFloat(android.content.ContentResolver resolver, String name, float def) {
            String v = value(name);
            try { return v == null ? def : Float.parseFloat(v); } catch (Throwable t) { return def; }
        }

        public static boolean putString(android.content.ContentResolver resolver, String name, String val) {
            java.lang.System.err.println("[settings] Secure.putString(" + name + ") 忽略");
            return true;
        }

        public static boolean putInt(android.content.ContentResolver resolver, String name, int val) { return true; }
        public static boolean putLong(android.content.ContentResolver resolver, String name, long val) { return true; }
    }

    public static final class System {
        public static String getString(android.content.ContentResolver resolver, String name) {
            java.lang.System.err.println("[settings] System.getString(" + name + ") → null");
            return null;
        }
        public static String getString(android.content.ContentResolver resolver, String name, String def) { return def; }
        public static int getInt(android.content.ContentResolver resolver, String name, int def) { return def; }
        public static long getLong(android.content.ContentResolver resolver, String name, long def) { return def; }
        public static boolean putString(android.content.ContentResolver resolver, String name, String val) { return true; }
        public static boolean putInt(android.content.ContentResolver resolver, String name, int val) { return true; }
    }

    public static final class Global {
        public static String getString(android.content.ContentResolver resolver, String name) {
            java.lang.System.err.println("[settings] Global.getString(" + name + ") → null");
            return null;
        }
        public static String getString(android.content.ContentResolver resolver, String name, String def) { return def; }
        public static int getInt(android.content.ContentResolver resolver, String name, int def) { return def; }
        public static long getLong(android.content.ContentResolver resolver, String name, long def) { return def; }
        public static boolean putString(android.content.ContentResolver resolver, String name, String val) { return true; }
        public static boolean putInt(android.content.ContentResolver resolver, String name, int val) { return true; }
    }
}
