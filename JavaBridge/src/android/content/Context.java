package android.content;

import android.content.pm.ApplicationInfo;
import java.io.File;

/** 桌面环境的 Context 实现（stub）：spider jar 在 PC 上运行所需的 android.content.Context。 */
public class Context {

    public static final int MODE_PRIVATE = 0;
    public static final int MODE_WORLD_READABLE = 1;
    public static final int MODE_WORLD_WRITEABLE = 2;
    public static final int MODE_APPEND = 3;

    /** 系统服务名（TVBox 系爬虫用 {@code getSystemService(Context.WIFI_SERVICE)} 取本机 IP）。 */
    public static final String WIFI_SERVICE = "wifi";
    public static final String CONNECTIVITY_SERVICE = "connectivity";
    public static final String ACTIVITY_SERVICE = "activity";
    public static final String NOTIFICATION_SERVICE = "notification";

    private final File baseDir = new File(System.getProperty("user.dir"), "data");

    public File getFilesDir() { File f = new File(baseDir, "files"); f.mkdirs(); return f; }
    public File getCacheDir() { File f = new File(baseDir, "cache"); f.mkdirs(); return f; }
    public File getExternalFilesDir(String type) { File f = new File(baseDir, "external" + (type == null ? "" : "/" + type)); f.mkdirs(); return f; }
    public File getDir(String name, int mode) { File f = new File(baseDir, name); f.mkdirs(); return f; }
    public File getNoBackupFilesDir() { return getFilesDir(); }
    public File getExternalCacheDir() { return getExternalFilesDir("cache"); }

    // ═══════════════════════════════════════════════════════════════════
    //  Guard 壳框架链路（2026-09-24 实测，缺一个就 NoSuchMethodError 断链）：
    //    · InitOrigin.init(ctx)          → Context.getDatabasePath(String)
    //    · Pan.showInputQRCode()         → Context.getResources()  ← 扫码登录第一步
    //  两者原来都没实现，被 InitOrigin/壳的 try/catch 吞掉，表现为「点登入自己网盘没反应」。
    // ═══════════════════════════════════════════════════════════════════

    /** 数据库目录（真实 Android 为 /data/data/<pkg>/databases/<name>）。 */
    public File getDatabasePath(String name) {
        File f = new File(getDir("databases", 0), name);
        File p = f.getParentFile();
        if (p != null) p.mkdirs();
        return f;
    }

    public File getDataDir() { return baseDir; }
    public File getCodeCacheDir() { return getCacheDir(); }
    public File getObbDir() { return getExternalFilesDir("obb"); }
    public File[] getExternalFilesDirs(String type) { return new File[]{ getExternalFilesDir(type) }; }
    public File[] getExternalCacheDirs() { return new File[]{ getExternalCacheDir() }; }
    public File[] getExternalMediaDirs() { return new File[]{ getExternalFilesDir("media") }; }

    public java.io.FileOutputStream openFileOutput(String name, int mode) throws java.io.FileNotFoundException {
        return new java.io.FileOutputStream(new File(getFilesDir(), name));
    }

    public java.io.FileInputStream openFileInput(String name) throws java.io.FileNotFoundException {
        return new java.io.FileInputStream(new File(getFilesDir(), name));
    }

    public String[] fileList() {
        String[] r = getFilesDir().list();
        return r == null ? new String[0] : r;
    }

    /** 资源表桩：字符串空串、尺寸 0、drawable null（详见 {@link android.content.res.Resources}）。 */
    public android.content.res.Resources getResources() { return new android.content.res.Resources(); }
    public android.content.res.Resources.Theme getTheme() { return new android.content.res.Resources.Theme(); }

    public String getPackageResourcePath() { return ""; }
    public String getPackageCodePath() { return ""; }
    public int checkCallingOrSelfPermission(String permission) { return 0; }

    public String getPackageName() { return "com.catclaw.video"; }
    public ApplicationInfo getApplicationInfo() { return new ApplicationInfo(); }

    public SharedPreferences getSharedPreferences(String name, int mode) { return new MemPrefs(); }
    public SharedPreferences getSharedPreferences(File file, int mode) { return new MemPrefs(); }
    public boolean deleteSharedPreferences(String name) { return true; }

    public String getString(int resId) { return ""; }
    public String getString(int resId, Object... formatArgs) { return ""; }

    /** ContentResolver：爬虫读系统设置/媒体库时用（实测 2026-09-16「瓜子」站因缺此方法整站挂）。 */
    public android.content.ContentResolver getContentResolver() {
        return new android.content.ContentResolver();
    }

    /**
     * PackageManager：TVBox 系爬虫用它读自身包名/签名做校验（<c>merge.cn.F5</c> 在端口调整线程里调）。
     * 缺这个 getter 会抛 <c>NoSuchMethodError: android.content.pm.PackageManager
     * android.content.Context.getPackageManager()</c> → 整条后台线程死掉。
     */
    public android.content.pm.PackageManager getPackageManager() {
        return new android.content.pm.PackageManager();
    }

    /** 主线程 Looper（部分爬虫用它 post 回调）。 */
    public android.os.Looper getMainLooper() {
        return android.os.Looper.getMainLooper();
    }

    /**
     * 系统服务：只实现爬虫真正会用的那几个（原来一律返回 null →
     * {@code WifiManager.getConnectionInfo().getIpAddress()} 直接 NPE，
     * 导致 TVBox 系 ProxyOrigin 取不到本机 IP，本地代理地址拼不出来）。
     */
    public Object getSystemService(String name) {
        if (name == null) return null;
        switch (name) {
            case WIFI_SERVICE:
                return new android.net.wifi.WifiManager();
            case "window":
                return new android.view.WindowManager();
            default:
                return null;
        }
    }
    public String getSystemServiceName(Class<?> serviceClass) { return null; }

    public java.lang.ClassLoader getClassLoader() { return Context.class.getClassLoader(); }
    public boolean isRestricted() { return false; }
    public int checkSelfPermission(String permission) { return 0; }
    public Context getApplicationContext() { return this; }

    public static class MemPrefs implements SharedPreferences {
        @Override public String getString(String key, String defValue) { Object v = SharedPreferences.DATA.get(key); return v instanceof String ? (String) v : defValue; }
        @Override public java.util.Set<String> getStringSet(String key, java.util.Set<String> defValue) { Object v = SharedPreferences.DATA.get(key); return v instanceof java.util.Set ? (java.util.Set<String>) v : defValue; }
        @Override public int getInt(String key, int defValue) { Object v = SharedPreferences.DATA.get(key); return v instanceof Integer ? (Integer) v : defValue; }
        @Override public long getLong(String key, long defValue) { Object v = SharedPreferences.DATA.get(key); return v instanceof Long ? (Long) v : defValue; }
        @Override public float getFloat(String key, float defValue) { Object v = SharedPreferences.DATA.get(key); return v instanceof Float ? (Float) v : defValue; }
        @Override public boolean getBoolean(String key, boolean defValue) { Object v = SharedPreferences.DATA.get(key); return v instanceof Boolean ? (Boolean) v : defValue; }
        @Override public boolean contains(String key) { return SharedPreferences.DATA.containsKey(key); }
        @Override public SharedPreferences.Editor edit() { return new MemEditor(); }
        @Override public void registerOnSharedPreferenceChangeListener(OnSharedPreferenceChangeListener l) { }
        @Override public void unregisterOnSharedPreferenceChangeListener(OnSharedPreferenceChangeListener l) { }
    }
    public static class MemEditor implements SharedPreferences.Editor {
        @Override public SharedPreferences.Editor putString(String k, String v) { SharedPreferences.DATA.put(k, v); return this; }
        @Override public SharedPreferences.Editor putStringSet(String k, java.util.Set<String> v) { SharedPreferences.DATA.put(k, v); return this; }
        @Override public SharedPreferences.Editor putInt(String k, int v) { SharedPreferences.DATA.put(k, v); return this; }
        @Override public SharedPreferences.Editor putLong(String k, long v) { SharedPreferences.DATA.put(k, v); return this; }
        @Override public SharedPreferences.Editor putFloat(String k, float v) { SharedPreferences.DATA.put(k, v); return this; }
        @Override public SharedPreferences.Editor putBoolean(String k, boolean v) { SharedPreferences.DATA.put(k, v); return this; }
        @Override public SharedPreferences.Editor remove(String k) { SharedPreferences.DATA.remove(k); return this; }
        @Override public SharedPreferences.Editor clear() { SharedPreferences.DATA.clear(); return this; }
        @Override public boolean commit() { return true; }
        @Override public void apply() { }
    }
}
