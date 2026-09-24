package android.app;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * <code>android.app.ActivityThread</code> 桩。
 *
 * <p>TVBox 系 jar 的公共类 <code>InitOrigin</code> 用反射向系统要「当前进程的 Activity」，
 * 用到的成员名（已用 <code>_scratch_tb/dump_strings.py</code> 从 jar 自己的解密函数还原，
 * 见项目记忆 2026-09-16）：</p>
 * <pre>
 *   Class&lt;?&gt; i = InitOrigin.i;                       // &lt;clinit&gt; 里 Class.forName(...) 存的类
 *   Object thread = i.getDeclaredMethod("currentActivityThread").invoke(null);
 *   Map&lt;?,?&gt; acts = field(thread, "mActivities");
 *   for (record : acts.values()) {
 *       if (record.paused) continue;
 *       return (Activity) record.activity;
 *   }
 * </pre>
 *
 * <p><b>宿主不提供这个类会发生什么</b>：<code>InitOrigin.i</code> 是 null →
 * <code>getActivity()</code>/<code>context()</code> 直接 NPE
 * （<code>Cannot invoke "java.lang.Class.getDeclaredMethod(...)" because "InitOrigin.i" is null</code>），
 * 于是所有走 TVBox 公共类的爬虫（新土豆/影视仓系）都拿不到 Context → 缓存、本机 IP、
 * 播放地址解析里带 <code>http://127.0.0.1:&lt;port&gt;</code> 的本地代理全部失败。</p>
 *
 * <p>本桩用常驻单例 + 一条「未 paused 的 ActivityClientRecord」把 <code>getActivity()</code>
 * 兜住：返回的是 {@link Activity} 桩（本身继承 {@code Context}，能取 SharedPreferences /
 * 文件目录 / WifiManager）。真实 Android 里这是系统类，宿主没有 Activity 也没有关系。</p>
 */
public class ActivityThread {

    /** 与 {@code Context.getPackageName()} 保持一致。 */
    public static final String PACKAGE_NAME = "com.catclaw.video";

    private static volatile ActivityThread sInstance;
    private static volatile Application sApplication;

    /** 反射读取目标（public，兼容 getField 与 getDeclaredField 两种写法）。 */
    public Map<Integer, ActivityClientRecord> mActivities = new LinkedHashMap<>();
    public Application mInitialApplication;
    public List<Application> mAllApplications = new ArrayList<>();
    public Object mBoundApplication;
    public Object mInstrumentation;

    public ActivityThread() {
        mInitialApplication = currentApplication();
        mAllApplications.add(mInitialApplication);
        mActivities.put(0, new ActivityClientRecord());
    }

    /** 系统静态方法（<code>i.getDeclaredMethod("currentActivityThread")</code> 就是找它）。 */
    public static ActivityThread currentActivityThread() {
        ActivityThread t = sInstance;
        if (t == null) {
            synchronized (ActivityThread.class) {
                if (sInstance == null) sInstance = new ActivityThread();
                t = sInstance;
            }
        }
        return t;
    }

    /** 供 <code>InitOrigin.context()</code> / 爬虫直接取 Application。 */
    public static Application currentApplication() {
        Application a = sApplication;
        if (a == null) {
            synchronized (ActivityThread.class) {
                if (sApplication == null) sApplication = new Application();
                a = sApplication;
            }
        }
        return a;
    }

    public static String currentPackageName() { return PACKAGE_NAME; }

    public static String currentOpPackageName() { return PACKAGE_NAME; }

    public static boolean isSystem() { return false; }

    public Application getApplication() { return currentApplication(); }

    public Object getSystemContext() { return currentApplication(); }

    public String getProcessName() { return PACKAGE_NAME; }

    public Object getPackageInfo() { return null; }

    /**
     * Guard 系爬虫的「兜底解密入口」—— 名字和签名都是它自己定的，必须一模一样。
     *
     * <p>字节码实测（{@code merge.Rc.KJ(String)}，2026-09-25 反编译）：
     * <pre>
     *   if (TextUtils.isEmpty(s)) return s;
     *   if (cn.yq) return HideUtils.decrypt(s);                  // so 在本进程加载成功
     *   try {
     *       Method m = InitOrigin.i.getDeclaredMethod("decrypt", String.class);  // i = ActivityThread.class
     *       return (String) m.invoke(null, s);                   // ← 桌面走这条
     *   } catch (Throwable t) { t.printStackTrace(); ... }
     * </pre>
     * 桌面 JVM 是 x64，加载不了 ARM 的 {@code ftyguard_v8.so} ⇒ {@code cn.yq} 恒 false ⇒
     * 只能靠这个反射。缺它时抛的 {@link NoSuchMethodException} 会让
     * {@code Cloud_quark.init} 失败，而壳自己的错误处理器 {@code merge.OW} case1 写的是
     * {@code "初始化失败:" + e.getCause().getMessage()} —— NoSuchMethodException 没有 cause，
     * 于是原始异常被 NPE 吞掉，表现成「我的夸父- 未登录」且 {@code searchContent} 零结果
     * （不发任何网络请求）。</p>
     */
    public static String decrypt(String s) {
        return bridge.GuardSession.decrypt(s);
    }

    /**
     * <code>mActivities</code> 里的记录。字段名与 Android 真机一致（<code>paused</code> /
     * <code>activity</code>），且必须是 <b>public</b>：爬虫用 <code>getDeclaredField</code> 读它们。
     */
    public static class ActivityClientRecord {
        public boolean paused = false;
        public Activity activity = new Activity();
        public Object token = null;
        public Object window = null;
    }
}
