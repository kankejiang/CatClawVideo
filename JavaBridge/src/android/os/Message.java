package android.os;

/** Message 桩（Handler/WebChromeClient 回调参数类型）。 */
public class Message {
    public int what;
    public int arg1;
    public int arg2;
    public Object obj;
    public Message() { }
    public static Message obtain() { return new Message(); }
    public static Message obtain(Handler h) { return new Message(); }
    public Message setData(Bundle data) { return this; }
    public Bundle getData() { return new Bundle(); }
    public void sendToTarget() { }
}
