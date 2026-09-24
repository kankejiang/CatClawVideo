package bridge;

import android.app.Dialog;
import android.content.DialogInterface;
import android.graphics.Bitmap;
import org.json.JSONArray;
import org.json.JSONObject;

import java.util.Iterator;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * 爬虫 UI → 宿主的事件桥。
 *
 * <p>TVBox 里 Guard 系网盘源（csp_MyDriveGuard 等）点「登入自己网盘」会<b>主动弹 Android
 * 对话框</b>（「已登录+启用中」列表 → 扫码二维码）。桌面桥没有 Activity/窗口栈，
 * {@code AlertDialog} 桩把 show() 转成 JSON 事件经 stdout 上行，宿主（C#/MAUI）渲染
 * 原生对话框并把用户操作经 {@code ui-result} op 回传，桥侧回调 jar 的 listener——
 * 交互与 TVBox 完全对齐（2026-09-24 用户实测需求）。</p>
 *
 * <p>事件形态（stdout 每行一条 JSON，与请求-响应共存；响应按 id 匹配、事件按 ev 字段识别）：
 * <pre>
 * → {"ev":"ui-dialog","seq":1,"title":..,"message":..,"items":[..],"positive":..,"negative":..,"qr":{w,h,pixels}}
 * → {"ev":"ui-dismiss","seq":1,"cancelled":false}
 * → {"ev":"ui-toast","text":".."}
 * ← {"id":-1,"op":"ui-result","seq":1,"which":0}   （which≥0=列表项，-1/-2/-3=肯定/否定/中性按钮）
 * </pre></p>
 */
public final class UiBridge {

    private static final AtomicInteger SEQ = new AtomicInteger(1);
    /** seq → 挂起的对话框（listeners 与 dialog 引用）。 */
    private static final Map<Integer, Pending> PENDING = new ConcurrentHashMap<>();

    /** 一个已 show 对话框的全部回调。 */
    public static final class Pending {
        public final Dialog dialog;
        public final DialogInterface.OnClickListener items;
        public final DialogInterface.OnClickListener positive;
        public final DialogInterface.OnClickListener negative;
        public final DialogInterface.OnClickListener neutral;
        public final DialogInterface.OnCancelListener cancel;
        public final DialogInterface.OnDismissListener dismiss;

        public Pending(Dialog d, DialogInterface.OnClickListener i, DialogInterface.OnClickListener p,
                       DialogInterface.OnClickListener n, DialogInterface.OnClickListener ne,
                       DialogInterface.OnCancelListener c, DialogInterface.OnDismissListener dis) {
            dialog = d; items = i; positive = p; negative = n; neutral = ne; cancel = c; dismiss = dis;
        }
    }

    private UiBridge() { }

    /** 上行一条事件（PrintStream 内部 synchronized，单行 JSON 不会被并发拆散）。 */
    public static void emit(JSONObject ev) {
        try { System.out.println(ev.toString()); System.out.flush(); } catch (Throwable ignored) { }
    }

    public static int nextSeq() { return SEQ.getAndIncrement(); }

    public static void register(int seq, Pending p) { PENDING.put(seq, p); }

    public static boolean isPending(int seq) { return PENDING.containsKey(seq); }

    /** Dialog.show()：上行 ui-dialog 事件（spec 由 Dialog.fillSpec 组装）。 */
    public static void shown(int seq, JSONObject spec) {
        try { spec.put("ev", "ui-dialog").put("seq", seq); emit(spec); } catch (Throwable ignored) { }
    }

    /** Dialog.dismiss()/cancel()：上行关窗事件并触发 jar 的 dismiss/cancel 监听。 */
    public static void dismissed(Dialog d, boolean cancelled) {
        Integer found = null;
        Pending p = null;
        for (Iterator<Map.Entry<Integer, Pending>> it = PENDING.entrySet().iterator(); it.hasNext(); ) {
            Map.Entry<Integer, Pending> e = it.next();
            if (e.getValue().dialog == d) { found = e.getKey(); p = e.getValue(); it.remove(); break; }
        }
        if (found == null) return;
        try { emit(new JSONObject().put("ev", "ui-dismiss").put("seq", (int) found).put("cancelled", cancelled)); } catch (Throwable ignored) { }
        if (p != null) {
            try { if (cancelled && p.cancel != null) p.cancel.onCancel(d); } catch (Throwable ignored) { }
            try { if (p.dismiss != null) p.dismiss.onDismiss(d); } catch (Throwable ignored) { }
        }
    }

    /** Toast.show()：上行提示事件（宿主自行决定展示方式）。 */
    public static void toast(CharSequence text) {
        try { emit(new JSONObject().put("ev", "ui-toast").put("text", text == null ? "" : text.toString())); } catch (Throwable ignored) { }
    }

    /** 二维码事件载荷：把 Bitmap 的黑白矩阵打包成 {"w":..,"h":..,"pixels":[1,0,..]}（1=黑）。 */
    public static JSONObject qrJson(Bitmap bm) {
        if (bm == null) return null;
        int[] px = bm.snapshotPixels();
        if (px == null || px.length == 0) return null;
        try {
            int w = bm.getWidth(), h = bm.getHeight();
            JSONArray arr = new JSONArray();
            for (int v : px) arr.put(((v & 0xFF) < 128 && ((v >> 24) & 0xFF) > 0) || ((v & 0xFFFFFF) == 0 && ((v >> 24) & 0xFF) > 0) ? 1 : 0);
            return new JSONObject().put("w", w).put("h", h).put("pixels", arr);
        } catch (Throwable t) {
            return null;
        }
    }

    /**
     * 宿主回传用户操作（Server 主循环收到 op=ui-result 分发到这里）。
     * 在独立线程执行 listener：回调里 jar 可能继续弹下一个框（扫码框）/发网络请求。
     * Android 语义：点击按钮/列表项后对话框自动关闭（触发 dismiss 监听 + 上行 ui-dismiss）。
     */
    public static void dispatchResult(JSONObject req) {
        final int seq = req.optInt("seq", -1);
        final int which = req.optInt("which", -2);
        Pending p = PENDING.remove(seq);
        if (p == null) return;
        final Dialog d = p.dialog;
        Thread t = new Thread(() -> {
            try {
                if (which >= 0 && p.items != null) p.items.onClick(d, which);
                else if (which == DialogInterface.BUTTON_POSITIVE && p.positive != null) p.positive.onClick(d, which);
                else if (which == DialogInterface.BUTTON_NEGATIVE && p.negative != null) p.negative.onClick(d, which);
                else if (which == DialogInterface.BUTTON_NEUTRAL && p.neutral != null) p.neutral.onClick(d, which);
            } catch (Throwable ig) { }
            try { if (p.dismiss != null) p.dismiss.onDismiss(d); } catch (Throwable ig) { }
            try { emit(new JSONObject().put("ev", "ui-dismiss").put("seq", seq).put("cancelled", false)); } catch (Throwable ig) { }
        }, "ui-result-" + seq);
        t.setDaemon(true);
        t.start();
    }
}
