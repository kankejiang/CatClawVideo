using System.Collections.Concurrent;
using System.Text;
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
        // 与破盾器共享 cookie 容器；SocketsHttpHandler 统一全平台行为
        //（AndroidMessageHandler 对挑战页非标准状态码 850 会抛异常，见 CdnDefendSolver）
        var client = new HttpClient(CdnDefendSolver.CreateSharedHandler(), disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/124.0");
        return client;
    }

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 搜索专用客户端：**不保存 cookie**。
    /// ⚠️ 帝国CMS（6V 电影等）用 cookie <c>tinmklastsearchtime</c> 实施「搜索间隔 60 秒」限流——
    /// 带 cookie 时只有第 1 次搜索出结果，之后全部返回 1202B 的拒绝页；
    /// 不带 cookie 时每次搜索都被当作新访客，可正常连搜（实测 4/5，唯一失败是没有该片名）。
    /// 破盾由 CdnDefendSolver 自带的 cookie 容器完成，不依赖本客户端。
    /// </summary>
    private static readonly HttpClient SearchHttp = CreateSearchHttp();

    private static HttpClient CreateSearchHttp()
    {
        var handler = new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/124.0");
        return client;
    }

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
                // 线路配对分组（v2.2）：detailRouteBlock 逐块枚举各线路剧集区，
                // detailSourceName 枚举线路名，按 DOM 顺序一一配对。
                // 覆盖「线路 tab 与剧集列表分处两个容器」的站点（如毒舌电影）。
                if (!string.IsNullOrEmpty(r.DetailRouteBlock))
                {
                    var blocks = Regex.Matches(html, r.DetailRouteBlock, RegexOptions.Singleline);
                    List<string>? names = null;
                    if (!string.IsNullOrEmpty(r.DetailSourceName))
                        names = Regex.Matches(html, r.DetailSourceName)
                            .Select(m => Decode(m.Groups["name"].Success ? m.Groups["name"].Value : ""))
                            .ToList();

                    var lineNo = 0;
                    for (var i = 0; i < blocks.Count; i++)
                    {
                        var eps = ParseEpisodes(blocks[i].Value, r.DetailPlay, detailUrl);
                        if (eps.Count == 0) continue;
                        lineNo++;
                        var name = Decode(blocks[i].Groups["name"].Value);
                        if (name.Length == 0 && names != null && i < names.Count)
                            name = names[i];
                        sources.Add(new VodPlaySource
                        {
                            Name = name.Length > 0 ? name : $"线路{lineNo}",
                            Episodes = eps,
                        });
                    }
                }

                // 站点常把多条线路各放一个 <h3> 区块（如 6V 的「播放地址一~四」各 89 集）：
                // 按 h3 区块分组解析，一个区块 = 一条线路；无 h3 分段时退回整体单线路。
                if (sources.Count == 0)
                {
                    var segments = SplitByH3(html);
                    if (segments.Count > 1)
                    {
                        var lineNo = 0;
                        foreach (var seg in segments)
                        {
                            var eps = ParseEpisodes(seg, r.DetailPlay, detailUrl);
                            if (eps.Count == 0) continue;
                            lineNo++;
                            sources.Add(new VodPlaySource { Name = $"线路{lineNo}", Episodes = eps });
                        }
                    }
                }

                if (sources.Count == 0)
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
            }

            if (!string.IsNullOrEmpty(r.DetailMagnet))
            {
                var episodes = new List<VodEpisode>();
                foreach (Match m in Regex.Matches(html, r.DetailMagnet))
                {
                    var name = m.Groups["name"].Success && Decode(m.Groups["name"].Value).Trim().Length > 0
                        ? Decode(m.Groups["name"].Value).Trim()
                        : "磁力播放";
                    episodes.Add(new VodEpisode { Name = name, Url = Decode(m.Groups["url"].Value) });
                }
                if (episodes.Count > 0)
                    sources.Add(new VodPlaySource { Name = "磁力播放", Episodes = episodes });
            }
        }

        return (sources, year, area, desc, sources.Count > 0 && sources[0].Name == "在线播放" ? "在线" : null);
    }

    /// <summary>
    /// 按 &lt;h3&gt; 标题把详情页切成区块（每块含标题与其后内容，直到下一个 h3）。
    /// 无 h3 时返回空列表（调用方退回整体解析）。
    /// </summary>
    private static List<string> SplitByH3(string html)
    {
        var marks = Regex.Matches(html, "<h3[^>]*>[^<]{1,120}</h3>");
        if (marks.Count < 2) return [];
        var segments = new List<string>();
        for (var i = 0; i < marks.Count; i++)
        {
            var start = marks[i].Index;
            var end = i + 1 < marks.Count ? marks[i + 1].Index : html.Length;
            segments.Add(html[start..end]);
        }
        return segments;
    }

    /// <summary>对单段 HTML 应用 detailPlay 规则并组装剧集（相对链接以详情页为基址转绝对）</summary>
    private List<VodEpisode> ParseEpisodes(string html, string rule, string baseUrl)
    {
        var episodes = new List<VodEpisode>();
        foreach (Match m in Regex.Matches(html, rule))
        {
            var rawUrl = m.Groups["url"].Success ? m.Groups["url"].Value : "";
            var url = rawUrl.Length > 0 ? Absolute(baseUrl, Decode(rawUrl)) : "";
            var name = m.Groups["name"].Success && Decode(m.Groups["name"].Value).Trim().Length > 0
                ? Decode(m.Groups["name"].Value).Trim()
                : $"播放源{episodes.Count + 1}";
            episodes.Add(new VodEpisode { Name = name, Url = url });
        }
        return episodes;
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

        // urlb64 捕获组约定：捕获内容为 base64 编码的直链（如站点 base64decode() 混淆），解码后使用
        string url;
        if (direct.Groups["urlb64"].Success)
        {
            var b64 = Decode(direct.Groups["urlb64"].Value).Trim();
            url = Absolute(current, Encoding.UTF8.GetString(Convert.FromBase64String(
                b64.Length % 4 == 0 ? b64 : b64 + new string('=', 4 - b64.Length % 4))));
        }
        else
        {
            url = Absolute(current, Decode(direct.Groups["url"].Value));
        }

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

    /// <summary>
    /// 搜索：优先 searchUrl 站点接口（GET，或按 searchMethod=post 走表单 POST）；
    /// 无规则时回退「分类前 N 页 + 标题过滤」本地搜索。
    /// </summary>
    public async Task<List<VodItem>> SearchAsync(CatClawSourceWeb web, string keyword, string sourceKey)
    {
        var kw = keyword?.Trim();
        if (string.IsNullOrEmpty(kw)) return [];

        if (!string.IsNullOrEmpty(web.Rules.SearchUrl))
        {
            var isPost = string.Equals(web.Rules.SearchMethod, "post", StringComparison.OrdinalIgnoreCase);
            string? html;
            if (isPost)
            {
                // 帝国CMS 之类只接受 POST 搜索（GET 会 404）；{kw} 替换为已 URL 编码的关键词
                var body = (web.Rules.SearchBody ?? "keyboard={kw}")
                    .Replace("{kw}", Uri.EscapeDataString(kw));
                var endpoint = Absolute(web, web.Rules.SearchUrl.Replace("{kw}", Uri.EscapeDataString(kw)));
                html = await SearchRequestAsync(endpoint, body);
            }
            else
            {
                var url = web.Rules.SearchUrl.Replace("{kw}", Uri.EscapeDataString(kw));
                html = await SearchRequestAsync(Absolute(web, url), null);
            }
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

    /// <summary>
    /// 多站点文件选择站点（v2.1）：sites 非空时按 id 取出单站点视图（缺 id 取首个）；
    /// 单站点文件原样返回。
    /// </summary>
    public static CatClawSourceWeb SelectSite(CatClawSourceWeb root, string? siteId)
    {
        if (root.Sites is not { Count: > 0 }) return root;
        var s = string.IsNullOrEmpty(siteId)
            ? root.Sites[0]
            : root.Sites.FirstOrDefault(x => string.Equals(x.Id, siteId, StringComparison.OrdinalIgnoreCase)) ?? root.Sites[0];
        return new CatClawSourceWeb
        {
            Magic = root.Magic,
            Version = root.Version,
            Mode = root.Mode,
            Name = s.Name,
            Site = s.Site,
            Updated = root.Updated,
            Categories = s.Categories,
            Rules = s.Rules,
            Id = s.Id,
            LoadedAt = root.LoadedAt,
        };
    }

    private async Task<string?> GetHtmlAsync(string url)
    {
        if (_htmlCache.TryGetValue(url, out var cached) && cached.At + CacheTtl > DateTime.Now)
            return cached.Html;
        try
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            var html = await resp.Content.ReadAsStringAsync();

            // cdndefend 盾：挑战页可能带非标准状态码（如 850），按内容识别而非状态码；
            // 本机算 SHA1 PoW cookie 后重取，破盾失败按抓取失败处理，挑战页绝不入缓存
            if (CdnDefendSolver.IsChallenge(html))
            {
                html = await CdnDefendSolver.SolveAsync(new Uri(url), html) ?? "";
                if (html.Length == 0 || CdnDefendSolver.IsChallenge(html)) return null;
            }
            else if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            _htmlCache[url] = (DateTime.Now, html);
            if (_htmlCache.Count > 200) CleanCache();
            return html;
        }
        catch (Exception ex)
        {
            var sb = new StringBuilder();
            var cur = ex;
            var depth = 0;
            while (cur != null && depth < 4)
            {
                sb.Append(' ', depth * 2).Append(cur.GetType().FullName).Append(": ").Append(cur.Message).Append(" | ");
                cur = cur.InnerException;
                depth++;
            }
            CatClawLog.Write($"[http] 异常 {url[..Math.Min(url.Length, 80)]} → {sb}");
            return null;
        }
    }

    private void CleanCache()
    {
        foreach (var kv in _htmlCache)
            if (kv.Value.At + CacheTtl < DateTime.Now)
                _htmlCache.TryRemove(kv.Key, out _);
    }

    /// <summary>
    /// 搜索请求（GET/POST 共用），走**无 cookie** 的 <see cref="SearchHttp"/>。
    /// 原因见 SearchHttp 注释：帝国CMS 的搜索间隔限流是 cookie 实现的，带 cookie 会只剩第 1 次能搜。
    /// 破盾仍按内容识别（挑战页 → CdnDefendSolver 解 PoW 后重取）。搜索不缓存。
    /// formBody 为 null 表示 GET；否则为已 URL 编码的表单体。
    /// </summary>
    private static async Task<string?> SearchRequestAsync(string url, string? formBody)
    {
        try
        {
            using var req = formBody == null
                ? new HttpRequestMessage(HttpMethod.Get, url)
                : new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(formBody, Encoding.UTF8, "application/x-www-form-urlencoded"),
                };
            using var resp = await SearchHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var html = await resp.Content.ReadAsStringAsync();

            if (CdnDefendSolver.IsChallenge(html))
            {
                html = await CdnDefendSolver.SolveAsync(new Uri(url), html) ?? "";
                if (html.Length == 0 || CdnDefendSolver.IsChallenge(html)) return null;
            }
            else if (!resp.IsSuccessStatusCode)
            {
                return null;
            }
            return html;
        }
        catch { return null; }
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
