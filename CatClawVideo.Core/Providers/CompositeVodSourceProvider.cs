using System.Collections.Concurrent;
using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;

namespace CatClawVideo.Core.Providers;

/// <summary>
/// 聚合路由 Provider：按站点的 CanHandle 把请求路由到具体实现
/// （MacCMS json 直连 / TVBox spider 爬虫）。HomeViewModel / WatchPage 统一注入本类。
/// </summary>
public class CompositeVodSourceProvider : IVodSourceProvider
{
    private readonly IReadOnlyList<IVodSourceProvider> _providers;

    public CompositeVodSourceProvider(IEnumerable<IVodSourceProvider> providers)
    {
        _providers = providers.ToList();
    }

    public string Id => "composite";
    public string Name => "聚合源";

    private IVodSourceProvider? Route(VodSiteInfo site) =>
        _providers.FirstOrDefault(p => p.CanHandle(site));

    public bool CanHandle(VodSiteInfo site) => Route(site) != null;

    private IVodSourceProvider Required(VodSiteInfo site) =>
        Route(site) ?? throw new NotSupportedException($"站点 {site.Name} 没有可用的源适配器（{site.StatusNote ?? "type " + site.Type}）");

    public Task<List<VodCategory>> GetCategoriesAsync(VodSiteInfo site, CancellationToken ct = default) =>
        Required(site).GetCategoriesAsync(site, ct);

    public Task<List<VodItem>> GetItemsAsync(VodSiteInfo site, VodCategory category, int page = 1, CancellationToken ct = default) =>
        Required(site).GetItemsAsync(site, category, page, ct);

    public async Task<List<VodPlaySource>> GetPlaySourcesAsync(VodSiteInfo site, VodItem item, CancellationToken ct = default)
    {
        var sources = await Required(site).GetPlaySourcesAsync(site, item, ct).ConfigureAwait(false);
        return await ExpandMagnetEpisodesAsync(sources, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 把「一条磁力 = 一集」展开成「种子里的每个视频文件 = 一集」。
    ///
    /// <para><b>为什么必须做</b>：磁力站的 vod_play_url 往往是几个**打包磁力**。例如
    /// 新6V《生逢其时》只给 3 条磁力（01-06 / 07 / 08-11），不展开就成了「只能看 1、7、8 三集」，
    /// 而 TVBox 显示 11 集 —— 因为它的 11 集正是种子文件列表（6+1+4）。</para>
    ///
    /// <para>展开靠优先引擎（迅雷）的文件列表：实测 35~567ms/磁力，详情页代价可接受；
    /// 引擎没起来或解析失败时**原样保留**，不影响任何既有行为。</para>
    ///
    /// <para>展开后集名 = 种子内文件名，播放时 <c>BtStreamService</c> 会用它做 preferName
    /// 命中同一个文件，因此无需额外传文件索引。</para>
    /// </summary>
    private static async Task<List<VodPlaySource>> ExpandMagnetEpisodesAsync(
        List<VodPlaySource> sources, CancellationToken ct)
    {
        var engine = Interfaces.MagnetEngines.Thunder;
        if (engine is null || !engine.IsReady) return sources;

        var magnetCount = sources
            .SelectMany(s => s.Episodes)
            .Count(e => e.Url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase));
        // 磁力太多就不展开：详情页会被串行解析拖住（宁可保持现状）
        if (magnetCount == 0 || magnetCount > MaxMagnetsToExpand) return sources;

        foreach (var src in sources)
        {
            var expanded = new List<VodEpisode>(src.Episodes.Count);
            foreach (var ep in src.Episodes)
            {
                if (!ep.Url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                {
                    expanded.Add(ep);
                    continue;
                }

                if (!ListedMagnets.TryGetValue(ep.Url, out var files))
                {
                    try { files = await engine.ListFilesAsync(ep.Url, ep.Name, ct).ConfigureAwait(false); }
                    catch { files = null; }
                    // 只缓存成功结果：失败（引擎未就绪等）下次进详情页重探
                    if (files is { Count: > 0 }) ListedMagnets[ep.Url] = files;
                }

                var videos = files?
                    .Where(f => VideoExtensions.Contains(Path.GetExtension(f.Name)))
                    .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // 展开失败（或种子内没有视频文件）→ 保留原本那一条，行为与改动前一致
                if (videos is null || videos.Count == 0)
                {
                    expanded.Add(ep);
                    continue;
                }

                foreach (var f in videos)
                    expanded.Add(new VodEpisode { Name = f.Name, Url = ep.Url, Flag = ep.Flag });
            }
            src.Episodes = expanded;
        }
        return sources;
    }

    /// <summary>单次详情页最多展开的磁力条数（超出则放弃展开，避免串行解析拖慢）</summary>
    private const int MaxMagnetsToExpand = 12;

    /// <summary>磁力 → 文件列表 的进程级缓存：每条磁力探测要 ~5s（引擎解析种子），5 条磁力的详情页
    /// 首次要 25s+。缓存后再次进入（含离开后回来、超引擎 10 分钟会话）直接命中，秒开。只存成功结果。</summary>
    private static readonly ConcurrentDictionary<string, List<Interfaces.MagnetFile>?> ListedMagnets =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".ts", ".m2ts", ".wmv", ".flv", ".mov",
        ".rmvb", ".rm", ".webm", ".m4v", ".mpg", ".mpeg", ".3gp", ".vob",
    };

    public Task<PlayRequest> ResolvePlayUrlAsync(VodSiteInfo site, VodEpisode episode, CancellationToken ct = default) =>
        Required(site).ResolvePlayUrlAsync(site, episode, ct);

    public Task<List<VodItem>> SearchAsync(VodSiteInfo site, string keyword, CancellationToken ct = default) =>
        Required(site).SearchAsync(site, keyword, ct);
}
