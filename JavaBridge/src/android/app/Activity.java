package android.app;
import android.content.Context;
public class Activity extends Context {
    public void runOnUiThread(Runnable r) { }
    public void finish() { }
    public android.view.Window getWindow() { return null; }
    public void setContentView(int layoutResID) { }
}
