package android.app;

import android.content.Context;

/**
 * <code>android.app.Application</code> 桩（同时是宿主注入 TVBox 公共类的那个 Context 实例）。
 *
 * <p>注意：TVBox 的 <c>InitOrigin.context()</c> 返回类型就是 Application，
 * 注入时**必须传本类实例**（传裸 Context 会被强转吞掉，见 bridge.Server#injectStaticContext）。</p>
 */
public class Application extends Context {

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
}
