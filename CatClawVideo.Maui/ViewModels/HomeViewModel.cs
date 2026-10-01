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

        // 用户首选站点优先（数据源弹窗选择后记忆）；拉取失败回退自动探测
        var preferredKey = Preferences.Default.Get(PreferredSiteKey, string.Empty);
        var preferred = sites.FirstOrDefault(s => s.Key == preferredKey);
        // 留痕（2026-09-30）：这里以前是 `catch { }` —— 首选站点失败的原因整条被吞，
        // 于是「每次重启都不回上次的站点」根本查不动。冷启动时爬虫桥（QEMU guest）
        // 还在起（实测 桥就绪 15:59:57.3，而首屏 15:59:46.7 就开跑），jar 源这一步必然受影响。
        DiagLog.Write($"[home] 首选 key={preferredKey} 命中={(preferred?.Name ?? "(不在可播列表)")}");

        var cats = new List<VodCategory>();
        VodSiteInfo? usedSite = null;
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
                // 冷启动竞态（2026-09-30 实测）：爬虫桥（QEMU guest + ART）就绪要约 12s
                // （16:25:38.1 首屏开跑 → 16:25:49.7 桥就绪），而 jar 源的取数路径在桥没起来时
                // 是**快速失败**而不是等待：`InvalidOperationException: ART guest 起不来`。
                // 旧代码一个 `catch { }` 把这句吞掉并立刻回退自动探测 ⇒ 用户看到的正是
                // 「每次重启都不回上次那个站点，而是落到 🗂我的云盘┃配置」。
                // 现在：留痕 + 有界重试（5 次 ×4s，覆盖 ~26s 冷启），全失败才回退探测。
                for (var attempt = 1; attempt <= 5 && usedSite == null; attempt++)
                {
                    string why;
                    try
                    {
                        cats = await _provider.GetCategoriesAsync(preferred);
                        if (cats.Count > 0)
                        {
                            usedSite = preferred;
                            _catsCache[preferred.Key] = cats;
                            break;
                        }
                        why = "分类为空";
                    }
                    catch (Exception ex) { why = $"{ex.GetType().Name}: {ex.Message}"; }
                    DiagLog.Write($"[home] 首选 {preferred.Name} 第 {attempt} 次取分类失败：{why}");
                    if (attempt < 5) await Task.Delay(4000);
                }
            }
        }

        // 回退探测：此前是串行 foreach——首选站点失效时（订阅里的死源很常见），
        // 每个死站都要吃满自身超时（MacCMS 20s、jar 源最长 90s），首页等于冻住。
        // 改为并发探测（带并发上限与整体超时），第一个出分类的站点即胜出。
        if (usedSite == null)
        {
            var win = await ProbeSitesAsync(sites.Where(s => s.Key != preferred?.Key));
            if (win is { } w)
            {
                usedSite = w.Site;
                cats = w.Cats;
                _catsCache[w.Site.Key] = w.Cats;
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
    /// 并发探测候选站点，返回第一个能出分类的站点。
    /// <para>并发上限 <see cref="ProbeConcurrency"/>（jar 源的桥内 load 有全局锁，放太多只会排队）；
    /// 整体超时 <see cref="ProbeTimeoutSeconds"/>：胜者产生或超时即收摊，其余探测随之取消，
    /// 不让死站把首页拖满自身超时。</para>
    /// </summary>
    private const int ProbeConcurrency = 4;
    private const int ProbeTimeoutSeconds = 15;

    private async Task<(VodSiteInfo Site, List<VodCategory> Cats)?> ProbeSitesAsync(IEnumerable<VodSiteInfo> candidates)
    {
        var list = candidates.ToList();
        if (list.Count == 0) return null;
        DiagLog.Write($"[home] 回退并发探测 {list.Count} 站（并发 {ProbeConcurrency}，超时 {ProbeTimeoutSeconds}s）");

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(ProbeTimeoutSeconds));
        using var gate = new SemaphoreSlim(ProbeConcurrency);

        async Task<(VodSiteInfo Site, List<VodCategory> Cats)?> ProbeOne(VodSiteInfo site)
        {
            try
            {
                await gate.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                try
                {
                    var cats = await _provider.GetCategoriesAsync(site, timeoutCts.Token).ConfigureAwait(false);
                    return cats.Count > 0 ? (site, cats) : null;
                }
                finally { gate.Release(); }
            }
            catch { return null; }
        }

        var tasks = list.Select(ProbeOne).ToList();
        while (tasks.Count > 0)
        {
            var done = await Task.WhenAny(tasks).ConfigureAwait(false);
            tasks.Remove(done);
            if (await done.ConfigureAwait(false) is { } win) return win;   // using 收摊时取消其余探测
        }
        return null;
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
