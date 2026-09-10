using System.Collections.Concurrent;
using System.Text.Json;
using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;

namespace CatClawVideo.Core.Providers;

/// <summary>
/// 猫爪源适配器（CatClaw Source，自建生态），按源文件 mode 分流：
/// <para>
/// · static（type=100，v1）：单文件纯静态整包（分类 + 影片全集内嵌播放直链），
///   拉取 + 内存缓存（10 分钟 TTL），适合自建轻量源；
/// · web（type=101，v2）：站点入口 + 声明式解析规则（几 KB），数据全部由
///   <see cref="CatClawWebEngine"/> 按需实时抓取，源永不携带内容，适配任意站点。
/// </para>
/// 无爬虫运行时依赖，全平台一致可播。
/// </summary>
public class CatClawSourceProvider : IVodSourceProvider
{
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CatClawVideo/1.0");
        return client;
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>static 整包缓存 TTL</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>static：site.Api → 整包缓存（Lazy 防并发重复下载）</summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<CatClawSourceDoc>>> _cache = new();

    /// <summary>web：site.Api → 规则文档缓存</summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<CatClawSourceWeb>>> _webCache = new();

    /// <summary>web 模式通用引擎</summary>
    private readonly CatClawWebEngine _engine = new();

    /// <summary>BT 流式引擎（磁力边下边播；未注入时磁力线路保持明确报错）</summary>
    private readonly Services.BtStreamService? _bt;

    public CatClawSourceProvider(Services.BtStreamService? bt = null) => _bt = bt;

    public string Id => "catclaw";
    public string Name => "猫爪源";

    public bool CanHandle(VodSiteInfo site) =>
        site.Type == CatClawSourceDoc.SiteType || site.Type == CatClawSourceWeb.WebSiteType;

    private bool IsWeb(VodSiteInfo site) => site.Type == CatClawSourceWeb.WebSiteType;

    // ═══════════════════ 分类 ═══════════════════

    public async Task<List<VodCategory>> GetCategoriesAsync(VodSiteInfo site, CancellationToken ct = default)
    {
        if (IsWeb(site))
        {
            var web = await LoadWebAsync(site);
            return web.Categories
                .Where(c => !string.IsNullOrEmpty(c.Id))
                .Select(c => new VodCategory { Id = c.Id, Name = c.Name })
                .ToList();
        }

        var doc = await LoadDocAsync(site, ct);
        return doc.Categories
            .Where(c => !string.IsNullOrEmpty(c.Id))
            .Select(c => new VodCategory { Id = c.Id, Name = c.Name })
            .ToList();
    }

    // ═══════════════════ 列表 ═══════════════════

    public async Task<List<VodItem>> GetItemsAsync(VodSiteInfo site, VodCategory category, int page = 1, CancellationToken ct = default)
    {
        if (IsWeb(site))
        {
            var web = await LoadWebAsync(site);
            var webCat = web.Categories.FirstOrDefault(c => c.Id == category.Id);
            if (webCat == null) return [];
            return await _engine.GetItemsAsync(web, webCat, page, site.Key);
        }

        var doc = await LoadDocAsync(site, ct);
        var matched = doc.Items
            .Where(i => string.IsNullOrEmpty(category.Id) ||
                        string.Equals(i.Category, category.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matched.Skip((page - 1) * PageSize).Take(PageSize).Select(i => ToVodItem(i, site)).ToList();
    }

    // ═══════════════════ 详情线路 ═══════════════════

    public async Task<List<VodPlaySource>> GetPlaySourcesAsync(VodSiteInfo site, VodItem item, CancellationToken ct = default)
    {
        if (IsWeb(site))
        {
            var web = await LoadWebAsync(site);
            var (sources, _, _, _, _) = await _engine.GetDetailAsync(web, item.Id, item.Title);
            return sources;
        }

        var doc = await LoadDocAsync(site, ct);
        var src = FindItem(doc, item.Id);
        if (src == null) return [];
        return ToPlaySources(src);
    }

    // ═══════════════════ 播放解析 ═══════════════════

    public async Task<PlayRequest> ResolvePlayUrlAsync(VodSiteInfo site, VodEpisode episode, CancellationToken ct = default)
    {
        var url = episode.Url ?? "";

        // 电驴：BT 引擎只覆盖 BT 协议，明确提示
        if (url.StartsWith("ed2k://", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("该集为电驴(ed2k)下载链接，暂不支持在线播放；可复制链接到下载工具");

        // 磁力：BT 流式引擎边下边播 → 本地 127.0.0.1 代理地址（可 Range 拖动）
        if (url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            if (_bt == null)
                throw new NotSupportedException("BT 引擎未初始化，磁力线路不可用");
            var session = await _bt.OpenAsync(url, episode.Name, ct);
            return new PlayRequest { Title = episode.Name, Url = session.Url };
        }

        // web 模式：非直链 URL（播放入口页）→ 规则引擎实时解析直链（时效签名现取现用）
        if (IsWeb(site) &&
            !url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) &&
            !url.Contains(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            var web = await LoadWebAsync(site);
            return await _engine.ResolvePlayAsync(web, episode.Name, url, ct);
        }

        // static 模式 resolve（v1.1）：非直链形态 → 通用嗅探
        var isDirect = url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                       url.Contains(".mp4", StringComparison.OrdinalIgnoreCase);
        if (!isDirect && url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var (resolved, referer) = await WebProbeResolver.ResolveAsync(url, ct: ct);
            return new PlayRequest { Title = episode.Name, Url = resolved, Referer = referer };
        }

        // 直链模式：可选 UA / Referer 防盗链
        return new PlayRequest
        {
            Title = episode.Name,
            Url = url,
            UserAgent = FindEpisode(url)?.Ua,
            Referer = FindEpisode(url)?.Referer ?? site.Api,
        };
    }

    // ═══════════════════ 搜索 ═══════════════════

    public async Task<List<VodItem>> SearchAsync(VodSiteInfo site, string keyword, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return [];

        if (IsWeb(site))
        {
            var web = await LoadWebAsync(site);
            return await _engine.SearchAsync(web, keyword, site.Key);
        }

        var doc = await LoadDocAsync(site, ct);
        return doc.Items
            .Where(i => i.Title.Contains(keyword.Trim(), StringComparison.OrdinalIgnoreCase))
            .Take(PageSize)
            .Select(i => ToVodItem(i, site))
            .ToList();
    }

    // ═══════════════════ 文档加载 ═══════════════════

    /// <summary>web 规则文档加载（带缓存；本地/远程通吃）</summary>
    private async Task<CatClawSourceWeb> LoadWebAsync(VodSiteInfo site)
    {
        var lazy = _webCache.GetOrAdd(site.Api, key => new Lazy<Task<CatClawSourceWeb>>(() => CatClawWebEngine.LoadWebAsync(key, Http)));
        return await lazy.Value;
    }

    /// <summary>static 整包拉取并解析（带缓存；解析失败抛出明确异常）</summary>
    private async Task<CatClawSourceDoc> LoadDocAsync(VodSiteInfo site, CancellationToken ct)
    {
        var lazy = _cache.GetOrAdd(site.Api, key => new Lazy<Task<CatClawSourceDoc>>(() => FetchDocAsync(key, ct)));
        var doc = await lazy.Value;
        if (doc.LoadedAt + CacheTtl < DateTime.Now)
        {
            // 过期：作废缓存重新加载（并发下可能多拉一次，可接受）
            _cache.TryRemove(site.Api, out _);
            return await LoadDocAsync(site, ct);
        }
        return doc;
    }

    private static async Task<CatClawSourceDoc> FetchDocAsync(string url, CancellationToken ct)
    {
        // 本地源文件：直接读盘（生态 v1 支持本地 ccs.json）
        string json;
        if (File.Exists(url) || url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            var path = url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                ? new Uri(url).LocalPath
                : url;
            json = await File.ReadAllTextAsync(path, ct);
        }
        else
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            json = await resp.Content.ReadAsStringAsync(ct);
        }

        var doc = JsonSerializer.Deserialize<CatClawSourceDoc>(json, JsonOpts)
                  ?? throw new InvalidOperationException("猫爪源解析结果为空");

        if (!string.Equals(doc.Magic, CatClawSourceDoc.ProtocolMagic, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"不是有效的猫爪源（magic={doc.Magic}），确认地址指向 ccs 源文件");
        if (doc.Version > CatClawSourceDoc.ProtocolVersion)
            throw new InvalidOperationException($"猫爪源协议版本过高（v{doc.Version} > v{CatClawSourceDoc.ProtocolVersion}），请升级 App");

        doc.LoadedAt = DateTime.Now;
        return doc;
    }

    // ═══════════════════ static 模式内部 ═══════════════════

    /// <summary>列表分页大小（内存分页，与 MacCMS 接口语义对齐）</summary>
    private const int PageSize = 60;

    private static CatClawSourceItem? FindItem(CatClawSourceDoc doc, string id) =>
        doc.Items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));

    /// <summary>按播放链接反查剧集配置（取可选 UA/Referer；同时匹配直链与 resolve 页面链接）</summary>
    private CatClawSourceEpisode? FindEpisode(string url)
    {
        foreach (var doc in _cache.Values)
        {
            if (!doc.IsValueCreated) continue;
            var task = doc.Value;
            if (!task.IsCompletedSuccessfully) continue;
            foreach (var item in task.Result.Items)
                foreach (var group in item.Sources)
                    foreach (var ep in group.Episodes)
                        if (string.Equals(ep.Url, url, StringComparison.Ordinal) ||
                            string.Equals(ep.Resolve, url, StringComparison.Ordinal))
                            return ep;
        }
        return null;
    }

    private static VodItem ToVodItem(CatClawSourceItem i, VodSiteInfo site) => new()
    {
        Id = i.Id,
        SourceKey = site.Key,
        Title = i.Title,
        Cover = i.Cover,
        Category = i.Category,
        Year = i.Year,
        Area = i.Area,
        Remarks = i.Remarks,
        Score = i.Score,
        Actors = i.Actors,
        Director = i.Director,
        Description = i.Description,
    };

    private static List<VodPlaySource> ToPlaySources(CatClawSourceItem item) =>
        item.Sources
            .Where(g => g.Episodes.Count > 0)
            .Select(g => new VodPlaySource
            {
                Name = string.IsNullOrEmpty(g.Name) ? "默认线路" : g.Name,
                Episodes = g.Episodes
                    .Where(e => !string.IsNullOrEmpty(e.Url) || !string.IsNullOrEmpty(e.Resolve))
                    .Select(e => new VodEpisode
                    {
                        Name = e.Name,
                        // resolve（页面链接）优先存入 Url；ResolvePlayUrlAsync 按是否直链形态分流
                        Url = string.IsNullOrEmpty(e.Resolve) ? e.Url ?? "" : e.Resolve,
                    })
                    .ToList(),
            })
            .ToList();
}
