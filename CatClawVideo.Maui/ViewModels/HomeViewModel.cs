using System.Collections.ObjectModel;
using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;
using CatClawVideo.Core.Services;
using CatClawVideo.Data;
using CatClawVideo.Maui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CatClawVideo.Maui.ViewModels;

/// <summary>
/// 首页 ViewModel：影片数据全部来自订阅源（设置 → 订阅源管理添加），不内置任何源。
/// 站点仓库见 <see cref="SiteRegistry"/>；仅 type=1 MacCMS 等可播站点参与首页聚合。
/// </summary>
public partial class HomeViewModel : ObservableObject
{
    private readonly IVodSourceProvider _provider;

    /// <summary>封面解析（源封面失效 → 豆瓣 → 占位海报）</summary>
    private readonly CoverImageService _covers;

    /// <summary>播放历史库（主页「观看历史」行用；未注入时该行为空）</summary>
    private readonly VideoDatabase? _db;

    /// <summary>当前生效站点（UI 显示/详情页拉取用；无可用源时为 null）</summary>
    public VodSiteInfo? Site => CurrentSite;

    /// <summary>顶部站点条显示文本（无源时给引导文案）</summary>
    public string SiteDisplayName => CurrentSite?.Name ?? "未选择数据源";

    /// <summary>可播站点列表（数据源选择弹窗用）</summary>
    public List<VodSiteInfo> PlayableSites => SiteRegistry.Playable.ToList();

    /// <summary>用户首选站点 Key（Preferences 持久记忆，跨启动生效）</summary>
    private const string PreferredSiteKey = "home_preferred_site";

    /// <summary>上次成功出首页数据的站点：下次启动优先探测它（命中即亚秒出首页）。</summary>
    private const string LastGoodSiteKey = "home_last_good_site";

    /// <summary>本会话内切换失败过的站点（弹窗置灰标注；重试成功会移除）</summary>
    public HashSet<string> FailedSites { get; } = new();

    // ═══════════ 会话级缓存（性能：切站/回访免网络）═══════════
    //
    // 此前切站 = 分类列表 + 首页条目两次串行网络请求（jar 源经桥每次 0.5~1.3s），
    // 且切回刚看过的站点也要全量重拉。TVBox 首页数据本就允许驻留，这里按会话缓存：
    //   ── 分类列表按站点缓存（LoadHome/SelectSite 共用）
    //   ── 首页第一页条目按「站点+分类+筛选」缓存（SelectCategory 命中即秒开）
    // 订阅变化（SiteRegistry.Changed）时整体作废。

    private readonly Dictionary<string, List<VodCategory>> _catsCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<VodItem>> _firstPageCache = new(StringComparer.Ordinal);

    /// <summary>首屏条目缓存上限（超出全部作废；会话缓存不求精准淘汰）。</summary>
    private const int MaxFirstPageCache = 24;

    private string FirstPageKey(VodSiteInfo site, VodCategory category)
    {
        var filter = _filter.Count == 0 ? ""
            : string.Join(";", _filter.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));
        return $"{site.Key}|{category.Id}|{filter}";
    }

    [ObservableProperty]
    private VodSiteInfo? _currentSite;

    [ObservableProperty]
    private bool _isHomeLoading;

    [ObservableProperty]
    private string _homeStatus = string.Empty;

    /// <summary>当前选中分类 ID（分类 chip 高亮用；改用 BindableLayout 后由本属性驱动）</summary>
    [ObservableProperty]
    private string _selectedCategoryId = string.Empty;

    /// <summary>分页状态：当前页 / 是否还有下一页（滚动到底自动加载）</summary>
    [ObservableProperty]
    private bool _hasMoreItems = true;
    private int _currentPage = 1;
    private bool _loadingMore;
    private VodCategory? _currentCategory;

    public ObservableCollection<VodCategory> Categories { get; } = new();
    public ObservableCollection<VodItem> Items { get; } = new();

    public HomeViewModel(IVodSourceProvider provider, CoverImageService covers, VideoDatabase? db = null)
    {
        _provider = provider;
        _covers = covers;
        _db = db;
        // 订阅变化后允许首页重新拉一次（常驻页，之前以 Categories.Count>0 跳过）
        SiteRegistry.Changed += () =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _catsCache.Clear();
                _firstPageCache.Clear();
                if (!IsHomeLoading) _ = LoadHomeCommand.ExecuteAsync(null);
            });
    }

    // ═══════════════════ 主页（TVBox 式推荐行，设置三选一）═══════════════════
    //
    // 对位 TVBox UserFragment：第一个 tab 固定「主页」，内容由设置决定（HawkConfig.HOME_REC
    // 三选一）——站点推荐（spider homeContent.list）/ 豆瓣热播（壳自己拉豆瓣）/ 观看历史。
    // 不依赖源 class（像「设置│中心」这类功能站 class 全是功能卡，靠源出不了推荐）。

    /// <summary>虚拟「主页」分类 Id（chips 第一个固定项；选中它显示推荐行）。</summary>
    public const string HomeCategoryId = "__home__";

    /// <summary>是否处于主页模式（选中虚拟主页分类 → 推荐网格；其余分类 → 源分类网格）。</summary>
    public bool IsHomeMode => SelectedCategoryId == HomeCategoryId;

    /// <summary>推荐网格标题（站点推荐/豆瓣热播/观看历史，随设置模式变）。</summary>
    [ObservableProperty]
    private string _homeRecTitle = string.Empty;

    /// <summary>主页推荐条目（平铺多行网格，排列与海报大小与片库网格一致）。</summary>
    public ObservableCollection<VodItem> HomeRecItems { get; } = new();

    /// <summary>行流缓存 key（偏好版本 + 模式 + 站点；任一变了才重建）。</summary>
    private string RowsKey() =>
        $"v{Services.HomeRecPrefs.Version}|{Services.HomeRecPrefs.Load()}|{CurrentSite?.Key}";

    /// <summary>主页推荐条数上限。</summary>
    private const int HomeRecTake = 40;

    /// <summary>行流缓存 key 快照（RowsKey() 的结果；不一致即重建）。</summary>
    private string _rowsKey = "";

    /// <summary>历史取最近多少条。</summary>
    private const int HistoryRowTake = 40;

    /// <summary>主页推荐构建重入防护（连点首页 tab / 事件并发时避免双写集合打崩绑定）。</summary>
    private bool _recLoading;

    /// <summary>站点就绪后的默认视图：第一个 chip 固定是「主页」。</summary>
    private Task EnterDefaultViewAsync() => SelectCategoryAsync(Categories[0]);

    /// <summary>
    /// 构建「主页」推荐网格（TVBox 三选一，多行平铺）：站点推荐 / 豆瓣热播 / 观看历史。
    /// </summary>
    public async Task LoadHomeRecRowsAsync()
    {
        if (CurrentSite == null || _recLoading) return;
        var key = RowsKey();
        if (HomeRecItems.Count > 0 && _rowsKey == key) return;
        _recLoading = true;

        try
        {
            IsHomeLoading = true;
            _rowsKey = key;
            var mode = Services.HomeRecPrefs.Load();
            HomeRecTitle = Services.HomeRecPrefs.Label(mode);
            HomeRecItems.Clear();

            switch (mode)
            {
                case Services.HomeRecPrefs.History:
                    await FillHistoryCardsAsync().ConfigureAwait(true);
                    break;
                case Services.HomeRecPrefs.DoubanHot:
                    FillDoubanCards(await DoubanService.GetHotAsync(HomeRecTake).ConfigureAwait(true));
                    break;
                default:   // SiteRecommend（TVBox 默认）：homeContent.list
                    var rec = await _provider.GetHomeRecommendAsync(CurrentSite).ConfigureAwait(true);
                    AppendCards(rec);
                    break;
            }
        }
        catch (Exception ex)
        {
            DiagLog.Write($"[home-rec] 构建失败: {ex.Message}");
        }
        finally
        {
            _recLoading = false;
            HomeStatus = HomeRecItems.Count == 0
                ? $"{CurrentSite.Name} · 主页 · {HomeRecTitle}暂无内容"
                : $"{CurrentSite.Name} · 主页 · {HomeRecTitle} {HomeRecItems.Count} 部";
            IsHomeLoading = false;
            DiagLog.Write($"[home-rec] 完成 mode={HomeRecTitle} items={HomeRecItems.Count}");
        }
    }

    /// <summary>设置里切了推荐内容 / 换站后，回主页时网格按需重建（OnTabShownAsync 调）。</summary>
    public Task RefreshHomeRecIfStaleAsync()
    {
        if (!IsHomeMode || CurrentSite == null || _recLoading) return Task.CompletedTask;
        if (HomeRecItems.Count > 0 && _rowsKey == RowsKey()) return Task.CompletedTask;
        return LoadHomeRecRowsAsync();
    }

    /// <summary>把条目挂上封面解析后追加进推荐网格。</summary>
    private void AppendCards(IEnumerable<VodItem> items)
    {
        var list = items.Take(HomeRecTake).ToList();
        CoverResolver.Attach(_covers, list);
        foreach (var it in list) HomeRecItems.Add(it);
    }

    /// <summary>豆瓣热播卡（TVBox 同款豆瓣接口；封面解析链已带豆瓣 Referer；点卡片跨源搜索）。</summary>
    private void FillDoubanCards(List<DoubanEntry> entries)
    {
        AppendCards(entries.Select(e => new VodItem
        {
            Id = "douban:" + e.Title,
            Title = e.Title,
            Cover = e.Cover,
            Remarks = string.IsNullOrWhiteSpace(e.Rate) ? "" : e.Rate + "分",
            Tag = "douban",
        }));
    }

    /// <summary>观看历史卡（本地最近 N 条；卡片带续看参数，点击直接回观看页）。</summary>
    private async Task FillHistoryCardsAsync()
    {
        if (_db is null) return;
        var entries = await _db.GetRecentHistoryAsync(HistoryRowTake).ConfigureAwait(true);
        var cards = new List<VodItem>(entries.Count);
        foreach (var e in entries)
        {
            // 与 HistoryPage 同一套回跳参数：type/api 取当前注册表的活源定义（旧记录可能指向死源）
            var hasSource = !string.IsNullOrEmpty(e.SourceKey) && !string.IsNullOrEmpty(e.ItemId);
            var site = SiteRegistry.Find(e.SourceKey);
            var type = site?.Type ?? e.ItemType;
            var api = site?.Api ?? e.ItemApi;
            var posParam = $"&pos={Math.Max(0, (int)e.PositionSeconds)}";
            var itemTitle = e.Title.Contains(" · ") ? e.Title.Split(" · ")[0] : e.Title;
            if (string.IsNullOrWhiteSpace(itemTitle)) itemTitle = e.EpisodeName;

            string query;
            if (hasSource)
            {
                query = $"watch?title={Uri.EscapeDataString(itemTitle)}" +
                        $"&sourceKey={Uri.EscapeDataString(e.SourceKey)}&type={type}" +
                        $"&api={Uri.EscapeDataString(api)}&itemId={Uri.EscapeDataString(e.ItemId)}" +
                        (string.IsNullOrEmpty(e.EpisodeName) ? "" : $"&resumeEp={Uri.EscapeDataString(e.EpisodeName)}") +
                        (string.IsNullOrEmpty(e.RouteName) ? "" : $"&route={Uri.EscapeDataString(e.RouteName)}") +
                        $"&year={Uri.EscapeDataString(e.Year)}&remarks={Uri.EscapeDataString(e.Remarks)}" +
                        $"&desc={Uri.EscapeDataString(e.Description)}&category={Uri.EscapeDataString(e.Category)}" +
                        posParam +
                        (string.IsNullOrEmpty(e.Cover) ? "" : $"&cover={Uri.EscapeDataString(e.Cover)}");
            }
            else
            {
                query = $"player?title={Uri.EscapeDataString(e.Title)}&url={Uri.EscapeDataString(e.Url)}" +
                        posParam +
                        (string.IsNullOrEmpty(e.Cover) ? "" : $"&cover={Uri.EscapeDataString(e.Cover)}");
            }

            var ep = string.IsNullOrEmpty(e.EpisodeName) ? "" : e.EpisodeName;
            cards.Add(new VodItem
            {
                Id = "history:" + e.Id,
                Title = itemTitle,
                Cover = e.Cover,
                Remarks = ep,
                Tag = "history",
                Action = query,   // OpenItem 据此直接 GoToAsync（历史卡不走 detailContent）
            });
        }
        AppendCards(cards);
    }

    /// <summary>首页首载：用户首选站点优先（失败回退自动探测第一个成功者）→ 进默认视图</summary>
    [RelayCommand]
    public async Task LoadHomeAsync()
    {
        if (Categories.Count > 0) return; // 常驻页只拉一次
        var sw = System.Diagnostics.Stopwatch.StartNew();
        IsHomeLoading = true;
        HomeStatus = "正在加载影片…";

        var sites = SiteRegistry.Playable.ToList();
        if (sites.Count == 0)
        {
            Categories.Clear();
            Items.Clear();
            HomeStatus = "暂无可用影片源，请在 设置 → 源配置 添加订阅";
            IsHomeLoading = false;
            return;
        }

        // ★ 首屏快照（2026-10-03，落盘在数据目录 home-snapshot.json）：先上屏，后台再刷新。
        //   没它的话每次冷启动都要现找活站拉分类 —— 实测 110 站只有 ~8 个能出分类，首屏 35~38s。
        //   下面的完整流程照跑，拉到新数据会整体替换（含站点切换）。
        var snap = Services.HomeSnapshotStore.Load();
        if (snap is { Categories.Count: > 0 })
        {
            foreach (var c in snap.Categories) Categories.Add(new VodCategory { Id = c.Id, Name = c.Name });
            SelectedCategoryId = snap.CategoryId is { Length: > 0 } cid ? cid : snap.Categories[0].Id;
            _currentCategory = Categories.FirstOrDefault(c => c.Id == SelectedCategoryId);
            foreach (var it in snap.Items)
                Items.Add(new VodItem { Id = it.Id, SourceKey = it.SourceKey, Title = it.Title, Cover = it.Cover });
            if (sites.FirstOrDefault(s => s.Key == snap.SiteKey) is { } snapSite) CurrentSite = snapSite;
            CoverResolver.Attach(_covers, Items.ToList());      // 封面走本地缓存，秒出
            HomeStatus = "已显示上次内容（" + snap.SiteName + "）· 正在刷新…";
            DiagLog.Write($"[home-cache] 首屏命中快照：{snap.Categories.Count} 分类 / {snap.Items.Count} 部" +
                          $"（{snap.SiteName}，存于 {snap.SavedUtc:MM-dd HH:mm}）");
        }

        // 用户首选站点优先（数据源弹窗选择后记忆）；拉取失败回退自动探测
        var preferredKey = Preferences.Default.Get(PreferredSiteKey, string.Empty);
        var preferred = sites.FirstOrDefault(s => s.Key == preferredKey);
        // 留痕（2026-09-30）：这里以前是 `catch { }` —— 首选站点失败的原因整条被吞，
        // 于是「每次重启都不回上次的站点」根本查不动。冷启动时爬虫桥（QEMU guest）
        // 还在起（实测 桥就绪 15:59:57.3，而首屏 15:59:46.7 就开跑），jar 源这一步必然受影响。
        DiagLog.Write($"[home] 首选 key={preferredKey} 命中={(preferred?.Name ?? "(不在可播列表)")}");

        var cats = new List<VodCategory>();
        VodSiteInfo? usedSite = null;

        // ★ 冷启动关键路径（2026-10-03）：回退探测**立即并行开跑**，不等首选站试完。
        //   首选若是 jar 站，冷启动时桥还没就绪、只会挂到预算耗尽；串行的话首屏要多等一个
        //   预算（实测 3~15s），而探测本可以立刻在别的站上拿到数据。谁先成功用谁。
        var probeTask = ProbeSitesAsync(sites);
        if (preferred != null)
        {
            // 会话缓存命中直接用（回启 App/切回旧站点免一次 0.5~1.3s 的分类请求）
            if (_catsCache.TryGetValue(preferred.Key, out var cached) && cached.Count > 0)
            {
                usedSite = preferred;
                cats = cached;
            }
            else
            {
                // ⚠ 必须有预算：jar 站（依赖 QEMU ART guest）在引擎不可用时会**挂住不返回**——
                // 2026-10-02 实测挂满 4 分钟也不抛异常，于是「致命回退」压根触发不了，
                // 冷启动遮罩只能干等（用户截图：97% 动不了）。ct 一路传到运行时
                // （SpiderVodProvider → HomeContentAsync → ConnectAsync），所以预算掐得住。
                var needsEngine = preferred.SpiderKind != VodSpiderKind.None;
                // 2026-10-03：冷启动时 ART 桥 ~28s 才就绪，jar 站此时只会挂住 —— 实测首选若是 jar 站，
                // 旧预算（50s）会把遮罩顶到 97% 干等。冷启动只给 3s，拿不到立刻交给回退探测。
                var budget = needsEngine ? PreferredEngineStartupBudgetSeconds : PlainSiteBudgetSeconds;
                for (var attempt = 1; attempt <= 5 && usedSite == null; attempt++)
                {
                    string why;
                    var fatal = false;
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(budget));
                    try
                    {
                        cats = await _provider.GetCategoriesAsync(preferred, cts.Token);
                        if (cats.Count > 0)
                        {
                            usedSite = preferred;
                            _catsCache[preferred.Key] = cats;
                            break;
                        }
                        why = "分类为空";
                    }
                    catch (OperationCanceledException)
                    {
                        why = $"取分类超时（预算 {budget}s）";
                        // 爬虫站冷启实测 36~46s；超 50s 还没动静 = 这条引擎链路已经废了
                        fatal = needsEngine;
                    }
                    catch (Exception ex)
                    {
                        why = $"{ex.GetType().Name}: {ex.Message}";
                        fatal = needsEngine && IsSpiderEngineDown(ex);
                    }
                    DiagLog.Write($"[home] 首选 {preferred.Name} 第 {attempt} 次取分类失败：{why}");
                    // 爬虫引擎（QEMU ART guest）整个起不来 / 挂死 ⇒ 间隔 4s 再试也是白等
                    // （VM 已经没了，不会自己长出来），5 次重试纯把首页冻住 ~8 分钟
                    // （2026-10-02 实测 22:09 那轮）。立刻回退自动探测：
                    // 并发探测里不依赖 guest 的 MacCMS 站能先把首页顶起来。
                    if (fatal)
                    {
                        DiagLog.Write("[home] 爬虫引擎不可用/超时，跳过剩余重试，直接回退自动探测");
                        // 该爬虫站就地退位（清空首选）：否则下一次冷启动又会先撞它、白等一个预算。
                        // 用户在「切换源」里随时能选回来，这里只影响「先试谁」。
                        DemotePreferredSite(preferred);
                        break;
                    }
                    if (attempt < 5) await Task.Delay(4000);
                }
            }
        }

        // 回退探测：此前是串行 foreach——首选站点失效时（订阅里的死源很常见），
        // 每个死站都要吃满自身超时（MacCMS 20s、jar 源最长 90s），首页等于冻住。
        // 改为并发探测（带并发上限与整体超时），第一个出分类的站点即胜出。
        if (usedSite == null)
        {
            // 探测已在上面并行启动（排序把「最近成功的站」放最前），这里只取结果
            var win = await probeTask;
            if (win is { } w)
            {
                usedSite = w.Site;
                cats = w.Cats;
                _catsCache[w.Site.Key] = w.Cats;
                Preferences.Default.Set(LastGoodSiteKey, w.Site.Key);
                // ★ 记忆胜者（2026-10-02）：此前只有「用户手动切站」才写首选，自动探测胜出**不写回**，
                // 于是首选一旦是坏站（例：🍼┆设置┆中心，本身是订阅的配置站还要占着首选位），
                // 每次冷启动都先撞它一次、等它超时 → 用户观感就是「改了也是白改」。
                // 写回之后冷启动直接命中可用站，一次到位。
                RememberPreferredSite(w.Site, "自动探测胜出");
            }
        }

        if (usedSite == null)
        {
            HomeStatus = "订阅站点均拉取失败，请检查网络或在源配置中更换订阅";
            IsHomeLoading = false;
            DiagLog.Write($"[home] 首载失败（候选 {sites.Count} 站，耗时 {sw.ElapsedMilliseconds}ms）");
            return;
        }

        CurrentSite = usedSite;
        SaveHomeSnapshot(cats, null);      // 分类一到就落盘（默认视图若走虚拟「主页」分类，
                                           // 之前挂在条目那步的落盘根本走不到）
        OnPropertyChanged(nameof(Site));
        OnPropertyChanged(nameof(SiteDisplayName));
        Categories.Clear();
        _rowsKey = "";   // 站点就绪/重拉后主页推荐要重建
        Categories.Add(new VodCategory { Id = HomeCategoryId, Name = "主页" });   // TVBox 同款：第一个 tab 固定主页
        foreach (var c in cats) Categories.Add(c);

        DiagLog.Write($"[home] 首载 site={usedSite.Name} 分类={cats.Count} 耗时={sw.ElapsedMilliseconds}ms");
        await EnterDefaultViewAsync();
    }

    /// <summary>
    /// 判断这次失败是不是「爬虫引擎整个不可用」（QEMU ART guest 起不来 / 桥链路不可用）。
    ///
    /// <para>这类失败**不会自愈**：VM 已经没了，隔几秒重试同一个 jar 源还是同样结果，
    /// 5 次重试白等 8 分钟（2026-10-02 实测：22:09 那轮首载耗时 8 分钟后报
    /// 「ART guest 起不来」，而同一时刻订阅里 111 个站有大量不依赖 guest 的 MacCMS 站可用）。
    /// 命中即让首选站快速让位给自动探测。</para>
    /// </summary>
    private static bool IsSpiderEngineDown(Exception ex)
    {
        var msg = ex.Message;
        return msg.Contains("ART guest 起不来", StringComparison.Ordinal)
            || msg.Contains("guest 桥", StringComparison.Ordinal)
            || msg.Contains("爬虫引擎不可用", StringComparison.Ordinal)
            || msg.Contains("ART 桥会话", StringComparison.Ordinal);
    }

    /// <summary>
    /// 记住真正拿到数据的站点为首选（自动探测胜出时调用）。
    ///
    /// <para>只在这一种情况下改写：用户手动选的站**取数彻底失败**（不是暂时抖动）才换人，
    /// 换的时候留痕。判据是「已经有一个能出分类的站」，所以不会把首页记到一个死源上。</para>
    /// </summary>
    private static void RememberPreferredSite(VodSiteInfo site, string reason)
    {
        try
        {
            var old = Preferences.Default.Get(PreferredSiteKey, string.Empty);
            if (old == site.Key) return;
            Preferences.Default.Set(PreferredSiteKey, site.Key);
            DiagLog.Write($"[home] 首选站记忆更新 {old} → {site.Key}（{site.Name}，{reason}）");
        }
        catch
        {
            // 写记忆失败只影响下次的耐心，不影响本次首载
        }
    }

    /// <summary>
    /// 首选站取数**致命失败**（爬虫引擎起不来 / 挂死）时把它从首选位上摘掉。
    ///
    /// <para>不清的后果很具体：每次冷启动都先花 50s 预算去撞同一个死站，
    /// 而这 50s 里用户什么都做不了（2026-10-02 用户原话「改了也是白改」）。
    /// 摘掉后下次冷启动直接进探测路径。随时可在「切换源」里选回来。</para>
    /// </summary>
    private static void DemotePreferredSite(VodSiteInfo site)
    {
        try
        {
            if (Preferences.Default.Get(PreferredSiteKey, string.Empty) != site.Key) return;
            Preferences.Default.Set(PreferredSiteKey, string.Empty);
            DiagLog.Write($"[home] 首选站 {site.Key}（{site.Name}）取数致命失败，已摘掉首选位（下次冷启直接走探测）");
        }
        catch
        {
        }
    }

    /// <summary>
    /// 并发探测候选站点，返回第一个能出分类的站点。
    /// <para>并发上限 <see cref="ProbeConcurrency"/>。候选里**纯 HTTP 站占绝大多数**（MacCMS 站），
    /// 2026-10-03 由 4 提到 16：实测并发 4 时前 4 个死站要各等满 8s 才轮到下一批，首个成功站
    /// 等到 36s；16 位并发下几批之内就能撞上可用站。jar 站的桥内 load 有全局锁，放多也只排队，
    /// 但探测顺序已把非 jar 站排前面，故不受影响；
    /// 整体超时 <see cref="ProbeTimeoutSeconds"/>：胜者产生或超时即收摊，其余探测随之取消，
    /// 不让死站把首页拖满自身超时。</para>
    /// </summary>
    private const int ProbeConcurrency = 8;   // 2026-10-03：4→8（温和提一点）；试过 16 无收益，且突发并发易被站方限流
    private const int ProbeTimeoutSeconds = 45;

    /// <summary>
    /// 爬虫站（依赖 QEMU ART guest）取分类的预算秒数。这类站不会抛异常，只会**一直挂着**
    /// （2026-10-02 实测挂满 4 分钟），必须有预算掐住。
    /// 2026-10-03 由 50 收到 15：冷启动时 ART 桥 ~28s 才就绪，等满 50s 只会把遮罩顶在 97% 干等。
    /// </summary>
    private const int EngineSiteBudgetSeconds = 15;

    /// <summary>
    /// 冷启动时给「依赖 guest 的首选站」的预算：桥还没就绪，久等无益 ——
    /// 3s 拿不到就立刻交给回退探测（那里按上次成功的站 + 非 jar 站优先排队）。
    /// </summary>
    private const int PreferredEngineStartupBudgetSeconds = 3;

    /// <summary>普通站（MacCMS 等直连）取分类的预算：一次网络往返，25s 足够。</summary>
    private const int PlainSiteBudgetSeconds = 25;

    /// <summary>
    /// 探测时**单个**候选站的预算。必须是它自己掐，而不是只靠一个全局窗口 ——
    /// 旧实现只有一个 15s 全局窗口，而单个死站能吃满自身 20s（MacCMS 超时），
    /// 于是前 4 个死站就把窗口耗光、110 个候选里剩下 106 个连排队都没轮到
    /// （2026-10-02 实测：探测 15s 零胜出 → 首页直接判失败）。
    /// </summary>
    private const int ProbeSiteTimeoutSeconds = 8;   // 2026-10-03：试过降到 5，结果误杀大量慢站（整轮零胜出）——保持 8

    private async Task<(VodSiteInfo Site, List<VodCategory> Cats)?> ProbeSitesAsync(IEnumerable<VodSiteInfo> candidates)
    {
        // 顺序即优先级：并发位只有 4 个，先把**不依赖 guest** 的站排前面（它们秒回），
        // 别让 4 个名额全被还在等 ART guest 的 jar 站占着、白等超时
        // （2026-10-02：guest 不可用时首页一片空白就是这个顺序问题）。
        // 上次成功出首页的站排最前（多数情况下它还在订阅里且还活着 → 第一批就命中）；
        // 其余仍按「不依赖 guest 优先」排队
        var lastGood = Preferences.Default.Get(LastGoodSiteKey, string.Empty);
        var list = candidates
            .OrderBy(s => s.Key == lastGood ? 0 : 1)
            .ThenBy(s => s.SpiderKind == VodSpiderKind.None ? 0 : 1)
            .ToList();
        if (list.Count == 0) return null;
        DiagLog.Write($"[home] 回退并发探测 {list.Count} 站（并发 {ProbeConcurrency}，单站 {ProbeSiteTimeoutSeconds}s，窗口 {ProbeTimeoutSeconds}s）");

        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(ProbeTimeoutSeconds));
        using var gate = new SemaphoreSlim(ProbeConcurrency);
        var winner = new TaskCompletionSource<(VodSiteInfo Site, List<VodCategory> Cats)>();
        var failed = 0;
        var attempted = 0;

        // 候选逐个排队进 4 个并发位；谁先出分类谁赢，赢后其余自然收敛（整体窗口到点收摊）
        var workers = list.Select(site => Task.Run(async () =>
        {
            if (winner.Task.IsCompleted || overall.IsCancellationRequested) return;
            var gotSlot = false;
            try
            {
                // ⚠ 必须**循环**拿位，不能「一次轮询没拿到就放弃」：110 个候选里只有 4 个能同时跑，
                // 没拿到位的那些要一直排着，直到有位空出来或整体窗口到点
                // （首版写成 if(!gotSlot) return ⇒ 106 个候选 1 秒内全退场，只试了最初 4 个，
                //   2026-10-02 实测「已试 106 站全废/未完，失败 4」）。
                while (!gotSlot && !winner.Task.IsCompleted && !overall.IsCancellationRequested)
                {
                    try { gotSlot = await gate.WaitAsync(TimeSpan.FromSeconds(1), overall.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
                if (!gotSlot || winner.Task.IsCompleted) return;

                Interlocked.Increment(ref attempted);
                using var siteCts = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
                siteCts.CancelAfter(TimeSpan.FromSeconds(ProbeSiteTimeoutSeconds));
                var cats = await _provider.GetCategoriesAsync(site, siteCts.Token).ConfigureAwait(false);
                if (cats.Count > 0) winner.TrySetResult((site, cats));
                else Interlocked.Increment(ref failed);
            }
            catch
            {
                Interlocked.Increment(ref failed);
            }
            finally
            {
                if (gotSlot) gate.Release();
            }
        }, overall.Token)).ToList();

        _ = await Task.WhenAny(Task.WhenAll(workers), winner.Task).ConfigureAwait(false);
        if (!winner.Task.IsCompletedSuccessfully)
        {
            DiagLog.Write($"[home] 探测收摊无胜出（实试 {attempted} 站，失败 {failed}，候选 {list.Count}）");
            return null;
        }
        DiagLog.Write($"[home] 探测胜出 {winner.Task.Result.Site.Name}（实试 {attempted} 站，失败 {failed}）");
        return winner.Task.Result;
    }

    /// <summary>
    /// 切换首页数据源（数据源弹窗选择后）：记忆首选 → 清空当前 → 直接加载所选站点。
    /// 失败不静默回退（回退会让用户以为切换无效），停在所选站点并给出具体原因。
    /// </summary>
    [RelayCommand]
    public async Task SelectSiteAsync(VodSiteInfo? site)
    {
        if (site == null) return;
        Preferences.Default.Set(PreferredSiteKey, site.Key);

        Categories.Clear();
        Items.Clear();
        HomeRecItems.Clear();
        _rowsKey = "";
        SelectedCategoryId = string.Empty;
        CurrentSite = site;
        OnPropertyChanged(nameof(Site));
        OnPropertyChanged(nameof(SiteDisplayName));

        IsHomeLoading = true;
        HomeStatus = $"正在切换到「{site.Name}」…";
        var sw = System.Diagnostics.Stopwatch.StartNew();

        List<VodCategory> cats;
        if (_catsCache.TryGetValue(site.Key, out var cached) && cached.Count > 0)
        {
            // 会话缓存：切回本会话看过的站点免整轮网络（分类+首页两跳串行）
            cats = cached;
        }
        else
        {
            try
            {
                cats = await _provider.GetCategoriesAsync(site);
                if (cats.Count == 0)
                {
                    FailedSites.Add(site.Key);
                    HomeStatus = $"「{site.Name}」未返回分类，该站点可能不可用";
                    IsHomeLoading = false;
                    return;
                }
                _catsCache[site.Key] = cats;
            }
            catch (Exception ex)
            {
                FailedSites.Add(site.Key);
                var reason = ex is NotSupportedException ? ex.Message : $"拉取失败：{ex.Message}";
                HomeStatus = $"「{site.Name}」不可用 · {reason}";
                IsHomeLoading = false;
                return;
            }
        }

        FailedSites.Remove(site.Key);
        Categories.Add(new VodCategory { Id = HomeCategoryId, Name = "主页" });   // TVBox 同款：第一个 tab 固定主页
        foreach (var c in cats) Categories.Add(c);
        DiagLog.Write($"[site-switch] 分类就绪 site={site.Name} cats={cats.Count} 耗时={sw.ElapsedMilliseconds}ms{(_catsCache.ContainsKey(site.Key) ? "（缓存命中）" : "")}");
        await EnterDefaultViewAsync();
    }

    /// <summary>
    /// 首屏快照落盘：站点 + 分类（+ 可选的第一页条目）。分类就绪时先存一次，条目就绪后再存一次；
    /// 条目为空时不覆盖盘上已有条目（站点与分类一致时保留），避免把首屏海报清空。
    /// </summary>
    private void SaveHomeSnapshot(IEnumerable<VodCategory> cats, IReadOnlyList<VodItem>? items)
    {
        if (CurrentSite is not { } cs) return;
        var list = cats.ToList();
        if (list.Count == 0) return;
        Services.HomeSnapshotStore.Save(new Services.HomeSnapshot
        {
            SiteKey = cs.Key,
            SiteName = cs.Name,
            CategoryId = SelectedCategoryId,
            Categories = list.Select(c => new Services.SnapCategory(c.Id, c.Name)).ToList(),
            Items = (items ?? []).Take(120).Select(i => new Services.SnapItem(i.Id, i.SourceKey, i.Title, i.Cover)).ToList(),
        });
    }

    /// <summary>切换分类并拉取第一页影片（仅当前站点）；分页状态复位。虚拟「主页」分类 → 豆瓣推荐行流。</summary>
    [RelayCommand]
    public async Task SelectCategoryAsync(VodCategory? category)
    {
        if (category == null) return;
        DiagLog.Write($"[cat-select] 开始 site={(CurrentSite?.Name ?? "null")} cat={category.Name} loading={IsHomeLoading}");
        SelectedCategoryId = category.Id;

        // 虚拟「主页」分类：豆瓣推荐行流，不进网格/筛选/分页链路
        if (category.Id == HomeCategoryId)
        {
            _currentCategory = null;
            _filter.Clear();
            FilterSummary = "";
            OnPropertyChanged(nameof(HasFilters));
            _currentPage = 1;
            HasMoreItems = true;
            Items.Clear();
            await LoadHomeRecRowsAsync();
            return;
        }

        // 从主页切到普通分类：推荐网格退场
        if (HomeRecItems.Count > 0) HomeRecItems.Clear();

        // 换「分类」才作废筛选条件；同一分类内重拉（改筛选/重试）要保留
        if (_currentCategory is null || _currentCategory.Id != category.Id)
        {
            _filter.Clear();
            FilterSummary = "";
        }
        _currentCategory = category;
        _currentPage = 1;
        HasMoreItems = true;
        IsHomeLoading = true;
        HomeStatus = $"正在加载「{category.Name}」…";
        Items.Clear();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var items = new List<VodItem>();
        var fromCache = false;
        if (CurrentSite != null)
        {
            var cacheKey = FirstPageKey(CurrentSite, category);
            if (_firstPageCache.TryGetValue(cacheKey, out var cachedItems))
            {
                items = cachedItems;   // 首屏缓存命中：切分类/回访免 0.5~1.3s 的一跳
                fromCache = true;
            }
            else
            {
                try
                {
                    items = await _provider.GetItemsAsync(CurrentSite, category, 1, FilterArg);
                    if (items.Count > 0)
                    {
                        if (_firstPageCache.Count >= MaxFirstPageCache) _firstPageCache.Clear();
                        _firstPageCache[cacheKey] = items;
                    }
                }
                catch (Exception ex) { DiagLog.Write($"[cat-select] 拉取失败: {ex.Message}"); }
            }
        }

        foreach (var it in items) Items.Add(it);
        CoverResolver.Attach(_covers, items);   // 列表先出，封面异步补齐（失败 → 占位海报）
        DiagLog.Write($"[cat-select] 完成 cat={category.Name} items={items.Count} 耗时={sw.ElapsedMilliseconds}ms{(fromCache ? "（缓存）" : "")}");
        HomeStatus = Items.Count == 0
            ? $"{CurrentSite?.Name ?? "当前源"} · {category.Name} · 暂无影片"
            : $"{CurrentSite!.Name} · {category.Name} · 已加载 {Items.Count} 部";
        IsHomeLoading = false;

        // 条目就绪后再落一次（含第一页，首屏海报因此能秒出）
        if (Items.Count > 0) SaveHomeSnapshot(Categories, Items);

        // 首屏填充保底：第一页条目太少（内容不满一屏）时滚动条不出现，
        // RemainingItemsThresholdReached 永远不触发 → 无限滚动死锁（2026-09-26 发行版实测：
        // 15 条无滚动条，卡死在第一页）。这里主动补载到可滚动，交给滚动接管。
        if (HasMoreItems && Items.Count < MinFillCount)
            await LoadMoreAsync();
    }

    // ═══════════════════ 分类筛选器（对位 TVBox GridFilterDialog）═══════════════════

    /// <summary>已选筛选值：组键 → 回传值。键集随分类变化，所以换分类会清空。</summary>
    private readonly Dictionary<string, string> _filter = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>当前分类是否提供筛选器（UI 据此显隐「筛选」入口）。</summary>
    public bool HasFilters => _currentCategory?.HasFilters == true;

    /// <summary>当前分类的筛选组定义。</summary>
    public List<VodFilterGroup> CurrentFilters => _currentCategory?.Filters ?? [];

    /// <summary>已选条件的回显文字（如「地区: 大陆 · 年份: 2024」）。</summary>
    [ObservableProperty] string _filterSummary = "";

    /// <summary>把已选条件交给 provider；空表传 null，免得爬虫收到「有对象但为空」的歧义输入。</summary>
    IReadOnlyDictionary<string, string>? FilterArg => _filter.Count > 0 ? _filter : null;

    /// <summary>设置/清除某个筛选值，然后重新拉第一页。</summary>
    public async Task SetFilterAsync(string key, VodFilterValue? value)
    {
        if (_currentCategory == null || CurrentSite == null || key.Length == 0) return;
        if (value is null) _filter.Remove(key);
        else _filter[key] = value.Param;

        var parts = new List<string>();
        foreach (var g in _currentCategory.Filters)
        {
            if (!_filter.TryGetValue(g.Key, out var param)) continue;
            var display = g.Values.Find(v => v.Param == param)?.Display ?? param;
            parts.Add($"{g.Name}: {display}");
        }
        FilterSummary = string.Join(" · ", parts);
        OnPropertyChanged(nameof(HasFilters));
        DiagLog.Write($"[filter] site={CurrentSite.Name} cat={_currentCategory.Name} → {(_filter.Count, FilterSummary)}");
        await SelectCategoryAsync(_currentCategory);   // 同分类 → 条件保留，只是重拉
    }

    /// <summary>清掉全部筛选条件并重拉一次（重拉一遍，而不是每组一次）。</summary>
    public async Task ClearFiltersAsync()
    {
        if (_filter.Count == 0) return;
        _filter.Clear();
        FilterSummary = "";
        await SelectCategoryAsync(_currentCategory!);
    }

    /// <summary>
    /// 滚动到底自动加载下一页（CollectionView RemainingItemsThresholdReached）。
    /// <para><b>首屏填充模式</b>：累计条目不足 <see cref="MinFillCount"/>（内容不满一屏 →
    /// 滚动条不出现 → 阈值触发永远不来）时，一次调用内连续拉页直到可滚动或加载完 ——
    /// 滚动触发时条目通常已够，循环即退化为原来的单页行为。</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    public async Task LoadMoreAsync()
    {
        DiagLog.Write($"[loadmore] 触发 page={_currentPage + 1} loading={_loadingMore} hasMore={HasMoreItems} site={(CurrentSite?.Name ?? "无")} cat={( _currentCategory?.Name ?? "无")}");
        if (_loadingMore || CurrentSite == null || _currentCategory == null) return;
        _loadingMore = true;
        LoadMoreCommand.NotifyCanExecuteChanged();
        try
        {
            // 翻页去重索引：正常源页间无重叠；爬虫不守约定（每页返回同样内容，如网盘源的
            // 「配置」类目固定 8 张卡）时靠它判停——此前 8 张卡拉到 40 部还在涨（2026-09-26 实测）
            var seen = new HashSet<string>(Items.Select(i => i.Id));
            while (true)
            {
                var next = _currentPage + 1;
                HomeStatus = $"{CurrentSite.Name} · {_currentCategory.Name} · 加载第 {next} 页…";
                var items = await _provider.GetItemsAsync(CurrentSite, _currentCategory, next, FilterArg);

                DiagLog.Write($"[loadmore] 第 {next} 页取到 {items.Count} 条（现有 {Items.Count} 条）");
                if (items.Count == 0)
                {
                    HasMoreItems = false;
                    HomeStatus = $"{CurrentSite.Name} · {_currentCategory.Name} · 已全部加载（{Items.Count} 部）";
                    return;
                }

                var fresh = new List<VodItem>(items.Count);
                foreach (var it in items)
                    if (seen.Add(it.Id)) fresh.Add(it);

                // 整页全是已有条目 = 该源不支持翻页（固定卡类目/末页重发）→ 判定加载完，不追加
                if (fresh.Count == 0)
                {
                    HasMoreItems = false;
                    HomeStatus = $"{CurrentSite.Name} · {_currentCategory.Name} · 已全部加载（{Items.Count} 部）";
                    DiagLog.Write($"[loadmore] 第 {next} 页与已有条目完全重复 → 判定无更多");
                    return;
                }

                _currentPage = next;
                foreach (var it in fresh) Items.Add(it);
                CoverResolver.Attach(_covers, fresh);   // 追加页同样补封面
                HomeStatus = $"{CurrentSite.Name} · {_currentCategory.Name} · 已加载 {Items.Count} 部";

                // 条目够滚动（≥ MinFill）或本就是滚动触发的单页请求 → 停，交还滚动接管
                if (Items.Count >= MinFillCount) return;
            }
        }
        catch
        {
            HasMoreItems = false;
        }
        finally
        {
            _loadingMore = false;
            LoadMoreCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>首屏填充阈值：低于它视为「内容不满一屏」，滚动条出不来（按最宽网格 8 列 × 3 行 + 余量）。</summary>
    private const int MinFillCount = 30;

    private bool CanLoadMore() => !_loadingMore && HasMoreItems && !IsHomeLoading;
}
