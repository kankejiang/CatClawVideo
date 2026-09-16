package android.view;

/**
 * <code>android.view.Window</code> 桩。
 *
 * <p>⚠ 注意 Java 语义：<b>方法解析发生在 null 检查之前</b> ——
 * 即使 <c>activity.getWindow()</c> 返回 null，只要 <c>getDecorView()</c> 这个方法不存在，
 * 就会先抛 <c>NoSuchMethodError</c>（而不是 NPE）。所以「宿主哪些方法必须有」要看报错，
 * 不能靠「反正返回 null 就没事」蒙过去。</p>
 *
 * <p>实测 2026-09-16：荐片 <c>playerContent</c> 19 条线路全部在同一处 0.00s 失败，
 * 报 <c>NoSuchMethodError: 'android.view.View android.view.Window.getDecorView()'</c>。</p>
 */
public class Window {

    private final View decorView = new View(android.app.Application.getInstance());

    public View getDecorView() { return decorView; }

    public View getRootView() { return decorView; }

    public android.content.Context getContext() { return android.app.Application.getInstance(); }

    public View findViewById(int id) { return null; }

    public void setContentView(int layoutResID) { }

    public void setContentView(View view) { }

    public void addContentView(View view, android.view.ViewGroup.LayoutParams params) { }

    public android.view.WindowManager.LayoutParams getAttributes() {
        return new android.view.WindowManager.LayoutParams();
    }

    public void setAttributes(android.view.WindowManager.LayoutParams params) { }

    public void setFlags(int flags, int mask) { }

    public void clearFlags(int flags) { }

    public void setSoftInputMode(int mode) { }

    public boolean isActive() { return true; }

    public void setBackgroundDrawable(android.graphics.drawable.Drawable drawable) { }
}
