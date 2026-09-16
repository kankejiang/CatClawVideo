package android.app;

import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;

/**
 * <code>android.app.Activity</code> 桩。
 *
 * <p>宿主没有真实 Activity，但 TVBox 系爬虫会调 <c>InitOrigin.getActivity()</c> 拿它，
 * 再访问常见成员（<c>getComponentName</c> / <c>getIntent</c> / <c>getLocalClassName</c> …）。
 * <b>缺任何一个都会抛 <c>NoSuchMethodError</c> 把整条线程打死</b>
 * —— 实测 2026-09-16 荐片播放失败的真凶就是
 * <c>NoSuchMethodError: android.content.ComponentName android.app.Activity.getComponentName()</c>，
 * 而报错发生在端口调整线程里，用户侧只看到「播放失败」。</p>
 *
 * <p>原则：这里的方法只做「不崩 + 返回合理值」，不实现真实 Android 行为。</p>
 */
public class Activity extends Context {

    private static final String LOCAL_CLASS = "MainActivity";

    public void runOnUiThread(Runnable r) { if (r != null) r.run(); }

    public void finish() { }

    private static final android.view.Window sWindow = new android.view.Window();

    /** 必须返回**非空**：爬虫会一路 getWindow().getDecorView()… 走下去。 */
    public android.view.Window getWindow() { return sWindow; }

    public void setContentView(int layoutResID) { }

    /** 爬虫常拿它当「当前页面标识」用（去重/日志）。 */
    public ComponentName getComponentName() {
        return new ComponentName(getPackageName(), getPackageName() + "." + LOCAL_CLASS);
    }

    public String getLocalClassName() { return LOCAL_CLASS; }

    public Intent getIntent() { return new Intent(); }

    public Application getApplication() { return Application.getInstance(); }

    public Context getApplicationContext() { return getApplication(); }

    public boolean isFinishing() { return false; }

    public boolean isDestroyed() { return false; }

    public Activity getParent() { return null; }

    public void startActivity(Intent intent) { }

    public void startActivityForResult(Intent intent, int requestCode) { }

    public void overridePendingTransition(int enterAnim, int exitAnim) { }

    public int getTaskId() { return 1; }

    public void onBackPressed() { }
}
