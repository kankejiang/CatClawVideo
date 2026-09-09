using System.Text.Json;
using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;

namespace CatClawVideo.Core.Providers;

/// <summary>
/// TVBox / 影视仓订阅解析器（明文 JSON 配置），带 okhttp UA（防直连源按 UA 发配置）。
/// 图片响应（饭太硬 /tv）自动提取图片尾部的 base64 隐写配置。
/// 站点类型映射：type 1 = MacCMS json（MacCmsJsonProvider 可播）、0 = xml（暂不支持）、
/// 3 = spider 爬虫源，再按 api 细分为：
///   · csp_Xxx    → Java jar/dex 爬虫（依赖订阅全局 spider 或站点自带 jar）
///   · http(s) 脚本地址 → JS 脚本爬虫（如 .js / drp，依赖 JS 引擎）
/// 两者当前都不可播，通过 <see cref="VodSiteInfo.StatusNote"/> 给出具体原因。
/// 加密配置（饭太硬等返回 logo 图/密文的源）识别后抛出明确异常。
/// </summary>
public class TvBoxSubscriptionManager : ISubscriptionManager
{
    private static readonly HttpClient Http = CreateHttp();

    /// <summary>
    /// 饭太硬等防直连源按 UA 区分响应：无 UA → 302 跳 HTML 页面；
    /// okhttp/4.x（TVBox/影视仓标准 UA）→ 返回带隐写配置的图片。
    /// </summary>
    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("okhttp/4.x");
        return client;
    }

    public async Task<List<VodSiteInfo>> LoadSubscriptionAsync(string subscriptionUrl, CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync(subscriptionUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);

        // 图片响应：饭太硬等防直连源把 base64 配置隐写在图片尾部（JPEG FFD9 之后），
        // 先尝试提取隐写配置，失败再抛明确异常
        string text;
        if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || IsBinary(bytes))
        {
            var steganography = TryExtractConfigFromImage(bytes);
            if (steganography is null)
                throw new NotSupportedException(
                    "该订阅地址返回的是图片/二进制内容，且未在图片中找到隐藏配置。\n" +
                    "请使用明文 TVBox json 或 MacCMS 直连地址。");
            text = steganography;
        }
        else
        {
            text = System.Text.Encoding.UTF8.GetString(bytes);
        }

        var sites = await ParseConfigTextAsync(text, subscriptionName: new Uri(subscriptionUrl).Host, ct);

        // 相对路径解析：小雅等站点 jar 写作 ./libs/x.jar（相对订阅源目录）
        var baseUrl = subscriptionUrl[..(subscriptionUrl.LastIndexOf('/') + 1)];
        foreach (var s in sites)
        {
            if (s.Jar is not null && s.Jar.StartsWith("./", StringComparison.Ordinal))
                s.Jar = baseUrl + s.Jar[2..];
            if (s.Ext is not null && s.Ext.StartsWith("./", StringComparison.Ordinal))
                s.Ext = baseUrl + s.Ext[2..];
        }
        return sites;
    }

    public Task<List<VodSiteInfo>> ParseConfigTextAsync(string jsonText, string subscriptionName, CancellationToken ct = default)
    {
        var trimmed = jsonText.TrimStart();

        // 加密配置识别：base64 大块无 { 开头 / 非 JSON 结构
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
            throw new NotSupportedException(
                "该订阅返回的是加密/混淆配置（非明文 JSON），暂不支持自动解密。\n" +
                "可改用明文 TVBox json 或 MacCMS 直连地址。");

        // 容忍行注释与尾逗号（饭太硬等源的配置常带 // 注释行）
        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        using var doc = JsonDocument.Parse(jsonText, options);
        var root = doc.RootElement;

        var sites = new List<VodSiteInfo>();
        if (!root.TryGetProperty("sites", out var siteArray) || siteArray.ValueKind != JsonValueKind.Array)
            return Task.FromResult(sites);

        // 全局 spider 包（csp_ 类站点未自带 jar 时回退到它）
        var globalSpider = root.TryGetProperty("spider", out var sp) && sp.ValueKind == JsonValueKind.String
            ? sp.GetString()
            : null;

        foreach (var s in siteArray.EnumerateArray())
        {
            var key = s.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
            var name = s.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var api = s.TryGetProperty("api", out var a) ? a.GetString() ?? "" : "";
            var type = s.TryGetProperty("type", out var t) && t.TryGetInt32(out var tv) ? tv : -1;
            if (key.Length == 0 || name.Length == 0) continue;

            // ext 可能是字符串（URL/密文），也可能是内嵌对象或对象数组（如小雅 Alist 的全局配置）
            var ext = s.TryGetProperty("ext", out var e) && e.ValueKind != JsonValueKind.Null
                ? (e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText())
                : null;

            var jar = s.TryGetProperty("jar", out var j) && j.ValueKind == JsonValueKind.String ? j.GetString() : null;
            var timeout = s.TryGetProperty("timeout", out var to) && to.TryGetInt32(out var tov) ? tov : (int?)null;

            // 源里 searchable/quickSearch 常写作 1/0 而非 true/false，此处按两者都兼容读取
            var searchableFlag = ReadFlag(s, "searchable");
            var quickSearchFlag = ReadFlag(s, "quickSearch");

            var (spiderKind, statusNote) = Classify(type, api);
            var (needsCreds, credServers) = DetectCredentials(ext);

            bool playable = spiderKind == VodSpiderKind.None &&
                            type == 1 &&
                            api.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                            !api.Contains("csp_", StringComparison.OrdinalIgnoreCase);

            sites.Add(new VodSiteInfo
            {
                Key = key,
                Name = name,
                Api = api,
                Type = type,
                Ext = ext,
                Jar = string.IsNullOrWhiteSpace(jar) ? globalSpider : jar,
                SpiderKind = spiderKind,
                TimeoutSeconds = timeout,
                SubscriptionName = subscriptionName,
                Playable = playable,
                Searchable = searchableFlag ?? playable,
                QuickSearch = quickSearchFlag ?? playable,
                StatusNote = playable ? null : statusNote,
                NeedsCredentials = needsCreds,
                CredentialServers = credServers,
            });
        }
        return Task.FromResult(sites);
    }

    /// <summary>
    /// 判定爬虫运行时类型与不可播原因。type=3 按 api 形态细分：
    /// csp_ 前缀为 jar 爬虫、http(s) 地址为脚本爬虫。
    /// </summary>
    private static (VodSpiderKind Kind, string Note) Classify(int type, string api)
    {
        if (type == 3)
        {
            if (api.StartsWith("csp_", StringComparison.OrdinalIgnoreCase))
                return (VodSpiderKind.Jar, "jar 爬虫源 · 需 spider 运行时");

            if (api.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return (VodSpiderKind.Script, "脚本爬虫源 · 需 JS 引擎");

            return (VodSpiderKind.Jar, "爬虫源 · 需 spider 运行时");
        }

        return type switch
        {
            0 => (VodSpiderKind.None, "xml 源 · 暂不支持"),
            1 => (VodSpiderKind.None, "MacCMS json · 地址不可用"),
            _ => (VodSpiderKind.None, $"type {type} · 暂不支持"),
        };
    }

    /// <summary>
    /// 检测站点是否需要账号认证（alist 类）：ext 为 JSON 数组、首个元素 type=global 且含
    /// username/password 字段、无现成 token 时成立；同时收集数组内全部 server 地址。
    /// </summary>
    private static (bool Needs, List<string> Servers) DetectCredentials(string? ext)
    {
        var servers = new List<string>();
        var trimmed = ext?.TrimStart();
        if (string.IsNullOrEmpty(trimmed) || !trimmed.StartsWith('[')) return (false, servers);

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var arr = doc.RootElement;
            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) return (false, servers);

            bool hasCredField = false, hasToken = false;
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                if (item.TryGetProperty("server", out var sv) && sv.ValueKind == JsonValueKind.String)
                {
                    var server = sv.GetString();
                    if (!string.IsNullOrEmpty(server) && !servers.Contains(server)) servers.Add(server);
                }

                if (item.TryGetProperty("type", out var tp) && tp.ValueKind == JsonValueKind.String &&
                    tp.GetString() == "global")
                {
                    if (item.TryGetProperty("username", out _) || item.TryGetProperty("password", out _))
                        hasCredField = true;
                    if (item.TryGetProperty("token", out var tk) &&
                        tk.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(tk.GetString()))
                        hasToken = true;
                }
            }
            return (hasCredField && !hasToken && servers.Count > 0, servers);
        }
        catch
        {
            return (false, servers);
        }
    }

    /// <summary>读取布尔标记，兼容 true/false、1/0、"1"/"0" 四种写法；字段缺失或类型异常返回 null。</summary>
    private static bool? ReadFlag(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var v)) return null;
        try
        {
            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => v.TryGetInt32(out var n) ? n != 0 : null,
                JsonValueKind.String => bool.TryParse(v.GetString(), out var b)
                    ? b
                    : (int.TryParse(v.GetString(), out var n2) ? n2 != 0 : null),
                _ => null,
            };
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsBinary(byte[] bytes)
    {
        if (bytes.Length < 4) return false;
        // JPEG FF D8 FF / PNG 89 50 4E 47 / GIF / BMP BM
        return (bytes[0] == 0xFF && bytes[1] == 0xD8) ||
               (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) ||
               (bytes[0] == 0x42 && bytes[1] == 0x4D);
    }

    /// <summary>
    /// 从图片中提取隐写配置（饭太硬防直连机制）：真实 base64 配置附加在图片结束标记之后。
    /// 流程：定位图片结束标记（JPEG FFD9 / PNG IEND）→ 清洗非 base64 字符 →
    /// 前缀可能混入干扰字符，按 4 字符对齐逐偏移尝试解码，取能解出 JSON 的起点。
    /// </summary>
    private static string? TryExtractConfigFromImage(byte[] bytes)
    {
        var end = FindImageEnd(bytes);
        if (end < 0 || end + 8 >= bytes.Length) return null;

        var tail = System.Text.Encoding.ASCII.GetString(bytes, end, bytes.Length - end);
        var clean = System.Text.RegularExpressions.Regex.Replace(tail, "[^A-Za-z0-9+/]", "");
        if (clean.Length < 16) return null;

        for (int skip = 0; skip < Math.Min(256, clean.Length - 4); skip += 4)
        {
            var seg = clean[skip..];
            if (seg.Length % 4 != 0) seg += new string('=', 4 - seg.Length % 4);
            try
            {
                var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(seg));
                var trimmed = decoded.TrimStart('\0').TrimStart();
                if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
                    return trimmed;
            }
            catch { }
        }
        return null;
    }

    /// <summary>定位图片结束标记位置（JPEG 最后一个 FFD9 / PNG IEND 块尾），未识别返回 -1</summary>
    private static int FindImageEnd(byte[] bytes)
    {
        // PNG：IEND + 4 字节 CRC
        for (int i = 0; i + 8 <= bytes.Length; i++)
        {
            if (bytes[i] == 'I' && bytes[i + 1] == 'E' && bytes[i + 2] == 'N' && bytes[i + 3] == 'D')
                return i + 8;
        }
        // JPEG：从尾往前找 FFD9
        for (int i = bytes.Length - 2; i >= 0; i--)
        {
            if (bytes[i] == 0xFF && bytes[i + 1] == 0xD9) return i + 2;
        }
        return -1;
    }
}
