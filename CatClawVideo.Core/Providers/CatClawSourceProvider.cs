using System.Collections.Concurrent;
using System.Text.Json;
using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;

namespace CatClawVideo.Core.Providers;

/// <summary>
/// 猫爪源适配器（CatClaw Source，自建生态 v1）：
/// 单文件纯静态 JSON（分类 + 影片全集内嵌播放直链），托管于任意静态空间，
/// 原生 HttpClient 拉取 + 内存缓存（10 分钟 TTL），无爬虫运行时依赖，全平台一致可播。
/// 协议模型见 <see cref="CatClawSourceDoc"/>。
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

    /// <summary>整包缓存 TTL（期间重复取分类/列表/详情零请求）</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>site.Api → 整包缓存（Lazy 防并发重复下载）</summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<CatClawSourceDoc>>> _cache = new();

    public string Id => "catclaw";
    public string Name => "猫爪源";

    public bool CanHandle(VodSiteInfo site) => site.Type == CatClawSourceDoc.SiteType;

    // ═══════════════════ ISpiderRuntime 协议实现 ═══════════════════

    public async Task<List<VodCategory>> GetCategoriesAsync(VodSiteInfo site, CancellationToken ct = default)
    {
        var doc = await LoadDocAsync(site, ct);
        return doc.Categories
            .Where(c => !string.IsNullOrEmpty(c.Id))
            .Select(c => new VodCategory { Id = c.Id, Name = c.Name })
            .ToList();
    }

    public async Task<List<VodItem>> GetItemsAsync(VodSiteInfo site, VodCategory category, int page = 1, CancellationToken ct = default)
    {
        var doc = await LoadDocAsync(site, ct);
        var matched = doc.Items
            .Where(i => string.IsNullOrEmpty(category.Id) ||
                        string.Equals(i.Category, category.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matched.Skip((page - 1) * PageSize).Take(PageSize).Select(i => ToVodItem(i, site)).ToList();
    }

    public async Task<List<VodPlaySource>> GetPlaySourcesAsync(VodSiteInfo site, VodItem item, CancellationToken ct = default)
    {
        var doc = await LoadDocAsync(site, ct);
        var src = FindItem(doc, item.Id);
        if (src == null) return [];
        return ToPlaySources(src);
    }

    public Task<PlayRequest> ResolvePlayUrlAsync(VodSiteInfo site, VodEpisode episode, CancellationToken ct = default)
    {
        // 猫爪源：剧集即播放直链（m3u8/mp4），可选 UA / Referer 防盗链
        return Task.FromResult(new PlayRequest
        {
            Title = episode.Name,
            Url = episode.Url,
            UserAgent = FindEpisode(episode.Url)?.Ua,
            Referer = FindEpisode(episode.Url)?.Referer ?? site.Api,
        });
    }

    public async Task<List<VodItem>> SearchAsync(VodSiteInfo site, string keyword, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return [];
        var doc = await LoadDocAsync(site, ct);
        return doc.Items
            .Where(i => i.Title.Contains(keyword.Trim(), StringComparison.OrdinalIgnoreCase))
            .Take(PageSize)
            .Select(i => ToVodItem(i, site))
            .ToList();
    }

    // ═══════════════════ 内部实现 ═══════════════════

    /// <summary>列表分页大小（内存分页，与 MacCMS 接口语义对齐）</summary>
    private const int PageSize = 60;

    /// <summary>拉取并解析整包（带缓存；解析失败抛出明确异常）</summary>
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
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        var doc = JsonSerializer.Deserialize<CatClawSourceDoc>(json, JsonOpts)
                  ?? throw new InvalidOperationException("猫爪源解析结果为空");

        if (!string.Equals(doc.Magic, CatClawSourceDoc.ProtocolMagic, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"不是有效的猫爪源（magic={doc.Magic}），确认地址指向 ccs 源文件");
        if (doc.Version > CatClawSourceDoc.ProtocolVersion)
            throw new InvalidOperationException($"猫爪源协议版本过高（v{doc.Version} > v{CatClawSourceDoc.ProtocolVersion}），请升级 App");

        doc.LoadedAt = DateTime.Now;
        return doc;
    }

    private static CatClawSourceItem? FindItem(CatClawSourceDoc doc, string id) =>
        doc.Items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));

    /// <summary>按播放直链反查剧集（取可选 UA/Referer）</summary>
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
                        if (string.Equals(ep.Url, url, StringComparison.Ordinal))
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
                    .Where(e => !string.IsNullOrEmpty(e.Url))
                    .Select(e => new VodEpisode { Name = e.Name, Url = e.Url })
                    .ToList(),
            })
            .ToList();
}
