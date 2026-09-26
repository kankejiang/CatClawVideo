package bridge;

import android.content.SharedPreferences;

import java.io.File;
import java.io.FileOutputStream;
import java.io.OutputStreamWriter;
import java.io.Writer;
import java.util.HashMap;
import java.util.Map;
import java.util.Set;

/**
 * guest（真 ART）侧的 SharedPreferences：内存 Map + 写穿到 {@code <data.dir>/art/shared_prefs/<name>.xml}，
 * 格式与 Android 的 {@code SharedPreferencesImpl} 一致（{@code <string>} 用文本体，其余用 {@code value=} 属性）。
 *
 * <p>为什么不用 {@code android.content.Context.MemPrefs}：那是我们桌面无 Android 时的替身，
 * 而 guest 里 {@code android.*} 归 boot classpath 的真 framework 管，它的实现要 mBase（ActivityThread）。
 * 名称按 prefs 分文件是硬要求：宿主与 guest 必须落在同一个 {@code shared_prefs/quark_ck} 之类的路径上，
 * 登录态才能被两侧读到同一份（见 PrefsStore 的来历）。</p>
 */
final class ArtPrefs implements SharedPreferences, SharedPreferences.Editor {

    private final File file;
    private final Map<String, Object> m = new HashMap<>();

    ArtPrefs(File f) {
        file = f;
        f.getParentFile().mkdirs();
        load();
    }

    private void load() {
        if (!file.isFile()) return;
        try (java.io.BufferedReader r = new java.io.BufferedReader(
                new java.io.InputStreamReader(new java.io.FileInputStream(file), "UTF-8"))) {
            String ln;
            while ((ln = r.readLine()) != null) {
                String s = ln.trim();
                String key = attr(s, "name");
                String type = tag(s);
                if (key == null || type == null) continue;
                switch (type) {
                    case "string": m.put(key, between(s)); break;
                    case "boolean": m.put(key, Boolean.valueOf(attr(s, "value"))); break;
                    case "int": m.put(key, Integer.valueOf(attr(s, "value"))); break;
                    case "long": m.put(key, Long.valueOf(attr(s, "value"))); break;
                    case "float": m.put(key, Float.valueOf(attr(s, "value"))); break;
                    default: break;
                }
            }
        } catch (Exception e) {
            System.err.println("[art] prefs 读 " + file.getName() + " 失败: " + e);
        }
    }

    private static String tag(String s) {
        if (!s.startsWith("<") || s.startsWith("</")) return null;
        int e = s.indexOf(' ', 1);
        if (e < 0) e = s.indexOf('>', 1);
        return e < 0 ? null : s.substring(1, e);
    }

    private static String attr(String s, String a) {
        String k = a + "=\"";
        int i = s.indexOf(k);
        if (i < 0) return null;
        int j = s.indexOf('"', i + k.length());
        return j < 0 ? null : s.substring(i + k.length(), j);
    }

    private static String between(String s) {
        int i = s.indexOf('>'), j = s.lastIndexOf('<');
        return i < 0 || j <= i ? "" : s.substring(i + 1, j);
    }

    /** 真 Android 接口有 getAll()，我们的桌面无 Android 替身没有 —— 所以不加 @Override，只对齐签名。 */
    public Map<String, ?> getAll() { return new HashMap<>(m); }
    @Override public String getString(String k, String d) { return m.containsKey(k) ? (String) m.get(k) : d; }
    @Override public boolean getBoolean(String k, boolean d) { return m.containsKey(k) ? (Boolean) m.get(k) : d; }
    @Override public float getFloat(String k, float d) { return m.containsKey(k) ? (Float) m.get(k) : d; }
    @Override public int getInt(String k, int d) { return m.containsKey(k) ? (Integer) m.get(k) : d; }
    @Override public long getLong(String k, long d) { return m.containsKey(k) ? (Long) m.get(k) : d; }
    @Override public Set<String> getStringSet(String k, Set<String> d) { return m.containsKey(k) ? (Set<String>) m.get(k) : d; }
    @Override public boolean contains(String k) { return m.containsKey(k); }
    @Override public Editor edit() { return this; }
    @Override public void registerOnSharedPreferenceChangeListener(OnSharedPreferenceChangeListener l) { }
    @Override public void unregisterOnSharedPreferenceChangeListener(OnSharedPreferenceChangeListener l) { }

    @Override public Editor putString(String k, String v) { m.put(k, v); return this; }
    @Override public Editor putBoolean(String k, boolean v) { m.put(k, v); return this; }
    @Override public Editor putInt(String k, int v) { m.put(k, v); return this; }
    @Override public Editor putLong(String k, long v) { m.put(k, v); return this; }
    @Override public Editor putFloat(String k, float v) { m.put(k, v); return this; }
    @Override public Editor putStringSet(String k, Set<String> v) { m.put(k, v); return this; }
    @Override public Editor remove(String k) { m.remove(k); return this; }
    @Override public Editor clear() { m.clear(); return this; }
    @Override public void apply() { commit(); }

    @Override public boolean commit() {
        StringBuilder sb = new StringBuilder("<?xml version='1.0' encoding='utf-8' standalone='yes' ?>\n<map>\n");
        for (Map.Entry<String, Object> e : m.entrySet()) {
            String k = e.getKey();
            Object v = e.getValue();
            if (v instanceof String) sb.append("    <string name=\"").append(k).append("\">").append(v).append("</string>\n");
            else if (v instanceof Boolean) sb.append("    <boolean name=\"").append(k).append("\" value=\"").append(v).append("\" />\n");
            else if (v instanceof Integer) sb.append("    <int name=\"").append(k).append("\" value=\"").append(v).append("\" />\n");
            else if (v instanceof Long) sb.append("    <long name=\"").append(k).append("\" value=\"").append(v).append("\" />\n");
            else if (v instanceof Float) sb.append("    <float name=\"").append(k).append("\" value=\"").append(v).append("\" />\n");
            else if (v instanceof Set) {
                sb.append("    <set name=\"").append(k).append("\">\n");
                for (String x : (Set<String>) v) sb.append("        <item>").append(x).append("</item>\n");
                sb.append("    </set>\n");
            }
        }
        sb.append("</map>\n");
        try (Writer w = new OutputStreamWriter(new FileOutputStream(file), "UTF-8")) {
            w.write(sb.toString());
            return true;
        } catch (Exception e) {
            System.err.println("[art] prefs 写 " + file.getName() + " 失败: " + e);
            return false;
        }
    }
}
