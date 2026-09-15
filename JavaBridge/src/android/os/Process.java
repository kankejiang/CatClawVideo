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

    public static int myPid() { return (int) ProcessHandle.current().pid(); }
    public static int myTid() { return (int) Thread.currentThread().getId(); }
    public static int myUid() { return 10000; }
    public static int getUidForPid(int pid) { return -1; }
    public static int getThreadPriority(int tid) { return THREAD_PRIORITY_DEFAULT; }
    public static void setThreadPriority(int priority) { }
    public static void setThreadPriority(int tid, int priority) { }
    public static void setPriority(int priority) { }
    public static boolean is64Bit() { return "64".equals(System.getProperty("sun.arch.data.model")); }
    public static void killProcess(int pid) { }
    public static void sendSignal(int pid, int signal) { }
}
