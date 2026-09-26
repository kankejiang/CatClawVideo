namespace CatClawVideo.Core.Services;

/// <summary>
/// 本地/局域网调试地址的解析（对位 TVBox <c>ApiConfig.clanToAddress</c> + <c>fixContentPath</c>）。
///
/// <para>TVBox 的三条规则：<c>clan://localhost/x</c> 改写成 <c>http://本机:9978/file/x</c>（绕自己的
/// Web 服务一圈）；<c>clan://&lt;ip:port&gt;/x</c> 改写成 <c>http://&lt;ip:port&gt;/file/x</c>
/// （真正的用途：在 PC 上起个静态服务，盒子直接读还没打包的订阅文件）；<c>file://x</c> 归到 localhost 那一支。</para>
///
/// <para><b>本仓的对应做法</b>：远端那一支照抄（纯地址改写，不引入任何监听端口，也就没有 TVBox
/// 那个「/file 全程无鉴权、局域网里谁都能翻你文件」的问题）；
/// 本机那一支不必绕 HTTP —— 同机直接读文件更短，所以解析成 <c>file://绝对路径</c>，
/// 并**强制不许逃出数据目录**（<c>../</c> 一律拒绝，见 <see cref="Resolve"/>）。</para>
/// </summary>
public static class ClanScheme
{
    const string LocalPrefix = "clan://localhost/";
    const string ClanPrefix = "clan://";

    /// <summary>
    /// 把 clan:///file:// 形态的地址换成可直接使用的形式；不该被放行的写法一律**原样返回**
    /// （让上层按「地址无效」正常报错，而不是拿着一个被悄悄改写过的地址去播）。
    /// </summary>
    public static string Resolve(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url ?? "";
        var text = url.Trim();

        // 1) 普通 http(s) 与本地绝对路径：不动
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return text;

        // 2) clan://localhost/x —— 同机读文件，根目录锁死在 AppPaths.DataRoot 之内
        if (text.StartsWith(LocalPrefix, StringComparison.OrdinalIgnoreCase))
            return FromRoot(text[LocalPrefix.Length..]) ?? text;

        // 3) file://x —— TVBox 把它当 localhost 那一支，这里同义
        if (text.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            var rest = text["file://".Length..];
            // 已经是绝对路径的 file:///d:/… 或 file://C:\… 直接放行
            if (rest.StartsWith('/') || (rest.Length > 2 && rest[1] == ':')) return text;
            return FromRoot(rest) ?? text;
        }

        // 4) clan://<ip:port>/x —— 纯地址改写，指向对方机器的 /file 静态服务
        if (text.StartsWith(ClanPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var rest = text[ClanPrefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash <= 0) return text;                       // 没有主机或没有路径：不当它是合法地址
            var authority = rest[..slash];
            // 校验只看主机名那段：带端口的 authority（192.168.1.7:8000）在 CheckHostName 里是非法串，
            // 直接喂给它会把 TVBox 最常用的「PC 上起服务 + 端口」这一支全判成不合法（N2 就是这么抓出来的）
            var name = authority.Contains(':') ? authority[..authority.IndexOf(':')] : authority;
            if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return text;
            return Uri.CheckHostName(name) == UriHostNameType.Unknown
                ? text
                : "http://" + authority + "/file/" + rest[(slash + 1)..];
        }

        // 5) 裸相对名（"tv.json"）：也按数据目录里的文件试一次，读不到就原样交回
        return FromRoot(text) ?? text;
    }

    /// <summary>相对数据目录解析一个本地文件；越界或不存在都返回 null（调用方据此放弃改写）。</summary>
    static string? FromRoot(string relative)
    {
        try
        {
            var root = Path.GetFullPath(AppPaths.DataRoot);
            var candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

            // 防穿越：规范化后必须仍在数据目录之内（../ 与绝对路径混写都逃不出去）
            if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)) return null;
            if (!File.Exists(candidate)) return null;
            return new Uri(candidate).AbsoluteUri;      // file:///…绝对地址
        }
        catch
        {
            return null;
        }
    }
}
