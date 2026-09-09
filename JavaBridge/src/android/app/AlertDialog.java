package android.app;
import android.content.Context;
public class AlertDialog {
    public AlertDialog(Context c) { }
    public void show() { }
    public static class Builder {
        public Builder(Context c) { }
        public Builder setTitle(String t) { return this; }
        public Builder setMessage(String m) { return this; }
        public Builder setPositiveButton(String t, Object l) { return this; }
        public Builder setNegativeButton(String t, Object l) { return this; }
        public AlertDialog create() { return new AlertDialog(null); }
        public AlertDialog show() { return new AlertDialog(null); }
    }
}
