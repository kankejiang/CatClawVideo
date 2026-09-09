using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CatClawVideo.Core.Models;
using CatClawVideo.Core.Providers;

// ═══════════════════════════════════════════════════════════════
// 猫爪源生成器（试点：xb6v.com 磁力下载站）
// 用法：
//   dotnet run -- --pages 1 --out xb6v.ccs.json
//   --pages  每个分类抓取的列表页数（默认 1，页大小约 25 条）
//   --out    输出的猫爪源文件路径
// 产出：符合 CatClaw Source v1 协议的本地源文件，App 设置页直接粘贴本地路径添加
// ═══════════════════════════════════════════════════════════════

var baseUrl = "https://www.xb6v.com";
var ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/124.0";

int maxPages = 1;
string output = "xb6v.ccs.json";
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--pages") int.TryParse(args[i + 1], out maxPages);
    if (args[i] == "--out") output = args[i + 1];
}
maxPages = Math.Clamp(maxPages, 1, 20);

// ═══ 自检模式：加载 web 规则源，走 列表→详情→播放解析 全链路并输出（规则调试器）═══
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] != "--selftest") continue;
    return await SelfTest.RunAsync(args[i + 1]);
}

// 分类清单（xb6v 栏目 → 猫爪源分类）
var categories = new (string Path, string Name)[]
{
    ("/juqingpian/", "剧情片"),
    ("/aiqingpian/", "爱情片"),
    ("/zhanzhengpian/", "战争片"),
    ("/xijupian/", "喜剧片"),
    ("/donghuapian/", "动画片"),
    ("/dianshiju/", "电视剧"),
    ("/ZongYi/", "综艺"),
};

using var http = new HttpClient();
http.Timeout = TimeSpan.FromSeconds(20);
http.DefaultRequestHeaders.UserAgent.ParseAdd(ua);

var semaphore = new SemaphoreSlim(4); // 礼貌并发
var items = new ConcurrentDictionary<string, CatClawSourceItem>(); // 按 url 去重

/// <summary>每部影片最多解析的在线播放入口数（防剧集过多拖慢生成）</summary>
const int MaxPlayEntriesPerItem = 24;

// ═══ 1. 抓分类列表页 ═══
var detailUrls = new ConcurrentBag<(string Url, string Category, string? Cover)>();
foreach (var (path, name) in categories)
{
    for (int page = 1; page <= maxPages; page++)
    {
        var listUrl = page == 1 ? baseUrl + path : $"{baseUrl}{path}index_{page}.html";
        try
        {
            var html = await GetAsync(listUrl);
            if (html == null) { Log($"[{name}] 第{page}页拉取失败"); break; }
            var entries = ParseList(html, path);
            foreach (var e in entries)
                detailUrls.Add((e.Url, name, e.Cover));
            Log($"[{name}] 第{page}页：{entries.Count} 条");
        }
        catch (Exception ex) { Log($"[{name}] 第{page}页异常：{ex.Message}"); break; }
    }
}
Log($"列表抓取完成，去重前详情页 {detailUrls.Count} 个");

// ═══ 2. 抓详情页（在线播放入口 + 磁力 + 简介）═══
var unique = detailUrls.DistinctBy(d => d.Url).ToList();
int done = 0;
var tasks = unique.Select(async entry =>
{
    await semaphore.WaitAsync();
    try
    {
        var html = await GetAsync(baseUrl + entry.Url);
        if (html == null) return;
        var detailOpt = ParseDetail(html);
        if (detailOpt is not { } detail) return;

        // 在线线路：只存稳定的播放页链接（resolve 模式），App 播放时实时嗅探直链——
        // 站点直链带时效签名，快照进源文件会整批过期
        var playEpisodes = detail.PlayUrls
            .Select((p, i) => new CatClawSourceEpisode
            {
                Name = detail.PlayNames.Count > i && detail.PlayNames[i].Length > 0
                    ? detail.PlayNames[i]
                    : $"播放源{i + 1}",
                Resolve = new Uri(new Uri(baseUrl), p).ToString(),
            })
            .Take(MaxPlayEntriesPerItem)
            .ToList();
        if (playEpisodes.Count == 0 && detail.Sources.Sum(s => s.Episodes.Count) == 0) return;

        if (playEpisodes.Count > 0)
            detail.Sources.Insert(0, new CatClawSourceGroup { Name = "在线播放", Episodes = playEpisodes });

        var id = entry.Url.GetHashCode().ToString("x8");
        items[entry.Url] = new CatClawSourceItem
        {
            Id = id,
            Title = detail.Title,
            Cover = detail.Cover ?? entry.Cover,
            Category = entry.Category,
            Year = detail.Year,
            Area = detail.Area,
            Remarks = detail.Remarks ?? (playEpisodes.Count > 0 ? "在线" : null),
            Score = detail.Score,
            Description = detail.Description,
            Sources = detail.Sources,
        };
        var n = Interlocked.Increment(ref done);
        if (n % 20 == 0 || n == unique.Count) Log($"详情进度 {n}/{unique.Count}");
    }
    catch { }
    finally { semaphore.Release(); }
});
await Task.WhenAll(tasks);

// ═══ 3. 生成猫爪源文件 ═══
var doc = new CatClawSourceDoc
{
    Magic = CatClawSourceDoc.ProtocolMagic,
    Version = CatClawSourceDoc.ProtocolVersion,
    Name = "6V电影磁力站",
    Updated = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
    Categories = categories.Select(c => new CatClawSourceCategory
    {
        Id = c.Name,
        Name = c.Name,
    }).ToList(),
    Items = items.Values.OrderBy(i => i.Category).ThenBy(i => i.Title).ToList(),
};

var opts = new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};
var json = JsonSerializer.Serialize(doc, opts);
var outPath = Path.GetFullPath(output);
Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
await File.WriteAllTextAsync(outPath, json, new UTF8Encoding(false));

Log($"完成：{doc.Items.Count} 部影片 → {outPath}");
Console.WriteLine();
Console.WriteLine($"把下面地址粘贴到 猫爪影视 → 设置 → 订阅源管理 即可使用：");
Console.WriteLine(outPath);

return 0;

// ═══════════════════ 解析与工具 ═══════════════════

/// <summary>解析列表页：条目 url / 标题 / 封面 / 年份</summary>
static List<(string Url, string Title, string? Cover, string? Year)> ParseList(string html, string categoryPath)
{
    var result = new List<(string, string, string?, string?)>();
    // 主列表条目：thumbnail 区块（带封面）+ h2 标题
    var rx = new Regex(
        @"<div class=""thumbnail"">\s*<a\s+href=""(?<url>/[^""]+\.html)""[^>]*title=""(?<title>[^""]*)"">\s*<img\s+src=""(?<cover>[^""]*)""",
        RegexOptions.Compiled);
    var metaRx = new Regex(@"◎年\s*代\s*(?<year>\d{4})", RegexOptions.Compiled);

    foreach (Match m in rx.Matches(html))
    {
        var url = m.Groups["url"].Value;
        if (!url.Contains(categoryPath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) continue;
        var title = System.Net.WebUtility.HtmlDecode(m.Groups["title"].Value).Trim();
        if (title.Length == 0) continue;

        // 年份从条目附近元数据取（同页面同一区块内的第一个 ◎年 代）
        string? year = null;
        var rest = html[(m.Index + m.Length)..];
        var nearMeta = metaRx.Match(rest.Length > 1500 ? rest[..1500] : rest);
        if (nearMeta.Success) year = nearMeta.Groups["year"].Value;

        result.Add((url, title, m.Groups["cover"].Value, year));
    }
    return result;
}

/// <summary>解析详情页：标题 / 播放入口(链接+集名) / 磁力列表 / 简介 / 年份产地 / 清晰度备注</summary>
static (string Title, string? Cover, string? Year, string? Area, string? Remarks, double Score,
         string? Description, List<string> PlayUrls, List<string> PlayNames, List<CatClawSourceGroup> Sources)? ParseDetail(string html)
{
    // 标题：h1（entry 标题，取第一个非站点名 h1）
    var titleMatch = Regex.Match(html, @"<h1[^>]*>\s*(?:<a[^>]*>)?([^<]{2,80})</a>\s*</h1>");
    var title = titleMatch.Success
        ? System.Net.WebUtility.HtmlDecode(titleMatch.Groups[1].Value).Trim()
        : Regex.Match(html, @"<title>([^<\-_]+)").Groups[1].Value.Trim();
    if (title.Length == 0) return null;

    // 在线播放入口（帝国CMS DownSys 播放页，pathid 为集/源序号）
    // 注意站点 HTML 不规范：href= 与引号间可能带空格（href= "..."）；集名取 a 标签内文本
    var playUrls = new List<string>();
    var playNames = new List<string>();
    foreach (Match m in Regex.Matches(html, @"<a[^>]*href\s*=\s*['""](/e/DownSys/play/[^'""]+)['""][^>]*>([^<]*)</a>"))
    {
        playUrls.Add(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value));
        playNames.Add(System.Net.WebUtility.HtmlDecode(m.Groups[2].Value).Trim());
    }
    if (playUrls.Count == 0)
    {
        // 兜底：只抓链接（a 结构不符时）
        playUrls = Regex.Matches(html, @"href\s*=\s*['""](/e/DownSys/play/[^'""]+)['""]")
            .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value))
            .Distinct()
            .ToList();
    }

    // 磁力链接：【下载地址】区块内 磁力：<a href="magnet:...">文件名</a>
    var sources = new List<CatClawSourceGroup>();
    var magnetRx = new Regex(
        @"磁力[：:]\s*<a\s+href=""(?<url>magnet:\?xt=urn:btih:[^""]+)""[^>]*>(?<name>[^<]+)</a>",
        RegexOptions.Compiled);
    var episodes = magnetRx.Matches(html)
        .Select(m => new CatClawSourceEpisode
        {
            Name = System.Net.WebUtility.HtmlDecode(m.Groups["name"].Value).Trim() is { Length: > 0 } n ? n : "磁力下载",
            Url = System.Net.WebUtility.HtmlDecode(m.Groups["url"].Value),
        })
        .ToList();
    if (episodes.Count > 0)
        sources.Add(new CatClawSourceGroup { Name = "磁力下载", Episodes = episodes });

    // 元数据行：◎年 代 / ◎产 地 / ◎豆瓣评分
    var text = System.Net.WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", ""));
    var year = Regex.Match(text, @"◎年\s*代[：:\s]*(\d{4})").Groups[1].Value;
    var area = Regex.Match(text, @"◎产\s*地[：:\s]*([^\r\n◎]{1,20})").Groups[1].Value.Trim('　', ' ', '：', ':');
    var scoreText = Regex.Match(text, @"◎豆瓣评分\s*([\d.]+)\s*/").Groups[1].Value;
    double.TryParse(scoreText, out var score);

    // 清晰度备注：取第一个磁力文件名里的质量标记
    string? remarks = null;
    var quality = Regex.Match(episodes.FirstOrDefault()?.Name ?? "", @"(1080p|720p|4K|2160p|HD|BD|TC|TS)", RegexOptions.IgnoreCase);
    if (quality.Success) remarks = quality.Value.ToUpperInvariant();

    // 简介：◎简 介 之后到 【下载地址】/ 下载地址 之前（◎ 与字段间可能是全角/半角空格）
    var desc = "";
    var descMatch = Regex.Match(text, @"◎简\s*介");
    if (descMatch.Success)
    {
        var seg = text[(descMatch.Index + descMatch.Length)..];
        var end = seg.IndexOf("【下载地址】", StringComparison.Ordinal);
        if (end < 0) end = seg.IndexOf("下载地址", StringComparison.Ordinal);
        if (end > 0) seg = seg[..end];
        desc = seg.Trim();
        if (desc.Length > 800) desc = desc[..800] + "…";
    }

    return (title, null, year.Length > 0 ? year : null, area.Length > 0 ? area : null,
            remarks, score, desc.Length > 0 ? desc : null, playUrls, playNames, sources);
}

async Task<string?> GetAsync(string url)
{
    try
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsStringAsync();
    }
    catch { return null; }
}

void Log(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");

/// <summary>规则源自检：列表 → 详情 → 播放解析全链路输出</summary>
internal static class SelfTest
{
    public static async Task<int> RunAsync(string ccsPath)
    {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(20);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64) Chrome/124.0");

        var web = await CatClawWebEngine.LoadWebAsync(ccsPath, http);
        Console.WriteLine($"源: {web.Name} (v{web.Version}, {web.Mode}) / 分类 {web.Categories.Count} 个");

        var cat = web.Categories.First();
        var engine = new CatClawWebEngine();
        Console.WriteLine($"\n── 列表 [{cat.Name}] 第1页 ──");
        var items = await engine.GetItemsAsync(web, cat, 1, "selftest");
        Console.WriteLine($"条目 {items.Count} 个，前 3 部:");
        foreach (var it in items.Take(3))
            Console.WriteLine($"  · {it.Title} ({it.Year}) 封面={(it.Cover != null)}");

        var first = items.FirstOrDefault();
        if (first == null) { Console.WriteLine("列表为空！检查 listItem 规则"); return 1; }

        Console.WriteLine($"\n── 详情 {first.Title} ──");
        var (sources, year, area, desc, remarks) = await engine.GetDetailAsync(web, first.Id, first.Title);
        Console.WriteLine($"年份={year} 产地={area} 备注={remarks} 简介长度={desc?.Length ?? 0}");
        foreach (var src in sources)
            Console.WriteLine($"  线路[{src.Name}] {src.Episodes.Count} 集: {string.Join(", ", src.Episodes.Take(3).Select(e => e.Name))}");

        var online = sources.FirstOrDefault(s => s.Name == "在线播放");
        if (online == null) { Console.WriteLine("\n无在线线路！检查 detailPlay 规则"); return 1; }

        Console.WriteLine($"\n── 播放解析 {online.Episodes[0].Name} ──");
        Console.WriteLine($"入口: {online.Episodes[0].Url}");
        var play = await engine.ResolvePlayAsync(web, online.Episodes[0].Name, online.Episodes[0].Url, default);
        Console.WriteLine($"直链: {play.Url}");
        Console.WriteLine($"Referer: {play.Referer}");

        // 直链可用性
        try
        {
            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, play.Url);
            req.Headers.Referrer = play.Referer != null ? new Uri(play.Referer) : null;
            using var resp = await http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"直链状态: HTTP {(int)resp.StatusCode}，{(body.StartsWith("#EXTM3U") ? "有效 m3u8 ✔" : $"内容开头: {body[..Math.Min(60, body.Length)]}")}");
        }
        catch (Exception ex) { Console.WriteLine($"直链请求失败: {ex.Message}"); }

        Console.WriteLine("\n全链路自检完成 ✔");
        return 0;
    }
}
