package dalvik.system;

/**
 * {@code DexFile} 编译桩 —— 仅为了让 bridge.jar 在桌面 JDK（--release 17）下编得过。
 *
 * <p>guest 里 {@code Art.stubFirst()} 用真 DexFile.loadDex/loadClass 把 ui_stub.dex 的
 * android 桩定义进「桩优先」加载器；boot 重复类解析给 framework，真类恒赢过本桩，
 * 桌面 JRE 永远不会执行到（stubFirst 只在 ART 路径被调）。</p>
 */
public final class DexFile {

    private DexFile() { }

    public static DexFile loadDex(String sourcePathName, String outputPathName, int flags)
            throws java.io.IOException {
        throw new java.io.IOException("DexFile 桩仅在 guest 真 ART 里可用");
    }

    @SuppressWarnings("rawtypes")
    public Class loadClass(String name, ClassLoader loader) {
        return null;
    }

    public void close() throws java.io.IOException { }
}
