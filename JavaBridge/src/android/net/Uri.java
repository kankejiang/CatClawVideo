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

    /** Uri.Builder 桩：spider 里常 new Uri.Builder().scheme(..).appendPath(..).build()。 */
    public static final class Builder {
        private String scheme;
        private String authority;
        private String path;
        private String query;
        private String fragment;

        public Builder() { }
        public Builder(Uri uri) { if (uri != null) { scheme = uri.getScheme(); authority = uri.getHost(); path = uri.getPath(); } }

        public Builder scheme(String s) { this.scheme = s; return this; }
        public Builder encodedScheme(String s) { return scheme(s); }
        public Builder authority(String a) { this.authority = a; return this; }
        public Builder encodedAuthority(String a) { return authority(a); }
        public Builder path(String p) { this.path = p; return this; }
        public Builder encodedPath(String p) { return path(p); }
        public Builder appendPath(String segment) {
            if (segment == null) return this;
            String encoded = urlEncode(segment);
            if (path == null || path.isEmpty()) path = encoded.startsWith("/") ? encoded : "/" + encoded;
            else path = (path.endsWith("/") ? path : path + "/") + (encoded.startsWith("/") ? encoded.substring(1) : encoded);
            return this;
        }
        public Builder appendEncodedPath(String segment) { return appendPath(segment); }
        public Builder query(String q) { this.query = q; return this; }
        public Builder encodedQuery(String q) { return query(q); }
        public Builder appendQueryParameter(String key, String value) {
            if (key == null) return this;
            String pair = urlEncode(key) + "=" + (value == null ? "" : urlEncode(value));
            query = (query == null || query.isEmpty()) ? pair : query + "&" + pair;
            return this;
        }
        public Builder fragment(String f) { this.fragment = f; return this; }
        public Builder encodedFragment(String f) { return fragment(f); }
        public Builder clearQuery() { query = null; return this; }

        public Uri build() {
            StringBuilder sb = new StringBuilder();
            if (scheme != null) sb.append(scheme).append(':');
            if (authority != null) sb.append("//").append(authority);
            if (path != null) sb.append(path);
            if (query != null && !query.isEmpty()) sb.append('?').append(query);
            if (fragment != null) sb.append('#').append(fragment);
            return new Uri(sb.toString());
        }

        @Override public String toString() { return build().toString(); }
    }

    private static String urlEncode(String x) {
        try { return java.net.URLEncoder.encode(x, "UTF-8").replace("+", "%20"); } catch (Exception e) { return x; }
    }

    public String getScheme() { int i = s.indexOf(':'); return i < 0 ? null : s.substring(0, i); }
    public String getAuthority() { int i = s.indexOf("//"); return i < 0 ? null : s.substring(i + 2).split("[/?#]")[0]; }
}
