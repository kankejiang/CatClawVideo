package android.app;
import android.content.Context;
public class Application extends Context {
    private static Application sInstance;
    public Application() { sInstance = this; }
    public static Application getInstance() { return sInstance; }
}
