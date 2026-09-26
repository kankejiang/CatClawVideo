using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CatClawVideo.Core.Providers;

/// <summary>
/// TVBox 解析引擎：parses type=1 JSON 接口并发解析 + 视频地址判定正则。
/// 对照 TVBox OSC：JsonParallel.java（并发先到先用）、Utils.jsonParse（返回体提取）、
/// DefaultConfig.snifferMatch（视频正则）、ParseBean.mixUrl（cat_ext 注入）。
/// </summary>
public static partial class TvBoxParseEngine
{
    /// <summary>视频地址判定（snifferMatch 移植）：嗅探与 JSON 解析共用</summary>
    [GeneratedRegex(
        "http((?!http).){12,}?\\.(m3u8|mp4|flv|avi|mkv|rm|wmv|mpg|m4a|mp3|aac|mpd)\\?.*|" +
        "http((?!http).){12,}\\.(m3u8|mp4|flv|avi|mkv|rm|wmv|mpg|m4a|mp3|aac|mpd)|" +
        "http((?!http).)*?video/tos|" +
        "http((?!http).){20,}?/m3u8\\?pt=m3u8.*|" +
        "http((?!http).)*?default\\.ixigua\\.com/.*|" +
        "http((?!http).)*?dycdn-tos\\.pstatp[^\\?]*|" +
        "http.*?/player/m3u8play\\.php\\?url=.*|" +
        "http.*?/player/.*?[pP]lay\\.php\\?url=.*|" +
        "http.*?/playlist/m3u8/\\?vid=.*|" +
        "http.*?\\.php\\?type=m3u8&.*|" +
        "http.*?/download\\.aspx\\?.*|" +
        "http.*?/api/up_api\\.php\\?.*|" +
        "https.*?\\.66yk\\.cn.*|" +
        "http((?!http).)*?netease\\.com/file/.*")]
    private static partial Regex VideoUrlRegex();

    private static readonly string[] VipHosts =
        ["iqiyi.com", "v.qq.com", "youku.com", "le.com", "tudou.com", "mgtv.com", "sohu.com", "acfun.cn", "bilibili.com", "baofeng.com", "pptv.com"];

    public static bool IsVipUrl(string url) =>
        VipHosts.Any(h => url.Contains(h, StringComparison.OrdinalIgnoreCase));

    public static bool IsVideoFormat(string url) =>
        !IsNonCandidate(url) && VideoUrlRegex().IsMatch(url);

    /// <summary>
    /// 排除项（对位 <c>PlayFragment.checkVideoFormat</c> 开头那两行 <c>url=http</c> / <c>.html</c>，
    /// 并额外排掉 <c>.js</c> / <c>.css</c> —— 嗅探时它们是最常见的误命中）。
    /// </summary>
    static bool IsNonCandidate(string url) =>
        url.Contains("url=http", StringComparison.Ordinal) ||
        url.Contains(".js", StringComparison.Ordinal) ||
        url.Contains(".css", StringComparison.Ordinal) ||
        url.Contains(".html", StringComparison.Ordinal);

    /// <summary>
    /// 嗅探候选判定（对位 <c>VideoParseRuler.checkIsVideoForParse</c>）：比 <see cref="IsVideoFormat"/>
    /// 多一层订阅下发的 host 规则 —— 通用正则不命中时，按<b>页面</b> host（不是候选 url 的 host）取规则组，
    /// <b>组内全命中才算数（AND）、任一组命中即算数（OR）</b>；该 host 无专属规则时回落到 <c>"*"</c> 通配组。
    /// <para>这是「非通用形态直链」（如 <c>/api/video?id=xxx</c> 这类不带扩展名的地址）能否被嗅到的关键。</para>
    /// </summary>
    public static bool CheckIsVideoForParse(string? pageUrl, string url, string? subscriptionKey = null)
    {
        if (IsNonCandidate(url)) return false;
        if (VideoUrlRegex().IsMatch(url)) return true;
        if (string.IsNullOrEmpty(subscriptionKey)) return false;

        var host = HostOf(pageUrl);
        if (host.Length == 0) return false;
        var groups = TvBoxConfigStore.RuleGroupsForHost(subscriptionKey, host)
                     ?? TvBoxConfigStore.RuleGroupsForHost(subscriptionKey, "*");
        return MatchesAnyGroup(groups, url);
    }

    /// <summary>
    /// 嗅探过滤（对位 <c>VideoParseRuler.isFilter</c>）：命中即「这个 URL 既不当候选、也不走广告拦截」。
    /// <para>⚠ 与判定侧有两处刻意的不对称（TVBox 原样）：没有 <c>"*"</c> 回落；极性是<b>排除</b>。</para>
    /// </summary>
    public static bool IsFiltered(string? pageUrl, string url, string? subscriptionKey = null)
    {
        if (string.IsNullOrEmpty(subscriptionKey)) return false;
        var host = HostOf(pageUrl);
        if (host.Length == 0) return false;
        return MatchesAnyGroup(TvBoxConfigStore.FilterGroupsForHost(subscriptionKey, host), url);
    }

    static bool MatchesAnyGroup(IReadOnlyList<IReadOnlyList<string>>? groups, string url)
    {
        if (groups is null) return false;
        foreach (var group in groups)
        {
            if (group.Count == 0) continue;   // TVBox：空组直接算不命中
            var allHit = true;
            foreach (var pattern in group)
            {
                var rx = HostPattern(pattern);
                if (rx is null || !rx.IsMatch(url))
                {
                    allHit = false;
                    break;
                }
            }
            if (allHit) return true;
        }
        return false;
    }

    /// <summary>页面 URL 的 host（不含端口）；非法 URL 回空串（对位 TVBox 的 try/catch → false）。</summary>
    static string HostOf(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        try { return new Uri(url).Host; } catch { return ""; }
    }

    /// <summary>过程留痕（订阅下发的正则是任意用户串，坏规则要能被看见）。</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>
    /// host 规则正则缓存。<b>必须容错</b>：这些串来自订阅（Java 正则方言），编译期不像 <c>GeneratedRegex</c>
    /// 那样有保证，一条坏规则若直接抛，整个嗅探就废了 —— 记日志后按「不命中」处理，让它只作废自己那一组。
    /// </summary>
    static readonly ConcurrentDictionary<string, Regex?> HostPatternCache = new();

    static Regex? HostPattern(string pattern) => HostPatternCache.GetOrAdd(pattern, p =>
    {
        try { return new Regex(p, RegexOptions.None, TimeSpan.FromSeconds(2)); }
        catch (Exception ex)
        {
            Log?.Invoke($"[sniffer] 订阅规则正则无效，已作废「{p}」: {ex.Message}");
            return null;
        }
    });

    private static bool IsBlackVodUrl(string input, string url) =>
        url.Contains("973973.xyz", StringComparison.OrdinalIgnoreCase) || url.Contains(".fit:", StringComparison.OrdinalIgnoreCase);

    private static void FixJsonVodHeader(Dictionary<string, string> headers, string input, string url)
    {
        if (input.Contains("www.mgtv.com") || url.Contains("titan.mgtv"))
        {
            headers["Referer"] = " ";
            headers["User-Agent"] = " Mozilla/5.0";
        }
        else if (input.Contains("bilibili"))
        {
            headers["Referer"] = " https://www.bilibili.com/";
            headers["User-Agent"] = " Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/94.0.4606.54 Safari/537.36";
        }
    }

    /// <summary>解析接口 URL 拼接（ParseBean.mixUrl）：ext 非空且接口带 ? 时注入 cat_ext=base64url(ext)</summary>
    public static string MixUrl(string url, string? ext)
    {
        if (string.IsNullOrWhiteSpace(ext) || url.Length == 0) return url;
        var idx = url.IndexOf('?');
        if (idx <= 0) return url;
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(ext))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return url[..(idx + 1)] + "cat_ext=" + b64 + "&" + url[(idx + 1)..];
    }

    /// <summary>从 mixUrl 拆出真实接口地址与 header（JsonParallel.getReqHeader）</summary>
    public static (string Url, Dictionary<string, string> Headers) SplitReqHeader(string mixUrl)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var url = mixUrl;
        var start = mixUrl.IndexOf("cat_ext=", StringComparison.Ordinal);
        if (start >= 0)
        {
            var end = mixUrl.IndexOf('&', start);
            var b64 = end > start ? mixUrl[(start + 8)..end] : mixUrl[(start + 8)..];
            try
            {
                var pad = b64.Length % 4 == 0 ? b64 : b64 + new string('=', 4 - b64.Length % 4);
                var ext = Encoding.UTF8.GetString(Convert.FromBase64String(pad.Replace('-', '+').Replace('_', '/')));
                using var doc = JsonDocument.Parse(ext);
                if (doc.RootElement.TryGetProperty("header", out var h) && h.ValueKind == JsonValueKind.Object)
                    foreach (var kv in h.EnumerateObject())
                        if (kv.Value.ValueKind == JsonValueKind.String)
                            headers[kv.Name] = kv.Value.GetString() ?? "";
                url = mixUrl[..start] + (end > start ? mixUrl[(end + 1)..] : "");
            }
            catch { }
        }
        return (url, headers);
    }

    /// <summary>
    /// 解析接口返回体 → 播放信息（Utils.jsonParse / PlayFragment.jsonParse 合并移植）。
    /// 返回 null = 无有效直链；ParseTail=true 表示给出的仍是中间页（需二段嗅探）。
    /// </summary>
    public static SniffResult? ExtractPlayFromJson(string input, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json,
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            var data = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : root;

            var url = GetStr(data, "url");
            if (url.Length == 0) url = GetStr(root, "url");
            if (url.StartsWith("//", StringComparison.Ordinal)) url = "https:" + url;
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("data:application", StringComparison.Ordinal))
                return null;

            var parseTail = false;
            if (url.StartsWith("video://", StringComparison.OrdinalIgnoreCase))
            {
                url = url[8..];
                parseTail = true;
            }
            if (TryGetInt(data, "parse", out var dv) && dv == 1) parseTail = true;
            if (TryGetInt(root, "parse", out var rv) && rv == 1) parseTail = true;

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AppendHeaders(headers, root);
            AppendHeaders(headers, data);
            var ua = GetStr(data, "user-agent");
            if (ua.Length == 0) ua = GetStr(root, "user-agent");
            if (ua.Trim().Length > 0) headers["User-Agent"] = " " + ua;
            var referer = GetStr(data, "referer");
            if (referer.Length == 0) referer = GetStr(root, "referer");
            if (referer.Trim().Length > 0) headers["Referer"] = " " + referer;
            FixJsonVodHeader(headers, input, url);

            if (url.Equals(input, StringComparison.OrdinalIgnoreCase) && (IsVipUrl(url) || !IsVideoFormat(url)))
                return null;
            if (IsBlackVodUrl(input, url)) return null;

            return new SniffResult(url, headers, parseTail);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetInt(JsonElement e, string name, out int value)
    {
        value = 0;
        return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out value);
    }

    private static void AppendHeaders(Dictionary<string, string> target, JsonElement e)
    {
        foreach (var key in new[] { "header", "headers" })
        {
            if (!e.TryGetProperty(key, out var h)) continue;
            if (h.ValueKind == JsonValueKind.Object)
            {
                foreach (var kv in h.EnumerateObject())
                    if (kv.Value.ValueKind == JsonValueKind.String)
                        target[kv.Name] = kv.Value.GetString() ?? "";
            }
            else if (h.ValueKind == JsonValueKind.String && h.GetString() is { Length: > 0 } s)
            {
                try
                {
                    using var doc = JsonDocument.Parse(s);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        foreach (var kv in doc.RootElement.EnumerateObject())
                            if (kv.Value.ValueKind == JsonValueKind.String)
                                target[kv.Name] = kv.Value.GetString() ?? "";
                }
                catch { }
            }
        }
    }

    private static string GetStr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>嗅探/解析结果</summary>
    public sealed record SniffResult(string Url, Dictionary<string, string> Headers, bool ParseTail);

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        });
        h.Timeout = TimeSpan.FromSeconds(15);
        h.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/94.0.4606.54 Safari/537.36");
        return h;
    }

    /// <summary>并发调多个 JSON 解析接口，第一个有效直链胜出（JsonParallel.parse 语义）</summary>
    public static async Task<SniffResult?> ParseJsonParallelAsync(
        List<(string Name, string MixUrl)> jxList, string pageUrl,
        IReadOnlyDictionary<string, string>? extraHeaders = null,
        CancellationToken ct = default)
    {
        if (jxList.Count == 0) return null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));

        var tasks = jxList.Select(async item =>
        {
            try
            {
                var (realUrl, headers) = SplitReqHeader(item.MixUrl);
                var reqHeaders = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
                if (extraHeaders is not null)
                    foreach (var kv in extraHeaders) reqHeaders[kv.Key] = kv.Value;

                using var request = new HttpRequestMessage(HttpMethod.Get, realUrl + pageUrl);
                foreach (var kv in reqHeaders) request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                using var resp = await Http.SendAsync(request, cts.Token);
                var json = await resp.Content.ReadAsStringAsync(cts.Token);
                return ExtractPlayFromJson(pageUrl, json);
            }
            catch { return null; }
        }).ToList();

        var pending = tasks.ToList();
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending);
            pending.Remove(done);
            var r = await done;
            if (r is not null)
            {
                cts.Cancel();
                return r;
            }
        }
        return null;
    }
}
