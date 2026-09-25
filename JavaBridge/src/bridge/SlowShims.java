package bridge;

import java.util.TimerTask;
import java.util.concurrent.Callable;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.TimeUnit;

/**
 * 延迟类 JDK 调用的伸缩 shim：{@link SleepPatcher} 把 spider jar 里的
 * {@code ScheduledExecutorService.schedule*} / {@code TimeUnit.sleep} / {@code Timer.schedule}
 * 调用点改道到这里——登录窗口内延迟 ×{@code SystemClock} 的伸缩系数，其余场景原样透传。
 * 与 SystemClock.sleep / Thread.sleep 重定向 / Handler.postDelayed 伸缩共同构成全集：
 * Guard 系网盘 jar 的扫码轮询窗口（约 13 次 × 节拍）因此可以从约 13 秒拉长到数分钟。
 */
public final class SlowShims {

    private SlowShims() { }

    private static long scale(long delay) {
        return android.os.SystemClock.scaleDelay(delay);
    }

    // ── ScheduledExecutorService.schedule（Runnable / Callable 两种重载）──

    public static ScheduledFuture<?> schedule(ScheduledExecutorService self,
                                              Runnable task, long delay, TimeUnit unit) {
        return self.schedule(task, scale(delay), unit);
    }

    public static <V> ScheduledFuture<?> schedule(ScheduledExecutorService self,
                                                  Callable<V> task, long delay, TimeUnit unit) {
        return self.schedule(task, scale(delay), unit);
    }

    public static ScheduledFuture<?> scheduleAtFixedRate(ScheduledExecutorService self,
                                                         Runnable task, long initialDelay, long period, TimeUnit unit) {
        return self.scheduleAtFixedRate(task, scale(initialDelay), scale(period), unit);
    }

    public static ScheduledFuture<?> scheduleWithFixedDelay(ScheduledExecutorService self,
                                                            Runnable task, long initialDelay, long delay, TimeUnit unit) {
        return self.scheduleWithFixedDelay(task, scale(initialDelay), scale(delay), unit);
    }

    // ── TimeUnit.sleep（内部 parkNanos，只能拦调用点）──

    public static void sleep(TimeUnit self, long timeout) throws InterruptedException {
        self.sleep(scale(timeout));
    }

    // ── Object.wait（轮询循环若用监视器等待，也只能拦调用点；调用点已持锁，可重入）──

    public static void wait(Object monitor, long timeout) throws InterruptedException {
        synchronized (monitor) {
            monitor.wait(scale(timeout));
        }
    }

    public static void wait(Object monitor, long timeout, int nanos) throws InterruptedException {
        synchronized (monitor) {
            monitor.wait(scale(timeout), nanos);
        }
    }

    // ── java.util.Timer.schedule（一次性 + 固定速率 + 固定延迟）──

    public static void schedule(java.util.Timer self, TimerTask task, long delay) {
        self.schedule(task, scale(delay));
    }

    public static void schedule(java.util.Timer self, TimerTask task, long delay, long period) {
        self.schedule(task, scale(delay), scale(period));
    }

    public static void schedule(java.util.Timer self, TimerTask task, java.util.Date firstTime, long period) {
        self.schedule(task, firstTime, scale(period));
    }

    public static void scheduleAtFixedRate(java.util.Timer self, TimerTask task, long delay, long period) {
        self.scheduleAtFixedRate(task, scale(delay), scale(period));
    }

    public static void scheduleAtFixedRate(java.util.Timer self, TimerTask task, java.util.Date firstTime, long period) {
        self.scheduleAtFixedRate(task, firstTime, scale(period));
    }
}
