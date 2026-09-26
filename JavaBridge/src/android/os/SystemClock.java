package android.os;

/**
 * <code>android.os.SystemClock</code> 桩。
 *
 * <p>⚠ {@link #sleep(long)} <b>必须有</b>：Guard 系爬虫的网盘登录回执线程
 * （{@code merge.Z.x} → {@code merge.sN.run}）用它给二维码轮询排节奏。缺它抛
 * {@link NoSuchMethodError} 直接把线程打死，登录请求虽然发出去了（TLS 到
 * {@code uop.quark.cn}），cookie 却永远没机会写进 SharedPreferences ——
 * 桌面实测表现就是「扫码当场显示已登录，重启又变未登录，且 data 目录一个字节都没变」
 * （2026-09-25 02:47:56 日志）。</p>
 */
public class SystemClock {

    /**
     * 单调时钟（毫秒）。
     *
     * <p>⚠ 与 {@link #elapsedRealtime()} <b>必须同源</b>：爬虫会做
     * {@code elapsedRealtime() - uptimeMillis() > 超时} 这类差值算术（二维码轮询的过期判定）。
     * 真机上两者出自同一计时、只差睡眠部分；这里一个给 epoch 一个给"开机以来"的话，
     * 差值直接爆成 1.79e12 ⇒ 一上来就判「已过期」⇒ 轮询一次都不发，
     * 只剩每 7 秒重弹一次「需扫码登录」（实测 2026-09-25 03:17）。</p>
     */
    public static long uptimeMillis() { return System.currentTimeMillis(); }

    /**
     * 含睡眠的单调时钟（毫秒）。与 {@link #uptimeMillis()} 同给 epoch 毫秒，理由见上。
     */
    public static long elapsedRealtime() { return System.currentTimeMillis(); }

    /** 同上，epoch 微秒量级。 */
    public static long elapsedRealtimeNanos() { return System.currentTimeMillis() * 1_000_000L; }

    /** 当前线程占用的 CPU 时间（毫秒）。桌面没有 per-thread CPU 时钟，退化成墙上时间。 */
    public static long currentThreadTimeMillis() { return System.currentTimeMillis(); }

    /**
     * 睡眠指定毫秒；被中断时提前结束但不抛。
     * <p>Android 的实现就是这个语义，爬虫据此做轮询退避。</p>
     *
     * <p><b>扫码登录窗口的节拍拉长</b>：Guard 系网盘 jar 的扫码登录循环按<b>次数</b>计重试预算
     * （实测约 13 次、每次 sleep 1 秒 ≈ 13 秒窗口），手机解锁→开夸克 App→扫码→点授权
     * 普遍超过 13 秒，轮询窗口一关确认永远等不到（表现为「扫了码也没用」，2026-09-25 mitm
     * 实测定位：token/出码/轮询/换票请求全部正确，唯独窗口太短）。jar 的轮询节拍全部走本
     * 方法——二维码框存续期间把节拍拉长 20 倍（13 次 ≈ 4.3 分钟），jar 逻辑零改动，
     * 对夸克/UC/百度/阿里等一切走这种轮询的网盘一视同仁。开关由 <c>UiBridge</c> 按二维码
     * 对话框的存续调用。</p>
     */
    private static final int LOGIN_SLEEP_SCALE = 20;

    /**
     * 登录窗口状态在 {@code bridge.UiBridge}（2026-09-26 guest 桩链：本类可能被装进
     * ui_stub.dex 的桩命名空间，UiBridge 在 boot —— 两边静态字段不共享，状态必须只有一份）。
     */
    public static void beginLoginWindow() { bridge.UiBridge.beginLoginWindow(); }

    public static void endLoginWindow() { bridge.UiBridge.endLoginWindow(); }

    private static boolean loginWindowActive() { return bridge.UiBridge.loginWindowActive(); }

    /** 延迟类调用的统一伸缩口：登录窗口内 ×{@value LOGIN_SLEEP_SCALE}，平时原样返回。 */
    public static long scaleDelay(long delayMillis) { return bridge.UiBridge.scaleDelay(delayMillis); }

    public static void sleep(long ms) {
        if (ms <= 0) return;
        if (loginWindowActive()) ms *= LOGIN_SLEEP_SCALE;
        try {
            Thread.sleep(ms);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        }
    }
}
