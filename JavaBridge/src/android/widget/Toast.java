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
}
