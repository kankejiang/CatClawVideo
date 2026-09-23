using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;

namespace CatClawVideo.Core.Providers;

/// <summary>
/// TVBox spider 站点适配器（type=3）：把 ISpiderRuntime 的 TVBox 协议 JSON
/// 解析为统一领域模型，接入现有播放管线。
/// - Script（JS 爬虫）→ csp_ 类走 <see cref="TvBoxJsSpiderRuntime"/>（社区 JS 协议）、
///   http 脚本走 <see cref="DrpyJsSpiderRuntime"/>（drpy2 协议）
/// - Jar（Java/dex）→ 运行时由平台侧提供（Android DexClassLoader）；不可用时抛出明确异常
/// </summary>
public class SpiderVodProvider : IVodSourceProvider
{
    private readonly ISpiderRuntime? _jsRuntime;
    private readonly ISpiderRuntime? _jarRuntime;
    private readonly ISpiderRuntime? _tvboxJsRuntime;
    private readonly Action<string>? _log;
    private readonly IWebSniffer? _sniffer;

    /// <summary>danmaku 钩子请求用（仅本机回环 proxy，独立实例避免全局超时配置干扰）</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public SpiderVodProvider(ISpiderRuntime? jsRuntime, ISpiderRuntime? jarRuntime = null,
        IWebSniffer? sniffer = null,
        Action<string>? log = null,
        ISpiderRuntime? tvboxJsRuntime = null)
    {
        _jsRuntime = jsRuntime;
        _jarRuntime = jarRuntime;
        _tvboxJsRuntime = tvboxJsRuntime;
        _log = log;
        _sniffer = sniffer;
    }

    public string Id => "spider";
    public string Name => "TVBox 爬虫源";

    private ISpiderRuntime? RuntimeFor(VodSiteInfo site)
    {
        return site.SpiderKind switch
        {
            // TVBox 社区 JS 协议源：csp_ + .js spider 包；其余 http 脚本走 drpy2
            VodSpiderKind.Script => site.Api.StartsWith("csp_", StringComparison.OrdinalIgnoreCase)
                ? (_tvboxJsRuntime ?? _jsRuntime)
                : _jsRuntime,
            VodSpiderKind.Jar => _jarRuntime,
            _ => null,
        };
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
        _log?.Invoke($"[解析] {site.Key}.playerContent(flag={episode.Flag},id={episode.Url}) → {json}");
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

        // ── Guard 系「云盘配置」卡片拦截（csp_MyDriveGuard 等，itemId=0000 登入 / 4444 排序…）──
        // playerContent 返回的不是视频流，而是宿主本地 proxy 提供的 HTML 配置页
        // （http://127.0.0.1:<port>/proxy?do=config&url=…；真机实测 jar 会先探测 9978…9999
        //   找活代理，探不到直接 Source error）。TVBox 里该地址嗅探后由 WebView 渲染成配置
        //   界面；这里标记 IsHtmlPage 交 UI 层打开 WebView，绝不能喂给播放器。
        if (IsHtmlConfigPage(play.Url))
            return new PlayRequest { Title = episode.Name, Url = play.Url, IsHtmlPage = true };

        // ── Guard 系「云盘配置」卡片（csp_MyDriveGuard 等，itemId=0000 登入 / 4444 排序…）──
        // playerContent 返回的 url 是**字面量 id**（非视频流），danmaku 字段携带本地 proxy 钩子
        // （do=danmu&url=0000）。GET 该钩子 → 回调 jar 的 proxy(Map) → jar 在 UI 线程弹
        // 「已登录+启用中」网盘配置对话框（TVBox 由弹幕加载隐式触发同一 URL）。
        // 不满足上述形态但 danmaku 指向本地 proxy 的，同样请求一次（幂等钩子）。
        if (play.DanmakuUrl is { Length: > 0 } danmaku &&
            danmaku.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase) &&
            (IsHtmlConfigPage(danmaku) || !play.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var hook = new HttpRequestMessage(HttpMethod.Get, danmaku);
                using var resp = await Http.SendAsync(hook, ct).ConfigureAwait(false);
                _log?.Invoke($"[解析] {site.Key}.danmaku钩子 → {(int)resp.StatusCode}");
            }
            catch (System.Exception ex)
            {
                _log?.Invoke($"[解析] {site.Key}.danmaku钩子失败: {ex.Message}");
            }
        }

        return await TvBoxPlayPipeline.ResolveAsync(site, play, episode.Flag ?? "", _sniffer, ct);
    }

    /// <summary>
    /// Guard 系网盘配置页 URL：宿主本地 proxy 的 do=config 端点
    /// （127.0.0.1 回环 + /proxy?do=config…）。普通视频反代（do=…/proxy?url=）不命中。
    /// </summary>
    private static bool IsHtmlConfigPage(string url)
        => url.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase)
           && url.Contains("/proxy?do=config", StringComparison.OrdinalIgnoreCase);

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

        // 展示名取**种子内真实文件名**而非站点给的打包名：磁力站的起播名往往是
        // 「第四季01-03-1080p」这种打包描述（磁力 dn），看不出正在播哪个文件；
        // 引擎解析时已拿到真实文件名，直接用它。
        var display = string.IsNullOrWhiteSpace(opened.FileName) ? episode.Name : opened.FileName;
        return new PlayRequest { Title = display, Url = opened.Url, Headers = play.Headers };
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
