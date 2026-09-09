using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CatClawVideo.Core.Models;

namespace CatClawVideo.Core.Providers;

/// <summary>
/// 猫爪源 v2 web 规则引擎：按源文件里的声明式规则实时抓取站点数据。
/// 分类零请求（静态声明）；列表/详情按需请求（HTML 内存缓存 5 分钟）；
/// 播放 = 详情播放入口 → playIframe（可选）→ playDirect 两级解析，直链不缓存（时效签名）。
/// 不针对特定站点——换站 = 换规则，不动代码。
/// </summary>
public class CatClawWebEngine
{
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64) Chrome/124.0");
        return client;
    }

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>HTML 缓存：url → (时刻, 内容)</summary>
    private readonly ConcurrentDictionary<string, (DateTime At, string Html)> _htmlCache = new();

    // ═══════════════════ 列表 ═══════════════════

    /// <summary>按分类与页码拉取列表（站点一页 = 一个 App 分页）</summary>
    public async Task<List<VodItem>> GetItemsAsync(
        CatClawSourceWeb web, CatClawSourceWebCategory category, int page, string sourceKey)
    {
        var url = BuildListUrl(web, category, page);
        var html = await GetHtmlAsync(url);
        if (html == null) return [];

        var result = new List<VodItem>();
        foreach (Match m in Regex.Matches(html, web.Rules.ListItem ?? ""))
        {
            var g = m.Groups;
            var itemUrl = g["url"].Success ? g["url"].Value : "";
            var title = Decode(g["title"].Success ? g["title"].Value : "");
            if (itemUrl.Length == 0 || title.Length == 0) continue;

            result.Add(new VodItem
            {
                Id = Absolute(web, itemUrl),
                SourceKey = sourceKey,
                Title = title,
                Cover = g["cover"].Success ? Absolute(web, Decode(g["cover"].Value)) : null,
                Category = category.Name,
                Year = g["year"].Success ? Decode(g["year"].Value) : null,
            });
        }
        return result;
    }

    /// <summary>列表页地址（page=1 用首页模板，page≥2 用翻页模板）</summary>
    private static string BuildListUrl(CatClawSourceWeb web, CatClawSourceWebCategory category, int page)
    {
        var template = page <= 1
            ? (web.Rules.ListPageFirst ?? "{path}")
            : (web.Rules.ListPage ?? "{path}");
        var path = template
            .Replace("{path}", category.Path)
            .Replace("{page}", page.ToString());
        return Absolute(web, path);
    }

    // ═══════════════════ 详情 ═══════════════════

    /// <summary>拉取详情（标题/年份/产地/简介）+ 在线播放入口 + 磁力线路</summary>
    public async Task<(List<VodPlaySource> Sources, string? Year, string? Area, string? Desc, string? Remarks)>
        GetDetailAsync(CatClawSourceWeb web, string detailUrl, string itemTitle)
    {
        var html = await GetHtmlAsync(detailUrl);
        var sources = new List<VodPlaySource>();
        string? year = null, area = null, desc = null;

        if (html != null)
        {
            var r = web.Rules;

            if (!string.IsNullOrEmpty(r.DetailYear))
                year = FirstGroup(html, r.DetailYear, "year");
            if (!string.IsNullOrEmpty(r.DetailArea))
                area = CleanField(FirstGroup(html, r.DetailArea, "area"));
            if (!string.IsNullOrEmpty(r.DetailDesc))
            {
                var raw = FirstGroupHtml(html, r.DetailDesc, "desc");
                if (!string.IsNullOrEmpty(raw))
                {
                    // 去标签 → 实体解码 → &nbsp; 空段/连续空白折叠为单空格
                    var text = Decode(Regex.Replace(raw, "<[^>]+>", ""));
                    desc = Regex.Replace(text, @"[\s\u00a0\u3000]+", " ").Trim();
                    if (desc.Length > 800) desc = desc[..800] + "…";
                }
            }

            if (!string.IsNullOrEmpty(r.DetailPlay))
            {
                var episodes = new List<VodEpisode>();
                foreach (Match m in Regex.Matches(html, r.DetailPlay))
                {
                    var url = Absolute(detailUrl, Decode(m.Groups["url"].Value));
                    var name = m.Groups["name"].Success && Decode(m.Groups["name"].Value).Trim().Length > 0
                        ? Decode(m.Groups["name"].Value).Trim()
                        : $"播放源{episodes.Count + 1}";
                    episodes.Add(new VodEpisode { Name = name, Url = url });
                }
                if (episodes.Count > 0)
                    sources.Add(new VodPlaySource { Name = "在线播放", Episodes = episodes });
            }

            if (!string.IsNullOrEmpty(r.DetailMagnet))
            {
                var episodes = new List<VodEpisode>();
                foreach (Match m in Regex.Matches(html, r.DetailMagnet))
                {
                    var name = m.Groups["name"].Success && Decode(m.Groups["name"].Value).Trim().Length > 0
                        ? Decode(m.Groups["name"].Value).Trim()
                        : "磁力下载";
                    episodes.Add(new VodEpisode { Name = name, Url = Decode(m.Groups["url"].Value) });
                }
                if (episodes.Count > 0)
                    sources.Add(new VodPlaySource { Name = "磁力下载", Episodes = episodes });
            }
        }

        return (sources, year, area, desc, sources.Count > 0 && sources[0].Name == "在线播放" ? "在线" : null);
    }

    // ═══════════════════ 播放解析 ═══════════════════

    /// <summary>
    /// 解析播放页为直链：playIframe（可选，跟一层）→ playDirect。
    /// 直链不缓存（时效签名每次现取）。
    /// </summary>
    public async Task<PlayRequest> ResolvePlayAsync(CatClawSourceWeb web, string episodeName, string playUrl, CancellationToken ct)
    {
        var r = web.Rules;
        var current = playUrl;
        string? referer = null;

        // 可选一层 iframe
        if (!string.IsNullOrEmpty(r.PlayIframe))
        {
            var html = await GetHtmlAsync(current);
            var iframe = html != null ? Regex.Match(html, r.PlayIframe) : Match.Empty;
            if (iframe.Success)
            {
                current = Absolute(current, Decode(iframe.Groups["url"].Value));
                referer = new Uri(playUrl).GetLeftPart(UriPartial.Authority);
            }
        }

        if (string.IsNullOrEmpty(r.PlayDirect))
            throw new NotSupportedException("源规则缺少 playDirect，无法解析播放直链");

        var html2 = await GetHtmlAsync(current) ?? throw new NotSupportedException("播放页拉取失败");
        var direct = Regex.Match(html2, r.PlayDirect);
        if (!direct.Success)
            throw new NotSupportedException("播放页中未找到视频直链");

        var url = Absolute(current, Decode(direct.Groups["url"].Value));

        // 集名修正（可选规则，如 iframe 页 title）
        var name = episodeName;
        if (!string.IsNullOrEmpty(r.PlayName))
        {
            var n = Regex.Match(html2, r.PlayName);
            if (n.Success && Decode(n.Groups["name"].Value).Trim().Length > 0)
                name = Decode(n.Groups["name"].Value).Trim();
        }

        return new PlayRequest { Title = name, Url = url, Referer = referer };
    }

    // ═══════════════════ 搜索 ═══════════════════

    /// <summary>搜索：优先 searchUrl 站点接口；无规则时回退「分类前 N 页 + 标题过滤」本地搜索。</summary>
    public async Task<List<VodItem>> SearchAsync(CatClawSourceWeb web, string keyword, string sourceKey)
    {
        var kw = keyword?.Trim();
        if (string.IsNullOrEmpty(kw)) return [];

        if (!string.IsNullOrEmpty(web.Rules.SearchUrl))
        {
            var url = web.Rules.SearchUrl.Replace("{kw}", Uri.EscapeDataString(kw));
            var html = await GetHtmlAsync(Absolute(web, url));
            if (html == null) return [];

            var itemRule = string.IsNullOrEmpty(web.Rules.SearchItem) ? web.Rules.ListItem : web.Rules.SearchItem;
            if (string.IsNullOrEmpty(itemRule)) return [];

            var result = new List<VodItem>();
            foreach (Match m in Regex.Matches(html, itemRule))
            {
                var g = m.Groups;
                var itemUrl = g["url"].Success ? g["url"].Value : "";
                var title = g["title"].Success ? Decode(g["title"].Value) : "";
                if (itemUrl.Length == 0 || title.Length == 0) continue;
                result.Add(new VodItem
                {
                    Id = Absolute(web, itemUrl),
                    SourceKey = sourceKey,
                    Title = title,
                    Cover = g["cover"].Success ? Absolute(web, Decode(g["cover"].Value)) : null,
                });
            }
            return result;
        }

        // 回退：抓各分类前 FallbackSearchPages 页，标题过滤（站点搜索接口防爬时的可用替代）
        var kwNoSpace = kw;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<VodItem>();
        foreach (var cat in web.Categories)
        {
            for (int page = 1; page <= FallbackSearchPages; page++)
            {
                var url = BuildListUrl(web, cat, page);
                var html = await GetHtmlAsync(url);
                if (html == null) break;

                if (string.IsNullOrEmpty(web.Rules.ListItem)) break;
                foreach (Match m in Regex.Matches(html, web.Rules.ListItem))
                {
                    var g = m.Groups;
                    var itemUrl = g["url"].Success ? g["url"].Value : "";
                    var title = g["title"].Success ? Decode(g["title"].Value) : "";
                    if (itemUrl.Length == 0 || title.Length == 0) continue;
                    if (!title.Contains(kwNoSpace, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!seen.Add(itemUrl)) continue;
                    results.Add(new VodItem
                    {
                        Id = Absolute(web, itemUrl),
                        SourceKey = sourceKey,
                        Title = title,
                        Cover = g["cover"].Success ? Absolute(web, Decode(g["cover"].Value)) : null,
                        Category = cat.Name,
                        Year = g["year"].Success ? Decode(g["year"].Value) : null,
                    });
                }
            }
        }
        return results;
    }

    /// <summary>回退搜索每分类抓取页数</summary>
    private const int FallbackSearchPages = 3;

    // ═══════════════════ 工具 ═══════════════════

    /// <summary>文档加载（web 源文件也走缓存）</summary>
    public static async Task<CatClawSourceWeb> LoadWebAsync(string url, HttpClient http)
    {
        var json = url.StartsWith("file://", StringComparison.OrdinalIgnoreCase) || File.Exists(url)
            ? await File.ReadAllTextAsync(url.StartsWith("file://") ? new Uri(url).LocalPath : url)
            : await http.GetStringAsync(url);
        var web = System.Text.Json.JsonSerializer.Deserialize<CatClawSourceWeb>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                  ?? throw new InvalidOperationException("猫爪源解析结果为空");
        if (!string.Equals(web.Magic, CatClawSourceDoc.ProtocolMagic, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"不是有效的猫爪源（magic={web.Magic}）");
        web.LoadedAt = DateTime.Now;
        return web;
    }

    private async Task<string?> GetHtmlAsync(string url)
    {
        if (_htmlCache.TryGetValue(url, out var cached) && cached.At + CacheTtl > DateTime.Now)
            return cached.Html;
        try
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode) return null;
            var html = await resp.Content.ReadAsStringAsync();
            _htmlCache[url] = (DateTime.Now, html);
            if (_htmlCache.Count > 200) CleanCache();
            return html;
        }
        catch { return null; }
    }

    private void CleanCache()
    {
        foreach (var kv in _htmlCache)
            if (kv.Value.At + CacheTtl < DateTime.Now)
                _htmlCache.TryRemove(kv.Key, out _);
    }

    /// <summary>执行规则正则并返回命名组（不存在的组返回空串）</summary>
    private static string FirstGroup(string html, string pattern, string group)
    {
        var m = Regex.Match(html, pattern);
        return m.Success && m.Groups[group].Success ? m.Groups[group].Value : "";
    }

    /// <summary>执行规则正则返回命名组原始 HTML（desc 用，保留标签后再统一去）</summary>
    private static string FirstGroupHtml(string html, string pattern, string group)
    {
        var m = Regex.Match(html, pattern, RegexOptions.Singleline);
        return m.Success && m.Groups[group].Success ? m.Groups[group].Value : "";
    }

    private static string Absolute(CatClawSourceWeb web, string url) => Absolute(web.Site, url);

    private static string Absolute(string baseUrl, string url)
    {
        url = Decode(url).Trim();
        try { return new Uri(new Uri(baseUrl), url).ToString(); }
        catch { return url; }
    }

    private static string Decode(string s) => System.Net.WebUtility.HtmlDecode(s ?? "").Trim();

    /// <summary>清理字段值（站点元数据常见前缀冒号/全角空白）</summary>
    private static string CleanField(string s) => s.Trim('　', ' ', '：', ':', '\u00a0');
}
