package android.content.pm;

/**
 * <code>android.content.pm.PackageInfo</code> 桩。
 *
 * <p>⚠ <b>字段名必须与真机一致</b>：TVBox 系爬虫会读 <c>signature</c>（<c>Signature[]</c>）
 * 取签名摘要做校验。字段缺失不是 NPE 而是
 * <c>NoSuchFieldError: Class android.content.pm.PackageInfo does not have member field
 * 'android.content.pm.Signature[] signature'</c>，会把整条线程打死
 * （实测 2026-09-16：荐片播放链路的端口调整线程就死在这）。</p>
 */
public class PackageInfo {

    public String packageName = "";

    public String versionName = "1.0.0";

    public int versionCode = 1;

    /** 签名（爬虫常取 toCharsString() 比对）。给稳定非空值，别返回 null。 */
    public Signature[] signature = new Signature[]{ new Signature("catclaw") };

    /** 新 API 的别名，一并给上（有的爬虫读这个）。 */
    public Signature[] signatures = new Signature[]{ new Signature("catclaw") };

    public long firstInstallTime = 0L;

    public long lastUpdateTime = 0L;

    public ApplicationInfo applicationInfo = new ApplicationInfo();
}
