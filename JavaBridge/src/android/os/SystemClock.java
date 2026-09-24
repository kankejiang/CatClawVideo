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

    private static final long START = System.nanoTime();

    /** 单调时钟（毫秒），Android 语义：不含深度睡眠时间。桌面用 nanoTime 起点差值近似。 */
    public static long uptimeMillis() { return (System.nanoTime() - START) / 1_000_000L; }

    /** 含睡眠的单调时钟（毫秒）。 */
    public static long elapsedRealtime() { return uptimeMillis(); }

    /** 含睡眠的单调时钟（纳秒）。 */
    public static long elapsedRealtimeNanos() { return (System.nanoTime() - START) / 1L; }

    /** 当前线程占用的 CPU 时间（毫秒）。桌面没有 per-thread CPU 时钟，退化成墙上时间。 */
    public static long currentThreadTimeMillis() { return System.currentTimeMillis(); }

    /**
     * 睡眠指定毫秒；被中断时提前结束但不抛。
     * <p>Android 的实现就是这个语义，爬虫据此做轮询退避。</p>
     */
    public static void sleep(long ms) {
        if (ms <= 0) return;
        try {
            Thread.sleep(ms);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        }
    }
}
