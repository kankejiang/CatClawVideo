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

    public Task<List<VodPlaySource>> GetPlaySourcesAsync(VodSiteInfo site, VodItem item, CancellationToken ct = default) =>
        Required(site).GetPlaySourcesAsync(site, item, ct);

    public Task<PlayRequest> ResolvePlayUrlAsync(VodSiteInfo site, VodEpisode episode, CancellationToken ct = default) =>
        Required(site).ResolvePlayUrlAsync(site, episode, ct);

    public Task<List<VodItem>> SearchAsync(VodSiteInfo site, string keyword, CancellationToken ct = default) =>
        Required(site).SearchAsync(site, keyword, ct);
}
