package android.app;

import android.content.Context;
import android.content.DialogInterface;
import android.view.View;
import android.view.ViewGroup;
import android.widget.ImageView;
import bridge.UiBridge;
import org.json.JSONArray;
import org.json.JSONObject;

/**
 * <code>android.app.AlertDialog</code> 桩——UI 桥接版。
 *
 * <p>Builder 链式参数在 show() 时组装成 JSON 事件上行（见 UiBridge），宿主渲染
 * MAUI 原生对话框；用户点击经 ui-result 回传并触发对应 listener。自定义视图
 * （setView）里若含二维码 ImageView（带像素数据），矩阵随事件上行、宿主重建图像。</p>
 */
public class AlertDialog extends Dialog {

    private String[] items;
    private DialogInterface.OnClickListener itemClick;
    private DialogInterface.OnClickListener positive, negative, neutral;
    private String positiveText = "", negativeText = "", neutralText = "";

    public AlertDialog() { super(); }
    public AlertDialog(Context c) { super(c); }

    public void setItems(String[] i, DialogInterface.OnClickListener l) { items = i; itemClick = l; }

    public void setButton(int which, CharSequence text, DialogInterface.OnClickListener l) {
        if (which == DialogInterface.BUTTON_POSITIVE) { positiveText = text == null ? "" : text.toString(); positive = l; }
        else if (which == DialogInterface.BUTTON_NEGATIVE) { negativeText = text == null ? "" : text.toString(); negative = l; }
        else { neutralText = text == null ? "" : text.toString(); neutral = l; }
    }

    public android.widget.Button getButton(int which) { return new android.widget.Button(); }
    public android.widget.ListView getListView() { return new android.widget.ListView(); }

    @Override protected DialogInterface.OnClickListener itemsClick() { return itemClick; }
    @Override protected DialogInterface.OnClickListener posClick() { return positive; }
    @Override protected DialogInterface.OnClickListener negClick() { return negative; }
    @Override protected DialogInterface.OnClickListener neuClick() { return neutral; }

    @Override protected void fillSpec(JSONObject spec) {
        try {
            if (items != null) spec.put("items", new JSONArray(items));
            if (positive != null || positiveText.length() > 0) spec.put("positive", positiveText.length() > 0 ? positiveText : "确定");
            if (negative != null || negativeText.length() > 0) spec.put("negative", negativeText.length() > 0 ? negativeText : "取消");
            if (neutral != null || neutralText.length() > 0) spec.put("neutral", neutralText.length() > 0 ? neutralText : "中性");
            JSONObject qr = findQr(view);
            if (qr != null) spec.put("qr", qr);
            // 排障留痕：「弹了但没东西」只能靠这个看清 jar 到底往 setView 里塞了什么、
            // 二维码像素有没有被桩接住（2026-09-24 扫码框空白的定位手段）
            System.err.println("[ui] 视图树 " + describe(view) + " qr="
                    + (qr == null ? "无" : qr.optInt("w") + "x" + qr.optInt("h")));
        } catch (Throwable ignored) { }
    }

    /** 把 View 树压成一行：{@code FrameLayout[ImageView(bmp=240x240,像素ok)][可点]}。 */
    private static String describe(View v) {
        if (v == null) return "null";
        StringBuilder sb = new StringBuilder(v.getClass().getSimpleName());
        if (v instanceof ImageView iv) {
            android.graphics.Bitmap b = iv.getImageBitmap();
            sb.append(b == null ? "(无图)" : "(bmp=" + b.getWidth() + "x" + b.getHeight()
                    + (b.snapshotPixels() == null ? ",像素未捕获" : ",像素ok") + ")");
        } else if (v instanceof android.widget.TextView t) {
            sb.append(" \"").append(t.getText()).append("\"");
        }
        if (v.clickListener() != null) sb.append("[可点]");
        if (v instanceof ViewGroup vg && vg.getChildCount() > 0) {
            sb.append('[');
            for (int i = 0; i < vg.getChildCount(); i++) {
                if (i > 0) sb.append(',');
                sb.append(describe(vg.getChildAt(i)));
            }
            sb.append(']');
        }
        return sb.toString();
    }

    /** 深挖视图树找带像素数据的 ImageView（扫码二维码）。 */
    private static JSONObject findQr(View v) {
        if (v instanceof ImageView iv) return UiBridge.qrJson(iv.getImageBitmap());
        if (v instanceof ViewGroup vg) {
            for (int i = 0; i < vg.getChildCount(); i++) {
                JSONObject r = findQr(vg.getChildAt(i));
                if (r != null) return r;
            }
        }
        return null;
    }

    public static class Builder {
        private final Context ctx;
        private CharSequence title, message;
        private String[] items;
        private DialogInterface.OnClickListener itemClick, positive, negative, neutral;
        private CharSequence positiveText = "", negativeText = "", neutralText = "";
        private View view;
        private boolean cancelable = true;
        private DialogInterface.OnCancelListener onCancel;
        private DialogInterface.OnDismissListener onDismiss;
        private DialogInterface.OnShowListener onShow;

        public Builder(Context c) { ctx = c; }

        public Builder setTitle(CharSequence t) { title = t; return this; }
        public Builder setTitle(int resId) { title = ctx != null ? ctx.getString(resId) : ""; return this; }
        public Builder setCustomTitle(View v) { return this; }
        public Builder setMessage(CharSequence m) { message = m; return this; }
        public Builder setIcon(int resId) { return this; }
        public Builder setIcon(android.graphics.drawable.Drawable d) { return this; }
        public Builder setCancelable(boolean b) { cancelable = b; return this; }

        public Builder setItems(CharSequence[] i, DialogInterface.OnClickListener l) {
            if (i != null) {
                items = new String[i.length];
                for (int k = 0; k < i.length; k++) items[k] = i[k] == null ? "" : i[k].toString();
            }
            itemClick = l;
            return this;
        }

        public Builder setItems(int resId, DialogInterface.OnClickListener l) { return this; }

        public Builder setSingleChoiceItems(CharSequence[] i, int checked, DialogInterface.OnClickListener l) {
            return setItems(i, l);
        }

        public Builder setMultiChoiceItems(CharSequence[] i, boolean[] checked, DialogInterface.OnMultiChoiceClickListener l) {
            return setItems(i, null);
        }

        public Builder setAdapter(android.widget.ListAdapter a, DialogInterface.OnClickListener l) { return this; }

        public Builder setPositiveButton(CharSequence t, DialogInterface.OnClickListener l) { positiveText = t; positive = l; return this; }
        public Builder setPositiveButton(int r, DialogInterface.OnClickListener l) { return setPositiveButton(ctx != null ? ctx.getString(r) : "确定", l); }
        public Builder setNegativeButton(CharSequence t, DialogInterface.OnClickListener l) { negativeText = t; negative = l; return this; }
        public Builder setNegativeButton(int r, DialogInterface.OnClickListener l) { return setNegativeButton(ctx != null ? ctx.getString(r) : "取消", l); }
        public Builder setNeutralButton(CharSequence t, DialogInterface.OnClickListener l) { neutralText = t; neutral = l; return this; }
        public Builder setNeutralButton(int r, DialogInterface.OnClickListener l) { return setNeutralButton(ctx != null ? ctx.getString(r) : "中性", l); }

        public Builder setView(View v) { view = v; return this; }
        public Builder setView(int resId) { return this; }

        public Builder setOnCancelListener(DialogInterface.OnCancelListener l) { onCancel = l; return this; }
        public Builder setOnDismissListener(DialogInterface.OnDismissListener l) { onDismiss = l; return this; }
        public Builder setOnShowListener(DialogInterface.OnShowListener l) { onShow = l; return this; }

        public AlertDialog create() {
            AlertDialog d = new AlertDialog(ctx);
            d.title = title;
            d.message = message;
            d.items = items;
            d.itemClick = itemClick;
            d.positive = positive; d.negative = negative; d.neutral = neutral;
            d.positiveText = positiveText == null ? "" : positiveText.toString();
            d.negativeText = negativeText == null ? "" : negativeText.toString();
            d.neutralText = neutralText == null ? "" : neutralText.toString();
            d.view = view;
            d.cancelable = cancelable;
            d.onCancel = onCancel;
            d.onDismiss = onDismiss;
            d.onShow = onShow;
            return d;
        }

        public AlertDialog show() { AlertDialog d = create(); d.show(); return d; }
    }
}
