package android.os;

/** Process 桩：进程/线程优先级相关，取值与真实语义相近即可。 */
public class Process {

    public static final int THREAD_PRIORITY_DEFAULT = 0;
    public static final int THREAD_PRIORITY_LOWEST = 19;
    public static final int THREAD_PRIORITY_BACKGROUND = 10;
    public static final int THREAD_PRIORITY_FOREGROUND = -2;
    public static final int THREAD_PRIORITY_DISPLAY = -4;
    public static final int THREAD_PRIORITY_URGENT_DISPLAY = -8;
    public static final int THREAD_PRIORITY_AUDIO = -16;
    public static final int THREAD_PRIORITY_URGENT_AUDIO = -19;
    public static final int THREAD_PRIORITY_MORE_FAVORABLE = -1;
    public static final int THREAD_PRIORITY_LESS_FAVORABLE = 1;

    public static final int SIGNAL_KILL = 9;
    public static final int SIGNAL_QUIT = 3;
    public static final int SIGNAL_USR1 = 10;

    /**
     * ⚠ 不能写 {@code ProcessHandle.current().pid()}：那是 JDK9 类，ART guest（Android 13 精简
     * core）里没有，解析即 {@code NoClassDefFoundError: java.lang.ProcessHandle} —— 实测磁力源
     * 播放时弹幕线程 {@code DexNative.danmuStart} 就崩在这（2026-09-30）。
     * 改读 {@code /proc/self/stat} 的首字段（Linux/Android 通用），读不到再退回 1。
     */
    public static int myPid() {
        try {
            java.io.RandomAccessFile f = new java.io.RandomAccessFile("/proc/self/stat", "r");
            try {
                String line = f.readLine();
                if (line != null) return Integer.parseInt(line.substring(0, line.indexOf(' ')).trim());
            } finally {
                f.close();
            }
        } catch (Throwable ignored) { }
        try {
            String name = java.lang.management.ManagementFactory.getRuntimeMXBean().getName();
            int at = name.indexOf('@');
            if (at > 0) return Integer.parseInt(name.substring(0, at));
        } catch (Throwable ignored) { }
        return 1;
    }
    public static int myTid() { return (int) Thread.currentThread().getId(); }
    public static int myUid() { return 10000; }
    public static int getUidForPid(int pid) { return -1; }
    public static int getThreadPriority(int tid) { return THREAD_PRIORITY_DEFAULT; }
    public static void setThreadPriority(int priority) { }
    public static void setThreadPriority(int tid, int priority) { }
    public static void setPriority(int priority) { }
    /**
     * ⚠️ ART guest 里**必须**返回 true（2026-10-01 实测根因）：
     * <p>原实现只读 {@code sun.arch.data.model} —— 那是 <b>HotSpot 专有属性，ART 里不存在</b>，
     * {@code getProperty} 返回 null ⇒ 恒 false ⇒ 插件判定「32 位设备」，去加载
     * {@code assets/FishGuard-v7.so}（ARM32）而不是 {@code FishGuard-v8.so}（ARM64）；
     * x86_64 guest 只能跑 ARM64（ndk_translation），ARM32 的 dlopen 必失败：
     * <pre>UnsatisfiedLinkError: dlopen failed: ".../libFishGuard-v7-….so" is 32-bit instead of 64-bit</pre>
     * 加密层因此全灭（FishCrypto 从未加载成功）→ 网盘登录态交换不出 refresh_token →
     * 界面永远显示「未登录」（而 cookie 走不需签名的路，所以点播照样能播）。</p>
     * <p>桌面 JRE 桥有该属性，行为不变；属性缺失时按 64 位处理 —— 本 guest 是
     * x86_64 + arm64 用户态转译，32 位 ARM 在这里根本不能执行，回去只会更坏。</p>
     */
    public static boolean is64Bit() {
        String m = System.getProperty("sun.arch.data.model");
        return m == null || m.isEmpty() || "64".equals(m);
    }
    public static void killProcess(int pid) { }
    public static void sendSignal(int pid, int signal) { }
}
