package android.preference;

import android.content.Context;
import android.content.SharedPreferences;

/**
 * <code>android.preference.PreferenceManager</code> 桩。
 *
 * <p>该类在 Android 10 起已废弃移出（但仍能被老代码引用）；TVBox 系 jar 里大量使用
 * <c>PreferenceManager.getDefaultSharedPreferences(ctx)</c>。宿主不提供 →
 * <c>ClassNotFoundException: android.preference.PreferenceManager</c>
 * （实测 2026-09-16：荐片 <c>playerContent</c> 就死在这）。</p>
 *
 * <p>实现走宿主自己的 SharedPreferences（内存实现），语义上等价于「读写本机默认配置」。</p>
 */
public class PreferenceManager {

    private static final String DEFAULT_NAME = "default";

    private final SharedPreferences sharedPreferences;

    private PreferenceManager(SharedPreferences sp) { this.sharedPreferences = sp; }

    public static SharedPreferences getDefaultSharedPreferences(Context context) {
        return context == null
                ? new Context.MemPrefs()
                : context.getSharedPreferences(DEFAULT_NAME, 0);
    }

    public static String getDefaultSharedPreferencesName(Context context) { return DEFAULT_NAME; }

    public static int getDefaultSharedPreferencesMode() { return 0; }

    public static PreferenceManager getDefaultSharedPreferencesInstance(Context context) {
        return new PreferenceManager(getDefaultSharedPreferences(context));
    }

    public SharedPreferences getSharedPreferences() { return sharedPreferences; }

    public SharedPreferences.Editor getEditor() {
        return sharedPreferences == null ? null : sharedPreferences.edit();
    }

    public static void setDefaultValues(Context context, int resId, boolean readAgain) { }

    public static void setDefaultValues(Context context, String sharedPreferencesName,
                                        int sharedPreferencesMode, int resId, boolean readAgain) { }
}
