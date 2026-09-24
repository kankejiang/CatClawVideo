package android.app;

import android.content.Context;
import android.content.DialogInterface;
import android.view.ViewGroup;
import bridge.UiBridge;
import org.json.JSONArray;
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
        JSONObject spec = null;
        try {
            spec = new JSONObject()
                    .put("title", title == null ? "" : title.toString())
                    .put("message", message == null ? "" : message.toString())
                    .put("cancelable", cancelable);
            fillSpec(spec);
        } catch (Throwable ignored) { }

        // 自定义 View 树摊平成条目 —— 必须在 register 之前，点击路由要进 Pending
        DialogInterface.OnClickListener itemL = itemsClick();
        if (itemL == null && spec != null) itemL = flattenView(spec);
        final DialogInterface.OnClickListener item = itemL;

        UiBridge.register(seq, new UiBridge.Pending(this, item, posClick(), negClick(), neuClick(),
                onCancel, onDismiss, viewFlattened));
        if (spec != null) UiBridge.shown(seq, spec);
        if (onShow != null) { try { onShow.onShow(this); } catch (Throwable ignored) { } }
    }

    /**
     * 把 {@code setView} 的自定义 View 树摊平成宿主可渲染、可点击的条目。
     *
     * <p>Guard 系网盘的「已登录+启用中」列表<b>不是</b> {@code setItems} 给的：jar 自己
     * new 出 LinearLayout + TextView、给每行挂 {@code OnClickListener}，再 {@code setView}。
     * 桩只看 title/message/items 的话，宿主收到的就是一个只有「确定」的空框
     * （2026-09-24 实测「弹出没东西」就是这个）。</p>
     *
     * @return 按 items 下标路由到各节点 {@code OnClickListener} 的监听；树里没有可点节点时返回 null
     */
    private DialogInterface.OnClickListener flattenView(JSONObject spec) {
        if (view == null) return null;
        java.util.List<android.view.View> nodes = new java.util.ArrayList<>();
        java.util.List<String> labels = new java.util.ArrayList<>();
        java.util.List<java.util.List<Integer>> rows = new java.util.ArrayList<>();
        StringBuilder plain = new StringBuilder();
        collectRows(view, nodes, labels, rows, plain);
        if (nodes.isEmpty()) {
            // 纯展示型视图：没有可点行，但至少把文本搬上去，别留空框
            if (plain.length() > 0 && (message == null || message.length() == 0)) {
                try { spec.put("message", plain.toString()); } catch (Throwable ignored) { }
            }
            return null;
        }
        try {
            // 树里那行「已登录+启用中(可进入和搜索自己盘)」是标题 TextView，jar 没走 setTitle ——
            // 不搬上去宿主只剩一串没有说明的按钮
            if (plain.length() > 0 && (title == null || title.length() == 0)) {
                int nl = plain.indexOf("\n");
                String head = nl < 0 ? plain.toString() : plain.substring(0, nl);
                String rest = nl < 0 ? "" : plain.substring(nl + 1).trim();
                spec.put("title", head);
                if (rest.length() > 0 && (message == null || message.length() == 0)) spec.put("message", rest);
            }
            JSONArray arr = new JSONArray();
            for (String s : labels) arr.put(s);
            spec.put("items", arr);
            // 行结构：一行的若干格 = 原容器里并排的几个可点控件（网盘行 = 盘名 + 启用/停用）。
            // 格文本用**裸文本**（宿主两栏布局下左列已说明是谁）；带归属的 labels 只服务 items 平铺兜底。
            JSONArray rowsArr = new JSONArray();
            for (java.util.List<Integer> r : rows) {
                JSONArray cells = new JSONArray();
                for (int i : r) cells.put(new JSONObject().put("t", nodeText(nodes.get(i))).put("i", i));
                rowsArr.put(cells);
            }
            spec.put("rows", rowsArr);
        } catch (Throwable ignored) { }
        viewFlattened = true;
        flatNodes = nodes;
        flatRows = rows;
        return (d, which) -> {
            if (which < 0 || which >= nodes.size()) return;
            android.view.View v = nodes.get(which);
            android.view.View.OnClickListener l = v.clickListener();
            if (l == null) { System.err.println("[ui] 行 " + which + " 没有 OnClickListener，忽略"); return; }
            // 异常必须打出来：jar 的监听器里可能是「dismiss + 重开一个框」（切换启用状态），
            // 也可能是弹扫码框失败。静默吞掉的话宿主只表现为「点了没反应」。
            System.err.println("[ui] 行点击 #" + which + " " + labels.get(which));
            try { l.onClick(v); } catch (Throwable t) {
                Throwable c = t;
                while (c.getCause() != null) c = c.getCause();
                System.err.println("[ui] 行监听器抛异常: " + c.getClass().getName() + ": " + c.getMessage());
                StackTraceElement[] st = c.getStackTrace();
                for (int i = 0; i < Math.min(5, st.length); i++) System.err.println("      at " + st[i]);
            }
            // jar 切「启用中/停用中」是就地 setText（Android 语义），宿主拿的是快照 ——
            // 不重发就表现为「点了没反应」（2026-09-24 实测：监听器无异常但界面不变）。
            refreshRows();
        };
    }

    /** 摊平结果：点击后按当前 View 文本重算行数据用（同一批节点，文字可能已被 jar 改掉）。 */
    private java.util.List<android.view.View> flatNodes;
    private java.util.List<java.util.List<Integer>> flatRows;

    private void refreshRows() {
        if (flatNodes == null || flatRows == null || seq < 0) return;
        try {
            JSONArray out = new JSONArray();
            for (java.util.List<Integer> r : flatRows) {
                JSONArray cells = new JSONArray();
                for (int i : r) cells.put(new JSONObject().put("t", nodeText(flatNodes.get(i))).put("i", i));
                out.put(cells);
            }
            UiBridge.emit(new JSONObject().put("ev", "ui-rows").put("seq", seq).put("rows", out));
        } catch (Throwable t) {
            System.err.println("[ui] 重发行数据失败: " + t);
        }
    }

    /** 节点当前文本（含后代 TextView），空格连接 —— jar 就地 setText 后按它重算。 */
    private static String nodeText(android.view.View v) {
        StringBuilder sb = new StringBuilder();
        textOf(v, sb);
        return sb.toString().trim();
    }

    /** 本次 show 的条目是否来自摊平的自定义视图（决定点击后对话框是否留着）。 */
    protected boolean viewFlattened;

    /**
     * 深度优先找「可点行」。
     * <para>带 OnClickListener 的节点算一行且不再深入。同一容器里有多个可点控件时
     * （网盘行 = 盘名 + 启用/停用两个按钮），除主角外都给标签补上盘名 ——
     * 宿主是平铺列表,光看「停用中」分不清是哪家盘。</para>
     */
    private static void collectRows(android.view.View v, java.util.List<android.view.View> nodes,
                                    java.util.List<String> labels,
                                    java.util.List<java.util.List<Integer>> rows, StringBuilder plain) {
        if (v == null) return;
        if (v.clickListener() != null) {
            labels.add(rowLabel(v, v, nodes.size()));
            nodes.add(v);
            rows.add(java.util.List.of(nodes.size() - 1));
            return;
        }
        if (v instanceof ViewGroup g) {
            java.util.List<android.view.View> kids = clickableChildren(g);
            if (kids.size() > 1) {
                // 一个容器里并排多个可点控件 = 一行（行内不再深入，免得子控件重复成行）
                java.util.List<Integer> row = new java.util.ArrayList<>();
                for (android.view.View k : kids) {
                    labels.add(rowLabel(k, kids.get(0), nodes.size()));
                    nodes.add(k);
                    row.add(nodes.size() - 1);
                }
                rows.add(row);
                return;
            }
            for (int i = 0; i < g.getChildCount(); i++) collectRows(g.getChildAt(i), nodes, labels, rows, plain);
            return;
        }
        StringBuilder sb = new StringBuilder();
        textOf(v, sb);
        String s = sb.toString().trim();
        if (s.length() > 0 && plain.length() < 400) {
            if (plain.length() > 0) plain.append('\n');
            plain.append(s);
        }
    }

    /** 容器的直接可点子控件（按原顺序）——第一个是这一行的「主角」（通常是盘名）。 */
    private static java.util.List<android.view.View> clickableChildren(ViewGroup g) {
        java.util.List<android.view.View> out = new java.util.ArrayList<>();
        for (int i = 0; i < g.getChildCount(); i++) {
            android.view.View k = g.getChildAt(i);
            if (k != null && k.clickListener() != null) out.add(k);
        }
        return out;
    }

    private static String rowLabel(android.view.View node, android.view.View owner, int idx) {
        StringBuilder sb = new StringBuilder();
        textOf(node, sb);
        String own = sb.toString().trim();
        if (own.isEmpty()) return "(第 " + (idx + 1) + " 项)";
        if (owner != node) {
            StringBuilder o = new StringBuilder();
            textOf(owner, o);
            String head = o.toString().trim();
            if (!head.isEmpty()) return own + " ─ " + head;
        }
        return own;
    }

    /** 节点及其后代的可见文本（TextView.getText），空格连接。 */
    private static void textOf(android.view.View v, StringBuilder out) {
        if (v instanceof android.widget.TextView t) {
            String s = String.valueOf(t.getText());
            if (!s.isEmpty() && !"null".equals(s)) {
                if (out.length() > 0) out.append(' ');
                out.append(s);
            }
        }
        if (v instanceof ViewGroup g) {
            for (int i = 0; i < g.getChildCount(); i++) textOf(g.getChildAt(i), out);
        }
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
