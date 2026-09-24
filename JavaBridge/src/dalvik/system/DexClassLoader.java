package dalvik.system;

/**
 * <code>dalvik.system.DexClassLoader</code> 桌面替身。
 *
 * <p><b>为什么需要它：</b>Guard 壳框架的 <code>com.github.catvod.spider.Init</code> 里有一个
 * <b>非静态</b>、声明类型为 {@code dalvik.system.DexClassLoader} 的字段：
 * <pre>
 *   private ClassLoader oOo0oOo0Oo0oO0Oo;
 *   private dalvik.system.DexClassLoader oOoOoOo0oOo0o0oO;   // ← 这个
 *   public static dalvik.system.DexClassLoader loader();
 * </pre>
 * TVBox 的 {@code ProtectedInitJar.bindDexLoader} 会把
 * {@code DexNative.getLoader()} 返回的 DexClassLoader <b>反射注入到该字段</b>；
 * 不注入则 {@code Init.loader()/classLoader()} 返回 <b>null</b>，jar 内部所有
 * {@code loadClass} 全部失效。</p>
 *
 * <p><b>桌面怎么实现：</b>Android 的 DexClassLoader 本质就是「按路径加载类的 ClassLoader」。
 * 我们的 dex 已被转换管线转成 jar，所以直接继承 {@code URLClassLoader} 即可 —— 行为等价，
 * 且<b>类型名与真实 Android 完全一致</b>，反射注入时 {@code isAssignableFrom} 才能命中。</p>
 */
public class DexClassLoader extends java.net.URLClassLoader {

    /**
     * 签名与 Android 一致（多出的 optimizedDirectory/librarySearchPath 在桌面无意义，保留以对齐契约）。
     *
     * @param dexPath             jar/目录路径（桌面传解壳产物 .jar）
     * @param optimizedDirectory  忽略（Android 的 odex 输出目录）
     * @param librarySearchPath   忽略（so 搜索路径）
     * @param parent              父加载器
     */
    public DexClassLoader(String dexPath, String optimizedDirectory, String librarySearchPath, ClassLoader parent) {
        super(buildUrls(dexPath), parent);
    }

    /** 免 optimizedDirectory 的便捷重载（桥内自用）。 */
    public DexClassLoader(String dexPath, ClassLoader parent) {
        super(buildUrls(dexPath), parent);
    }

    private static java.net.URL[] buildUrls(String dexPath) {
        try {
            return new java.net.URL[]{new java.io.File(dexPath).toURI().toURL()};
        } catch (Exception e) {
            throw new RuntimeException("DexClassLoader 构造失败: " + dexPath, e);
        }
    }

    /** 兼容 Android 的 getPath 查询（部分壳会调）。 */
    public String getDexPath() {
        java.net.URL[] urls = getURLs();
        return urls.length > 0 ? urls[0].getPath() : "";
    }

    @Override
    public String toString() {
        java.net.URL[] urls = getURLs();
        return "DexClassLoader[" + (urls.length > 0 ? urls[0].getPath() : "") + "]";
    }
}
