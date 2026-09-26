package bridge;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.Iterator;
import java.util.Map;
import java.util.Set;
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
 *
 * <p><b>命名空间约束（2026-09-26 guest 桩链定死）</b>：本类在 boot（gb.dex）里，而调用方
 * （android.app.Dialog/Toast 桩）在 guest 的 ui_stub.dex「桩优先」命名空间里 —— 同名
 * android.* 在两边解析成<b>不同的 Class 对象</b>。所以本类 API 一律收发 {@code Object}
 * （stub Dialog 实例、桩 listener、stub Bitmap），对桩专有方法（snapshotPixels /
 * onClick 回调）走反射 —— 否则桩侧调用点因类型身份不符直接 ICCE/校验失败。
 * JRE 桥里桩与桥同 jar，同一份代码行为不变。</p>
 */
public final class UiBridge {

    private static final AtomicInteger SEQ = new AtomicInteger(1);
    /** seq → 挂起的对话框（listeners 与 dialog 引用）。 */
    private static final Map<Integer, Pending> PENDING = new ConcurrentHashMap<>();
    /** 在屏的二维码登录框 seq 集合：期间 SystemClock 进入慢速节拍（jar 的扫码轮询只有约 13 次预算，须拉长窗口）。 */
    private static final Set<Integer> QR_SEQS = java.util.Collections.newSetFromMap(new ConcurrentHashMap<>());

    /**
     * 扫码登录窗口状态（原在 android.os.SystemClock 桩里 —— 桩可能在 stub 命名空间而本类在
     * boot，两边静态字段不共享；搬到这里两边都够得着）。
     * <p>二维码框存续期间把 jar 的轮询节拍拉长 {@value #LOGIN_SLEEP_SCALE} 倍
     * （13 次 ≈ 4.3 分钟），开关由二维码对话框的存续调用；10 分钟自愈上限防漏减。</p>
     */
    private static final int LOGIN_SLEEP_SCALE = 20;
    private static volatile int loginWindows;
    private static volatile long loginWindowStartMs;

    /** 二维码登录框开始展示：进入慢速节拍（可嵌套，按计数归零退出）。 */
    public static void beginLoginWindow() {
        if (loginWindows == 0) loginWindowStartMs = System.currentTimeMillis();
        loginWindows++;
    }

    /** 二维码登录框关闭：退出慢速节拍。 */
    public static void endLoginWindow() {
        if (loginWindows > 0) loginWindows--;
    }

    /** 扫码登录窗口是否生效。 */
    public static boolean loginWindowActive() {
        return loginWindows > 0
                && System.currentTimeMillis() - loginWindowStartMs < 10 * 60_000L;
    }

    /** 延迟类调用的统一伸缩口：登录窗口内 ×{@value LOGIN_SLEEP_SCALE}，平时原样返回。 */
    public static long scaleDelay(long delayMillis) {
        return loginWindowActive() ? delayMillis * LOGIN_SLEEP_SCALE : delayMillis;
    }

    /** 一个已 show 对话框的全部回调。全部 {@code Object}：调用方在桩命名空间（见类注释）。 */
    public static final class Pending {
        public final Object dialog;
        public final Object items;
        public final Object positive;
        public final Object negative;
        public final Object neutral;
        public final Object cancel;
        public final Object dismiss;
        /**
         * 条目来自摊平的自定义 View 树 → 点完**不关框**。
         * <para>Android 里 {@code setItems} 的列表框点一项就自动收，但网盘那种
         * 「盘名 + 启用/停用」的自定义视图框点了要留着（用户会连续切几家盘，
         * 点盘名还会再弹扫码框）。照 setItems 语义一收，宿主就表现为一点即关。</para>
         */
        public final boolean keepOpen;

        public Pending(Object d, Object i, Object p, Object n, Object ne,
                       Object c, Object dis, boolean keepOpen) {
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
        try {
            // 二维码登录框在屏：进入慢速节拍，把 jar 的扫码轮询窗口从约 13 秒拉长到数分钟
            if (spec != null && spec.has("qr") && QR_SEQS.add(seq)) {
                beginLoginWindow();
                System.err.println("[ui] 二维码登录框 #" + seq + " → 慢速节拍开启（当前窗口 " + QR_SEQS.size() + "）");
            }
            spec.put("ev", "ui-dialog").put("seq", seq);
            emit(spec);
        } catch (Throwable ignored) { }
    }

    /** 二维码框关闭（dismiss / 用户取消两条路径都走这里）：最后一个关闭时退出慢速节拍。 */
    private static void onQrDialogClosed(int seq) {
        if (seq > 0 && QR_SEQS.remove(seq) && QR_SEQS.isEmpty()) {
            endLoginWindow();
            System.err.println("[ui] 二维码登录框 #" + seq + " → 慢速节拍关闭");
        }
    }

    /** Dialog.dismiss()/cancel()：上行关窗事件并触发 jar 的 dismiss/cancel 监听。 */
    public static void dismissed(Object d, boolean cancelled) {
        Integer found = null;
        Pending p = null;
        for (Iterator<Map.Entry<Integer, Pending>> it = PENDING.entrySet().iterator(); it.hasNext(); ) {
            Map.Entry<Integer, Pending> e = it.next();
            if (e.getValue().dialog == d) { found = e.getKey(); p = e.getValue(); it.remove(); break; }
        }
        if (found == null) return;
        try {
            emit(new JSONObject().put("ev", "ui-dismiss").put("seq", (int) found).put("cancelled", cancelled));
        } catch (Throwable ignored) { }
        onQrDialogClosed(found);
        if (p != null) {
            try { if (cancelled && p.cancel != null) invoke1(p.cancel, "onCancel", d); } catch (Throwable ignored) { }
            try { if (p.dismiss != null) invoke1(p.dismiss, "onDismiss", d); } catch (Throwable ignored) { }
        }
    }

    /** Toast.show()：上行提示事件（宿主自行决定展示方式）。 */
    public static void toast(CharSequence text) {
        // 诊断：CheckValidityCk 等关键提示的来源定位（打印调用栈到 stderr）
        if (text != null && String.valueOf(text).startsWith("CheckValidity")) {
            new Exception("[toast-src] " + text).printStackTrace();
        }
        try { emit(new JSONObject().put("ev", "ui-toast").put("text", text == null ? "" : text.toString())); } catch (Throwable ignored) { }
    }

    /** 二维码事件载荷：把 Bitmap 的黑白矩阵打包成 {"w","h","pixels","png"}（1=黑；png 是放大 4 倍的 PNG base64）。 */
    public static JSONObject qrJson(Object bm) {
        if (bm == null) return null;
        try {
            // 桩命名空间的 Bitmap：反射取桩专有的 snapshotPixels（boot 里没有这个方法）
            java.lang.reflect.Method mPx = bm.getClass().getMethod("snapshotPixels");
            mPx.setAccessible(true);
            int[] px = (int[]) mPx.invoke(bm);
            if (px == null || px.length == 0) return null;
            int w = ((Number) bm.getClass().getMethod("getWidth").invoke(bm)).intValue();
            int h = ((Number) bm.getClass().getMethod("getHeight").invoke(bm)).intValue();
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
        final Object d = p.dialog;
        Thread t = new Thread(() -> {
            try {
                if (which >= 0 && p.items != null) invokeClick(p.items, d, which);
                else if (which == -1 && p.positive != null) invokeClick(p.positive, d, which);
                else if (which == -2 && p.negative != null) invokeClick(p.negative, d, which);
                else if (which == -3 && p.neutral != null) invokeClick(p.neutral, d, which);
            } catch (Throwable ig) { }
            if (stay) return;
            try { if (p.dismiss != null) invoke1(p.dismiss, "onDismiss", d); } catch (Throwable ig) { }
            onQrDialogClosed(seq);
            try { emit(new JSONObject().put("ev", "ui-dismiss").put("seq", seq).put("cancelled", false)); } catch (Throwable ig) { }
        }, "ui-result-" + seq);
        t.setDaemon(true);
        t.start();
    }

    /**
     * 反射调桩 listener 的 {@code onClick(dialog, which)}。
     * listener 在桩命名空间（其 onClick 参数是<b>桩</b> DialogInterface），本类在 boot ——
     * 直接调会类型身份不符，按名字+形状匹配方法反射调（JRE 里同样成立）。
     */
    private static void invokeClick(Object listener, Object dialog, int which) throws Exception {
        for (java.lang.reflect.Method m : listener.getClass().getMethods()) {
            if (!m.getName().equals("onClick") || m.getParameterCount() != 2) continue;
            Class<?>[] pt = m.getParameterTypes();
            if (pt[1] != int.class) continue;
            m.setAccessible(true);
            m.invoke(listener, dialog, which);
            return;
        }
    }

    /** 反射调桩 listener 的单参回调（onCancel / onDismiss）。 */
    private static void invoke1(Object listener, String name, Object dialog) throws Exception {
        for (java.lang.reflect.Method m : listener.getClass().getMethods()) {
            if (!m.getName().equals(name) || m.getParameterCount() != 1) continue;
            m.setAccessible(true);
            m.invoke(listener, dialog);
            return;
        }
    }
}
