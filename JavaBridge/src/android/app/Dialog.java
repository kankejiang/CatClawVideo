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
            // 裸 Dialog 以前根本不找码：三层取码只写在 AlertDialog.fillSpec 里，而壳的扫码二维码
            // 正是 new Dialog + setContentView(qrView) 那条路 ⇒ 宿主只收到 68B 的空框
            // （2026-10-01 实测 seq=3 有框无码）。这里补一次同样的搜索。
            if (!spec.has("qr") && !spec.has("qrText")) {
                JSONObject q = AlertDialog.huntQr(view, false);
                if (q != null) {
                    spec.put("qr", q);
                    System.err.println("[ui] 裸 Dialog 取到码 " + q.optInt("w") + "x" + q.optInt("h")
                            + " png=" + (q.has("png") ? "有" : "无"));
                } else {
                    // 契约通道（与源无关）：没有位图就看有没有登录 URL 文本，有就上行 qrText，
                    // 图由宿主出。裸 Dialog 以前只找位图 ⇒ 只给链接的源在这里也会被丢成空框。
                    String u = AlertDialog.firstUrl(view);
                    if (u != null) {
                        spec.put("qrText", u);
                        System.err.println("[ui] 裸 Dialog 视图树里有登录 URL " + u.length()
                                + " 字符 → 上行 qrText（宿主出码）");
                    } else {
                        System.err.println("[ui] 裸 Dialog 没取到码（视图树 "
                                + (view == null ? "空" : view.getClass().getName()) + "）");
                    }
                }
            }
        } catch (Throwable ignored) { }

        // 自定义 View 树摊平成条目 —— 必须在 register 之前，点击路由要进 Pending
        DialogInterface.OnClickListener itemL = itemsClick();
        if (itemL == null && spec != null) itemL = flattenView(spec);
        final DialogInterface.OnClickListener item = itemL;

        UiBridge.register(seq, new UiBridge.Pending(this, item, posClick(), negClick(), neuClick(),
                onCancel, onDismiss, viewFlattened));
        if (spec != null) UiBridge.shown(seq, spec);
        if (onShow != null) { try { onShow.onShow(this); } catch (Throwable ignored) { } }
        // 记下「当前对话框」并起刷新线程：壳异步取到码后那次 View.invalidate() 才有落点
        dirty = false; qrSent = false; sLastShown = this;
        startRefresherIfNeeded();
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
        // A 路线（2026-10-02）：一遍 DFS 同时产出「树 + 行」，下标分配规则与旧 collectRows
        // 完全一致 ⇒ 点击路由（which = 节点下标）零变化；tree 带全部视觉属性，宿主 1:1 渲染。
        JSONObject tree = treeOf(view, nodes, labels, rows, plain);
        if (nodes.isEmpty()) {
            // 纯展示型视图：没有可点行，但至少把文本搬上去，别留空框
            if (plain.length() > 0 && (message == null || message.length() == 0)) {
                try { spec.put("message", plain.toString()); } catch (Throwable ignored) { }
            }
            // A 路线（2026-10-02）：没有可点行也可能是要展示的界面（「加载中…正在获取账号信息」
            // 进度框）——树照样上行，宿主树页连底部按钮一起渲染，不再掉进 MAUI DisplayAlert。
            try { if (tree != null) spec.put("tree", tree); } catch (Throwable ignored) { }
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
            if (tree != null) spec.put("tree", tree);
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
            System.err.println("[ui] 行点击 #" + which + " " + AlertDialog.mask(labels.get(which)));
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
            JSONObject ev = new JSONObject().put("ev", "ui-rows").put("seq", seq).put("rows", out);
            // A 路线（2026-10-02）：jar 就地 setText 改的不只是可点行（还有「未登录→已登录」这类
            // 状态文本）——整树重序列化上行，宿主树渲染页整树换新。节点下标分配规则不变，
            // 点击路由不受影响。
            if (view != null) {
                java.util.List<android.view.View> nodes2 = new java.util.ArrayList<>();
                java.util.List<String> labels2 = new java.util.ArrayList<>();
                java.util.List<java.util.List<Integer>> rows2 = new java.util.ArrayList<>();
                StringBuilder plain2 = new StringBuilder();
                org.json.JSONObject tree = treeOf(view, nodes2, labels2, rows2, plain2);
                if (tree != null) ev.put("tree", tree);
            }
            UiBridge.emit(ev);
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
     * 深度优先序列化整棵 View 树（A 路线核心，2026-10-02）。
     * <para>与旧 {@code collectRows} 同一套行规则（下标分配完全一致）：</para>
     * <list>带 OnClickListener 的节点 = 一行（记下标、不再深入）；同一容器 ≥2 个可点
     * 子控件 = 一行多格；其余节点照常下钻，叶子文本进 plain。</list>
     * <para>每个节点带视觉属性（文本/字号/颜色/padding/背景色圆角/方向/禁用态/位图），
     * 宿主按它 1:1 还原 jar 作者设计的界面（不再是宿主主题的胶囊按钮）。</para>
     */
    private org.json.JSONObject treeOf(android.view.View v, java.util.List<android.view.View> nodes,
                                       java.util.List<String> labels,
                                       java.util.List<java.util.List<Integer>> rows, StringBuilder plain) {
        if (v == null) return null;
        org.json.JSONObject n = nodeJson(v);
        if (v.clickListener() != null) {
            int idx = nodes.size();
            labels.add(rowLabel(v, v, idx));
            nodes.add(v);
            rows.add(java.util.List.of(idx));
            try {
                n.put("i", idx);
                // 行容器 = 标题+副标题+箭头那组 TextView（jar 的网盘行），文字必须展示出来 ——
                // 行内子树只作为显示内容序列化（displayOf，不带点击下标；点击是整行一个单元）。
                if (v instanceof ViewGroup g) {
                    org.json.JSONArray arr = new org.json.JSONArray();
                    for (int i2 = 0; i2 < g.getChildCount(); i2++) {
                        org.json.JSONObject cn = displayOf(g.getChildAt(i2));
                        if (cn != null) arr.put(cn);
                    }
                    if (arr.length() > 0) n.put("c", arr);
                }
            } catch (Throwable ignored) { }
            return n;
        }
        if (v instanceof ViewGroup g) {
            java.util.List<android.view.View> kids = clickableChildren(g);
            org.json.JSONArray arr = new org.json.JSONArray();
            if (kids.size() > 1) {
                // 一个容器里并排多个可点控件 = 一行（行内不再深入，免得子控件重复成行）
                java.util.List<Integer> row = new java.util.ArrayList<>();
                for (android.view.View k : kids) {
                    int idx = nodes.size();
                    labels.add(rowLabel(k, kids.get(0), idx));
                    nodes.add(k);
                    row.add(idx);
                    org.json.JSONObject kn = nodeJson(k);
                    try {
                        kn.put("i", idx);
                        if (k instanceof ViewGroup kg) {
                            org.json.JSONArray karr = new org.json.JSONArray();
                            for (int i2 = 0; i2 < kg.getChildCount(); i2++) {
                                org.json.JSONObject cn = displayOf(kg.getChildAt(i2));
                                if (cn != null) karr.put(cn);
                            }
                            if (karr.length() > 0) kn.put("c", karr);
                        }
                    } catch (Throwable ignored) { }
                    arr.put(kn);
                }
                rows.add(row);
            } else {
                for (int i = 0; i < g.getChildCount(); i++) {
                    org.json.JSONObject cn = treeOf(g.getChildAt(i), nodes, labels, rows, plain);
                    if (cn != null) arr.put(cn);
                }
            }
            try { if (arr.length() > 0) n.put("c", arr); } catch (Throwable ignored) { }
            return n;
        }
        StringBuilder sb = new StringBuilder();
        textOf(v, sb);
        String s = sb.toString().trim();
        if (s.length() > 0 && plain.length() < 400) {
            if (plain.length() > 0) plain.append('\n');
            plain.append(s);
        }
        return n;
    }

    /** 单个节点的视觉属性（字段名缩写位：树会跟着每个对话框上行，省体积）。 */
    private static org.json.JSONObject nodeJson(android.view.View v) {
        org.json.JSONObject n = new org.json.JSONObject();
        try {
            n.put("k", v.getClass().getSimpleName());
            if (v instanceof android.widget.TextView t) {
                n.put("t", String.valueOf(t.getText()));
                n.put("ts", (double) t.getTextSize());
                n.put("tc", t.getCurrentTextColor());
                int g = t.getGravity();
                if (g >= 0) n.put("g", g);
            }
            if (v instanceof android.widget.ImageView iv && iv.getImageBitmap() != null) {
                org.json.JSONObject img = UiBridge.qrJson(iv.getImageBitmap());
                if (img != null) n.put("img", img);
            }
            int w = v.getWidth(), h = v.getHeight();
            if (w > 0) n.put("w", w);
            if (h > 0) n.put("h", h);
            ViewGroup.LayoutParams lp = v.uiLayoutParams();
            if (lp != null) {
                if (lp.width > 0) n.put("w", lp.width);
                if (lp.height > 0) n.put("h", lp.height);
                if (lp.width == ViewGroup.LayoutParams.MATCH_PARENT) n.put("wm", 1);
                if (lp.height == ViewGroup.LayoutParams.MATCH_PARENT) n.put("hm", 1);
                // weight：真机标题行的「已登录」徽章靠它被推到最右（flex-grow）
                if (lp instanceof android.widget.LinearLayout.LayoutParams llp && llp.weight > 0)
                    n.put("wt", (double) llp.weight);
            }
            int pl = v.uiPadL(), pt = v.uiPadT(), pr = v.uiPadR(), pb = v.uiPadB();
            if (pl != 0 || pt != 0 || pr != 0 || pb != 0) {
                org.json.JSONArray p = new org.json.JSONArray();
                p.put(pl).put(pt).put(pr).put(pb);
                n.put("p", p);
            }
            int color = 0; float radius = 0; boolean has = false;
            if (v.uiBgColorSet()) { color = v.uiBgColor(); has = true; }
            else if (v.uiBgDrawable() instanceof android.graphics.drawable.ColorDrawable cd) {
                color = cd.getColor(); has = true;
            } else if (v.uiBgDrawable() instanceof android.graphics.drawable.GradientDrawable gd) {
                color = gd.getColor(); radius = gd.getCornerRadius(); has = true;
            }
            if (has && (color != 0 || radius > 0)) {
                org.json.JSONObject bg = new org.json.JSONObject().put("c", color);
                if (radius > 0) bg.put("r", (double) radius);
                n.put("bg", bg);
            }
            if (v instanceof android.widget.LinearLayout ll) n.put("o", ll.getOrientation());
            if (v.uiVisibility() != android.view.View.VISIBLE) n.put("gone", 1);
            if (!v.uiEnabled()) n.put("dis", 1);
        } catch (Throwable ignored) { }
        return n;
    }

    /**
     * 展示专用子树序列化（可点行内部）：带视觉属性、不带点击下标、不进 plain/行结构 ——
     * jar 的网盘行内部是「标题+副标题+箭头」那组 TextView，文字必须展示出来。
     */
    private static org.json.JSONObject displayOf(android.view.View v) {
        if (v == null) return null;
        org.json.JSONObject n = nodeJson(v);
        if (v instanceof ViewGroup g && g.getChildCount() > 0) {
            org.json.JSONArray arr = new org.json.JSONArray();
            for (int i = 0; i < g.getChildCount(); i++) {
                org.json.JSONObject cn = displayOf(g.getChildAt(i));
                if (cn != null) arr.put(cn);
            }
            try { if (arr.length() > 0) n.put("c", arr); } catch (Throwable ignored) { }
        }
        return n;
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

    // ── 异步取码回灌（2026-10-01 扫码链路）───────────────────────────────
    /**
     * 最近一次 show 的对话框。壳的登录二维码多是「先弹框 → 异步取回登录 URL → invalidate()」，
     * 桌面没有真重绘管线时这条链路会静默断掉（宿主只收到一个没有码的框）。
     */
    private static volatile Dialog sLastShown;
    private static volatile Thread sRefresher;
    /** 视图树里有人 invalidate() 过 ⇒ 重新取一次码。 */
    private volatile boolean dirty;
    /** 已经补发过二维码 ⇒ 不再刷（一次登录框只出一张码）。 */
    private volatile boolean qrSent;
    /** 补发轮次跳过「等码」重试（见 AlertDialog.fillSpec），避免刷新线程一卡 4s。 */
    volatile boolean quickHunt;

    /** {@link android.view.View#invalidate()} 的上报口：只有当前对话框视图树内的节点才算脏。 */
    public static void noteInvalidate(android.view.View v) {
        Dialog d = sLastShown;
        if (d == null || v == null || d.view == null || d.seq < 0) return;
        if (!nodeIn(d.view, v)) return;
        d.dirty = true;
    }

    private static boolean nodeIn(android.view.View n, android.view.View t) {
        if (n == t) return true;
        if (n instanceof android.view.ViewGroup vg) {
            for (int i = 0; i < vg.getChildCount(); i++) {
                if (nodeIn(vg.getChildAt(i), t)) return true;
            }
        }
        return false;
    }

    /** 盯 120s（240×500ms）：一旦脏了就重建 spec，取到码就按 seq 补发 ui-qr（宿主据此换成扫码页）。 */
    private static void startRefresherIfNeeded() {
        if (sRefresher != null) return;
        Thread t = new Thread(() -> {
            try {
                for (int i = 0; i < 240; i++) {
                    try { Thread.sleep(500); } catch (InterruptedException e) { return; }
                    Dialog d = sLastShown;
                    if (d == null || !d.dirty || d.qrSent || !UiBridge.isPending(d.seq)) continue;
                    d.dirty = false;
                    JSONObject spec = new JSONObject();
                    d.quickHunt = true;
                    try { d.fillSpec(spec); } catch (Throwable ignored) { } finally { d.quickHunt = false; }
                    if (spec.opt("qr") instanceof JSONObject q) {
                        d.qrSent = true;
                        try {
                            UiBridge.emit(new JSONObject().put("ev", "ui-qr").put("seq", d.seq)
                                    .put("title", d.title == null ? "" : d.title.toString()).put("qr", q));
                            System.err.println("[ui] 补发 ui-qr seq=" + d.seq + " " + q.optInt("w") + "x" + q.optInt("h"));
                        } catch (Throwable ignored) { }
                    } else if (spec.opt("qrText") instanceof String u && u.length() > 0) {
                        // 链接是异步才落进视图树的（壳先弹框、拿到 URL 再 setText+invalidate）：
                        // 补发同一 seq 的 ui-qr，但带 qrText —— 宿主自己出码，桩侧不画任何东西。
                        d.qrSent = true;
                        try {
                            UiBridge.emit(new JSONObject().put("ev", "ui-qr").put("seq", d.seq)
                                    .put("title", d.title == null ? "" : d.title.toString()).put("qrText", u));
                            System.err.println("[ui] 补发 ui-qr(qrText) seq=" + d.seq + " " + u.length() + " 字符");
                        } catch (Throwable ignored) { }
                    }
                }
            } finally {
                // 线程到点/中断退出后必须清哨兵，否则之后所有对话框永远走不进 startRefresherIfNeeded
                //（旧版 sRefresher 永不复位 ⇒ 120s 后扫码补发链路整条静默失效）
                if (sRefresher == Thread.currentThread()) sRefresher = null;
            }
        }, "claw-ui-refresh");
        t.setDaemon(true);
        sRefresher = t;
        t.start();
    }

    public void dismiss() { UiBridge.dismissed(this, false); }
    public void cancel() { UiBridge.dismissed(this, true); }
    public boolean isShowing() { return seq >= 0 && UiBridge.isPending(seq); }

    public android.view.Window getWindow() { return new android.view.Window(); }

    // ── 自动补齐：jar 引用到但桩缺失的成员（android-33 签名，空实现）──
    public Dialog(android.content.Context p0, int p1) { }
    public android.content.Context getContext() { return android.app.Application.getInstance(); }
    public android.view.View getCurrentFocus() { return null; }
    public android.view.View findViewById(int p0) { return null; }
    /**
     * ⚠ 必须真的存住内容视图：裸 {@code android.app.Dialog} 的扫码二维码就是走
     * {@code setContentView(qrView)} 上屏的，旧实现是三个空覆写 ⇒ 内容被静默丢掉 ⇒
     * 宿主只收到 68B 的空对话框（2026-10-01 实测 seq=3 有框无码）。
     * show() 之后才 setContentView 的情况也要重取一次码，所以顺手标脏。
     */
    public void setContentView(int p0) { }

    public void setContentView(android.view.View p0) { view = p0; dirty = true; }

    public void setContentView(android.view.View p0, android.view.ViewGroup.LayoutParams p1) {
        view = p0; dirty = true;
    }
    public boolean requestWindowFeature(int p0) { return false; }
    public void setOnKeyListener(android.content.DialogInterface.OnKeyListener p0) { }
}
