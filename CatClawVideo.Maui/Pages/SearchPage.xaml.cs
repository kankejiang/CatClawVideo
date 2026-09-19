using System.Collections.ObjectModel;
using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;
using CatClawVideo.Core.Services;
using CatClawVideo.Data;
using CatClawVideo.Maui.Services;
using CatClawVideo.Maui.ViewModels;
using Microsoft.Maui.Controls.Shapes;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 搜索页（遥控器优先，三端同一套横屏布局）。
///
/// <para><b>为什么需要虚拟键盘</b>：搜索的第一动作是「输入文字」，而遥控器没有文字输入能力，
/// TV 上的系统输入法要么不存在、要么是为触屏设计的（遥控器点不动软键盘）。故自带键盘。
/// 手机端同样启用这套 UI —— 本项目约定三端共用横屏界面（<c>Entry</c> 仍保留，
/// 手机/电脑也能用系统输入法或物理键盘直接打字，虚拟键盘同步响应）。</para>
///
/// <para><b>首字母搜索</b>：输入 <c>lr</c> → 本地索引里找出「流人」等首字母以 LR 开头的影片，
/// OK 直接进观看页（不走网络）。本地未命中时才退化为跨源搜索（保留原有并发聚合行为）。
/// 索引由 <see cref="SearchIndex"/> 在浏览时被动积累。</para>
///
/// <para><b>版面</b>（2026-09-19 重构）：全高双栏 ——
/// 左栏「键盘 + 最近搜索」，右栏「热门搜索 + 继续观看」，结果态键盘整列收起、结果铺满整宽。
/// 此前键盘与热词都是自然高度，<c>*</c> 行里约 400px 无人认领（用户反馈「下方太空了」）；
/// 现在余量由「继续观看」海报墙（<c>*</c> 行）吸收，行数随窗口高度自动增减。</para>
///
/// <para><b>焦点分层</b>：键盘 / 最近搜索 / 热门搜索（候选、筛选条）/ 继续观看 / 结果 五个区。
/// <c>↑↓←→</c> 在区内移动，跨区按版面的上下左右关系走；
/// <c>OK</c> 输入或激活；<c>Back</c> 逐层退出。</para>
/// </summary>
/// <summary>支持带关键词进入：<c>search?q=片名</c>（收藏所属源失效时直接跨源找回该片）。</summary>
[QueryProperty(nameof(Keyword), "q")]
public partial class SearchPage : ContentPage, IRemoteKeyHandler
{
    // ═══════════════════════ 依赖 ═══════════════════════

    private readonly IVodSourceProvider _provider;
    private readonly CoverImageService _covers;
    private readonly VideoDatabase _db;

    // ═══════════════════════ 状态 ═══════════════════════

    /// <summary>路由入参关键词（Shell 已 URL 解码）；进入页面时消费一次并自动搜索。</summary>
    private string? _autoKeyword;

    /// <summary><c>search?q=...</c> 入参。仅用于自动搜索，不在页面上长期保存。</summary>
    public string? Keyword
    {
        get => _autoKeyword;
        set => _autoKeyword = value;
    }

    public Command SearchCommand { get; }

    private bool _searching;
    private bool _hotLoaded;

    /// <summary>结果态（true）／输入态（false）。结果态下键盘收起、结果显示。</summary>
    private bool _resultMode;

    // ─────────── 焦点 ───────────

    /// <summary>
    /// 焦点所在区。
    ///
    /// <para>2026-09-19 版面重构（全高双栏）后由 3 区变 5 区：
    /// 左栏下半多了「最近搜索」、右栏下半多了「继续观看」。分区必须与实际版面一一对应 ——
    /// 否则 ←→↑↓ 的落点会与用户眼睛看到的相邻关系对不上。</para>
    /// </summary>
    private enum Zone
    {
        /// <summary>虚拟键盘（左栏上）。</summary>
        Keyboard,
        /// <summary>左栏下：最近搜索 chip。</summary>
        Shortcuts,
        /// <summary>右栏上：候选 / 热门搜索 / 站点筛选条。</summary>
        Right,
        /// <summary>右栏下：继续观看海报墙。</summary>
        Continue,
        /// <summary>结果海报网格。</summary>
        Grid,
    }

    /// <summary>初始焦点放在**热门搜索**上：不想打字的用户一步就能搜，这是遥控器最省事的路径。</summary>
    private Zone _zone = Zone.Right;

    private int _rightIndex;        // 右栏（候选 / 热门搜索）当前项
    private int _shortcutIndex;     // 左栏下（最近搜索）当前项
    private int _continueIndex;     // 继续观看当前项
    private int _continueColumns = 4;
    private int _resultIndex;       // 结果网格当前项
    private int _resultColumns = 5;

    // ─────────── 右栏视觉件（代码构建，便于精确控制焦点） ───────────

    private readonly List<Border> _hotChips = [];
    private readonly List<Border> _historyChips = [];

    /// <summary>
    /// 「继续观看」条目：历史记录 + 其展示模型 + 预先算好的跳转路由。
    /// 路由在此算好（而不是点击时再算）是为了复用 HistoryPage 那套续播参数，
    /// 且点击路径上没有 await，遥控器连按不会错乱。
    /// </summary>
    private readonly List<(PlayHistoryEntry Entry, VodItem Item, string Query)> _continueItems = [];

    /// <summary>继续观看的海报 Border（与 <see cref="_continueItems"/> 同序）：焦点环与滚动定位要用。</summary>
    private readonly List<Border> _continueCards = [];
    private readonly List<Border> _candidateRows = [];

    /// <summary>
    /// 片名联想候选（**已按片名聚合去重**）。
    ///
    /// <para><b>为什么是「片名」而不是「影片」</b>（2026-09-19 用户实测反馈）：原先候选直接列
    /// 本地索引里的**条目**，而同一部片在每个源各有一条 —— 搜「流人」会出来十个「流人 第六季」
    /// 仅站点不同，用户看到一堆重复，反而挑不出片名。输入字母阶段用户要的是
    /// <b>「我想搜哪部片」</b>，不是「这部片在哪个源」，所以先按片名聚合、只给片名；
    /// 选定片名后再走跨源搜索去挑具体源。</para>
    /// </summary>
    private readonly List<SuggestItem> _candidates = [];

    /// <summary>
    /// 一条片名联想（**按系列聚合**）。
    ///
    /// <para>2026-09-19 用户实测两次反馈后的最终形态：</para>
    /// <list type="number">
    /// <item>最初按「索引条目」列 → 同一部片在 10 个源重复 10 行；</item>
    /// <item>改成按「完整片名」聚合 → 又变成只出某一季（输 <c>frx</c> 只给「凡人修仙传第六季」，
    ///   前几季不见）；</item>
    /// <item>最终按**系列基名**聚合：<c>frx</c> → 「凡人修仙传 · 6 季」，点开就是整个系列。</item>
    /// </list>
    /// </summary>
    private sealed class SuggestItem
    {
        /// <summary>系列基名（聚合键），如「凡人修仙传」</summary>
        public string Title { get; init; } = string.Empty;

        /// <summary>基名的拼音首字母（展示用）</summary>
        public string Initials { get; init; } = string.Empty;

        /// <summary>该系列下各季（按季号升序；单片只有一个成员）</summary>
        public List<SearchIndexEntry> Seasons { get; init; } = [];

        /// <summary>来源标签（在线联想 / 可直接播放 / 搜过）。合并同基名时会更新。</summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>
        /// **匹配精确度**（越小越靠前）。排序主键，见 <see cref="RankOf"/>。
        /// 不能用「是否有本地定位」当主键 —— 那会让「之前浏览过的某一季」顶到「完全匹配的片名」前面，
        /// 用户看到的就是「越精确的反而越靠后」（2026-09-19 反馈）。
        /// </summary>
        public int Rank { get; set; } = MatchWeak;

        /// <summary>加入顺序（同精确度时保持来源本身的顺序，如在线接口的相关度序）。</summary>
        public int Order { get; set; }

        /// <summary>季数（&gt;1 时界面显示「N 季」）</summary>
        public int SeasonCount => Seasons.Count;

        /// <summary>有本地定位可直放（取第一季即可，换季在观看页内做）</summary>
        public bool HasEntry => Seasons.Count > 0;

        /// <summary>代表条目（封面/播放用）：优先第一季</summary>
        public SearchIndexEntry? Primary => Seasons.Count > 0 ? Seasons[0] : null;
    }

    // ─────────── 匹配精确度分级 ───────────

    /// <summary>片名与输入完全相同。</summary>
    private const int MatchExact = 0;
    /// <summary>片名以输入开头（「凡人修仙传」对「凡人修仙传仙界篇」）。</summary>
    private const int MatchPrefix = 1;
    /// <summary>片名里某个词以输入开头（「令人心动的offer」对输入 offer）。</summary>
    private const int MatchToken = 2;
    /// <summary>片名里含输入（位置较后）。</summary>
    private const int MatchSubstring = 3;
    /// <summary>接口给的相关性联想，但字面并不含输入（如「类似凡人修仙传的动漫」）。</summary>
    private const int MatchWeak = 4;

    /// <summary>
    /// 计算片名相对输入的匹配精确度。
    ///
    /// <para>纯字母输入（首字母搜索）走**首字母前缀**判断：片名「凡人修仙传」的首字母
    /// <c>FRXXC</c> 以 <c>FRX</c> 开头 → 强匹配；而「类似凡人修仙传的动漫」首字母是
    /// <c>SLFRXXCDDM</c>，不以 <c>FRX</c> 开头 → 弱匹配，自然沉底。</para>
    ///
    /// <para>中文输入走**字面**判断（完全相等 / 前缀 / 词首 / 包含）。</para>
    /// </summary>
    private static int RankOf(string baseName, string query)
    {
        if (baseName.Length == 0 || query.Length == 0) return MatchWeak;

        var isAsciiQuery = query.All(char.IsAsciiLetterOrDigit);
        if (isAsciiQuery)
        {
            var initials = PinyinInitial.OfTitle(baseName);
            if (initials.Length == 0) return MatchWeak;
            if (string.Equals(initials, query, StringComparison.OrdinalIgnoreCase)) return MatchExact;
            if (initials.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return MatchPrefix;
            return MatchWeak;
        }

        if (string.Equals(baseName, query, StringComparison.OrdinalIgnoreCase)) return MatchExact;
        if (baseName.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return MatchPrefix;

        // 词首匹配：「令人心动的offer」对输入「offer」
        foreach (var token in baseName.Split([' ', '：', ':', '·', '-', '_'], StringSplitOptions.RemoveEmptyEntries))
            if (token.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return MatchToken;

        if (baseName.Contains(query, StringComparison.OrdinalIgnoreCase)) return MatchSubstring;
        return MatchWeak;
    }

    /// <summary>
    /// 搜索结果条目的匹配等级（用于结果网格排序）。
    ///
    /// <para>对齐 TVBox 的 <c>isHighMatchSearchResult</c>：它先去空白再判
    /// <c>video.name.startsWith(searchTitle)</c>，把「片名以关键词开头」的视为高匹配优先展示。
    /// 这里在此基础上再分出「完全相等 &gt; 前缀 &gt; 词首 &gt; 包含」，让最贴切的排最前。</para>
    /// </summary>
    private static int ResultRankOf(string? title, string query)
    {
        var t = (title ?? string.Empty).Trim();
        var q = query.Trim();
        if (t.Length == 0 || q.Length == 0) return MatchWeak;

        // 与 TVBox 一致：先去掉空白再比较，避免「凡人 修仙传」这类空格干扰
        var tn = t.Replace(" ", "").Replace("\u3000", "");
        var qn = q.Replace(" ", "").Replace("\u3000", "");

        if (tn.Equals(qn, StringComparison.OrdinalIgnoreCase)) return MatchExact;
        if (tn.StartsWith(qn, StringComparison.OrdinalIgnoreCase)) return MatchPrefix;
        if (tn.Contains(qn, StringComparison.OrdinalIgnoreCase)) return MatchSubstring;
        return MatchWeak;
    }

    // ─────────── 结果与站点筛选 ───────────

    private readonly ObservableCollection<VodItem> _results = [];
    private readonly List<VodItem> _sourceResults = [];   // 跨源原始结果（筛选前）
    private readonly List<Border> _filterChips = [];
    private readonly List<string> _filterSites = [];               // [0] = "全部"
    private string? _activeSite;                                   // null = 全部

    private CancellationTokenSource? _searchCts;

    // ═══════════════════════ 构造 ═══════════════════════

    public SearchPage(IVodSourceProvider provider, CoverImageService covers, VideoDatabase db)
    {
        InitializeComponent();
#if ANDROID
        // Edge-to-Edge：推入式页面必须自己补顶部安全区，否则顶栏压状态栏
        SafeAreaHelper.ApplyPageTopInset(this);
#endif
#if WINDOWS
        // 无边框窗口内容延伸进标题栏区：顶部留出 caption 按钮条高度，
        // 避免右上角「搜索」按钮与窗口控件重叠
        Padding = new Thickness(0, 48, 0, 0);
#endif
        _provider = provider;
        _covers = covers;
        _db = db;

        // 先建命令再设 BindingContext：页面未实现 INPC，绑定时 SearchCommand 必须已就位，
        // 否则搜索按钮 / Entry.ReturnCommand 绑定到 null 后永不刷新（点击无反应）
        SearchCommand = new Command(() => _ = DoSearchAsync(SearchEntry.Text), () => !_searching);

        BindingContext = this;
        ResultGrid.ItemsSource = _results;

        // 结果网格尺寸变化 → 重算列数（焦点导航按列走，必须与显示一致）
        ResultGrid.SizeChanged += (_, _) =>
        {
#if WINDOWS
            PosterLayoutHelper.Apply(ResultGrid, ResultGrid.Width, ResultGrid.Height, cap: 252);
#else
            PosterLayoutHelper.Apply(ResultGrid, ResultGrid.Width, ResultGrid.Height);
#endif
            RecalcResultColumns();
        };

        // 继续观看海报墙：卡片宽按容器宽度分列（手机横屏 3 列、桌面 5 列），
        // 列数同步给焦点导航（上下移动 = ±列数）；宽度变化导致列数变化时才重建卡片。
        ContinueHost.SizeChanged += (_, _) => BuildContinueCards();

        // 键盘高度随左栏可用高度自适应（见 SyncKeyboardHeight）
        KeyboardPane.SizeChanged += (_, _) => SyncKeyboardHeight();
        HistorySection.SizeChanged += (_, _) => SyncKeyboardHeight();   // 历史 chip 上屏/换行后也要重算

        // 虚拟键盘接线
        Keyboard.CharacterPressed += (_, ch) => AppendChar(ch);
        Keyboard.BackspacePressed += (_, _) => Backspace();
        Keyboard.SearchPressed += (_, _) => _ = DoSearchAsync(SearchEntry.Text);
        Keyboard.FocusExitRight += (_, _) => MoveToRightFromKeyboard();
        Keyboard.FocusExitDown += (_, _) => MoveToRightFromKeyboard();

        // 物理键盘 / 系统输入法：直接改 Entry 时同步候选与首字母标记
        SearchEntry.TextChanged += (_, _) => OnQueryChanged();

        BuildHintBar();
    }

    // ═══════════════════════ 生命周期 ═══════════════════════

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        RemoteKeyRouter.Push(this);

        // 带关键词进入（收藏所属源失效 → 直接跨源搜索该片）：填框并自动搜
        var auto = _autoKeyword;
        _autoKeyword = null;
        if (!string.IsNullOrWhiteSpace(auto))
        {
            SearchEntry.Text = auto;
            _ = DoSearchAsync(auto);
        }

        RefreshFocusVisual();
        await LoadHotWordsAsync();
        LoadHistory();
        await LoadContinueAsync();
        await RefreshCandidatesAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        RemoteKeyRouter.Pop(this);
        _searchCts?.Cancel();
        PushHistory(SearchEntry.Text);   // 记一笔历史（空则忽略）
        _ = SearchIndex.FlushAsync();    // 索引落盘
    }

    // ═══════════════════════ 热搜 / 历史 ═══════════════════════

    private async Task LoadHotWordsAsync()
    {
        if (_hotLoaded) return;
        _hotLoaded = true;

        // 热词（**照抄 TVBox**）：豆瓣热门剧集标题；拉不到 → 用 TVBox 那套硬编码默认热词兜底。
        // TVBox 也是这个策略（useDefaultHotWords），保证离线/被风控时页面不空白。
        var words = await DoubanHotService.GetHotWordsAsync();
        var usingDefault = words.Count == 0;
        if (usingDefault) words = [.. TvBoxSuggestService.DefaultHotWords];

        MainThread.BeginInvokeOnMainThread(() =>
        {
            HotStatus.IsVisible = false;
            foreach (var w in words) HotWords.Add(BuildWordChip(w, _hotChips));
            RightScroll.IsVisible = !ResultSection.IsVisible;
            RefreshFocusVisual();
        });
    }

    /// <summary>搜索历史（本地 Preferences，最多 12 条）。</summary>
    private void LoadHistory()
    {
        var list = GetHistory();
        if (list.Count == 0) return;

        HistorySection.IsVisible = true;
        foreach (var w in list) HistoryWords.Add(BuildWordChip(w, _historyChips));
    }

    // ═══════════════════════ 继续观看 ═══════════════════════

    /// <summary>
    /// 装载「继续观看」（读 <c>play_history</c>，带封面与进度）。
    ///
    /// <para><b>为什么搜索页要有它</b>：全高双栏改造前，键盘与热词只占上半屏，
    /// 下半屏是一整块空白（用户反馈「下方太空了」）。与其留白，不如把「上次看到哪」摆出来 ——
    /// 电视端用户打开搜索页往往正是想接着看某部片，这比让他在键盘上重新打一遍片名省事得多。</para>
    ///
    /// <para>区块高度由 <c>flex</c>（XAML 的 <c>*</c> 行）吸收右栏余量，海报行数随窗口高度自动增减；
    /// 空历史时整块隐藏，布局自动上移。</para>
    /// </summary>
    private async Task LoadContinueAsync()
    {
        if (_continueItems.Count > 0) return;   // 页面反复进出不重复装载
        try
        {
            var list = await _db.GetRecentHistoryAsync(24);
            if (list.Count == 0) return;

            var items = new List<VodItem>();
            foreach (var h in list)
            {
                var title = HistoryTitleOnly(h.Title);
                if (title.Length == 0) continue;
                // 退化标题（清洗后只剩「·」这类分隔符，实机见过一张这样的空占位海报）：
                // 卡片上没有任何可读文字，看起来像坏图，不如直接跳过。
                if (!title.Any(char.IsLetterOrDigit)) continue;

                var site = SiteRegistry.Find(h.SourceKey);
                var item = new VodItem
                {
                    SourceKey = h.SourceKey,
                    Id = h.ItemId,
                    Title = title,
                    Cover = h.Cover,
                    Remarks = h.EpisodeName,             // 集数角标：接着看哪一集
                    Year = ProgressText(h),               // 「看过 62%」/「已看完」
                };
                items.Add(item);
                _continueItems.Add((h, item, BuildHistoryQuery(h, site?.Type ?? h.ItemType,
                    site?.Api ?? h.ItemApi)));
            }
            if (_continueItems.Count == 0) return;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                ContinueStatus.Text = $"▶ 继续观看 · {_continueItems.Count}";
                ContinueSection.IsVisible = !ResultSection.IsVisible && !CandidateSection.IsVisible;
                BuildContinueCards(force: true);
            });

            CoverResolver.Attach(_covers, items);   // 封面异步补齐（顺带喂首字母索引）
        }
        catch { /* 历史读取失败不该影响搜索页 */ }
    }

    /// <summary>历史标题去掉集名后缀（「流人 第六季 · 第04集」→「流人 第六季」）。</summary>
    private static string HistoryTitleOnly(string? title)
    {
        var t = (title ?? string.Empty).Trim();
        var i = t.IndexOf(" · ", StringComparison.Ordinal);
        if (i > 0) t = t[..i].Trim();
        // 兼容「庆余年 第02集」这类不带分隔符的写法
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\s*第\s*\d+\s*[集话期]\s*$", string.Empty);
        return t.Trim();
    }

    /// <summary>「看过 62%」这类进度文案（时长未知时给兜底，不留空）。</summary>
    private static string ProgressText(PlayHistoryEntry h)
    {
        if (h.DurationSeconds > 1)
        {
            var p = (int)Math.Clamp(h.PositionSeconds / h.DurationSeconds * 100, 0, 100);
            return p >= 98 ? "已看完" : $"看过 {p}%";
        }
        return h.PositionSeconds > 1 ? "有进度" : "已看过";
    }

    /// <summary>
    /// 由历史记录构造跳转路由（与 <c>HistoryPage</c> 同一套规则）：
    /// 有来源定位 → 观看页（带 <c>resumeEp</c>/<c>route</c>/<c>pos</c> 续播到那一集）；
    /// 无定位（网页直链等）→ 播放器页直接播历史里的地址。
    /// </summary>
    private static string BuildHistoryQuery(PlayHistoryEntry e, int type, string api)
    {
        var pos = $"&pos={Math.Max(0, (int)e.PositionSeconds)}";
        var cover = string.IsNullOrEmpty(e.Cover) ? "" : $"&cover={Uri.EscapeDataString(e.Cover)}";

        var hasSource = !string.IsNullOrWhiteSpace(e.SourceKey) && !string.IsNullOrWhiteSpace(e.ItemId);
        if (!hasSource)
            return $"player?title={Uri.EscapeDataString(e.Title)}" +
                   $"&url={Uri.EscapeDataString(e.Url)}" + pos + cover;

        // 影片标题 = 「影片 · 集名」去掉集名部分（极端情况退化用集名，标题栏不能为空）
        var itemTitle = e.Title.Contains(" · ") ? e.Title.Split(" · ")[0] : e.Title;
        if (string.IsNullOrWhiteSpace(itemTitle)) itemTitle = e.EpisodeName;

        return $"watch?title={Uri.EscapeDataString(itemTitle)}" +
               $"&sourceKey={Uri.EscapeDataString(e.SourceKey)}&type={type}" +
               $"&api={Uri.EscapeDataString(api)}&itemId={Uri.EscapeDataString(e.ItemId)}" +
               (string.IsNullOrEmpty(e.EpisodeName) ? "" : $"&resumeEp={Uri.EscapeDataString(e.EpisodeName)}") +
               (string.IsNullOrEmpty(e.RouteName) ? "" : $"&route={Uri.EscapeDataString(e.RouteName)}") +
               $"&year={Uri.EscapeDataString(e.Year)}" +
               $"&remarks={Uri.EscapeDataString(e.Remarks)}" +
               $"&desc={Uri.EscapeDataString(e.Description)}" +
               $"&category={Uri.EscapeDataString(e.Category)}" + pos + cover;
    }

    /// <summary>继续观看卡片的最小宽度（决定列数）与卡片间距。</summary>
    private const double MinCardWidth = 110;
    private const double CardGap = 10;

    /// <summary>
    /// 构建「继续观看」海报墙（代码构建的换行布局，**不是** CollectionView）。
    ///
    /// <para><b>为什么不用 CollectionView</b>：它现在位于 ScrollView 内，而 ScrollView 会给子元素
    /// 无界高度 → CollectionView 撑开全部内容再被父级裁掉（结果区踩过的同一个坑）。
    /// 换行 FlexLayout 的高度天然由内容决定，交给外层 ScrollView 滚动即可。</para>
    ///
    /// <para>列数按容器宽度算（不写死）：手机横屏 3 列、桌面 5~6 列。
    /// 列数与卡片数都没变就不重建 —— 否则每次尺寸变化都会闪一下。</para>
    /// </summary>
    private void BuildContinueCards(bool force = false)
    {
        try
        {
            if (_continueItems.Count == 0) return;
            var avail = ContinueHost.Width;
            if (avail <= 0) return;

            var cols = Math.Clamp((int)Math.Floor((avail + CardGap) / (MinCardWidth + CardGap)), 3, 6);
            if (!force && cols == _continueColumns && _continueCards.Count == _continueItems.Count) return;

            _continueColumns = cols;
            var cardW = Math.Floor((avail - (cols - 1) * CardGap) / cols);
            var cardH = Math.Round(cardW * 1.5);

            ContinueHost.Children.Clear();
            _continueCards.Clear();
            foreach (var (_, item, query) in _continueItems)
            {
                var (card, poster) = BuildContinueCard(item, cardW, cardH, query);
                ContinueHost.Children.Add(card);
                _continueCards.Add(poster);
            }

            if (_continueIndex >= _continueCards.Count)
                _continueIndex = Math.Max(0, _continueCards.Count - 1);
        }
        catch { }
    }

    /// <summary>单张继续观看卡：海报（占位 + 集数角标）+ 片名 + 进度文案。</summary>
    private (View Card, Border Poster) BuildContinueCard(VodItem item, double cardW, double cardH, string query)
    {
        var img = new Image { Source = item.CoverDisplay, Aspect = Aspect.AspectFill };

        var placeholder = new Border
        {
            StrokeThickness = 0,
            Padding = 8,
            Background = ResBrush("PosterPlaceholderBrush"),
            IsVisible = item.CoverDisplay is null,
            Content = new Label
            {
                Text = item.Title,
                FontSize = 11.5,
                FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb("#D8DDF5"),
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalOptions = LayoutOptions.Center,
                MaxLines = 3,
                LineBreakMode = LineBreakMode.TailTruncation,
            },
        };

        // 封面异步补齐后回填（占位随之隐藏）
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(VodItem.CoverDisplay)) return;
            img.Source = item.CoverDisplay;
            placeholder.IsVisible = item.CoverDisplay is null;
        };

        var badge = new Border
        {
            StrokeThickness = 0,
            BackgroundColor = Color.FromArgb("#80000000"),
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            Padding = new Thickness(6, 1),
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Start,
            Margin = new Thickness(0, 6, 6, 0),
            IsVisible = !string.IsNullOrEmpty(item.Remarks),
            Content = new Label
            {
                Text = item.Remarks ?? string.Empty,
                FontSize = 9.5,
                TextColor = Color.FromArgb("#FFD76E"),
                MaxLines = 1,
            },
        };

        var poster = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            BackgroundColor = Res("CardBackgroundColor", Color.FromArgb("#332F5A")),
            WidthRequest = cardW,
            HeightRequest = cardH,
            Content = new Grid { Children = { img, placeholder, badge } },
        };

        var card = new VerticalStackLayout
        {
            Spacing = 0,
            WidthRequest = cardW,
            Margin = new Thickness(0, 0, CardGap, 12),
            Children =
            {
                poster,
                new Label
                {
                    Text = item.Title, FontSize = 11, Margin = new Thickness(0, 5, 0, 0),
                    LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 1,
                    TextColor = Res("TextPrimaryColor", Colors.White),
                },
                new Label
                {
                    Text = item.Year ?? string.Empty, FontSize = 10, Margin = new Thickness(0, 1, 0, 0),
                    LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 1,
                    TextColor = Res("TextHintColor", Color.FromArgb("#868CAE")),
                },
            },
        };

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => { if (query.Length > 0) Shell.Current.GoToAsync(query); };
        card.GestureRecognizers.Add(tap);

        return (card, poster);
    }

    private const string HistoryPrefKey = "search_history";

    private static List<string> GetHistory()
    {
        try
        {
            var raw = Preferences.Default.Get(HistoryPrefKey, string.Empty);
            if (string.IsNullOrWhiteSpace(raw)) return [];
            return [.. raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        }
        catch { return []; }
    }

    private static void PushHistory(string? word)
    {
        var w = word?.Trim();
        if (string.IsNullOrEmpty(w) || w.Length > 40) return;
        try
        {
            var list = GetHistory();
            list.RemoveAll(x => string.Equals(x, w, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, w);
            if (list.Count > 12) list.RemoveRange(12, list.Count - 12);
            Preferences.Default.Set(HistoryPrefKey, string.Join('\n', list));
        }
        catch { }
    }

    /// <summary>热词 / 历史 chip（点击填入并搜索）。</summary>
    private Border BuildWordChip(string word, List<Border> track)
    {
        var chip = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            BackgroundColor = Res("ChipInactiveColor", Color.FromArgb("#15FFFFFF")),
            Padding = new Thickness(14, 6),
            Margin = new Thickness(0, 0, 8, 8),
            Content = new Label
            {
                Text = word,
                FontSize = 12,
                TextColor = Res("TextSecondaryColor", Color.FromArgb("#BCC0DD")),
            },
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            SearchEntry.Text = word;
            _ = DoSearchAsync(word);
        };
        chip.GestureRecognizers.Add(tap);
        track.Add(chip);
        return chip;
    }

    // ═══════════════════════ 输入与候选 ═══════════════════════

    private void AppendChar(string ch)
    {
        SearchEntry.Text = (SearchEntry.Text ?? string.Empty) + ch;
    }

    private void Backspace()
    {
        var t = SearchEntry.Text ?? string.Empty;
        if (t.Length > 0) SearchEntry.Text = t[..^1];
    }

    /// <summary>输入变化：刷新首字母标记 + 本地候选。</summary>
    private void OnQueryChanged() => _ = RefreshCandidatesAsync();

    /// <summary>
    /// 刷新片名联想。
    ///
    /// <para>输入是「纯字母/数字」→ 按首字母在**片名池**里联想片名；
    /// 输入含中文 → 说明在用输入法打片名，直接当关键词走跨源搜索。</para>
    /// </summary>
    private async Task RefreshCandidatesAsync()
    {
        var q = (SearchEntry.Text ?? string.Empty).Trim();

        var isInitialQuery = q.Length > 0 && q.All(c => char.IsAsciiLetterOrDigit(c));
        ModeChip.IsVisible = isInitialQuery;

        if (!isInitialQuery)
        {
            ClearCandidates();
            ShowInputPanels(showCandidates: false);
            return;
        }

        var hit = await QueryTitleSuggestionsAsync(q);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            // 期间用户又改了输入 → 丢弃过期结果
            if (!string.Equals((SearchEntry.Text ?? string.Empty).Trim(), q, StringComparison.Ordinal)) return;

            _candidates.Clear();
            _candidates.AddRange(hit);
            BuildCandidateRows();
            ShowInputPanels(showCandidates: _candidates.Count > 0);
            RefreshFocusVisual();
        });
    }

    /// <summary>
    /// 查片名联想（**主路径照抄 TVBox：调在线拼音联想接口**）。
    ///
    /// <para><b>为什么不再自己做本地方案</b>：中文「拼音 → 汉字」反查需要完整拼音库，
    /// TVBox 也没自己做（它的 <c>PinyinAdapter</c> 只是字符串 adapter，毫无拼音逻辑），
    /// 而是交给腾讯智能搜索接口。我们自造的「本地索引 + 启动预热」既做不到覆盖面，
    /// 又因主动并发抓站而**有风控风险**（2026-09-19 用户指出，判断正确）。</para>
    ///
    /// <para>本地索引保留为**增强**：接口能给出片名，但不知道「这片在我哪个源上」；
    /// 若本地索引/历史/收藏里恰好有这个片名，就把它标为「可直接播放」并附上站点定位，
    /// 省掉一次跨源搜索。接口不可用时，本地数据仍能独立提供候选。</para>
    /// </summary>
    private async Task<List<SuggestItem>> QueryTitleSuggestionsAsync(string q)
    {
        var byBase = new Dictionary<string, SuggestItem>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<SuggestItem>();

        // 本地命中表：片名基名 → 有定位的条目集合（用于给联想项补「可直放」与季信息）
        var localByBase = new Dictionary<string, List<SearchIndexEntry>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var series in await _db.SearchSeriesByInitialsAsync(q, 40))
                if (series.BaseTitle.Length > 0) localByBase[series.BaseTitle] = series.Seasons;
        }
        catch { }

        // ① 在线联想（主路径，照抄 TVBox）—— `frx` → 「凡人修仙传」「凡人修仙传仙界篇」…
        List<string> online;
        try { online = await TvBoxSuggestService.SuggestAsync(q); }
        catch { online = []; }

        void Add(string title, string source)
        {
            var cleaned = TitleNormalizer.Clean(title);
            if (cleaned.Length == 0) return;

            var baseName = TitleNormalizer.StripSeason(cleaned);
            if (baseName.Length == 0) return;

            // 精确度：**先按来源原文算**（「类似凡人修仙传的动漫」→ 弱），
            // 再对基名重算一次取更好的那个 —— 基名剥离可能让原本弱匹配的变强
            //（「凡人修仙传仙界篇」弱于「凡人修仙传」，但两者都强于叙述性短语）。
            var rank = Math.Min(RankOf(cleaned, q), RankOf(baseName, q));

            // 同基名合并（在线接口会给出「凡人修仙传动漫第一季」等衍生词）
            if (byBase.TryGetValue(baseName, out var old))
            {
                if (old.Source.Length == 0) old.Source = source;
                if (rank < old.Rank) old.Rank = rank;    // 取更精确的
                return;
            }

            var seasons = localByBase.TryGetValue(baseName, out var s) ? s : [];
            var item = new SuggestItem
            {
                Title = baseName,
                Initials = PinyinInitial.OfTitle(baseName),
                Seasons = seasons,
                Source = seasons.Count > 0 ? "可直接播放" : source,
                Rank = rank,
                Order = ordered.Count,
            };
            ordered.Add(item);
            byBase[baseName] = item;
        }

        // ① 在线联想（照抄 TVBox；失败则列表为空，下面本地兜底）
        foreach (var w in online) Add(w, "联想");

        // ② 本地索引里**本次输入能命中但在线没给出**的片名（离线可用 / 补覆盖）
        foreach (var series in localByBase.Keys) Add(series, "本地");

        // ③ 搜索历史（用户自己搜过的，最贴合意图）
        foreach (var w in GetHistory()) Add(w, "搜过");

        // 排序：**精确度优先**（越接近输入的越靠前），同精确度时保持来源顺序
        //（在线接口本身的相关度序），最后才让「可直接播放」占一点点便宜。
        //
        // ⚠ 这里踩过一次坑：早先是以「有无本地定位」为主键，结果**用户浏览过的某一季**
        //   会顶到「完全匹配的片名」前面 —— 正是用户反馈的「越精确的反而越靠后」。
        //   本地定位只该是「同等精确度下的加分项」，不能越过精确度。
        ordered.Sort((a, b) =>
        {
            var c = a.Rank.CompareTo(b.Rank);
            if (c != 0) return c;
            c = b.HasEntry.CompareTo(a.HasEntry);
            if (c != 0) return c;
            return a.Order.CompareTo(b.Order);
        });
        return ordered.Count > 40 ? ordered.GetRange(0, 40) : ordered;
    }

    /// <summary>清空联想候选。</summary>
    private void ClearCandidates()
    {
        _candidates.Clear();
        _candidateRows.Clear();
        CandidateHost.Children.Clear();
    }

    private void BuildCandidateRows()
    {
        CandidateHost.Children.Clear();
        _candidateRows.Clear();

        if (_candidates.Count == 0) return;

        // 封面只对「有本地定位」的条目能拿到（索引里存了 cover）；
        // 其余是纯片名联想（热搜/历史），走占位海报 + 片名，等选定片名后结果页才有真封面。
        var items = new List<VodItem>();
        foreach (var c in _candidates)
            items.Add(c.Primary is not null ? ToVodItem(c.Primary) : new VodItem { Title = c.Title });
        CoverResolver.Attach(_covers, items);

        for (int i = 0; i < _candidates.Count; i++)
        {
            var row = BuildCandidateRow(_candidates[i], items[i]);
            CandidateHost.Children.Add(row);
            _candidateRows.Add(row);
        }

        var direct = _candidates.Count(c => c.HasEntry);
        var multiSeason = _candidates.Count(c => c.SeasonCount > 1);
        CandidateStatus.Text = direct > 0
            ? $"「{SearchEntry.Text!.Trim()}」匹配 {_candidates.Count} 个片名"
              + (multiSeason > 0 ? $"（{multiSeason} 个含多季）" : "")
            : $"「{SearchEntry.Text!.Trim()}」匹配 {_candidates.Count} 个片名（OK 去搜索）";
    }

    /// <summary>联想行（海报缩略 + 片名 + 首字母 + 来源标签）。</summary>
    private Border BuildCandidateRow(SuggestItem cand, VodItem item)
    {
        var thumb = new Border
        {
            WidthRequest = 42,
            HeightRequest = 58,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 7 },
            Background = ResBrush("PosterPlaceholderBrush"),
            Content = new Image { Source = item.CoverDisplay, Aspect = Aspect.AspectFill },
        };
        // 封面异步补齐后回填（仅本地索引条目有封面）
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VodItem.CoverDisplay) && thumb.Content is Image img)
                img.Source = item.CoverDisplay;
        };

        var title = new Label
        {
            Text = cand.Title,
            FontSize = 13.5,
            FontFamily = "OpenSansSemibold",
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1,
            VerticalOptions = LayoutOptions.Center,
            TextColor = Res("TextPrimaryColor", Colors.White),
        };
        var initials = new Label
        {
            Text = cand.Initials,
            FontSize = 12,
            FontFamily = "OpenSansSemibold",
            TextColor = Res("PrimaryColor", Color.FromArgb("#9B7ED8")),
            VerticalOptions = LayoutOptions.Center,
        };

        // 多季系列：显示「N 季」并列出季号，用户一眼看出「系列是全的」而不是只有某一季
        var seasonText = cand.SeasonCount > 1
            ? $"{cand.SeasonCount} 季"
            : (cand.HasEntry ? "▶ 播放" : cand.Source);

        // 多季时在下方补一行季号明细（如「第1季 · 第2季 · …第6季」），
        // 直接回答用户「前 5 季在哪」——它们全在这条里。
        var sub = new Label
        {
            Text = cand.SeasonCount > 1 ? SeasonSummary(cand) : string.Empty,
            FontSize = 10.5,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1,
            TextColor = Res("TextHintColor", Color.FromArgb("#868CAE")),
            VerticalOptions = LayoutOptions.Center,
        };

        var textCol = new VerticalStackLayout { Spacing = 1, VerticalOptions = LayoutOptions.Center };
        textCol.Add(title);
        if (sub.Text.Length > 0) textCol.Add(sub);

        var tag = new Label
        {
            Text = seasonText,
            FontSize = 10.5,
            TextColor = cand.HasEntry ? Color.FromArgb("#8FF0C4") : Res("TextHintColor", Color.FromArgb("#868CAE")),
            VerticalOptions = LayoutOptions.Center,
        };

        var grid = new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
            },
        };
        grid.Add(thumb, 0, 0);
        grid.Add(textCol, 1, 0);
        grid.Add(initials, 2, 0);
        grid.Add(tag, 3, 0);

        var box = new Border
        {
            StrokeThickness = 1,
            Stroke = new SolidColorBrush(Res("DividerColor", Color.FromArgb("#14FFFFFF"))),
            StrokeShape = new RoundRectangle { CornerRadius = 11 },
            BackgroundColor = Res("CardBackgroundColor", Color.FromArgb("#332F5A")),
            Padding = new Thickness(12, 10),
            Content = grid,
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => ActivateCandidate(cand);
        box.GestureRecognizers.Add(tap);
        return box;
    }

    /// <summary>
    /// 多季系列的季号摘要（如「第1季 · 第2季 · … · 第6季」）。
    /// 直接回答「前 5 季在哪」——它们都在这条系列里。
    /// </summary>
    private static string SeasonSummary(SuggestItem cand)
    {
        var nums = cand.Seasons
            .Select(e => TitleNormalizer.SeasonNumber(e.Title))
            .Where(n => n != int.MaxValue)
            .Distinct()
            .OrderBy(n => n)
            .ToList();
        if (nums.Count == 0) return string.Empty;

        var head = nums.Count <= 6
            ? string.Join(" · ", nums.Select(n => $"第{n}季"))
            : $"第{nums[0]}季 · … · 第{nums[^1]}季";
        return head;
    }

    /// <summary>
    /// 选中一条片名联想：
    /// <list type="bullet">
    /// <item>多季系列 → 用**基名**走跨源搜索，把各季都捞出来（本地索引可能只覆盖部分季）；</item>
    /// <item>有本地定位的单片 → 直接进观看页，省掉一次网络搜索；</item>
    /// <item>只有片名（热搜/历史来的）→ 用**片名**（中文）走跨源搜索，而不是把字母丢给站点 API。</item>
    /// </list>
    /// </summary>
    private void ActivateCandidate(SuggestItem cand)
    {
        PushHistory(cand.Title);

        // 多季系列：搜基名，让结果页把「第一季…最新季」全列出来。
        // 只播第一季会让用户以为「前几季没收录」，而其实其它季在别的源上。
        if (cand.SeasonCount > 1)
        {
            SearchEntry.Text = cand.Title;
            _ = DoSearchAsync(cand.Title);
            return;
        }

        if (cand.Primary is { } entry && SiteRegistry.Find(entry.SourceKey) is { } site)
        {
            Shell.Current.GoToAsync(BuildWatchQuery(entry.SourceKey, site, entry.ItemId,
                entry.Title, entry.Year, entry.Remarks, entry.Description, entry.Cover));
            return;
        }

        // 无定位：把片名填进搜索框，走跨源搜索（用户随后在结果页挑源）
        SearchEntry.Text = cand.Title;
        _ = DoSearchAsync(cand.Title);
    }

    /// <summary>输入态下的面板显隐：有候选显示候选，否则显示热搜/历史。</summary>
    private void ShowInputPanels(bool showCandidates)
    {
        CandidateSection.IsVisible = showCandidates;
        RightScroll.IsVisible = !showCandidates;
        // 「继续观看」属于「没在输入」时的展示：一旦有候选就收起，把右栏让给联想列表
        ContinueSection.IsVisible = !showCandidates && _continueCards.Count > 0;
        ResultSection.IsVisible = false;
        _resultMode = false;
        SetResultFullWidth(false);
    }

    /// <summary>触发 chip 的 Tap（复用同一套「填入并搜索」逻辑，避免两处实现漂移）。</summary>
    private static void TapChip(Border chip)
    {
        if (chip.GestureRecognizers.FirstOrDefault() is TapGestureRecognizer tap)
            tap.SendTapped(chip);
    }

    /// <summary>
    /// 按左栏可用高度重算键盘高度。
    ///
    /// <para><b>为什么必须自适应</b>（2026-09-19 手机实机截图确认）：键盘原本写死 300，
    /// 而手机横屏的逻辑高度只有约 360dp —— 顶栏 + 键盘 + 底部提示条加起来超过屏高，
    /// 第 4 行（<c>ZXCVBNM 删除 搜索</c>）直接溢到提示条下面看不见，
    /// 等于「退格」和「搜索」两个键按不到。</para>
    ///
    /// <para>桌面可用高度约 540dp，会顶到上限 300（保持设计尺寸）；手机压到刚好放下四行。
    /// 下限 150 兜住可用性（再小键帽就按不准了）。</para>
    /// </summary>
    private void SyncKeyboardHeight()
    {
        try
        {
            var avail = KeyboardPane.Height
                        - (HistorySection.IsVisible ? HistorySection.Height + PaneGap : 0);
            if (avail <= 0) return;   // 结果态整列隐藏时高度为 0：不动

            var h = Math.Clamp(avail, 150, 300);
            if (Math.Abs(Keyboard.HeightRequest - h) > 1) Keyboard.HeightRequest = h;
        }
        catch { }
    }

    /// <summary>左栏键盘与「最近搜索」之间的间距（XAML 里 KeyboardPane 的 RowSpacing）。</summary>
    private const double PaneGap = 13;

    // 说明：右栏「热门搜索 + 继续观看」合成一个滚动容器后，
    // 不再需要「按右栏高度裁剪热词行数」「按区块高度缩海报卡片」这两处补丁 ——
    // 内容放不下就滚动，不会再有某一方被挤没。

    /// <summary>
    /// 结果态是否铺满整宽。
    ///
    /// <para><b>为什么需要</b>：输入态是「左键盘 + 右候选」两列（键盘要有位置），
    /// 但结果海报墙挤在右半列会只显示三四列、还偏在一边。结果态把键盘列宽压到 0，
    /// 结果区即铺满整宽（2026-09-19 用户反馈「搜索结果应该全页面显示」）。</para>
    /// </summary>
    private void SetResultFullWidth(bool full)
    {
        try
        {
            KeyboardPane.IsVisible = !full;
            BodyArea.ColumnSpacing = full ? 0 : 16;

            if (!full)
            {
                // 还原输入态：键盘与内容各占一半
                BodyArea.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
                BodyArea.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
                return;
            }

            // 结果态：键盘列宽归零，内容列吃掉全部宽度
            BodyArea.ColumnDefinitions[0].Width = new GridLength(0);
            BodyArea.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
        }
        catch { }
    }

    // ═══════════════════════ 跨源搜索 ═══════════════════════

    /// <summary>跨源真实搜索：并发搜全部可播站点，聚合结果；点击卡片进观看页</summary>
    private async Task DoSearchAsync(string? keyword)
    {
        var kw = keyword?.Trim();
        if (string.IsNullOrEmpty(kw) || _searching) return;

        // ★ 纯字母/数字**绝不**直接发给站点。
        //
        // 站点（尤其爬虫源）对关键词做模糊匹配，把 `FRX` 丢过去会返回一堆风马牛不相及的结果
        // （实测：输 FRX 出来「凡人修仙传第六季」——它只是把字母当子串碰上了），
        // 用户看到的是「搜索结果很糟糕」。字母的正确用途是**在本地联想池里找片名**，
        // 找到中文片名后再拿片名去搜（见 ActivateCandidate）。
        if (kw.All(c => char.IsAsciiLetterOrDigit(c)))
        {
            // 有候选 → 直接用第一条（最相关）去搜，比弹提示让用户再操作一步更省事
            var top = _candidates.FirstOrDefault();
            if (top is not null)
            {
                kw = top.Title;
                SearchEntry.Text = kw;
            }
            else
            {
                _ = DisplayAlertAsync("提示",
                    $"「{kw}」是首字母，暂未匹配到片名。\n" +
                    "片名库首次准备需要片刻（可稍后再试），或直接用中文输入片名。", "知道了");
                return;
            }
        }

        _searching = true;
        ((Command)SearchCommand).ChangeCanExecute();

        PushHistory(kw);
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();

        // 切到结果态（结果铺满整宽：键盘列宽归零）
        _resultMode = true;
        CandidateSection.IsVisible = false;
        RightScroll.IsVisible = false;
        ResultSection.IsVisible = true;
        SetResultFullWidth(true);
        FilterBar.IsVisible = false;
        FilterChips.Children.Clear();
        _filterChips.Clear();
        _filterSites.Clear();
        _activeSite = null;
        _results.Clear();
        _sourceResults.Clear();
        _resultIndex = 0;
        StatusLabel.Text = $"正在搜索「{kw}」…";
        StatusLabel.IsVisible = true;
        Keyboard.IsEngaged = false;
        // 初始焦点给海报墙（用户主要在看结果）；站点列在其左侧，按 ← 即达
        _zone = Zone.Grid;
        RefreshFocusVisual();

        try
        {
            var sites = SiteRegistry.Playable.ToList();
            if (sites.Count == 0)
            {
                StatusLabel.Text = "暂无可用影片源，请先在设置中添加订阅";
                return;
            }

            // 边搜边出：每个站返回即增量上屏，不等全场（死站/慢站用 12s 超时熔断，不拖整体）
            var gate = new object();
            var doneCount = 0;

            var tasks = sites.Select(async site =>
            {
                try
                {
                    // ⚠ 不要用 Task.WhenAny(search, Delay(12s)) 把「迟到的结果」丢掉：
                    //   实测某盘搜站点 14.2s 才返回几十条，被整批丢弃会让用户看到的条数远少于实际。
                    //   改成「先到先上屏，迟到照样收」，总时长由外层两段式等待控制。
                    var items = await _provider.SearchAsync(site, kw);
                    if (items.Count == 0) return;

                    // 跨站聚合：每条结果标出来源站（卡片左下角站点角标，方便同片多站时挑源）
                    foreach (var it in items) { it.SiteName = site.Name; it.Category ??= site.Name; }
                    List<VodItem> added;
                    lock (gate)
                    {
                        foreach (var it in items) _sourceResults.Add(it);
                        added = items;
                    }
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        ApplySiteFilter();   // 增量刷新列表 + 站点筛选条
                        StatusLabel.Text = $"「{kw}」已找到 {_sourceResults.Count} 个结果…（{site.Name} +{items.Count}）";
                        CoverResolver.Attach(_covers, added);   // 增量补封面
                    });
                }
                catch { }
                finally
                {
                    Interlocked.Increment(ref doneCount);
                }
            }).ToArray();

            // 两段式等待：12s 内到达的先上屏（不等慢站）；慢站再给 20s，迟到的结果照样补进列表。
            // 总上限 ≈32s，避免个别死站把整页拖到桥那边的 90s 调用超时。
            var all = Task.WhenAll(tasks);
            await Task.WhenAny(all, Task.Delay(12000));
            await Task.WhenAny(all, Task.Delay(20000));

            MainThread.BeginInvokeOnMainThread(() =>
            {
                StatusLabel.Text = _sourceResults.Count > 0
                    ? $"「{kw}」找到 {_sourceResults.Count} 个结果（{sites.Count} 个站点）"
                    : $"未找到与「{kw}」相关的内容";
            });
        }
        catch
        {
            StatusLabel.Text = "搜索失败，请稍后重试";
        }
        finally
        {
            _searching = false;
            ((Command)SearchCommand).ChangeCanExecute();
        }
    }

    // ═══════════════════════ 站点筛选 ═══════════════════════

    /// <summary>
    /// 按 `SiteName` 重建筛选条并应用过滤 + **按匹配精确度排序**。
    ///
    /// <para>只列**本次真有结果**的站点：搜一部片可能只有 6 个站有货，
    /// 把没货的列出来只是噪音。每个 chip 带条数，用户一眼看出哪个站资源多。</para>
    ///
    /// <para><b>为什么必须排序</b>：结果是「哪个站先返回就先上屏」的顺序，完全不代表相关性 ——
    /// 慢站的好结果会被压在最后。TVBox 在结果页同样做了精确匹配优先
    /// （<c>isHighMatchSearchResult</c>：片名以关键词开头者优先）。</para>
    /// </summary>
    private void ApplySiteFilter()
    {
        // 站点按结果数降序（多的排前，更可能是用户想看的）
        var groups = _sourceResults
            .Where(i => !string.IsNullOrEmpty(i.SiteName))
            .GroupBy(i => i.SiteName!)
            .OrderByDescending(g => g.Count())
            .ToList();

        var signature = string.Join('|', groups.Select(g => $"{g.Key}:{g.Count()}"));
        if (signature != _filterSignature)
        {
            _filterSignature = signature;
            RebuildFilterChips(groups);
        }

        // 应用过滤
        var filtered = _activeSite is null
            ? _sourceResults
            : _sourceResults.Where(i => i.SiteName == _activeSite).ToList();

        // 按匹配精确度排序（同精确度保持到达顺序，稳定排序保证不抖动）
        var q = (SearchEntry.Text ?? string.Empty).Trim();
        var sorted = q.Length > 0
            ? filtered.Select((it, idx) => (it, idx))
                      .OrderBy(x => ResultRankOf(x.it.Title, q))
                      .ThenBy(x => x.idx)
                      .Select(x => x.it)
                      .ToList()
            : filtered;

        // 增量更新：只在集合内容真变了时整体重建（条数通常几十，成本可接受）
        if (sorted.Count != _results.Count || !sorted.SequenceEqual(_results))
        {
            _results.Clear();
            foreach (var it in sorted) _results.Add(it);
        }

        FilterBar.IsVisible = _filterChips.Count > 1;
        if (_resultIndex >= _results.Count) _resultIndex = Math.Max(0, _results.Count - 1);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            RenderFilterSelection();
            RefreshFocusVisual();
        });
    }

    /// <summary>筛选条签名（避免每次增量上屏都重建 chips）。</summary>
    private string _filterSignature = string.Empty;

    private void RebuildFilterChips(List<IGrouping<string, VodItem>> groups)
    {
        FilterChips.Children.Clear();
        _filterChips.Clear();
        _filterSites.Clear();

        // 「全部」在最前
        _filterSites.Add("全部");
        FilterChips.Children.Add(BuildFilterChip("全部", _sourceResults.Count, isAll: true));
        foreach (var g in groups)
        {
            _filterSites.Add(g.Key);
            FilterChips.Children.Add(BuildFilterChip(g.Key, g.Count(), isAll: false));

            // 增量上屏会反复走到这里：把 _filterIndex 校准到当前选中站点，
            // 否则站点顺序变化（按结果数降序重排）后焦点会停在别的站上。
            if (_activeSite == g.Key) _filterIndex = _filterSites.Count - 1;
        }
    }

    /// <summary>
    /// 单个站点筛选项（**左侧纵列**：整宽 + 站点名与条数分行，长站名才不会被挤掉）。
    /// </summary>
    private Border BuildFilterChip(string site, int count, bool isAll)
    {
        var name = new Label
        {
            Text = site,
            FontSize = 12,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1,
            TextColor = Res("TextSecondaryColor", Color.FromArgb("#BCC0DD")),
        };
        var num = new Label
        {
            Text = $"{count} 个",
            FontSize = 10.5,
            TextColor = Res("TextHintColor", Color.FromArgb("#868CAE")),
        };
        var text = new VerticalStackLayout { Spacing = 1, VerticalOptions = LayoutOptions.Center };
        text.Add(name);
        text.Add(num);

        var chip = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 9 },
            BackgroundColor = Res("ChipInactiveColor", Color.FromArgb("#15FFFFFF")),
            Padding = new Thickness(11, 7),
            Content = text,
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            _activeSite = isAll ? null : site;
            _filterIndex = _filterSites.IndexOf(site);
            ApplySiteFilter();
            RenderFilterSelection();
        };
        chip.GestureRecognizers.Add(tap);

        chip.ClassId = isAll ? "all" : site;
        _filterChips.Add(chip);
        return chip;
    }

    private int _filterIndex;

    /// <summary>
    /// 给 chip 内所有文本染色。
    /// <para>chip 内容有两种形态：站点纵列是 <c>VerticalStackLayout</c>（站名 + 条数），
    /// 热词/历史是单个 <c>Label</c> —— 这里统一处理，避免两处样式分叉。</para>
    /// </summary>
    private static void TintChip(Border chip, Color color)
    {
        switch (chip.Content)
        {
            case Label l:
                l.TextColor = color;
                break;
            case Layout layout:
                foreach (var child in layout.Children)
                    if (child is Label cl) cl.TextColor = color;
                break;
        }
    }

    /// <summary>
    /// 把「当前选中站点」画出来（选中 = 主色实底 + 白字）。
    /// 与焦点态是**两个维度**：焦点表示「遥控器停在哪」，选中表示「正在看哪个站的资源」——
    /// 不区分的话用户按 OK 后看不出筛选是否生效（结果数量变了但没有任何视觉反馈）。
    /// </summary>
    private void RenderFilterSelection()
    {
        var primary = Res("PrimaryColor", Color.FromArgb("#9B7ED8"));
        for (int i = 0; i < _filterChips.Count; i++)
        {
            var chip = _filterChips[i];
            var isSelected = i < _filterSites.Count
                             && (_filterSites[i] == "全部" ? _activeSite is null : _filterSites[i] == _activeSite);
            chip.ClassId = _filterSites[i] == "全部" ? "all" : _filterSites[i];

            // 焦点优先：焦点态由 ApplyChipStyle 画（避免两套样式打架）
            if (ReferenceEquals(chip, _highlighted)) continue;

            if (isSelected)
            {
                chip.BackgroundColor = primary.WithAlpha(0.85f);
                chip.StrokeThickness = 0;
                TintChip(chip, Colors.White);
            }
            else
            {
                chip.BackgroundColor = Res("ChipInactiveColor", Color.FromArgb("#15FFFFFF"));
                chip.StrokeThickness = 0;
                TintChip(chip, Res("TextSecondaryColor", Color.FromArgb("#BCC0DD")));
            }
        }
    }

    // ═══════════════════════ 焦点渲染 ═══════════════════════

    /// <summary>上一次高亮的视觉件（退出时复原）。</summary>
    private VisualElement? _highlighted;

    private void RefreshFocusVisual()
    {
        // 先清旧高亮（筛选条要按「选中态」复原，不能一律回到未选中样式）
        if (_highlighted is not null)
        {
            var wasFilter = _filterChips.Contains(_highlighted);
            ApplyChipStyle(_highlighted, focused: false);
            _highlighted = null;
            if (wasFilter) RenderFilterSelection();
        }

        // 离开海报区时清掉焦点环 —— 两处海报墙的焦点都由数据项自己的 IsFocused 驱动，
        // 不复位就会留着上一处的亮框，看起来像「两个地方同时有焦点」。
        if (_continueHighlighted is not null)
        {
            ApplyPosterCardFocus(_continueHighlighted, false);
            _continueHighlighted = null;
        }
        if (_zone != Zone.Grid) ClearPosterFocus(_results);

        // 键盘持焦：键盘自己画焦点，其余区不需要高亮
        Keyboard.IsEngaged = _zone == Zone.Keyboard;
        if (_zone == Zone.Keyboard)
        {
            HintFocus.Text = "焦点：键盘";
            return;
        }

        VisualElement? target = null;
        string desc = "";

        switch (_zone)
        {
            case Zone.Shortcuts:
                if (_historyChips.Count > 0)
                {
                    _shortcutIndex = Math.Clamp(_shortcutIndex, 0, _historyChips.Count - 1);
                    target = _historyChips[_shortcutIndex];
                    desc = $"最近搜索 {_shortcutIndex + 1}/{_historyChips.Count}";
                }
                break;

            case Zone.Right:
                // 筛选条 > 候选 > 热门搜索（按当前显示的面板决定）
                if (_resultMode && _filterChips.Count > 1)
                {
                    _filterIndex = Math.Clamp(_filterIndex, 0, _filterChips.Count - 1);
                    target = _filterChips[_filterIndex];
                    desc = $"筛选：{_filterSites[_filterIndex]}";
                }
                else if (CandidateSection.IsVisible && _candidateRows.Count > 0)
                {
                    _rightIndex = Math.Clamp(_rightIndex, 0, _candidateRows.Count - 1);
                    target = _candidateRows[_rightIndex];
                    desc = $"候选 {_rightIndex + 1}/{_candidateRows.Count}";
                }
                else if (_hotChips.Count > 0)
                {
                    _rightIndex = Math.Clamp(_rightIndex, 0, _hotChips.Count - 1);
                    target = _hotChips[_rightIndex];
                    desc = $"热门搜索 {_rightIndex + 1}/{_hotChips.Count}";
                }
                break;

            case Zone.Continue:
                if (_continueCards.Count > 0)
                {
                    _continueIndex = Math.Clamp(_continueIndex, 0, _continueCards.Count - 1);
                    var card = _continueCards[_continueIndex];
                    ApplyPosterCardFocus(card, true);
                    _continueHighlighted = card;
                    // 右栏是整体滚动区：焦点下移后要把卡片滚进视野，否则用户看不到自己在选什么
                    _ = RightScroll.ScrollToAsync(card, ScrollToPosition.MakeVisible, false);
                    desc = $"继续观看 {_continueIndex + 1}/{_continueCards.Count}";
                }
                break;

            case Zone.Grid:
                if (_results.Count > 0)
                {
                    _resultIndex = Math.Clamp(_resultIndex, 0, _results.Count - 1);
                    // 结果网格的焦点画在数据项上（XAML DataTrigger 绑定 IsFocused）
                    for (int i = 0; i < _results.Count; i++)
                        _results[i].IsFocused = i == _resultIndex;
                    ScrollResultIntoView(_resultIndex);
                    desc = $"结果 {_resultIndex + 1}/{_results.Count}";
                }
                break;
        }

        if (target is not null)
        {
            ApplyChipStyle(target, focused: true);
            _highlighted = target;
        }

        HintFocus.Text = "焦点：" + (string.IsNullOrEmpty(desc) ? "—" : desc);
    }

    /// <summary>清掉一组海报卡上的焦点环（焦点环由数据项 IsFocused 驱动，离开该区必须复位）。</summary>
    private static void ClearPosterFocus(IEnumerable<VodItem> items)
    {
        foreach (var it in items) it.IsFocused = false;
    }

    /// <summary>当前高亮的继续观看卡（与 chip 高亮分开：海报卡与 chip 的焦点样式不同）。</summary>
    private Border? _continueHighlighted;

    /// <summary>继续观看卡的焦点环。不能走 chip 那套：海报底色被封面盖住、文字又在卡片外。</summary>
    private void ApplyPosterCardFocus(Border box, bool focused)
    {
        var primary = Res("PrimaryColor", Color.FromArgb("#9B7ED8"));
        box.Stroke = new SolidColorBrush(primary);
        box.StrokeThickness = focused ? 3 : 0;
        box.Scale = focused ? 1.04 : 1;
    }

    /// <summary>chip 焦点样式（与 FocusableRow 同一套：紫底 + 描边 + 柔光）。</summary>
    private void ApplyChipStyle(VisualElement el, bool focused)
    {
        if (el is not Border box) return;
        var primary = Res("PrimaryColor", Color.FromArgb("#9B7ED8"));

        if (focused)
        {
            // 站点筛选（有底色的整条）用主色实底；热词/历史 chip 用淡底 + 描边
            var isFilter = box.ClassId is "all" || _filterSites.Contains(box.ClassId);
            box.BackgroundColor = isFilter ? primary : primary.WithAlpha(0.22f);
            box.Stroke = new SolidColorBrush(primary);
            box.StrokeThickness = 2;
            TintChip(box, Colors.White);
        }
        else
        {
            box.StrokeThickness = 0;
            box.BackgroundColor = Res("ChipInactiveColor", Color.FromArgb("#15FFFFFF"));
            TintChip(box, Res("TextSecondaryColor", Color.FromArgb("#BCC0DD")));
        }
    }

    private void ScrollResultIntoView(int index)
    {
        try { if (index >= 0 && index < _results.Count) ResultGrid.ScrollTo(index, position: ScrollToPosition.MakeVisible, animate: false); }
        catch { }
    }

    /// <summary>键盘越界 → 焦点进右栏。</summary>
    private void MoveToRightFromKeyboard()
    {
        _zone = Zone.Right;
        RefreshFocusVisual();
    }

    /// <summary>
    /// 从 <see cref="PosterLayoutHelper"/> 写入的实际 span 同步列数。
    /// 焦点导航按「上下移动 = ±列数」计算，列数与显示不一致会导致跳到错的行。
    /// </summary>
    private void RecalcResultColumns()
    {
        try
        {
            if (ResultGrid.ItemsLayout is GridItemsLayout g && g.Span > 0)
                _resultColumns = g.Span;
        }
        catch { }
    }

    // ═══════════════════════ 遥控器按键 ═══════════════════════

    /// <summary>外层（主壳层）把焦点送进来：落到键盘（本页的主输入区）。</summary>
    public void FocusContent()
    {
        _zone = Zone.Keyboard;
        Keyboard.IsEngaged = true;
        RefreshFocusVisual();
    }

    public bool Handle(RemoteKey key)
    {
        switch (key)
        {
            case RemoteKey.Left:
                if (_zone == Zone.Keyboard) return Keyboard.MoveLeft();
                // 结果态：站点纵列在最左 → 逐列左移，到最左列则进站点列
                if (_zone == Zone.Grid)
                {
                    if (_resultIndex % _resultColumns > 0) { _resultIndex--; RefreshFocusVisual(); }
                    else if (FilterBar.IsVisible) { _zone = Zone.Right; RefreshFocusVisual(); }
                    return true;
                }
                // 右栏 / 继续观看 → 左栏键盘（键盘在它们左边，视觉上对得上）
                if (_zone is Zone.Right or Zone.Continue) { _zone = Zone.Keyboard; RefreshFocusVisual(); return true; }
                // 左栏下半（最近搜索）：栏内左移，到头则回键盘
                if (_zone == Zone.Shortcuts)
                {
                    if (_shortcutIndex > 0) { _shortcutIndex--; RefreshFocusVisual(); }
                    else { _zone = Zone.Keyboard; RefreshFocusVisual(); }
                    return true;
                }
                return true;

            case RemoteKey.Right:
                if (_zone == Zone.Keyboard) return Keyboard.MoveRight();
                // 结果态：站点纵列 → 海报墙
                if (_zone == Zone.Right && _resultMode && FilterBar.IsVisible)
                {
                    _zone = Zone.Grid;
                    RefreshFocusVisual();
                    return true;
                }
                if (_zone == Zone.Grid)
                {
                    var cols = _resultColumns;
                    if (_resultIndex % cols < cols - 1 && _resultIndex + 1 < _results.Count)
                    { _resultIndex++; RefreshFocusVisual(); }
                    return true;
                }
                if (_zone == Zone.Shortcuts)
                {
                    if (_shortcutIndex < _historyChips.Count - 1) { _shortcutIndex++; RefreshFocusVisual(); }
                    return true;
                }
                if (_zone == Zone.Continue)
                {
                    var cols = Math.Max(1, _continueColumns);
                    if (_continueIndex % cols < cols - 1 && _continueIndex + 1 < _continueCards.Count)
                    { _continueIndex++; RefreshFocusVisual(); }
                    return true;
                }
                if (_zone == Zone.Right)
                {
                    var chips = CandidateSection.IsVisible ? _candidateRows : _hotChips;
                    if (_rightIndex < chips.Count - 1) { _rightIndex++; RefreshFocusVisual(); }
                    return true;
                }
                return true;

            case RemoteKey.Up:
                if (_zone == Zone.Keyboard) return Keyboard.MoveUp();   // 到顶行（数字行）返回 false → 交外层
                // 站点纵列：↑↓ 在站点间移动（它在最左，纵向移动才自然）
                if (_zone == Zone.Right && _resultMode && FilterBar.IsVisible)
                {
                    if (_filterIndex > 0) { _filterIndex--; ApplySiteFilter(); }
                    return true;
                }
                if (_zone == Zone.Grid)
                {
                    // 已到顶行 → 焦点交站点列（若可见），否则交键盘
                    if (_resultIndex >= _resultColumns) { _resultIndex -= _resultColumns; RefreshFocusVisual(); }
                    else if (FilterBar.IsVisible) { _zone = Zone.Right; RefreshFocusVisual(); }
                    else { _zone = Zone.Keyboard; RefreshFocusVisual(); }
                    return true;
                }
                if (_zone == Zone.Continue)
                {
                    // 首行再往上 → 回「热门搜索」
                    if (_continueIndex >= _continueColumns) { _continueIndex -= _continueColumns; RefreshFocusVisual(); }
                    else { _zone = Zone.Right; RefreshFocusVisual(); }
                    return true;
                }
                // 最近搜索：chip 会按宽度换行，逐行上移的落点难以预测，直接回键盘更符合直觉
                if (_zone == Zone.Shortcuts) { _zone = Zone.Keyboard; RefreshFocusVisual(); return true; }
                return true;

            case RemoteKey.Down:
                if (_zone == Zone.Keyboard) return Keyboard.MoveDown();   // 末行返回 false → 交外层（左栏「最近搜索」）
                if (_zone == Zone.Right && _resultMode && FilterBar.IsVisible)
                {
                    if (_filterIndex < _filterChips.Count - 1) { _filterIndex++; ApplySiteFilter(); }
                    else { _zone = Zone.Grid; RefreshFocusVisual(); }   // 站点到底 → 进海报墙
                    return true;
                }
                if (_zone == Zone.Right)
                {
                    if (CandidateSection.IsVisible)
                    {
                        if (_rightIndex < _candidateRows.Count - 1) { _rightIndex++; RefreshFocusVisual(); }
                        return true;
                    }
                    // 热门搜索 → 继续观看（落到首行）
                    if (_continueCards.Count > 0)
                    {
                        _continueIndex = Math.Clamp(_continueIndex, 0, Math.Max(0, _continueColumns - 1));
                        _zone = Zone.Continue;
                        RefreshFocusVisual();
                    }
                    return true;
                }
                if (_zone == Zone.Continue)
                {
                    if (_continueIndex + _continueColumns < _continueCards.Count)
                    { _continueIndex += _continueColumns; RefreshFocusVisual(); }
                    return true;
                }
                if (_zone == Zone.Grid)
                {
                    if (_resultIndex + _resultColumns < _results.Count) { _resultIndex += _resultColumns; RefreshFocusVisual(); }
                    return true;
                }
                return true;

            case RemoteKey.Enter:
                return Activate();

            case RemoteKey.Back:
                return HandleBack();
        }
        return false;
    }

    /// <summary>OK：按当前焦点激活。</summary>
    private bool Activate()
    {
        switch (_zone)
        {
            case Zone.Keyboard:
                Keyboard.Activate();
                return true;

            case Zone.Right:
                if (_resultMode && _filterChips.Count > 1)
                {
                    var site = _filterSites[Math.Clamp(_filterIndex, 0, _filterSites.Count - 1)];
                    _activeSite = site == "全部" ? null : site;
                    ApplySiteFilter();
                    return true;
                }
                if (CandidateSection.IsVisible && _candidateRows.Count > 0)
                {
                    _rightIndex = Math.Clamp(_rightIndex, 0, _candidates.Count - 1);
                    ActivateCandidate(_candidates[_rightIndex]);
                    return true;
                }
                var chips = _hotChips;
                if (chips.Count > 0)
                {
                    _rightIndex = Math.Clamp(_rightIndex, 0, chips.Count - 1);
                    TapChip(chips[_rightIndex]);
                    return true;
                }
                return true;

            case Zone.Shortcuts:
                if (_historyChips.Count > 0)
                {
                    _shortcutIndex = Math.Clamp(_shortcutIndex, 0, _historyChips.Count - 1);
                    TapChip(_historyChips[_shortcutIndex]);
                }
                return true;

            case Zone.Continue:
                if (_continueCards.Count > 0)
                {
                    _continueIndex = Math.Clamp(_continueIndex, 0, _continueCards.Count - 1);
                    var q = _continueItems[_continueIndex].Query;
                    if (q.Length > 0) Shell.Current.GoToAsync(q);
                }
                return true;

            case Zone.Grid:
                if (_results.Count > 0)
                {
                    _resultIndex = Math.Clamp(_resultIndex, 0, _results.Count - 1);
                    OnResultActivated(_results[_resultIndex]);
                }
                return true;
        }
        return false;
    }

    /// <summary>
    /// Back 逐层退出（**总是消费**，避免落到主壳层去移动顶部 tab）：
    /// 结果态 → 回输入态；输入非空 → 退格；焦点在右栏 → 回键盘；否则 → 返回上一页。
    /// </summary>
    private bool HandleBack()
    {
        if (_resultMode)
        {
            _resultMode = false;
            ResultSection.IsVisible = false;
            ShowInputPanels(showCandidates: _candidates.Count > 0);
            _zone = Zone.Keyboard;
            Keyboard.IsEngaged = true;
            RefreshFocusVisual();
            return true;
        }

        var text = SearchEntry.Text ?? string.Empty;
        if (text.Length > 0)
        {
            Backspace();
            return true;
        }

        if (_zone != Zone.Keyboard)
        {
            _zone = Zone.Keyboard;
            Keyboard.IsEngaged = true;
            RefreshFocusVisual();
            return true;
        }

        Shell.Current.GoToAsync("..");
        return true;
    }

    // ═══════════════════════ 结果点击 ═══════════════════════

    /// <summary>结果卡点击 → 观看页（还原站点 type/api 路由）</summary>
    private void OnResultSelected(object? sender, SelectionChangedEventArgs e)
    {
        ResultGrid.SelectedItem = null;
        if (e.CurrentSelection.FirstOrDefault() is not VodItem item) return;
        OnResultActivated(item);
    }

    private void OnResultActivated(VodItem item)
    {
        var site = SiteRegistry.Find(item.SourceKey);
        if (site == null)
        {
            _ = DisplayAlertAsync("提示", "该结果所属源已失效，请重新搜索", "确定");
            return;
        }
        PushHistory(item.Title);
        Shell.Current.GoToAsync(BuildWatchQuery(item.SourceKey, site, item.Id,
            item.Title, item.Year, item.Remarks, item.Description, item.Cover));
    }

    /// <summary>构造观看页路由（两条路径共用）。</summary>
    private static string BuildWatchQuery(string sourceKey, VodSiteInfo site, string itemId,
        string title, string? year, string? remarks, string? desc, string? cover) =>
        $"watch?title={Uri.EscapeDataString(title)}" +
        $"&sourceKey={Uri.EscapeDataString(sourceKey)}" +
        $"&type={site.Type}" +
        $"&api={Uri.EscapeDataString(site.Api)}" +
        $"&itemId={Uri.EscapeDataString(itemId)}" +
        $"&year={Uri.EscapeDataString(year ?? "")}" +
        $"&remarks={Uri.EscapeDataString(remarks ?? "")}" +
        $"&desc={Uri.EscapeDataString(desc ?? "")}" +
        $"&cover={Uri.EscapeDataString(cover ?? "")}";

    private static VodItem ToVodItem(SearchIndexEntry e) => new()
    {
        SourceKey = e.SourceKey,
        Id = e.ItemId,
        Title = e.Title,
        Cover = e.Cover,
        Year = e.Year,
        Remarks = e.Remarks,
        Category = e.Category,
        Description = e.Description,
    };

    // ═══════════════════════ 底部提示条 ═══════════════════════

    /// <summary>底部按键提示：TV 上没有这份提示用户不知道该按什么。</summary>
    private void BuildHintBar()
    {
        AddHint("↑↓←→", "移动");
        AddHint("OK", "输入 / 激活");
        AddHint("Back", "退格 / 返回");
    }

    private void AddHint(string key, string text)
    {
        var stack = new HorizontalStackLayout { Spacing = 7, VerticalOptions = LayoutOptions.Center };
        stack.Add(new Border
        {
            StrokeThickness = 1,
            Stroke = new SolidColorBrush(Res("DividerColor", Color.FromArgb("#14FFFFFF"))),
            BackgroundColor = Res("ChipInactiveColor", Color.FromArgb("#15FFFFFF")),
            StrokeShape = new RoundRectangle { CornerRadius = 6 },
            Padding = new Thickness(7, 2),
            Content = new Label
            {
                Text = key,
                FontSize = 11,
                TextColor = Res("TextPrimaryColor", Colors.White),
            },
        });
        stack.Add(new Label
        {
            Text = text,
            FontSize = 11.5,
            TextColor = Res("TextHintColor", Color.FromArgb("#868CAE")),
            VerticalOptions = LayoutOptions.Center,
        });
        HintLeft.Add(stack);
    }

    // ═══════════════════════ 工具 ═══════════════════════

    private void OnBackTapped(object? sender, TappedEventArgs e) => Shell.Current.GoToAsync("..");

    /// <summary>主题切换后刷新代码构建件的配色。</summary>
    public void RefreshThemeColors()
    {
        Keyboard.RefreshThemeColors();
        RefreshFocusVisual();
    }

    private static Color Res(string key, Color fallback)
    {
        try
        {
            if (Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c)
                return c;
        }
        catch { }
        return fallback;
    }

    private static Brush? ResBrush(string key)
    {
        try
        {
            if (Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Brush b)
                return b;
        }
        catch { }
        return null;
    }
}
