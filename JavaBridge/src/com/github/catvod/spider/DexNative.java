package com.github.catvod.spider;

/**
 * {@code DexNative} 桌面替身（桥内置）。
 *
 * <p>壳框架（BaseSpiderGuard）把解密/签名/真实类获取全部委托给本类的 native 方法；
 * 桌面 JVM 无法执行 ARM so，这里把每个调用转发到 {@code bridge.GuardSession}
 * （转发 QEMU Guard VM + 解壳产物注入），壳框架因此在桌面完整运行——
 * 「已登录+启用中」对话框/扫码/网盘管理全部是 jar 框架自己的实现。</p>
 *
 * <p>⚠️ 加载顺序契约：壳 jar 转换时必须<b>排除</b>原 DexNative.class（转换管线负责），
 * 否则 URLClassLoader child-first 会用到无实现的 native 声明。</p>
 */
public final class DexNative {

    private DexNative() { }

    public static int[] calcResult(int[] input) {
        return bridge.GuardSession.calcResult(input);
    }

    public static String decrypt(String s) {
        return bridge.GuardSession.decrypt(s);
    }

    public static String encrypt(String s) {
        return bridge.GuardSession.encrypt(s);
    }

    public static Object getLoader(Object ctx) {
        return bridge.GuardSession.getLoader(ctx);
    }

    public static Object getSpider(Object loader, String name) {
        return bridge.GuardSession.getSpider(loader, name);
    }

    public static String native_ting_md5(String s) {
        return bridge.GuardSession.md5(s);
    }

    public static String noxSign(String a, String b, String c) {
        return bridge.GuardSession.noxSign(a, b, c);
    }

    public static Object[] proxyInvoke(Object a, Object b) {
        return bridge.GuardSession.proxyInvoke(a, b);
    }

    /**
     * 弹幕本地服务。真机由 ARM so 起一个监听端口；桌面这套已经由宿主的
     * {@code SpiderProxyServer(/proxy?do=danmu)} 承担，所以这里必须**安静返回**。
     *
     * <p>⚠ 旧桩根本没声明这几个方法 ⇒ 爬虫一调就 {@code NoSuchMethodError}，
     * 而调用方是在自己的线程里调的 ⇒ <b>整条线程当场死掉</b>，
     * 表现成「点了没反应／弹幕服务启动失败」（实测 2026-09-25，日志里 27 次）。</p>
     */
    public static void danmuStart() { danmuStart(false); }

    public static void danmuStart(boolean auto) {
        System.err.println("[dexnative] danmuStart(auto=" + auto + ") → 桌面由宿主 /proxy?do=danmu 承担，no-op");
    }

    /** so 侧打开授权网页。桌面交给宿主决定，这里只留痕，绝不抛。 */
    public static void GoWeb() {
        System.err.println("[dexnative] GoWeb() → 桌面 no-op");
    }
}
