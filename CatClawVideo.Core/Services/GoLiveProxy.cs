using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace CatClawVideo.Core.Services;

/// <summary>
/// 内置直播代理链 —— 移植 TVBox <c>util/Proxy.java</c> 的 <c>go=</c> 命名空间。
///
/// <para><b>和 <c>do=</c> 的分工</b>（这是 parity 的关键，2026-09-24 实读源码定案）：
/// TVBox 的 <c>/proxy</c> 有两个互不相同的命名空间 —— <c>do=&lt;任意值&gt;</c> 原样透传给爬虫的
/// <c>proxy(Map)</c>（<c>ApiConfig.proxyLocal</c>），而 <c>go=live|bom|SuperParse</c> 走宿主**内置**实现。
/// 本项目此前只有 <c>do=</c>，等于缺了整条内置改写链：itv 类直播源的切片/密钥都要带
/// ua/referer/origin/cookie 才不 403，而这些头只能由宿主在改写时一路带下去。</para>
///
/// <para><b>分支</b>：<c>go=live</c>（<c>type=m3u8|ts|media|key</c>）、<c>go=bom</c>（去 UTF-8 BOM）。
/// <c>go=ad</c> 在 TVBox 里就是 <c>//TODO return null</c>，不实现；<c>go=SuperParse</c> 是 TVBox
/// 自己拼多 iframe 网页播放器的内部端点，本项目的解析链不产出该地址，收到时按「未处理」回 502。</para>
/// </summary>
public static class GoLiveProxy
{
    /// <summary>一次 go= 请求的处理结果（对位 TVBox 的 <c>Object[]{code, mime, stream, headers}</c>）。</summary>
    public sealed record Result(int Status, string Mime, byte[] Body, Dictionary<string, string>? Headers = null);

    /// <summary>过程留痕。</summary>
    public static Action<string>? Log;

    /// <summary>
    /// 取流用的客户端。对位 TVBox <c>OkGoHelper.ItvClient</c>：<b>跟随重定向</b>
    /// （<c>followRedirects(true)</c> + <c>followSslRedirects(true)</c>）。
    /// 这与 <see cref="SpiderProxyServer"/> 那个「不自动跳转、手动带 Range 续跟」的客户端**故意不同**：
    /// itv 直播源大量靠 302 换 CDN，跳转时丢头才是它们失败的原因。
    /// </summary>
    static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = DecompressionMethods.None,
    })
    { Timeout = TimeSpan.FromSeconds(30) };

    static readonly Regex UriAttr = new("URI=\"([^\"]+)\"", RegexOptions.Compiled);

    /// <summary>
    /// 入口（对位 <c>Proxy.proxy(params)</c>）。返回 null = 本命名空间不认这个 <c>go</c> 值。
    /// </summary>
    /// <param name="args">已解码的 query 参数（<c>go</c> 值本身也在里面）。</param>
    /// <param name="localPort">请求进来的端口，改写出的下一跳地址要用同一个端口才保证在听。</param>
    /// <param name="requestHeaders">播放器原始请求行/头（用来把 Range 带下去）。</param>
    public static async Task<Result?> HandleAsync(IReadOnlyDictionary<string, string> args, int localPort,
        string[] requestHeaders, CancellationToken ct)
    {
        var what = args.GetValueOrDefault("go");
        try
        {
            return what switch
            {
                "live" => await ItvAsync(args, localPort, requestHeaders, ct).ConfigureAwait(false),
                "bom" => await RemoveBomFromM3u8Async(args, ct).ConfigureAwait(false),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            // TVBox 整段 catch (Throwable ignored)，这里至少留一行痕，否则表现为「黑屏且无线索」
            Log?.Invoke($"[go={what}] 处理失败: {ex.Message}");
            return null;
        }
    }

    // ═══════════════ go=live（对位 Proxy.itv）═══════════════

    static async Task<Result?> ItvAsync(IReadOnlyDictionary<string, string> args, int localPort,
        string[] requestHeaders, CancellationToken ct)
    {
        var url = Decode(args.GetValueOrDefault("url"));
        var type = args.GetValueOrDefault("type");
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(type))
        {
            Log?.Invoke("[go=live] 缺 url 或 type");
            return null;
        }
        if (type is not ("m3u8" or "ts" or "media" or "key"))
        {
            Log?.Invoke($"[go=live] 不支持的 type={type}");
            return null;
        }

        using var req = BuildRequest(url, args, requestHeaders);

        if (type == "m3u8")
        {
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Log?.Invoke($"[go=live] m3u8 取回 {(int)resp.StatusCode}");
                return null;
            }
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? url;
            var text = ProcessM3u8Content(body, finalUrl, args, localPort);
            return new Result(200, "application/vnd.apple.mpegurl", Encoding.UTF8.GetBytes(text));
        }

        // ts / media / key：流式读，状态码与四个响应头要回传给播放器
        using var r = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var bytes = await r.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (!r.IsSuccessStatusCode)
        {
            Log?.Invoke($"[go=live] {type} 取回 {(int)r.StatusCode}");
            return null;
        }
        return new Result((int)r.StatusCode, GetMime(type, url, r), bytes, ResponseHeaders(r));
    }

    /// <summary>
    /// 组装上游请求头（对位 <c>Proxy.buildRequest</c> 的 15 次 <c>copyHeader</c>）。
    /// 语义要点：同名头按声明顺序**后者覆盖前者**，所以 <c>User-Agent</c> 参数优先于 <c>user-agent</c> 优先于 <c>ua</c>；
    /// 空值不写。Range 额外兜一层播放器自己发来的（TVBox 只认参数，我们的播放器拖动时只发标准头）。
    /// </summary>
    static HttpRequestMessage BuildRequest(string url, IReadOnlyDictionary<string, string> args,
        string[] requestHeaders)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);

        void Copy(string paramKey, string headerKey)
        {
            var v = Dec(args.GetValueOrDefault(paramKey));
            if (v.Length == 0) return;
            req.Headers.Remove(headerKey);
            req.Headers.TryAddWithoutValidation(headerKey, v);
        }

        Copy("ua", "User-Agent");
        Copy("user-agent", "User-Agent");
        Copy("User-Agent", "User-Agent");
        Copy("referer", "Referer");
        Copy("Referer", "Referer");
        Copy("origin", "Origin");
        Copy("Origin", "Origin");
        Copy("cookie", "Cookie");
        Copy("Cookie", "Cookie");
        Copy("range", "Range");
        Copy("Range", "Range");
        Copy("accept", "Accept");
        Copy("Accept", "Accept");
        Copy("accept-language", "Accept-Language");
        Copy("Accept-Language", "Accept-Language");

        if (req.Headers.Range is null)
        {
            var incoming = SpiderProxyServer.Header(requestHeaders, "Range");
            if (!string.IsNullOrEmpty(incoming)) req.Headers.TryAddWithoutValidation("Range", incoming);
        }
        if (req.Headers.UserAgent.Count == 0)
            req.Headers.TryAddWithoutValidation("User-Agent", SpiderProxyServer.DefaultUserAgent);

        return req;
    }

    /// <summary>
    /// 播放列表逐行改写（对位 <c>Proxy.processM3u8Content</c>）。
    /// 与 <see cref="SpiderProxyServer"/> 里那套 <c>do=</c> 改写的区别：这里给下一跳带上 <c>type=</c>
    /// 与四个头参数，于是分片/密钥请求能复用同一套防盗链头。
    /// </summary>
    static string ProcessM3u8Content(string? content, string m3u8Url, IReadOnlyDictionary<string, string> args,
        int localPort)
    {
        if (content is null) return "";
        if (content.StartsWith('\uFEFF')) content = content[1..];
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var sb = new StringBuilder();
        var nextIsVariant = false;

        foreach (var line in normalized.Split('\n'))
        {
            var item = line.Trim();
            if (item.Length == 0)
            {
                sb.Append(line).Append('\n');
                continue;
            }
            if (item.StartsWith('#'))
            {
                sb.Append(RewriteUriAttributes(m3u8Url, line, args, localPort)).Append('\n');
                nextIsVariant = item.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal);
                continue;
            }
            var type = nextIsVariant || IsM3u8Url(item) ? "m3u8" : "media";
            sb.Append(JoinUrl(m3u8Url, line, type, args, localPort)).Append('\n');
            nextIsVariant = false;
        }
        // TVBox 原样保留这一步：爬虫偶尔把换行写成字面量 \n
        return sb.ToString().Replace("\\n\\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>一行里可能有多个 <c>URI="…"</c>（KEY/MAP/SESSION-KEY），逐个改写。</summary>
    static string RewriteUriAttributes(string baseUrl, string line, IReadOnlyDictionary<string, string> args,
        int localPort)
    {
        if (!line.Contains("URI=\"", StringComparison.Ordinal)) return line;
        var sb = new StringBuilder();
        var last = 0;
        foreach (Match m in UriAttr.Matches(line))
        {
            sb.Append(line, last, m.Index - last);
            var uri = m.Groups[1].Value;
            sb.Append("URI=\"").Append(JoinUrl(baseUrl, uri, ProxyTypeForUriAttr(line, uri), args, localPort))
                .Append('"');
            last = m.Index + m.Length;
        }
        sb.Append(line, last, line.Length - last);
        return sb.ToString();
    }

    static string ProxyTypeForUriAttr(string line, string uri)
    {
        var upper = (line ?? "").TrimStart().ToUpperInvariant();
        if (upper.StartsWith("#EXT-X-KEY") || upper.StartsWith("#EXT-X-SESSION-KEY")) return "key";
        if (IsM3u8Url(uri) || upper.StartsWith("#EXT-X-I-FRAME-STREAM-INF")) return "m3u8";
        return "media";
    }

    static bool IsM3u8Url(string? url)
    {
        if (url is null) return false;
        var lower = url.ToLowerInvariant();
        var q = lower.IndexOf('?');
        if (q >= 0) lower = lower[..q];
        return lower.EndsWith(".m3u8") || lower.EndsWith(".m3u");
    }

    /// <summary>
    /// 生成下一跳的 <c>proxy?go=live&amp;type=…&amp;ua=…&amp;url=…</c>（对位 <c>Proxy.joinUrl</c>）。
    /// 四种相对形态照抄：绝对 / <c>://x</c>（补 scheme）/ <c>//x</c>（补 scheme:）/ 其余按 base 解析。
    /// <c>data:</c> / <c>blob:</c> 原样返回。
    /// </summary>
    static string? JoinUrl(string baseUrl, string url, string type, IReadOnlyDictionary<string, string> args,
        int localPort)
    {
        baseUrl ??= "";
        url = (url ?? "").Trim();
        if (url.StartsWith("data:", StringComparison.Ordinal) || url.StartsWith("blob:", StringComparison.Ordinal))
            return url;

        var proxyUrl = new StringBuilder()
            .Append("http://127.0.0.1:").Append(localPort)
            .Append("/proxy?go=live&type=").Append(type)
            .Append(HeaderQuery(args))
            .Append("&url=").ToString();

        string absolute;
        try
        {
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                absolute = url;
            else if (url.StartsWith("://", StringComparison.Ordinal))
                absolute = SchemeOf(baseUrl) + url;
            else if (url.StartsWith("//", StringComparison.Ordinal))
                absolute = SchemeOf(baseUrl) + ":" + url;
            else
                absolute = ResolveAgainst(baseUrl, url);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[go=live] 地址解析失败 {url}: {ex.Message}");
            return null;
        }
        return proxyUrl + WebUtility.UrlEncode(absolute);
    }

    static string SchemeOf(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) ? u.Scheme : "http";

    /// <summary>相对地址按 base 解析（对位 <c>java.net.URI.resolve</c>）。</summary>
    static string ResolveAgainst(string baseUrl, string relative)
    {
        try { return new Uri(baseUrl, UriKind.Absolute) is { } b ? new Uri(b, relative).ToString() : relative; }
        catch { return relative; }
    }

    /// <summary>
    /// 生产端：把一个「需要带防盗链头的直播 m3u8」包成 <c>proxy?go=live&amp;type=m3u8</c>。
    /// <para>TVBox 只在 <c>IjkMediaPlayer</c> 里对单个 itv 域名（<c>ITV_TARGET_DOMAIN</c>）做这件事，
    /// 目的是绕 DNS；这里按「是 m3u8 且确实带了 ua/referer/origin/cookie 之一」来包，
    /// 解的是另一类真实故障：<b>播放器直连首个 m3u8 成功、列表里的 .ts 裸连被 CDN 403</b>，
    /// 表现为「直播几秒后黑屏」。包一层之后分片与密钥都继承同一套头。</para>
    /// <para>不满足条件时返回 null（调用方原样播），所以无头的普通频道行为完全不变。</para>
    /// </summary>
    public static string? WrapForLive(string url, IReadOnlyDictionary<string, string>? headers, int port)
    {
        if (port <= 0 || headers is null or { Count: 0 }) return null;
        if (!IsM3u8Url(url)) return null;
        // 只防「已经是我们自己生成的 go=live 地址」再包一层（自环）；
        // 不能按 127.0.0.1 前缀挡 —— NAS 上的直播源就在回环/内网地址上。
        if (url.Contains("/proxy?go=live", StringComparison.OrdinalIgnoreCase)) return null;

        var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Pick(string headerKey, string paramKey)
        {
            if (headers.TryGetValue(headerKey, out var v) && !string.IsNullOrEmpty(v)) q[paramKey] = v;
        }

        Pick("User-Agent", "ua");
        Pick("user-agent", "ua");
        Pick("Referer", "referer");
        Pick("referer", "referer");
        Pick("Origin", "origin");
        Pick("origin", "origin");
        Pick("Cookie", "cookie");
        Pick("cookie", "cookie");
        if (q.Count == 0) return null;

        return $"http://127.0.0.1:{port}/proxy?go=live&type=m3u8{HeaderQuery(q)}&url={WebUtility.UrlEncode(url)}";
    }

    /// <summary>
    /// 把头参数回写成 query（对位 <c>Proxy.headerQuery</c>）：只带 ua/referer/origin/cookie 四个，
    /// 且<b>同一目标键只写第一次</b>（TVBox 用 <c>sb.indexOf("&amp;to=")&gt;=0</c> 去重），空值不写。
    /// <para>⚠ 与 TVBox 有<b>一处刻意的偏差</b>：它只读 <c>User-Agent</c>/<c>user-agent</c> 两个参数名，
    /// 不读 <c>ua</c>；而它自己生成的下一跳地址写的恰恰是 <c>&amp;ua=</c> —— 于是从第二跳起 UA 就丢了，
    /// 分片裸奔被 403。这里多读一个 <c>ua</c> 把链打通（2026-09-25 台架 P1 用例定位），优先级仍与它一致。</para>
    /// </summary>
    static string HeaderQuery(IReadOnlyDictionary<string, string> args)
    {
        var sb = new StringBuilder();
        Append(sb, "User-Agent", "ua");
        Append(sb, "user-agent", "ua");
        Append(sb, "ua", "ua");
        Append(sb, "Referer", "referer");
        Append(sb, "referer", "referer");
        Append(sb, "Origin", "origin");
        Append(sb, "origin", "origin");
        Append(sb, "Cookie", "cookie");
        Append(sb, "cookie", "cookie");
        return sb.ToString();

        void Append(StringBuilder b, string from, string to)
        {
            if (b.ToString().Contains("&" + to + "=", StringComparison.Ordinal)) return;
            var v = Dec(args.GetValueOrDefault(from));
            if (v.Length == 0) return;
            b.Append('&').Append(to).Append('=').Append(WebUtility.UrlEncode(v));
        }
    }

    /// <summary>
    /// 参数值解码。对位 NanoHTTPD <c>getParms()</c>——它交出来的参数是<b>已解码</b>的，
    /// 而我们的 <c>ParseQuery</c> 存的是原始串。不补这一步，爬虫传来的 <c>ua=Mozilla%2F5.0</c>
    /// 会原样进请求头（上游收到字面量 "Mozilla%2F5.0" → 照样 403），改写下一跳时还会二次编码成 %252F。
    /// </summary>
    static string Dec(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        try { return Uri.UnescapeDataString(raw); } catch { return raw; }
    }

    /// <summary>上游没给 Content-Type 时按扩展名猜（对位 <c>Proxy.getMime</c>）。</summary>
    static string GetMime(string type, string url, HttpResponseMessage resp)
    {
        var ct = resp.Content.Headers.ContentType?.ToString();
        if (!string.IsNullOrEmpty(ct)) return ct;
        if (type == "key") return "application/octet-stream";
        var lower = url.ToLowerInvariant();
        var q = lower.IndexOf('?');
        if (q >= 0) lower = lower[..q];
        if (lower.EndsWith(".m4s") || lower.EndsWith(".mp4") || lower.EndsWith(".m4v")) return "video/mp4";
        if (lower.EndsWith(".aac")) return "audio/aac";
        if (lower.EndsWith(".vtt")) return "text/vtt";
        return "video/mp2t";
    }

    /// <summary>只回传这四个响应头（对位 <c>Proxy.responseHeaders</c>）——其余头会让播放器误判。</summary>
    static Dictionary<string, string>? ResponseHeaders(HttpResponseMessage resp)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Copy(string key)
        {
            string? v = key switch
            {
                "Content-Range" => resp.Content.Headers.ContentRange?.ToString(),
                "Accept-Ranges" => resp.Headers.Contains("Accept-Ranges")
                    ? string.Join(",", resp.Headers.GetValues("Accept-Ranges")) : null,
                "Content-Length" => resp.Content.Headers.ContentLength?.ToString(CultureInfo.InvariantCulture),
                "Cache-Control" => resp.Headers.CacheControl?.ToString() ??
                                   (resp.Headers.Contains("Cache-Control")
                                       ? string.Join(",", resp.Headers.GetValues("Cache-Control")) : null),
                _ => null,
            };
            if (!string.IsNullOrEmpty(v)) headers[key] = v;
        }

        Copy("Content-Range");
        Copy("Accept-Ranges");
        Copy("Content-Length");
        Copy("Cache-Control");
        return headers.Count > 0 ? headers : null;
    }

    // ═══════════════ go=bom（对位 Proxy.removeBOMFromM3U8）═══════════════

    static async Task<Result?> RemoveBomFromM3u8Async(IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        var url = Decode(args.GetValueOrDefault("url"));
        if (string.IsNullOrEmpty(url)) return null;

        // TVBox 先用「不跟随重定向」的一跳拿 Location，再拿最终地址取正文
        var redirectUrl = await GetRedirectedUrlAsync(url, ct).ConfigureAwait(false);
        using var req = new HttpRequestMessage(HttpMethod.Get, redirectUrl);
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            Log?.Invoke($"[go=bom] 取回 {(int)resp.StatusCode}");
            return null;
        }
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (text.StartsWith('\uFEFF')) text = text[1..];
        return new Result(200, "application/vnd.apple.mpegurl", Encoding.UTF8.GetBytes(text));
    }

    static async Task<string> GetRedirectedUrlAsync(string url, CancellationToken ct)
    {
        try
        {
            using var noRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(15) };
            using var resp = await noRedirect.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            var code = (int)resp.StatusCode;
            if (code is >= 300 and < 400)
            {
                var loc = resp.Headers.Location;
                if (loc is not null)
                    return loc.IsAbsoluteUri ? loc.ToString() : new Uri(new Uri(url), loc).ToString();
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[go=bom] 预探重定向失败（按原地址继续）: {ex.Message}");
        }
        return url;
    }

    /// <summary>url 参数按 TVBox 约定是 URLEncoder 编码过的；未编码的原样返回。</summary>
    static string Decode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        try { return Uri.UnescapeDataString(raw); } catch { return raw; }
    }
}
