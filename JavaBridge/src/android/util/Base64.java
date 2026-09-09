package android.util;
public class Base64 {
    public static final int DEFAULT = 0;
    public static final int NO_PADDING = 1;
    public static final int NO_WRAP = 2;
    public static final int CRLF = 4;
    public static final int URL_SAFE = 8;
    public static final int NO_CLOSE = 16;
    public static byte[] decode(String str, int flags) {
        try { return java.util.Base64.getMimeDecoder().decode(str); } catch (Exception e) { return new byte[0]; }
    }
    public static byte[] decode(byte[] input, int flags) {
        try { return java.util.Base64.getMimeDecoder().decode(input); } catch (Exception e) { return new byte[0]; }
    }
    public static String encodeToString(byte[] input, int flags) {
        return (flags & URL_SAFE) != 0 ? java.util.Base64.getUrlEncoder().encodeToString(input) : java.util.Base64.getEncoder().encodeToString(input);
    }
    public static byte[] encode(byte[] input, int flags) {
        return (flags & URL_SAFE) != 0 ? java.util.Base64.getUrlEncoder().encode(input) : java.util.Base64.getEncoder().encode(input);
    }
}
