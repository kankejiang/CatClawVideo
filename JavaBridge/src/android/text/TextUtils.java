package android.text;
public class TextUtils {
    public static boolean isEmpty(CharSequence s) { return s == null || s.length() == 0; }
    public static boolean isNotEmpty(CharSequence s) { return !isEmpty(s); }
    public static boolean isDigitsOnly(CharSequence s) { if (s == null) return false; for (int i = 0; i < s.length(); i++) if (!Character.isDigit(s.charAt(i))) return false; return true; }
    public static String join(CharSequence delimiter, Object[] tokens) { StringBuilder sb = new StringBuilder(); for (int i = 0; i < tokens.length; i++) { if (i > 0) sb.append(delimiter); sb.append(tokens[i]); } return sb.toString(); }
    public static String join(CharSequence delimiter, Iterable<?> tokens) { StringBuilder sb = new StringBuilder(); boolean first = true; for (Object t : tokens) { if (!first) sb.append(delimiter); sb.append(t); first = false; } return sb.toString(); }
    public static boolean equals(CharSequence a, CharSequence b) { return java.util.Objects.equals(a == null ? null : a.toString(), b == null ? null : b.toString()); }
    public static String htmlEncode(String s) { return s == null ? "" : s.replace("&", "&amp;"); }
    public static int getTrimmedLength(CharSequence s) { return s.toString().trim().length(); }
}
