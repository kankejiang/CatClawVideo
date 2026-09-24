package android.widget;

import android.content.Context;

/**
 * <code>android.widget.Toast</code> 桩——UI 桥接版：show() 经 UiBridge 上行，
 * 宿主以非阻塞提示展示（TVBox 爬虫常用它反馈「请扫码」「已提交」等状态）。
 */
public class Toast {
    public static final int LENGTH_SHORT = 0;
    public static final int LENGTH_LONG = 1;

    private CharSequence text = "";

    public static Toast makeText(Context c, CharSequence text, int duration) {
        Toast t = new Toast();
        t.text = text;
        return t;
    }

    public void show() { bridge.UiBridge.toast(text); }

    public void setText(CharSequence t) { text = t; }

    public CharSequence getText() { return text; }

    /**
     * 位置/边距/视图这几个 setter 必须存在：爬虫常在 {@code show()} 前调它们，
     * 缺一个就 {@code NoSuchMethodError}，而调用点常在业务线程里 ⇒ 整条线程死掉
     * （实测 2026-09-25 网盘登录回执线程 {@code Thread-7} 就死在 setGravity 上，
     * 用户既看不到「登录成功」提示，cookie 也可能没走完落盘）。
     * 桌面提示的位置由宿主决定，这里按 Android 语义收下参数即可。
     */
    public void setGravity(int gravity, int xOffset, int yOffset) { }

    public void setMargin(float horizontalMargin, float verticalMargin) { }

    public android.view.View getView() { return null; }

    public void setView(android.view.View v) { }
}
