package android.net;
import java.util.LinkedHashMap;
import java.util.Map;
public class Uri {
    private final String s;
    private Uri(String s) { this.s = s == null ? "" : s; }
    public static Uri parse(String s) { return new Uri(s); }
    public static Uri fromFile(java.io.File f) { return new Uri(f.toURI().toString()); }
    public String toString() { return s; }
    public String getPath() { try { java.net.URI u = java.net.URI.create(s); return u.getPath() == null ? "" : u.getPath(); } catch (Exception e) { return ""; } }
    public String getHost() { try { java.net.URI u = java.net.URI.create(s); return u.getHost() == null ? "" : u.getHost(); } catch (Exception e) { return ""; } }
    public String getQueryParameter(String key) {
        Map<String, String> q = query();
        String v = q.get(key);
        return v == null ? null : v;
    }
    public Map<String, String> query() {
        Map<String, String> out = new LinkedHashMap<>();
        int i = s.indexOf('?');
        if (i < 0) return out;
        for (String pair : s.substring(i + 1).split("&")) {
            int eq = pair.indexOf('=');
            if (eq > 0) out.put(urlDecode(pair.substring(0, eq)), urlDecode(pair.substring(eq + 1)));
        }
        return out;
    }
    private static String urlDecode(String x) { try { return java.net.URLDecoder.decode(x, "UTF-8"); } catch (Exception e) { return x; } }
}
