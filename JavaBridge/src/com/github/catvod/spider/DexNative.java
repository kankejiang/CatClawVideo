package com.github.catvod.spider;

/**
 * {@code DexNative} 桌面替身（桥内置）。
 *
 * <p>壳框架（BaseSpiderGuard）把解密/签名/真实类获取全部委托给本类的 native 方法；
 * 桌面 JVM 无法执行 ARM so，这里把每个调用转发到 {@code bridge.GuardSession}
 * （常驻 unidbg 会话 + 解壳产物注入），壳框架因此在桌面完整运行——
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
}
