package android.content;

import android.content.pm.ApplicationInfo;
import java.io.File;

/** 桌面环境的 Context 实现（stub）：spider jar 在 PC 上运行所需的 android.content.Context。 */
public class Context {

    public static final int MODE_PRIVATE = 0;
    public static final int MODE_WORLD_READABLE = 1;
    public static final int MODE_WORLD_WRITEABLE = 2;
    public static final int MODE_APPEND = 3;

    private final File baseDir = new File(System.getProperty("user.dir"), "data");

    public File getFilesDir() { File f = new File(baseDir, "files"); f.mkdirs(); return f; }
    public File getCacheDir() { File f = new File(baseDir, "cache"); f.mkdirs(); return f; }
    public File getExternalFilesDir(String type) { File f = new File(baseDir, "external" + (type == null ? "" : "/" + type)); f.mkdirs(); return f; }
    public File getDir(String name, int mode) { File f = new File(baseDir, name); f.mkdirs(); return f; }
    public File getNoBackupFilesDir() { return getFilesDir(); }
    public File getExternalCacheDir() { return getExternalFilesDir("cache"); }

    public String getPackageName() { return "com.catclaw.video"; }
    public ApplicationInfo getApplicationInfo() { return new ApplicationInfo(); }

    public SharedPreferences getSharedPreferences(String name, int mode) { return new MemPrefs(); }
    public SharedPreferences getSharedPreferences(File file, int mode) { return new MemPrefs(); }
    public boolean deleteSharedPreferences(String name) { return true; }

    public String getString(int resId) { return ""; }
    public String getString(int resId, Object... formatArgs) { return ""; }

    public Object getSystemService(String name) { return null; }
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
