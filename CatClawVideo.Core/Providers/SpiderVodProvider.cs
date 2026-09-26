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
public class SpiderVodProvider : IVodSourceProvider, IActionVodSourceProvider
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

    /// <summary>
    /// 把卡片的 action 交给爬虫（TVBox doAction 语义）。
    /// <para>返回值：<c>null</c> = 这条链路不支持/调用失败；**空串是正常结果** ——
    /// 「清除XX Cookie」这类动作执行完只回一个 toast，爬虫本身不返 JSON。
    /// 早先把空串折成 null，调用方误判成「没执行」而继续往下掉，于是点清除却打开了推送页。</para>
    /// </summary>
    public async Task<string?> DoActionAsync(VodSiteInfo site, VodItem item, CancellationToken ct = default)
    {
        if (item.Action.Length == 0) return null;
        if (RuntimeFor(site) is not ISpiderActionRuntime rt) return null;
        try
        {
            var json = await rt.ActionAsync(site, item.Action, ct).ConfigureAwait(false);
            _log?.Invoke($"[动作] {site.Key}.action({item.Action}) → {json}");
            return json ?? "";
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[动作] {site.Key}.action 失败: {ex.Message}");
            return null;
        }
    }

    public async Task<List<VodCategory>> GetCategoriesAsync(VodSiteInfo site, CancellationToken ct = default)
    {
        var rt = RuntimeFor(site) ?? throw new NotSupportedException(site.StatusNote ?? "爬虫运行时不可用");
        var raw = await rt.HomeContentAsync(site, ct);
        var cats = SpiderJsonParser.ParseCategories(raw);
        _log?.Invoke($"[解析] {site.Key}.home → {raw.Length}B → 分类 {cats.Count} 个");
        return cats;
    }

    public async Task<List<VodItem>> GetItemsAsync(VodSiteInfo site, VodCategory category, int page = 1,
        IReadOnlyDictionary<string, string>? filter = null, CancellationToken ct = default)
    {
        var rt = RuntimeFor(site) ?? throw new NotSupportedException(site.StatusNote ?? "爬虫运行时不可用");
        var raw = await rt.CategoryContentAsync(site, category.Id, page.ToString(), filter, ct);
        var items = SpiderJsonParser.ParseItems(raw, site.Key);
        _log?.Invoke($"[解析] {site.Key}.category(tid={category.Id},pg={page}"
                     + (filter is { Count: > 0 } ? $",筛选={string.Join('/', filter.Select(kv => kv.Key + ":" + kv.Value))}" : "")
                     + $") → {raw.Length}B → {items.Count} 条"
                     + $"（带 action {items.Count(i => i.Action.Length > 0)}，"
                     + $"tag={string.Join('/', items.Select(i => i.Tag).Where(t => t.Length > 0).Distinct())}）");
        return items;
    }

    public async Task<List<VodPlaySource>> GetPlaySourcesAsync(VodSiteInfo site, VodItem item, CancellationToken ct = default)
    {
        var rt = RuntimeFor(site) ?? throw new NotSupportedException(site.StatusNote ?? "爬虫运行时不可用");

        // ── Guard 系网盘源的配置入口卡片（登入自己网盘/排序等固定数字 id）──
        // detailContent 需要下载云盘配置文件（饭太硬 github.tbap.top 已 403，实测白等 16s 才炸）
        // 且其返回对这类卡片无意义（TVBox 同样忽略）——直接走 playerContent + danmaku 钩子
        // 触发 jar 的配置 UI（弹窗事件经桥上行宿主展示），秒级完成。
        if (IsDriveConfigEntry(site, item))
        {
            await TriggerDriveConfigUiAsync(rt, site, item, ct).ConfigureAwait(false);
            return [ConfigPageSource(site, item)];
        }

        List<VodPlaySource> sources;
        try
        {
            sources = SpiderJsonParser.ParsePlaySources(await rt.DetailContentAsync(site, item.Id, ct));
        }
        catch (Exception ex) when (IsDriveConfigEntry(site, item))
        {
            _log?.Invoke($"[解析] {site.Key}.detailContent 失败（网盘配置入口容错）: {ex.Message}");
            await TriggerDriveConfigUiAsync(rt, site, item, ct).ConfigureAwait(false);
            return [ConfigPageSource(site, item)];
        }
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

    /// <summary>
    /// 触发 jar 的网盘配置 UI 链（TVBox 语义）：playerContent 让 jar 记录选中上下文，
    /// 随后 GET danmaku 钩子（do=danmu&amp;url=&lt;id&gt;）→ jar proxy(Map) 弹「已登录+启用中」
    /// 对话框/扫码二维码 → 桥 UiBridge 事件上行 → 宿主渲染（jar 解析 UI、宿主展示）。
    /// </summary>
    private async Task TriggerDriveConfigUiAsync(Core.Interfaces.ISpiderRuntime rt, VodSiteInfo site, VodItem item, CancellationToken ct)
    {
        try
        {
            var playJson = await rt.PlayerContentAsync(site, flag: "", id: item.Id, ct).ConfigureAwait(false);
            var play = SpiderJsonParser.ParsePlayRequest(playJson, item.Title);
            _log?.Invoke($"[解析] {site.Key}.playerContent(入口) → url={play.Url} danmaku={play.DanmakuUrl}");
            if (play.DanmakuUrl is { } hook &&
                hook.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase))
            {
                using var h = new HttpRequestMessage(HttpMethod.Get, hook);
                using var r = await Http.SendAsync(h, ct).ConfigureAwait(false);
                _log?.Invoke($"[解析] {site.Key}.danmaku钩子 → {(int)r.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[解析] {site.Key}.配置UI触发失败: {ex.Message}");
        }
    }

    /// <summary>是否为 Guard 系网盘源的「配置入口」卡片（登入/排序等固定 id 卡片）。</summary>
    private static bool IsDriveConfigEntry(VodSiteInfo site, VodItem item)
        => site.Api.StartsWith("csp_", StringComparison.OrdinalIgnoreCase)
           && site.Api.EndsWith("Guard", StringComparison.OrdinalIgnoreCase)
           && item.Id.Length <= 4 && uint.TryParse(item.Id, out _);

    /// <summary>网盘配置入口的合成线路：单集指向宿主本地 proxy 的 do=config 配置页。</summary>
    private static VodPlaySource ConfigPageSource(VodSiteInfo site, VodItem item)
    {
        var url = Services.SpiderProxyServer.ActivePort > 0
            ? $"http://127.0.0.1:{Services.SpiderProxyServer.ActivePort}/proxy?do=config&url={Uri.EscapeDataString(item.Id)}"
            : item.Id;
        return new VodPlaySource
        {
            Name = site.Name,
            Episodes = [new VodEpisode { Name = item.Title, Url = url }],
        };
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

        // 网盘配置入口容错：episode.Url 已是宿主本地 proxy 的 do=config 配置页地址
        // （detailContent 失败时 GetPlaySourcesAsync 合成）——直接返回 IsHtmlPage，
        // 绝不能进 playerContent（spider 会再炸一次）。
        if (IsHtmlConfigPage(episode.Url ?? ""))
            return new PlayRequest { Title = episode.Name, Url = episode.Url, IsHtmlPage = true };

        var json = await rt.PlayerContentAsync(site, flag: episode.Flag ?? "", id: episode.Url, ct);
        _log?.Invoke($"[解析] {site.Key}.playerContent(flag={episode.Flag},id={episode.Url}) → {json}");
        var play = SpiderJsonParser.ParsePlayRequest(json, episode.Name);
        if (string.IsNullOrEmpty(play.Url))
        {
            // 爬虫明确给了失败原因（网盘「容量不足」这类）时**不要**拿剧集 id 兜底：
            // 网盘文件的 id 是一整段 JSON,喂给播放器只会 MalformedURLException,
            // 把真正该告诉用户的原因盖成「Source error」。
            if (play.Message.Length == 0) play.Url = episode.Url;
            else return play;
        }

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
