package com.github.catvod.crawler;

/**
 * 宿主侧爬虫日志类（逐字移植自 TVBox 参考实现 app/src/main/java/com/github/catvod/crawler/SpiderDebug.java）。
 *
 * <p>为什么必须有：spider jar（含 Guard 加固包解密后的真实实现）会直接调用
 * {@code SpiderDebug.log(...)} / {@code SpiderDebug.ec(...)}，该类在 jar 中不打包，
 * 必须由宿主 App 提供。缺失时类加载抛
 * {@code NoClassDefFoundError: Failed resolution of: Lcom/github/catvod/crawler/SpiderDebug;}
 * —— 因为发生在反射调用链里，最终表现为爬虫 {@code init()} 抛 InvocationTargetException，
 * 外层只能看到一句毫无信息量的英文兜底文案。</p>
 */
public class SpiderDebug {

    public static void log(Throwable th) {
        try {
            android.util.Log.d("SpiderLog", th.getMessage(), th);
        } catch (Throwable th1) {

        }
    }

    public static void log(String msg) {
        try {
            android.util.Log.d("SpiderLog", msg);
        } catch (Throwable th1) {

        }
    }

    public static String ec(int i) {
        return "";
    }
}
