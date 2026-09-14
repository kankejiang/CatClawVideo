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
    private readonly IWebSniffer? _sniffer;
    private readonly Services.BtStreamService? _bt;

    public SpiderVodProvider(ISpiderRuntime? jsRuntime, ISpiderRuntime? jarRuntime = null,
        IWebSniffer? sniffer = null, Services.BtStreamService? bt = null)
    {
        _jsRuntime = jsRuntime;
        _jarRuntime = jarRuntime;
        _sniffer = sniffer;
        _bt = bt;
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
        var json = await rt.PlayerContentAsync(site, flag: episode.Flag ?? "", id: episode.Url, ct);
        var play = SpiderJsonParser.ParsePlayRequest(json, episode.Name);
        if (string.IsNullOrEmpty(play.Url))
            play.Url = episode.Url;

        // ⚠️ 磁力拦截必须在进解析管线**之前**：
        // ① magnet: 不是视频格式，会被判为「需嗅探」，交给网页嗅探器只会拿到垃圾；
        // ② 就算侥幸直通播放器，ExoPlayer 也会以
        //    HttpDataSourceException: unknown protocol: magnet 报 Source error
        //    （2026-09-14 真机实测：磁力站的「此磁力源是边下载边播…」线路点播必炸）。
        // BT 站点的剧集链接本身就是 magnet:，须转成本机 BT 流式代理地址。
        var bt = await TryOpenBtAsync(play, episode, ct);
        if (bt is not null) return bt;

        return await TvBoxPlayPipeline.ResolveAsync(site, play, episode.Flag ?? "", _sniffer, ct);
    }

    /// <summary>
    /// BT 流式拦截（对齐 <see cref="CatClawSourceProvider"/> 的磁力分支）：
    /// magnet: → 本机 127.0.0.1 BT 代理地址（可 Range 拖动）；ed2k 明确不支持并给出可读提示。
    /// 非磁力/电驴返回 null，交回常规解析管线。
    /// </summary>
    private async Task<PlayRequest?> TryOpenBtAsync(PlayRequest play, VodEpisode episode, CancellationToken ct)
    {
        var url = play.Url ?? "";

        if (url.StartsWith("ed2k://", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("该集为电驴(ed2k)下载链接，暂不支持在线播放；可复制链接到下载工具");

        if (!url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            return null;

        if (_bt is null)
            throw new NotSupportedException("BT 引擎未初始化，磁力线路不可用");

        var session = await _bt.OpenAsync(url, episode.Name, ct);
        return new PlayRequest { Title = episode.Name, Url = session.Url, Headers = play.Headers };
    }

    public async Task<List<VodItem>> SearchAsync(VodSiteInfo site, string keyword, CancellationToken ct = default)
    {
        var rt = RuntimeFor(site) ?? throw new NotSupportedException(site.StatusNote ?? "爬虫运行时不可用");
        return SpiderJsonParser.ParseItems(
            await rt.SearchContentAsync(site, keyword, "1", ct), site.Key);
    }
}
