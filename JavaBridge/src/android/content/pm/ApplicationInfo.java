package android.content.pm;
public class ApplicationInfo extends PackageItemInfo {
    public String sourceDir = ".";
    public String nativeLibraryDir = ".";
    public String dataDir = System.getProperty("user.dir") + "/data";
    public String processName = "com.catclaw.video";
    public int flags = 0;
    public String packageName = "com.catclaw.video";
    public String className;
    /**
     * ABI 面（2026-10-01 实测根因）：插件装载原生库时会读这两个字段决定取 arm64 还是 arm32 变体。
     * 原来这两个字段**根本不存在** ⇒ 插件读不到 ABI 信息（配合 {@code Process.is64Bit()} 曾恒 false），
     * 于是去挑 {@code assets/FishGuard-v7.so}（ARM32）；x86_64 guest 只能跑 ARM64
     * （ndk_translation），ARM32 的 dlopen 必失败 → 加密层全灭 → 网盘登录态交换不出 refresh_token
     * → 界面永远「未登录」（cookie 走不需签名的路，所以点播照样能播）。
     * guest 里由 {@code bridge.Art} 填 {@code arm64-v8a}（与 Build.SUPPORTED_ABIS 一致）。
     */
    public String primaryCpuAbi;
    public String secondaryCpuAbi;
}
