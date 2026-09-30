package android.app;

import android.content.Context;
import android.content.ContextWrapper;

/**
 * <code>android.app.Application</code> 桩（同时是宿主注入 TVBox 公共类的那个 Context 实例）。
 *
 * <p>注意：TVBox 的 <c>InitOrigin.context()</c> 返回类型就是 Application，
 * 注入时**必须传本类实例**（传裸 Context 会被强转吞掉，见 bridge.Server#injectStaticContext）。</p>
 *
 * <p>父链按真机走 {@code ContextWrapper}（以前直接 {@code extends Context}）——
 * 壳里 {@code (ContextWrapper) getApplicationContext()} 这类写法要靠它才过验证器。</p>
 */
public class Application extends ContextWrapper {

    private static volatile Application sInstance;

    public Application() { sInstance = this; }

    /** 惰性创建：爬虫可能在宿主注入之前就调它（返回 null 会引发 NPE 一片）。 */
    public static Application getInstance() {
        Application a = sInstance;
        if (a == null) {
            synchronized (Application.class) {
                if (sInstance == null) sInstance = new Application();
                a = sInstance;
            }
        }
        return a;
    }

    // ── 自动补齐：jar 引用到但桩缺失的成员（android-33 签名，空实现）──
    public void registerActivityLifecycleCallbacks(android.app.Application.ActivityLifecycleCallbacks p0) { }
    public void unregisterActivityLifecycleCallbacks(android.app.Application.ActivityLifecycleCallbacks p0) { }

    /** 真机同名嵌套接口：jar 的 ActivityLifecycleCallbacks 实现类靠它与桩 Activity 同源。 */
    public interface ActivityLifecycleCallbacks {
        void onActivityCreated(android.app.Activity activity, android.os.Bundle savedInstanceState);
        void onActivityStarted(android.app.Activity activity);
        void onActivityResumed(android.app.Activity activity);
        void onActivityPaused(android.app.Activity activity);
        void onActivityStopped(android.app.Activity activity);
        void onActivitySaveInstanceState(android.app.Activity activity, android.os.Bundle outState);
        void onActivityDestroyed(android.app.Activity activity);
    }
}
