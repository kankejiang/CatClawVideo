package android.content;

public class ComponentName {
    private final String pkg;
    private final String cls;
    public ComponentName(String pkg, String cls) { this.pkg = pkg; this.cls = cls; }
    public ComponentName(Context pkg, String cls) { this.pkg = ""; this.cls = cls; }
    public String getPackageName() { return pkg; }
    public String getClassName() { return cls; }
    public String flattenToString() { return pkg + "/" + cls; }
    public String flattenToShortString() { return flattenToString(); }
    public static ComponentName unflattenFromString(String str) {
        int slash = str.indexOf('/');
        if (slash < 0) return new ComponentName("", str);
        return new ComponentName(str.substring(0, slash), str.substring(slash + 1));
    }
    @Override public String toString() { return "ComponentInfo{" + pkg + "/" + cls + "}"; }
}