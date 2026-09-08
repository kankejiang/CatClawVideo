using System.Text.Json;
using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;

namespace CatClawVideo.Core.Providers;

/// <summary>
/// TVBox / 影视仓订阅解析器（明文 JSON 配置）。
/// 站点类型映射：type 1 = MacCMS json（MacCmsJsonProvider 可播）、0 = xml（暂不支持）、
/// 3 = csp spider（依赖 jar 运行时，标记 Playable=false——spider 运行时为独立大项）。
/// 加密配置（饭太硬等返回 logo 图/密文的源）识别后抛出明确异常。
/// </summary>
public class TvBoxSubscriptionManager : ISubscriptionManager
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<List<VodSiteInfo>> LoadSubscriptionAsync(string subscriptionUrl, CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync(subscriptionUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);

        // 二进制/图片响应：这类订阅地址（如饭太硬 /tv）返回的是 logo 或二维码图，配置已防直连
        if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || IsBinary(bytes))
            throw new NotSupportedException(
                "该订阅地址返回的是图片/二进制内容（多为防白嫖的二维码或 logo），无法直接解析。\n" +
                "请使用明文 TVBox json 或 MacCMS 直连地址。");

        var text = System.Text.Encoding.UTF8.GetString(bytes);
        return await ParseConfigTextAsync(text, subscriptionName: new Uri(subscriptionUrl).Host, ct);
    }

    public async Task<List<VodSiteInfo>> ParseConfigTextAsync(string jsonText, string subscriptionName, CancellationToken ct = default)
    {
        var trimmed = jsonText.TrimStart();

        // 加密配置识别：base64 大块无 { 开头 / 2423 前缀（$$ 加密标记）/ 非 JSON 结构
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
            throw new NotSupportedException(
                "该订阅返回的是加密/混淆配置（非明文 JSON），暂不支持自动解密。\n" +
                "可改用明文 TVBox json 或 MacCMS 直连地址。");

        using var doc = JsonDocument.Parse(jsonText);
        var root = doc.RootElement;

        var sites = new List<VodSiteInfo>();
        if (!root.TryGetProperty("sites", out var siteArray) || siteArray.ValueKind != JsonValueKind.Array)
            return sites;

        foreach (var s in siteArray.EnumerateArray())
        {
            var key = s.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
            var name = s.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var api = s.TryGetProperty("api", out var a) ? a.GetString() ?? "" : "";
            var type = s.TryGetProperty("type", out var t) && t.TryGetInt32(out var tv) ? tv : -1;
            var ext = s.TryGetProperty("ext", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            if (key.Length == 0 || name.Length == 0) continue;

            bool playable = type == 1 && api.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                            !api.Contains("csp_", StringComparison.OrdinalIgnoreCase);
            sites.Add(new VodSiteInfo
            {
                Key = key,
                Name = name,
                Api = api,
                Type = type,
                Ext = ext,
                SubscriptionName = subscriptionName,
                Playable = playable,
                Searchable = playable,
            });
        }
        return sites;
    }

    private static bool IsBinary(byte[] bytes)
    {
        if (bytes.Length < 4) return false;
        // JPEG FF D8 FF / PNG 89 50 4E 47 / GIF / BMP BM
        return (bytes[0] == 0xFF && bytes[1] == 0xD8) ||
               (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) ||
               (bytes[0] == 0x42 && bytes[1] == 0x4D);
    }
}
