using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;

namespace CatClawVideo.Core.Providers;

/// <summary>
/// TVBox spider 站点适配器（type=3）：把 ISpiderRuntime 的 TVBox 协议 JSON
/// 解析为统一领域模型，接入现有播放管线。
/// - Script（drpy2 JS）→ <see cref="DrpyJsSpiderRuntime"/>
/// - Jar（Java/dex）→ 运行时由平台侧提供（Android DexClassLoader）；不可用时抛出明确异常
/// </summary>
public class SpiderVodProvider : IVodSourceProvider
{
    private readonly ISpiderRuntime? _jsRuntime;
    private readonly ISpiderRuntime? _jarRuntime;

    public SpiderVodProvider(ISpiderRuntime? jsRuntime, ISpiderRuntime? jarRuntime = null)
    {
        _jsRuntime = jsRuntime;
        _jarRuntime = jarRuntime;
    }

    public string Id => "spider";
    public string Name => "TVBox 爬虫源";

    private ISpiderRuntime? RuntimeFor(VodSiteInfo site) => site.SpiderKind switch
    {
        VodSpiderKind.Script => _jsRuntime,
        VodSpiderKind.Jar => _jarRuntime,
        _ => null,
    };

    public bool CanHandle(VodSiteInfo site) => site.SpiderKind != VodSpiderKind.None && RuntimeFor(site) != null;

    public async Task<List<VodCategory>> GetCategoriesAsync(VodSiteInfo site, CancellationToken ct = default)
    {
        var rt = RuntimeFor(site) ?? throw new NotSupportedException(site.StatusNote ?? "爬虫运行时不可用");
        return SpiderJsonParser.ParseCategories(await rt.HomeContentAsync(site, ct));
    }

    public async Task<List<VodItem>> GetItemsAsync(VodSiteInfo site, VodCategory category, int page = 1, CancellationToken ct = default)
    {
        var rt = RuntimeFor(site) ?? throw new NotSupportedException(site.StatusNote ?? "爬虫运行时不可用");
        return SpiderJsonParser.ParseItems(
            await rt.CategoryContentAsync(site, category.Id, page.ToString(), ct), site.Key);
    }

    public async Task<List<VodPlaySource>> GetPlaySourcesAsync(VodSiteInfo site, VodItem item, CancellationToken ct = default)
    {
        var rt = RuntimeFor(site) ?? throw new NotSupportedException(site.StatusNote ?? "爬虫运行时不可用");
        var sources = SpiderJsonParser.ParsePlaySources(await rt.DetailContentAsync(site, item.Id, ct));
        if (sources.Count == 0)
        {
            // TVBox 「二级=*」语义：detail 不带播放列表，播放时由 playerContent 解析 vod_id
            sources.Add(new VodPlaySource
            {
                Name = site.Name,
                Episodes = [new VodEpisode { Name = item.Title, Url = item.Id }],
            });
        }
        return sources;
    }

    public Task<PlayRequest> ResolvePlayUrlAsync(VodSiteInfo site, VodEpisode episode, CancellationToken ct = default)
    {
        var rt = RuntimeFor(site);
        if (rt == null)
            return Task.FromResult(new PlayRequest { Title = episode.Name, Url = episode.Url });

        return ResolveCoreAsync(rt, site, episode, ct);
    }

    private async Task<PlayRequest> ResolveCoreAsync(ISpiderRuntime rt, VodSiteInfo site, VodEpisode episode, CancellationToken ct)
    {
        // episode.Url 可能是 "线路名$id"（来自 vod_play_url 的 集$链接 拆分，此处只剩链接）
        var json = await rt.PlayerContentAsync(site, flag: "", id: episode.Url, ct);
        var play = SpiderJsonParser.ParsePlayRequest(json, episode.Name);
        if (string.IsNullOrEmpty(play.Url))
            play.Url = episode.Url;
        return play;
    }

    public async Task<List<VodItem>> SearchAsync(VodSiteInfo site, string keyword, CancellationToken ct = default)
    {
        var rt = RuntimeFor(site) ?? throw new NotSupportedException(site.StatusNote ?? "爬虫运行时不可用");
        return SpiderJsonParser.ParseItems(
            await rt.SearchContentAsync(site, keyword, "1", ct), site.Key);
    }
}
