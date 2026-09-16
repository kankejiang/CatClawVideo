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
    /// <summary>手机解析节点（Guard 站点借它解析；未配置时为 null）</summary>
    private readonly RemoteSpiderRuntime? _remote;
    private readonly Action<string>? _log;
    private readonly IWebSniffer? _sniffer;

    public SpiderVodProvider(ISpiderRuntime? jsRuntime, ISpiderRuntime? jarRuntime = null,
        IWebSniffer? sniffer = null,
        Action<string>? log = null)
    {
        _jsRuntime = jsRuntime;
        _jarRuntime = jarRuntime;
        _remote = new RemoteSpiderRuntime(log);
        _log = log;
        _sniffer = sniffer;
    }

    public string Id => "spider";
    public string Name => "TVBox 爬虫源";

    private ISpiderRuntime? RuntimeFor(VodSiteInfo site)
    {
        var local = site.SpiderKind switch
        {
            VodSpiderKind.Script => _jsRuntime,
            VodSpiderKind.Jar => _jarRuntime,
            _ => null,
        };

        // Guard 加固站点：jar 的解密器是 ARM Android native，PC 的 x64 JVM 没有执行路径
        // ⇒ 借手机解析（手机原生就能跑 Guard）。手机不在/超时 → 自动回退本地
        // （本地还有「非 Guard 同族 jar 替代」这条兜底）。
        if (local is not null && _remote is not null && RemoteSpiderNode.NeedsRemote(site))
        {
            _log?.Invoke($"[路由] {site.Name} 是 Guard 站点 → 走手机解析节点（{RemoteSpiderNode.BaseUrl}）");
            return new FallbackSpiderRuntime(_remote, local, _log);
        }

        return local;
    }

    public bool CanHandle(VodSiteInfo site) => site.SpiderKind != VodSpiderKind.None && RuntimeFor(site) != null;

    public async Task<List<VodCategory>> GetCategoriesAsync(VodSiteInfo site, CancellationToken ct = default)
    {
        var rt = RuntimeFor(site) ?? throw new NotSupportedException(site.StatusNote ?? "爬虫运行时不可用");
        var raw = await rt.HomeContentAsync(site, ct);
        var cats = SpiderJsonParser.ParseCategories(raw);
        _log?.Invoke($"[解析] {site.Key}.home → {raw.Length}B → 分类 {cats.Count} 个");
        return cats;
    }

    public async Task<List<VodItem>> GetItemsAsync(VodSiteInfo site, VodCategory category, int page = 1, CancellationToken ct = default)
    {
        var rt = RuntimeFor(site) ?? throw new NotSupportedException(site.StatusNote ?? "爬虫运行时不可用");
        var raw = await rt.CategoryContentAsync(site, category.Id, page.ToString(), ct);
        var items = SpiderJsonParser.ParseItems(raw, site.Key);
        _log?.Invoke($"[解析] {site.Key}.category(tid={category.Id},pg={page}) → {raw.Length}B → {items.Count} 条");
        return items;
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
        // ⚠️ 荐片（csp_Jianpian）必须先把宿主本地 P2P 服务拉起来：它在 playerContent 内部
        // 就会逐个探测 127.0.0.1:9978…9999，探不到会直接拼出端口位为空的地址
        // （真机实测 → 播放器报 Source error / MalformedURLException: invalid port: -1）。
        // 幂等操作，已就绪时零开销；未注册实现（如 Windows）时是 no-op。
        var jpHost = JpP2PSupport.Current;
        if (jpHost is not null)
        {
            try { await jpHost.EnsureReadyAsync(); } catch { }
        }

        // episode.Url 可能是 "线路名$id"（来自 vod_play_url 的 集$链接 拆分，此处只剩链接）
        var json = await rt.PlayerContentAsync(site, flag: episode.Flag ?? "", id: episode.Url, ct);
        var play = SpiderJsonParser.ParsePlayRequest(json, episode.Name);
        if (string.IsNullOrEmpty(play.Url))
            play.Url = episode.Url;

        // 荐片私有地址（tvbox-xg: / ftp…gbl.114s）：交宿主 P2P 引擎转成本地 http 地址
        var jp = TryResolveJianpian(play, episode);
        if (jp is not null) return jp;

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

        // 磁力只走迅雷引擎（内置 BT 引擎已移除：公共 BT 网络实测无速度，见 docs/playback-latency-analysis.md）
        var engine = Interfaces.MagnetEngines.Thunder;
        if (engine is null || !engine.IsReady)
            throw new NotSupportedException("磁力播放需要迅雷引擎，当前不可用；请确认迅雷运行时已就绪");

        var opened = await engine.TryOpenAsync(url, episode.Name, ct)
            ?? throw new NotSupportedException("迅雷无法解析该磁力链接（无可用资源或资源不存在）");
        return new PlayRequest { Title = episode.Name, Url = opened.Url, Headers = play.Headers };
    }

    /// <summary>
    /// 荐片地址接管：<c>tvbox-xg:</c> 前缀，或含 <c>gbl.114s</c> 的 ftp 地址
    /// （对齐 TVBox <c>Jianpian.isJpUrl</c>）。
    /// <para>荐片爬虫自己不下载，播放地址由宿主 P2P 引擎提供的本地 httpd 供给。
    /// 宿主不支持时返回 null（交回原管线，由播放器报错）。</para>
    /// </summary>
    private static PlayRequest? TryResolveJianpian(PlayRequest play, VodEpisode episode)
    {
        var host = JpP2PSupport.Current;
        var url = play.Url ?? "";
        if (host is null || !host.IsJpUrl(url)) return null;

        if (!host.IsReady)
            throw new NotSupportedException("该线路需要内置 P2P 组件（荐片），当前不可用；请换线路或换源");

        var local = host.Decode(url);
        if (string.IsNullOrEmpty(local))
            throw new NotSupportedException("荐片线路地址解析失败，请换线路或换源");

        return new PlayRequest { Title = episode.Name, Url = local, Headers = play.Headers };
    }

    public async Task<List<VodItem>> SearchAsync(VodSiteInfo site, string keyword, CancellationToken ct = default)
    {
        var rt = RuntimeFor(site) ?? throw new NotSupportedException(site.StatusNote ?? "爬虫运行时不可用");
        return SpiderJsonParser.ParseItems(
            await rt.SearchContentAsync(site, keyword, "1", ct), site.Key);
    }
}
