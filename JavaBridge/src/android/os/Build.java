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

    public static final String MODEL = "CatClawVideo-Windows";

    public static final String MANUFACTURER = "CatClaw";

    public static final String BRAND = "CatClaw";

    public static final String DEVICE = "catclaw_windows";

    public static final String PRODUCT = "catclaw_windows";

    public static final String BOARD = "catclaw";

    public static final String HARDWARE = "desktop";

    public static final String FINGERPRINT = "catclaw/catclaw_windows/catclaw:13/TQ3A/1:user/release-keys";

    /** 序列号：老 API 用（实测「瓜子」站就调它）。给固定值，别用随机。 */
    public static final String SERIAL = "catclaw0000000001";

    public static final String ID = "TQ3A.230805.001";

    public static final String DISPLAY = "TQ3A.230805.001";

    public static final String TYPE = "user";

    public static final String TAGS = "release-keys";

    public static final String HOST = "catclaw";

    public static final String USER = "catclaw";

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
