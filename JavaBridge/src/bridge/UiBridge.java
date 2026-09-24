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
        /**
         * 条目来自摊平的自定义 View 树 → 点完**不关框**。
         * <para>Android 里 {@code setItems} 的列表框点一项就自动收，但网盘那种
         * 「盘名 + 启用/停用」的自定义视图框点了要留着（用户会连续切几家盘，
         * 点盘名还会再弹扫码框）。照 setItems 语义一收，宿主就表现为一点即关。</para>
         */
        public final boolean keepOpen;

        public Pending(Dialog d, DialogInterface.OnClickListener i, DialogInterface.OnClickListener p,
                       DialogInterface.OnClickListener n, DialogInterface.OnClickListener ne,
                       DialogInterface.OnCancelListener c, DialogInterface.OnDismissListener dis,
                       boolean keepOpen) {
            dialog = d; items = i; positive = p; negative = n; neutral = ne; cancel = c; dismiss = dis;
            this.keepOpen = keepOpen;
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

    /** 二维码事件载荷：把 Bitmap 的黑白矩阵打包成 {"w","h","pixels","png"}（1=黑；png 是放大 4 倍的 PNG base64）。 */
    public static JSONObject qrJson(Bitmap bm) {
        if (bm == null) return null;
        int[] px = bm.snapshotPixels();
        if (px == null || px.length == 0) return null;
        try {
            int w = bm.getWidth(), h = bm.getHeight();
            JSONArray arr = new JSONArray();
            for (int v : px) arr.put(((v & 0xFF) < 128 && ((v >> 24) & 0xFF) > 0) || ((v & 0xFFFFFF) == 0 && ((v >> 24) & 0xFF) > 0) ? 1 : 0);
            JSONObject o = new JSONObject().put("w", w).put("h", h).put("pixels", arr);
            // 同时出一张 PNG：宿主手搓 24 位 BMP 在 WinUI 上渲染不出来（整页只剩「取消」），
            // 而 java.desktop（ImageIO）在随包的 jlink 运行时里就有，桥侧出图最稳。
            String png = pngB64(px, w, h);
            if (png != null) o.put("png", png);
            return o;
        } catch (Throwable t) {
            return null;
        }
    }

    /** 黑白矩阵 → 放大 4 倍的 PNG（base64，无换行）。任何 AWT 异常都退回 null（宿主仍可用 pixels 兜底）。 */
    private static String pngB64(int[] px, int w, int h) {
        try {
            final int scale = 4;
            int ow = w * scale, oh = h * scale;
            java.awt.image.BufferedImage img = new java.awt.image.BufferedImage(
                    ow, oh, java.awt.image.BufferedImage.TYPE_INT_RGB);
            for (int y = 0; y < oh; y++) {
                int sy = y / scale;
                for (int x = 0; x < ow; x++) {
                    int m = px[sy * w + x / scale];
                    boolean black = (m & 0xFF) < 128 && ((m >> 24) & 0xFF) > 0
                            || (m & 0xFFFFFF) == 0 && ((m >> 24) & 0xFF) > 0;
                    img.setRGB(x, y, black ? 0xFF000000 : 0xFFFFFFFF);
                }
            }
            java.io.ByteArrayOutputStream bos = new java.io.ByteArrayOutputStream();
            if (!javax.imageio.ImageIO.write(img, "png", bos)) return null;
            return java.util.Base64.getEncoder().encodeToString(bos.toByteArray());
        } catch (Throwable t) {
            System.err.println("[ui] 二维码转 PNG 失败（宿主改用 pixels 兜底）: " + t);
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
        final Pending p = PENDING.get(seq);
        if (p == null) return;
        // 摊平的自定义视图框：**只有点条目（which≥0）不收框**，点按钮（取消/确定）照旧收 ——
        // 网盘那种框用户要连续切几家盘；而「取消」就是真取消。Android 的 which 常量：
        // BUTTON_POSITIVE=-1 / NEGATIVE=-2 / NEUTRAL=-3，与宿主回传值一致。
        final boolean stay = p.keepOpen && which >= 0;
        if (!stay) PENDING.remove(seq);
        final Dialog d = p.dialog;
        Thread t = new Thread(() -> {
            try {
                if (which >= 0 && p.items != null) p.items.onClick(d, which);
                else if (which == DialogInterface.BUTTON_POSITIVE && p.positive != null) p.positive.onClick(d, which);
                else if (which == DialogInterface.BUTTON_NEGATIVE && p.negative != null) p.negative.onClick(d, which);
                else if (which == DialogInterface.BUTTON_NEUTRAL && p.neutral != null) p.neutral.onClick(d, which);
            } catch (Throwable ig) { }
            if (stay) return;
            try { if (p.dismiss != null) p.dismiss.onDismiss(d); } catch (Throwable ig) { }
            try { emit(new JSONObject().put("ev", "ui-dismiss").put("seq", seq).put("cancelled", false)); } catch (Throwable ig) { }
        }, "ui-result-" + seq);
        t.setDaemon(true);
        t.start();
    }
}
