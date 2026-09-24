package android.app;

import android.content.Context;
import android.content.DialogInterface;
import bridge.UiBridge;
import org.json.JSONObject;

/**
 * <code>android.app.Dialog</code> 桩——UI 桥接版。
 *
 * <p>show() 把标题/消息/自定义视图（含二维码）组装成 JSON 事件经 UiBridge 上行，
 * 宿主渲染原生对话框；用户操作经 {@code ui-result} op 回传并触发 jar 的 listener。
 * dismiss()/cancel() 对应上行 ui-dismiss。</p>
 */
public class Dialog implements DialogInterface {

    protected int seq = -1;
    protected CharSequence title = "";
    protected CharSequence message = "";
    protected android.view.View view;
    protected boolean cancelable = true;
    protected OnCancelListener onCancel;
    protected OnDismissListener onDismiss;
    protected OnShowListener onShow;

    public Dialog() { }
    public Dialog(Context c) { }

    public void setTitle(CharSequence t) { title = t; }
    public CharSequence getTitle() { return title; }
    public void setMessage(CharSequence m) { message = m; }
    public CharSequence getMessage() { return message; }

    public void setView(android.view.View v) { view = v; }

    public void setCancelable(boolean b) { cancelable = b; }
    public void setCanceledOnTouchOutside(boolean b) { }
    public void setOnCancelListener(OnCancelListener l) { onCancel = l; }
    public void setOnDismissListener(OnDismissListener l) { onDismiss = l; }
    public void setOnShowListener(OnShowListener l) { onShow = l; }

    /** show()：分配 seq、注册回调、上行 ui-dialog 事件、触发 onShow。 */
    public void show() {
        if (seq < 0) seq = UiBridge.nextSeq();
        UiBridge.register(seq, new UiBridge.Pending(this, itemsClick(), posClick(), negClick(), neuClick(), onCancel, onDismiss));
        try {
            JSONObject spec = new JSONObject()
                    .put("title", title == null ? "" : title.toString())
                    .put("message", message == null ? "" : message.toString())
                    .put("cancelable", cancelable);
            fillSpec(spec);
            UiBridge.shown(seq, spec);
            if (onShow != null) { try { onShow.onShow(this); } catch (Throwable ignored) { } }
        } catch (Throwable ignored) { }
    }

    /** 子类扩展 spec（items/按钮/二维码等）。 */
    protected void fillSpec(JSONObject spec) { }

    /** 子类提供各按钮监听（Builder 填充）。 */
    protected DialogInterface.OnClickListener itemsClick() { return null; }
    protected DialogInterface.OnClickListener posClick() { return null; }
    protected DialogInterface.OnClickListener negClick() { return null; }
    protected DialogInterface.OnClickListener neuClick() { return null; }

    public void dismiss() { UiBridge.dismissed(this, false); }
    public void cancel() { UiBridge.dismissed(this, true); }
    public boolean isShowing() { return seq >= 0 && UiBridge.isPending(seq); }

    public android.view.Window getWindow() { return new android.view.Window(); }
}
