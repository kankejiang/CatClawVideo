package android.os;

/**
 * <code>android.os.Build</code> 桩。
 *
 * <p>⚠ <b>字段比方法更容易被忽略</b>：字段缺失抛的是 <code>NoSuchFieldError</code>（不是 NPE），
 * 且爬虫常用它做设备指纹/校验。实测 2026-09-16：「瓜子」站因缺 <c>Build.SERIAL</c> 整站 load 失败。</p>
 *
 * <p>原则：返回值要**稳定且像真机**（同一进程内多次读取必须一致，否则爬虫的指纹校验会飘）。</p>
 */
public class Build {

    // ── 设备身份（2026-10-01 起对齐真机：Xiaomi Mi 11 / venus）──
    // 此前报的是 "CatClawVideo-Windows"/"CatClaw" 这种一眼假的身份。壳会把设备指纹报给
    // 服务端（真机 spUtils 里存着 hide_appgz_android_id，说明它确实在用设备身份），
    // 假身份是「真机行、虚拟机不行」类问题（扫码成功却取不到账号信息）的常见根因。
    // ⚠ 同一进程内必须稳定（壳做指纹校验，飘了会重新注册）。
    public static final String MODEL = "M2011K2C";

    public static final String MANUFACTURER = "Xiaomi";

    public static final String BRAND = "Xiaomi";

    public static final String DEVICE = "venus";

    public static final String PRODUCT = "venus";

    public static final String BOARD = "venus";

    public static final String HARDWARE = "qcom";

    public static final String FINGERPRINT = "Xiaomi/venus/venus:13/TKQ1.221114.001/V14.0.6.0.TKBCNXM:user/release-keys";

    /** 序列号：老 API 用（实测「瓜子」站就调它）。与真机一致；给固定值，别用随机。 */
    public static final String SERIAL = "93ea7079";

    /**
     * ABI 字段。实测 2026-09-24：缺失会让 jar 的弹幕服务初始化直接失败 ——
     * <code>NoSuchFieldError: android.os.Build does not have member field 'java.lang.String CPU_ABI'</code>
     * ⇒ 抛「弹幕服务初始化失败」toast，danmaku 相关分支整体不可用。
     * 桌面按 arm64 报（与打包的 android-arm64 产物一致）。
     */
    public static final String CPU_ABI = "arm64-v8a";

    public static final String CPU_ABI2 = "";

    /**
     * ⚠ <b>32 位 ABI 必须为空</b>（2026-09-30 实测根因）：guest 只注册了 arm64 binfmt
     * （<c>ro.product.cpu.abilist32=""</c>），而 jar 的 Go 代理/Guard SO 装载会**优先挑 32 位**
     * —— 报 armeabi-v7a 时它选中 <c>pvideo-armeabi-v7a</c>，guest 里没人能执行 ARM32 ELF，
     * 落到 shell 解析 ⇒ <c>syntax error: unexpected "("</c>，紧接着 guest ART SIGSEGV、
     * 桥进程死亡（每次冷启约 2.5s 后准时发生）。同一个偏好还让 FishCrypto 选
     * <c>libFishGuard-v7-*.so</c> ⇒ <c>is 32-bit instead of 64-bit</c>。
     * 按 guest 真实属性报（见 qemu-src/art/gen_props.py 的 x64 段）：32 位空、64 位只有 arm64。
     */
    public static final String[] SUPPORTED_ABIS = {"arm64-v8a"};

    public static final String[] SUPPORTED_32_BIT_ABIS = {};

    public static final String[] SUPPORTED_64_BIT_ABIS = {"arm64-v8a"};

    /** 部分爬虫读它做设备区分。 */
    public static final long TIME = 1700000000000L;

    public static String getRadioVersion() { return RADIO; }

    public static final String ID = "TKQ1.221114.001";

    public static final String DISPLAY = "TKQ1.221114.001";

    public static final String TYPE = "user";

    public static final String TAGS = "release-keys";

    public static final String HOST = "cmbuild";

    public static final String USER = "builder";

    public static final String BOOTLOADER = "unknown";

    public static final String RADIO = "unknown";

    public static final String VERSION_RELEASE = "13";

    /** 部分爬虫直接读顶层 VERSION_* 常量（历史遗留写法）。 */
    public static final int VERSION_SDK_INT = 33;

    public static class VERSION {

        public static final int SDK_INT = 33;

        public static final String RELEASE = "13";

        public static final String INCREMENTAL = "catclaw";

        public static final String CODENAME = "REL";

        public static final String SECURITY_PATCH = "2024-01-01";

        public static final String BASE_OS = "";

        public static final int PREVIEW_SDK_INT = 0;

        public static final int RESOURCES_SDK_INT = SDK_INT;
    }

    /** 常用版本门槛常量（爬虫里 `if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O)` 这种写法很常见）。 */
    public static class VERSION_CODES {
        public static final int BASE = 1;
        public static final int GINGERBREAD = 9;
        public static final int HONEYCOMB = 11;
        public static final int ICE_CREAM_SANDWICH = 14;
        public static final int JELLY_BEAN = 16;
        public static final int KITKAT = 19;
        public static final int LOLLIPOP = 21;
        public static final int M = 23;
        public static final int N = 24;
        public static final int O = 26;
        public static final int P = 28;
        public static final int Q = 29;
        public static final int R = 30;
        public static final int S = 31;
        public static final int TIRAMISU = 33;
        public static final int UPSIDE_DOWN_CAKE = 34;
    }
}
