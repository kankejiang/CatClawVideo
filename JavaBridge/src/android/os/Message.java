package android.os;

/**
 * Message 桩（Handler/WebChromeClient 回调参数类型）。
 *
 * <p>⚠ {@link #sendToTarget()} 必须把消息交给 {@link #target}：爬虫用
 * {@code Message.obtain(handler); msg.what=..; msg.sendToTarget()} 触发延迟任务，
 * 旧实现是空方法，消息被静默丢弃。</p>
 */
public class Message {
    public int what;
    public int arg1;
    public int arg2;
    public Object obj;
    public Handler target;

    public Message() { }

    public static Message obtain() { return new Message(); }

    public static Message obtain(Handler h) {
        Message m = new Message();
        m.target = h;
        return m;
    }

    public static Message obtain(Handler h, int what) {
        Message m = obtain(h);
        m.what = what;
        return m;
    }

    public static Message obtain(Handler h, int what, Object obj) {
        Message m = obtain(h, what);
        m.obj = obj;
        return m;
    }

    public Message setData(Bundle data) { return this; }

    public Bundle getData() { return new Bundle(); }

    public void recycle() { }

    public void sendToTarget() {
        Handler h = target;
        if (h != null) h.sendMessage(this);
    }
}
