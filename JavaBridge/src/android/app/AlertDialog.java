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
            // 刷新节拍（Dialog 的补发线程）走的那一遍要安静：2026-10-01 实测 90s 刷出 2044 行，
            // 「兜底未命中/空白」每 250ms 一轮，把真正的轮询/授权回执行埋掉了。
            JSONObject qr = huntQr(view, quickHunt);
            // 壳的二维码常在 show() 之后才画（异步取登录 URL）。⚠ 不能在 show() 里阻塞等：
            // 8×500ms 的重试让**每个**对话框都慢 4s 才弹出来（2026-10-02 用户实测）。
            // 改为后台线程继续找，找到就经 ui-qr 按补发通道升级界面（宿主已支持）；show() 立即返回。
            if (qr == null && !quickHunt) {
                final View v0 = view;
                Thread t = new Thread(() -> {
                    for (int retry = 1; retry <= 8; retry++) {
                        try { Thread.sleep(500); } catch (InterruptedException ie) { return; }
                        JSONObject q = huntQr(v0, true);
                        if (q == null) continue;
                        try {
                            if (isShowing()) {
                                UiBridge.emit(new JSONObject().put("ev", "ui-qr").put("seq", seq)
                                        .put("title", title == null ? "" : title.toString()).put("qr", q));
                                System.err.println("[ui] 后台补码 " + q.optInt("w") + "x" + q.optInt("h") + " seq=" + seq);
                            }
                        } catch (Throwable ignored) { }
                        return;
                    }
                }, "claw-qr-late");
                t.setDaemon(true);
                t.start();
            }
            // 抓不到位图时改走**契约通道**：视图树里有 http(s) 链接文本就把它作为 `qrText` 上行，
            // 由宿主自己出码（对齐 TVBox：爬虫给串、宿主 QRCodeGen）。这条路不需要任何绘制仿真。
            if (qr == null) {
                String url = firstUrl(view);
                if (url != null) {
                    spec.put("qrText", url);
                    System.err.println("[ui] 视图树里有登录 URL " + url.length() + " 字符 → 上行 qrText（宿主出码）");
                } else {
                    System.err.println("[ui] 视图状态 " + stateDump(view));
                }
            }
            if (qr != null) spec.put("qr", qr);
            // 排障留痕：「弹了但没东西」只能靠这个看清 jar 到底往 setView 里塞了什么、
            // 二维码像素有没有被桩接住（2026-09-24 扫码框空白的定位手段）
            System.err.println("[ui] 视图树 " + describe(view) + " qr="
                    + (qr == null ? "无" : qr.optInt("w") + "x" + qr.optInt("h")));
        } catch (Throwable ignored) { }
    }

    /**
     * 日志去敏：视图文本里可能整段就是登录 URL（会话 token 在 query 里）。
     * ⚠ 只用 Matcher 循环 —— {@code String.replaceAll(Function)} 是 Java 9 API，ART 里没有。
     */
    static String mask(String s) {
        if (s == null) return null;
        java.util.regex.Matcher m = java.util.regex.Pattern
                .compile("https?://[^\\s\"'<>]+").matcher(s);
        StringBuilder sb = new StringBuilder();
        int last = 0;
        while (m.find()) {
            sb.append(s, last, m.start()).append('<').append(m.end() - m.start())
                    .append("B url·host=").append(m.group().replaceFirst("^https?://([^/]*).*", "$1")).append('>');
            last = m.end();
        }
        return last == 0 ? s : sb.append(s, last, s.length()).toString();
    }

    /** 把 View 树压成一行：{@code FrameLayout[ImageView(bmp=240x240,像素ok)][可点]}。 */
    private static String describe(View v) {
        if (v == null) return "null";
        // 全名而非简单名：混淆后简单名只剩「m」，看不出包名 → 判断不了是谁家的类
        StringBuilder sb = new StringBuilder(v.getClass().getName());
        if (v instanceof ImageView iv) {
            android.graphics.Bitmap b = iv.getImageBitmap();
            sb.append(b == null ? "(无图)" : "(bmp=" + b.getWidth() + "x" + b.getHeight()
                    + (b.snapshotPixels() == null ? ",像素未捕获" : ",像素ok") + ")");
        } else if (v instanceof android.widget.TextView t) {
            sb.append(" \"").append(mask(String.valueOf(t.getText()))).append("\"");
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

    /**
     * 两层取码：① 视图树里的桩 ImageView（带像素）② 近 15s 内新建的方形有位图像素。
     *
     * <p>2026-10-01 删掉过 ③「驱动自定义 View 的 onDraw 到离屏画布」：那是给每个源画 UI，
     * 宿主侧不可能跟着适配所有订阅源。抓不到位图就改走契约通道 {@link #firstUrl}——
     * 视图树里有登录 URL 文本就上行 {@code qrText}，图由宿主出（对齐 TVBox 的 QRCodeGen）。</p>
     *
     * @param quiet 重试轮次里压掉「未命中」这类噪声，只留驱动结果
     */
    static JSONObject huntQr(View v, boolean quiet) {
        JSONObject qr = findQr(v);
        if (qr != null) return qr;
        android.graphics.Bitmap cand = android.graphics.Bitmap.recentQrCandidate();
        if (cand != null) {
            qr = UiBridge.qrJson(cand);
            if (qr != null) {
                System.err.println("[ui] qr 兜底命中 " + cand.getWidth() + "x" + cand.getHeight()
                        + "（jar 自己建并灌过像素的位图，不是桩 ImageView）");
                return qr;
            }
        } else if (!quiet) {
            System.err.println("[ui] qr 兜底未命中：近 15s 内没有「方形/≥100px」的位图");
        }
        return null;
    }

    /**
     * 反射打一行壳侧自定义 View 的内部状态（实例字段名=值；Bitmap 打尺寸，字符串截 48 字符）。
     * 「抓不到码」时只有这个能分清是**码还没生成**还是**生成了但没经桩的位图 API**。
     */
    private static String stateDump(View v) {
        if (v == null) return "null";
        StringBuilder sb = new StringBuilder(v.getClass().getName());
        Class<?> sup = v.getClass().getSuperclass();
        sb.append(" extends ").append(sup == null ? "?" : sup.getSimpleName()).append(" {");
        try {
            int n = 0;
            for (java.lang.reflect.Field f : v.getClass().getDeclaredFields()) {
                if (java.lang.reflect.Modifier.isStatic(f.getModifiers())) continue;
                Object val;
                try { f.setAccessible(true); val = f.get(v); } catch (Throwable t) { continue; }
                String p;
                if (val == null) p = "null";
                else if (val instanceof android.graphics.Bitmap bm) p = "Bitmap" + bm.getWidth() + "x" + bm.getHeight();
                else if (val instanceof String s) p = mask(s.length() > 48 ? s.substring(0, 48) + "…" : s);
                else p = String.valueOf(val);
                if (p.length() > 60) p = p.substring(0, 60) + "…";
                if (n++ > 0) sb.append(", ");
                if (n > 18) { sb.append("…"); break; }
                sb.append(f.getName()).append('=').append(p);
            }
        } catch (Throwable t) { sb.append("字段读取失败 ").append(t); }
        return sb.append('}').toString();
    }

    /**
     * 在视图树里找第一个带 http(s) 的文本（壳的「请在手机上打开 …」一类节点）。
     *
     * <p>这里<b>不切边界</b>：把「http」之后的整段交出去，由宿主用同一套正则
     * （{@code QrPng.FirstUrl}）截出纯 URL。桩侧以前自己按字符阈值切，把比较写反了
     * （要求 {@code > 0x2000} 才继续 ⇒ 第一个 ASCII 就停）⇒ 永远截出空串 ⇒ qrText 这条
     * 通道自打写下就没通过（2026-10-01 自检 op=qrtext 实测：宿主只收到 message，没有 qrText）。</p>
     */
    static String firstUrl(View v) {
        if (v == null) return null;
        if (v instanceof android.widget.TextView t) {
            String s = null;
            try {
                Object cs = t.getText();          // 桩的 getText() 返回 CharSequence
                s = cs == null ? null : String.valueOf(cs);
            } catch (Throwable ignored) { }
            if (s != null) {
                int a = s.indexOf("http");
                if (a >= 0 && s.startsWith("http", a) && s.length() - a > 12) return s.substring(a);
            }
        }
        if (v instanceof ViewGroup vg) {
            for (int i = 0; i < vg.getChildCount(); i++) {
                String u = firstUrl(vg.getChildAt(i));
                if (u != null) return u;
            }
        }
        return null;
    }

    /**
     * 通用登录通道的受控自检（只由桥的调试泵 {@code op=qrtext} 触发，不属于任何源的适配）。
     *
     * <p>「爬虫给 URL、宿主出码」这条契约通道此前没有任何真实订阅源走过（夸克给的是像素码，
     * 走的是取图那条），所以只能自带正控制：造一个只含 URL 文本的对话框走完整生产管线
     * （show → fillSpec → 取码失败 → firstUrl → 上行 {@code qrText} → 宿主 QrPng 出码）。
     * {@code async=true} 复现壳的时序（先弹框、异步才拿到链接），验证 invalidate → 刷新线程
     * → 补发 ui-qr 那一半。</p>
     */
    public static String selftest(String url, boolean async) {
        try {
            android.content.Context ctx = android.app.Application.getInstance();
            android.widget.TextView tv = new android.widget.TextView(ctx);
            tv.setText(async ? "正在获取登录链接…" : url);
            AlertDialog d = new Builder(ctx).setTitle("通用登录自检").setView(tv).create();
            d.show();
            if (async) {
                tv.setText(url);
                tv.invalidate();                   // 桩的 invalidate → Dialog.noteInvalidate → 标脏
                try { Thread.sleep(1600); } catch (InterruptedException ignored) { }   // 刷新节拍 500ms
            }
            JSONObject probe = new JSONObject();
            d.quickHunt = true;
            try { d.fillSpec(probe); } finally { d.quickHunt = false; }
            return new JSONObject().put("seq", d.seq).put("async", async)
                    .put("qrText", probe.optString("qrText"))
                    .put("qr", probe.has("qr") ? "有图" : "无图")
                    .put("hostShould", async ? "收到 ui-qr(qrText)" : "收到 ui-dialog(qrText)")
                    .toString();
        } catch (Throwable t) {
            return "{\"error\":\"" + t + "\"}";
        }
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
