package android.os;

import java.util.ArrayList;
import java.util.IdentityHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.TimeUnit;

/**
 * <code>android.os.Handler</code> 桩。
 *
 * <p>⚠ <b>{@code postDelayed} 必须真的在延迟后执行 Runnable</b>。旧实现直接
 * {@code return true} 却不跑任务，于是所有排在延迟队列上的活儿都静默消失 ——
 * 网盘扫码登录就是死在这：二维码轮询拿到登录结果后，「把 cookie 写进
 * SharedPreferences」这一步是 {@code postDelayed} 出去的，任务被丢掉，
 * 结果界面上翻成「已登录」而磁盘一个字节都没变（2026-09-25 实测：
 * 登录成功、TLS 打到 uop.quark.cn、线程也不再抛 NoSuchMethodError，
 * 但 {@code [prefs] put} 计数仍为 0）。</p>
 *
 * <p>桌面没有真正的 Looper，这里用一个常驻守护线程池代跑：延迟语义保住，
 * 同时不阻塞任何调用方。任务抛异常只打印，绝不让线程池线程死掉。</p>
 */
public class Handler {

    private static final ScheduledExecutorService POOL =
            Executors.newScheduledThreadPool(2, r -> {
                Thread t = new Thread(r, "android-handler");
                t.setDaemon(true);
                return t;
            });

    /** 按 Runnable 身份记待执行任务，供 {@link #cancel} 与 removeCallbacks 取消。 */
    private static final Map<Object, ScheduledFuture<?>> SCHEDULED =
            java.util.Collections.synchronizedMap(new IdentityHashMap<>());

    /** 供 {@link android.view.View#postDelayed} 等复用：桌面统一的"延迟执行"落点。 */
    public static ScheduledFuture<?> schedule(Runnable r, long delayMillis) {
        if (r == null) return null;
        // 扫码登录窗口：jar 经 Handler.postDelayed 重挂轮询（网盘登录回执线程的节拍），
        // 与 SystemClock.sleep 同一套伸缩开关——窗口内延迟 ×20，二维码轮询窗口从约 13 秒
        // 拉长到数分钟（jar 按次数计预算，扫不完就放弃，2026-09-25 mitm 实测定位）。
        long scaled = SystemClock.scaleDelay(Math.max(0L, delayMillis));
        ScheduledFuture<?> f = POOL.schedule(() -> {
            SCHEDULED.remove(r);
            try {
                r.run();
            } catch (Throwable t) {
                t.printStackTrace();
            }
        }, scaled, TimeUnit.MILLISECONDS);
        SCHEDULED.put(r, f);
        return f;
    }

    /** 取消某个 Runnable 尚未执行的延迟任务（{@code View.removeCallbacks} 也走这里）。 */
    public static boolean cancel(Runnable r) {
        if (r == null) return false;
        ScheduledFuture<?> f = SCHEDULED.remove(r);
        if (f == null) return false;
        f.cancel(false);
        return true;
    }

    public interface Callback {
        boolean handleMessage(Message m);
    }

    private final Looper looper;
    private final Callback callback;

    public Handler() { this(Looper.getMainLooper(), null); }

    public Handler(Looper looper) { this(looper, null); }

    public Handler(Looper looper, Callback callback) {
        this.looper = looper == null ? Looper.getMainLooper() : looper;
        this.callback = callback;
    }

    public Looper getLooper() { return looper; }

    public boolean post(Runnable r) { return postDelayed(r, 0L); }

    public boolean postAtTime(Runnable r, long uptimeMillis) {
        return postDelayed(r, uptimeMillis - SystemClock.uptimeMillis());
    }

    public boolean postAtTime(Runnable r) { return postDelayed(r, 0L); }

    public boolean postDelayed(Runnable r, long delayMillis) {
        if (r == null) return false;
        schedule(r, delayMillis);
        return true;
    }

    public boolean sendMessage(Message m) { return sendMessageDelayed(m, 0L); }

    public boolean sendMessageDelayed(Message m, long delayMillis) {
        if (m == null) return false;
        schedule(() -> deliver(m), delayMillis);
        return true;
    }

    public boolean sendEmptyMessage(int what) {
        Message m = Message.obtain();
        m.what = what;
        return sendMessageDelayed(m, 0L);
    }

    public boolean sendEmptyMessageDelayed(int what, long delayMillis) {
        Message m = Message.obtain();
        m.what = what;
        return sendMessageDelayed(m, delayMillis);
    }

    private void deliver(Message m) {
        try {
            if (callback != null) callback.handleMessage(m);
        } catch (Throwable t) {
            t.printStackTrace();
        }
    }

    public void removeCallbacks(Runnable r) { cancel(r); }

    public void removeMessages(int what) { /* 桌面不区分 what：任务一旦排入即视为已投递 */ }

    public void removeCallbacksAndMessages(Object token) {
        List<ScheduledFuture<?>> all;
        synchronized (SCHEDULED) { all = new ArrayList<>(SCHEDULED.values()); SCHEDULED.clear(); }
        for (ScheduledFuture<?> f : all) f.cancel(false);
    }
}
