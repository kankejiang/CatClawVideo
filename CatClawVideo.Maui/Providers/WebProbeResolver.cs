using System.Text.RegularExpressions;

namespace CatClawVideo.Core.Providers;

/// <summary>
/// 通用网页直链嗅探器（猫爪源 resolve 模式的执行端，不针对特定站点）：
/// 拉取页面 → 页面内找 m3u8/mp4（js 变量/属性/任意出现）→ 无则跟进 iframe（最多 2 层）
/// → 相对地址自动按所在页面绝对化。源文件只需存稳定的页面链接，直链（含时效签名）播放时实时获取。
/// </summary>
public static class WebProbeResolver
{
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/124.0");
        return client;
    }

    private static readonly Regex VideoUrlRegex = new(
        @"https?://[^\s""'<>\\]+?\.(?:m3u8|mp4)[^\s""'<>\\]*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>相对直链：引号内含 .m3u8/.mp4 的值（如 const url = "/xxx/index.m3u8?sign=..."）</summary>
    private static readonly Regex RelativeVideoRegex = new(
        @"[""']([^""'\n]+?\.(?:m3u8|mp4)[^""'\n]*)[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>嗅探页面中的播放直链；找不到或超链层数用尽抛出 NotSupportedException。</summary>
    public static async Task<(string Url, string? Referer)> ResolveAsync(
        string pageUrl, string? referer = null, CancellationToken ct = default, int depth = 0)
    {
        if (depth > 2)
            throw new NotSupportedException("嗅探失败：页面嵌套过深，未找到播放直链");

        var html = await GetAsync(pageUrl, referer, ct)
                   ?? throw new NotSupportedException($"嗅探失败：页面拉取失败 {pageUrl}");

        // 1) 页面内直链：任意 .m3u8/.mp4 出现（含 const url = "..."、source src、播放器配置等）
        var hit = VideoUrlRegex.Match(html);
        if (hit.Success)
            return (Absolute(pageUrl, hit.Value), referer);

        // 2) 相对直链（播放器 JS 里常见相对路径），按当前页面绝对化
        var rel = RelativeVideoRegex.Match(html);
        if (rel.Success)
            return (Absolute(pageUrl, rel.Groups[1].Value), referer);

        // 3) 页面内 iframe → 跟进（播放器常见嵌套结构）
        var iframe = Regex.Match(html, @"iframe[^>]*?src\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        if (iframe.Success)
        {
            var iframeUrl = Absolute(pageUrl, System.Net.WebUtility.HtmlDecode(iframe.Groups[1].Value));
            var iframeReferer = new Uri(pageUrl).GetLeftPart(UriPartial.Authority);
            return await ResolveAsync(iframeUrl, iframeReferer, ct, depth + 1);
        }

        throw new NotSupportedException("嗅探失败：页面中未找到播放直链或播放器 iframe");
    }

    private static string Absolute(string baseUrl, string url)
    {
        url = System.Net.WebUtility.HtmlDecode(url).Trim();
        try { return new Uri(new Uri(baseUrl), url).ToString(); }
        catch { return url; }
    }

    private static async Task<string?> GetAsync(string url, string? referer, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(referer))
                req.Headers.Referrer = new Uri(referer);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch { return null; }
    }
}
