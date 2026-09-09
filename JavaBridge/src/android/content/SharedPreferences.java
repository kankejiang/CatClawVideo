package android.content;
import java.util.HashMap;
import java.util.Map;
import java.util.Set;
public interface SharedPreferences {
    Map<String, Object> DATA = new HashMap<>();
    String getString(String key, String defValue);
    Set<String> getStringSet(String key, Set<String> defValues);
    int getInt(String key, int defValue);
    long getLong(String key, long defValue);
    float getFloat(String key, float defValue);
    boolean getBoolean(String key, boolean defValue);
    boolean contains(String key);
    Editor edit();
    void registerOnSharedPreferenceChangeListener(OnSharedPreferenceChangeListener l);
    void unregisterOnSharedPreferenceChangeListener(OnSharedPreferenceChangeListener l);
    interface OnSharedPreferenceChangeListener { void onSharedPreferenceChanged(SharedPreferences p, String k); }
    interface Editor {
        Editor putString(String k, String v);
        Editor putStringSet(String k, Set<String> v);
        Editor putInt(String k, int v);
        Editor putLong(String k, long v);
        Editor putFloat(String k, float v);
        Editor putBoolean(String k, boolean v);
        Editor remove(String k);
        Editor clear();
        boolean commit();
        void apply();
    }
}
