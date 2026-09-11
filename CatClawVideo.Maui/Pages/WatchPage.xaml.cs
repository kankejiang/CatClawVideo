using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.Maui.Controls.Shapes;
using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;
using CatClawVideo.Core.Services;
using CatClawVideo.Data;
using CatClawVideo.Maui.Controls;
using CatClawVideo.Maui.Services;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 观看页（主流播放器布局）：播放器 + 右侧选集栏（等高滚动）+ 底部信息区。
/// 路由携带 sourceKey/type/api/itemId，进入后拉播放线路与选集；
/// 播放统一走 ResolvePlayUrlAsync（web 源实时解析直链、磁力拦截、防盗链参数）。
/// </summary>
public partial class WatchPage : ContentPage, IQueryAttributable
{
    private readonly IVodSourceProvider _provider;
    private readonly VideoDatabase _db;

    private VodSiteInfo _site = new();
    private VodItem _item = new();
    private List<VodPlaySource> _sources = [];
    private VodEpisode? _currentEpisode;
    private int _currentSourceIndex;
    private int _currentEpisodeIndex = -1;
    private readonly List<EpisodeRow> _episodeRows = [];

    /// <summary>选集分页：每页 20 集（2 列 × 10 行）/ 当前页（0 基）/ 当前页已渲染格的可视件（高亮用）</summary>
    /// <summary>选集列数按集数自适应：少→1 列大按钮，中→2 列，多→3 列密排（每列 10 行）</summary>
    private static int EpisodeColumnsFor(int episodeCount) =>
        episodeCount <= 10 ? 1 : episodeCount <= 40 ? 2 : 3;

    /// <summary>每页行数按选集框**可视高度**铺满（单行 ≈40dp：格子 36 + 行距 4），
    /// 不再固定 10 行——面板多高就排多少行，一页正好铺满（2026-09-11 用户要求）。
    /// 注：46dp 估值偏保守会少排一行（真机实测 28 集只排到 8 集/页），取 40dp。</summary>
    private int RowsPerPage()
    {
        double h = EpisodeScroll.Height;
        int rows = h > 80 ? (int)Math.Floor(h / 40.0) : 10;   // 未布局时先按 10 行兜底，布局完成会重排
        return Math.Clamp(rows, 3, 20);
    }

    private int PerPageFor(int episodeCount) => RowsPerPage() * EpisodeColumnsFor(episodeCount);

    private int _lastRowsPerPage;
    private int _episodePage;
    private readonly List<(Border Border, Label Name, Border Num, EpisodeRow Row)> _pageVisuals = [];

    private bool _playing;
    private bool _seeking;
    private bool _loaded;
    private bool _descExpanded;
    private string _descFull = string.Empty;

    /// <summary>最近一次成功解析的播放请求（全屏复用解析结果；原始 episode.Url 未必可播）</summary>
    private PlayRequest? _resolvedPlay;

    /// <summary>播放代次：快速切集/切线路时废弃过期解析结果，防止旧响应覆盖新播放</summary>
    private int _playGeneration;

    /// <summary>线路芯片（SelectSource 高亮用；LinesHost 首位可能是"线路"前缀标签）</summary>
    private readonly List<Border> _lineChips = [];

    /// <summary>播放历史会话（观看页播放也落库，海报墙封面来自 _item.Cover）</summary>
    private readonly VideoPlaybackManager _playback;

    /// <summary>BT 引擎（网速徽章数据源；非 BT 播放时不显示）</summary>
    private readonly BtStreamService? _bt;

    /// <summary>下载管理器（磁力资源行"下载"按钮入口）</summary>
    private readonly DownloadManager? _downloads;

    /// <summary>网速徽章：BT 会话 infoHash / 刷新定时器 / 鼠标悬浮开关</summary>
    private string? _btInfoHex;
    private IDispatcherTimer? _speedTimer;

    /// <summary>控制层自动隐藏：鼠标移出播放框（或手指离开）后 3s 隐藏；播放中且非拖动时才生效</summary>
    private IDispatcherTimer? _controlsHideTimer;
    private const double ControlsHideSeconds = 3.0;

    /// <summary>原地全屏：同一播放器实例放大铺满，不新开页面/不重新拉流</summary>
    private bool _isFullscreen;

    /// <summary>播放历史跳转携带的续看定位：选集加载后自动选该集，MediaOpened 后 seek</summary>
    private string? _resumeEpisodeName;
    private string? _resumeRouteName;

    /// <summary>查询参数就绪信号：Shell 在不同入口下 ApplyQueryAttributes 与 OnAppearing/Load 的
    /// 先后顺序不保证——若 Load 先跑，续看上下文与 item 身份都还没有，会回落默认线路并覆盖历史。</summary>
    private readonly TaskCompletionSource _argsReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private double _resumePosition;

    public WatchPage(IVodSourceProvider provider, VideoDatabase db, VideoPlaybackManager playback,
        BtStreamService? bt = null, DownloadManager? downloads = null)
    {
        InitializeComponent();
        _provider = provider;
        _db = db;
        _playback = playback;
        _bt = bt;
        _downloads = downloads;

#if ANDROID
        // 顶栏避开状态栏：Edge-to-Edge 下页面从 y=0 起绘，返回按钮会顶进状态栏
        // （2026-09-11 真机实测）。状态栏高度动态取自 SafeAreaHelper；
        // 原地全屏隐藏 TopBarGrid 时边距随之消失，不留缝。
        ApplyTopBarInset();
        SafeAreaHelper.SafeAreaChanged += (_, _) =>
            MainThread.BeginInvokeOnMainThread(ApplyTopBarInset);
#endif

        ContentStack.Padding = NormalContentPadding;

        Player.PositionChanged += (_, _) => MainThread.BeginInvokeOnMainThread(UpdateProgress);

        // 首帧布局后把面板高度对齐到播放框实际高度
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(600), () => { SyncSidebarHeight(); return false; });

        // 选集框尺寸就绪/变化后按实际高度重排每页集数（一页铺满面板）
        EpisodeScroll.SizeChanged += (_, _) =>
        {
            int rows = RowsPerPage();
            if (rows == _lastRowsPerPage) return;
            _lastRowsPerPage = rows;
            MainThread.BeginInvokeOnMainThread(() => { try { RenderEpisodePage(); } catch { } });
        };
        Player.MediaOpened += (_, _) => MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateProgress();

            // 播放历史续看：媒体就绪（时长已知）后 seek 到上次位置。
            // 刚 Open 的瞬间部分后端尚不可 seek（Windows 实测直接 Seek 会被丢弃），
            // 故首次失败后 300ms/900ms 各重试一次（位置明显偏离目标才重试）。
            if (_resumePosition > 0)
            {
                var pos = _resumePosition;
                _resumePosition = 0;
                _resumeEpisodeName = null;
                TrySeekToResume(pos, retry: 0);
            }
        });
        Player.StateChanged += (_, _) => MainThread.BeginInvokeOnMainThread(UpdatePlayIcon);
        // 播放失败必须有可见反馈（此前磁力/解析失败静默，用户以为"没反应"）
        Player.MediaFailed += (_, _) => MainThread.BeginInvokeOnMainThread(async () =>
        {
            ShowBuffering(false);
            _playing = false;
            UpdatePlayIcon();
            try { await ShowTipAsync($"播放失败：{Player.ErrorMessage ?? "格式或网络错误"}"); }
            catch { }
        });

        // 网速徽章：鼠标移入播放画面显示（BT 播放时），0.8s 刷新
        // 指针手势挂在 ControlsOverlay 上：该层本身**常驻不隐藏**（隐藏的是它的子元素），
        // 否则控制层一旦隐藏，其自身手势失效，控件就再也唤不出来。
        var pointer = new PointerGestureRecognizer();
        pointer.PointerEntered += OnPlayerPointerEntered;
        pointer.PointerMoved += OnPlayerPointerMoved;
        pointer.PointerExited += OnPlayerPointerExited;
        ControlsOverlay.GestureRecognizers.Add(pointer);
        _speedTimer = Dispatcher.CreateTimer();
        _speedTimer.Interval = TimeSpan.FromMilliseconds(800);
        _speedTimer.IsRepeating = true;
        _speedTimer.Tick += (_, _) => UpdateSpeedBadge();

        // 控制层自动隐藏：鼠标移出播放框 / 手指离开后 3s 隐藏
        _controlsHideTimer = Dispatcher.CreateTimer();
        _controlsHideTimer.Interval = TimeSpan.FromSeconds(ControlsHideSeconds);
        _controlsHideTimer.IsRepeating = false;
        _controlsHideTimer.Tick += (_, _) => HideControlsIfIdle();
    }

    private void OnPlayerPointerEntered(object? sender, PointerEventArgs e)
    {
        // 鼠标进入播放框：控制层常亮（取消倒计时，等鼠标离开再重新计时）
        ShowControls();

        _btInfoHex = BtStreamService.ExtractInfoHash(_resolvedPlay?.Url);
        if (_bt == null || _btInfoHex == null) return; // 非 BT 播放不显示
        UpdateSpeedBadge();
        SpeedBadge.IsVisible = true;
        _speedTimer?.Start();
    }

    /// <summary>鼠标在播放框内移动：保持控制层可见（离开播放框才开始 3s 倒计时）</summary>
    /// <summary>顶栏避开状态栏：Edge-to-Edge 下页面从 y=0 起绘。取状态栏高度再上收 12dp——
    /// 完整 inset 会显得过低（用户实测反馈），留一点与状态栏的呼吸感更自然。
    /// 原地全屏隐藏 TopBarGrid 时边距随之消失，不留缝。</summary>
#if ANDROID
    /// <summary>顶栏避开状态栏：Edge-to-Edge 下页面从 y=0 起绘。取状态栏高度再上收 12dp——
    /// 完整 inset 会显得过低（用户实测反馈），留一点与状态栏的呼吸感更自然。
    /// 原地全屏隐藏 TopBarGrid 时边距随之消失，不留缝。</summary>
    private void ApplyTopBarInset() =>
        TopBarGrid.Margin = new Thickness(0, Math.Max(0, SafeAreaHelper.TopInset - 12), 0, 0);
#endif

    /// <summary>非全屏时的内容内边距。Windows 顶栏必须落在窗口标题栏按钮行（最小化/最大化/关闭，
    /// 绘制在内容之上、约占顶部 32px）之下，故顶部多留 30px，避免返回键/线路芯片与按钮重叠
    /// （2026-09-11 真机截图核对；同时使播放框与选集框整体下移）。</summary>
    private static Thickness NormalContentPadding =>
#if WINDOWS
        new(24, 44, 24, 28);
#else
        new(24, 14, 24, 28);
#endif

#if WINDOWS
    /// <summary>把顶栏中间的空白元素声明为窗口拖拽区（照抄猫爪音乐 Window.SetTitleBar 方案）：
    /// 只有该元素区域参与拖拽，返回键/标题/线路芯片照常可点，顶栏保持沉浸式。</summary>
    private void AttachTitleBarDragArea()
    {
        try
        {
            if (TitleBarDragArea?.Handler?.PlatformView is Microsoft.UI.Xaml.UIElement el)
                App.SetTitleBarDragElement(el);
        }
        catch { }
    }
#endif

    /// <summary>临时诊断：续看链路关键决策写文件（Windows 无控制台输出），
    /// 路径 %APPDATA%/CatClawVideo/watch-debug.log</summary>
    private static void WatchLog(string msg)
    {
        try
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CatClawVideo");
            Directory.CreateDirectory(dir);
            File.AppendAllText(System.IO.Path.Combine(dir, "watch-debug.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>续看定位：seek 到上次位置；若后端尚未可 seek（位置未生效）则按 300/900/1800ms 重试。</summary>
    private void TrySeekToResume(double pos, int retry)
    {
        var delay = retry switch { 0 => 300, 1 => 900, 2 => 1800, _ => 0 };
        if (delay == 0) return;
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(delay), () =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    if (Player.Duration > TimeSpan.Zero && pos > Player.Duration.TotalSeconds - 1) return;
                    // 已在目标附近（±15s）视为成功，不再重试
                    if (Math.Abs(Player.Position.TotalSeconds - pos) <= 15) return;
                    Player.Seek(TimeSpan.FromSeconds(pos));
                    TrySeekToResume(pos, retry + 1);
                }
                catch { }
            });
            return false;
        });
    }

    /// <summary>从历史读取本片续播点（首页/收藏等入口进入时也用）：
    /// 已看完（距结尾 &lt;20s）或位置过短不续；集名不同则仍按集恢复、位置交给 MediaOpened 校验。</summary>
    private async Task ApplyHistoryResumeAsync()
    {
        try
        {
            var h = await _db.FindHistoryAsync(_item.SourceKey, _item.Id);
            WatchLog($"[history] 查到={(h == null ? "无" : $"{h.Title}/{h.EpisodeName}/{h.RouteName}/{h.PositionSeconds:F0}s")}");
            if (h == null) return;
            if (h.PositionSeconds < 10) return;
            if (h.DurationSeconds > 0 && h.PositionSeconds > h.DurationSeconds - 20) return;
            _resumeEpisodeName = string.IsNullOrEmpty(h.EpisodeName) ? null : h.EpisodeName;
            _resumePosition = h.PositionSeconds;
            if (!string.IsNullOrEmpty(h.RouteName)) _resumeRouteName = h.RouteName;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Watch] 读取续播点失败: {ex.Message}");
        }
    }

    /// <summary>上一集/下一集：沿当前线路按索引跳集；越界提示。</summary>
    private void OnPrevEpisodeClicked(object? sender, EventArgs e) => SkipEpisode(-1);

    private void OnNextEpisodeClicked(object? sender, EventArgs e) => SkipEpisode(1);

    private void SkipEpisode(int delta)
    {
        if (_currentSourceIndex < 0 || _currentSourceIndex >= _sources.Count) return;
        var episodes = _sources[_currentSourceIndex].Episodes;
        var target = _currentEpisodeIndex + delta;
        if (target < 0 || target >= episodes.Count)
        {
            _ = ShowTipAsync(delta < 0 ? "已经是第一集了" : "已经是最后一集了");
            return;
        }
        PlayEpisodeByRow(_episodeRows[target]);
    }

    /// <summary>快退/快进 10s（拖动进度条外的快捷键位）。</summary>
    private void OnRewindClicked(object? sender, EventArgs e) => SeekRelative(-10);

    private void OnForwardClicked(object? sender, EventArgs e) => SeekRelative(10);

    private void SeekRelative(double deltaSeconds)
    {
        var target = Player.Position + TimeSpan.FromSeconds(deltaSeconds);
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (Player.Duration > TimeSpan.Zero && target > Player.Duration)
            target = Player.Duration - TimeSpan.FromSeconds(1);
        if (target < TimeSpan.Zero) return;
        Player.Seek(target);
        ShowControls();
        RestartControlsHideTimer();
    }

    private void OnPlayerPointerMoved(object? sender, PointerEventArgs e) => ShowControls();

    private void OnPlayerPointerExited(object? sender, PointerEventArgs e)
    {
        SpeedBadge.IsVisible = false;
        _speedTimer?.Stop();

        // 鼠标移出播放框：3s 后隐藏控制层（暂停/拖动中不隐藏）
        RestartControlsHideTimer();
    }

    private void UpdateSpeedBadge()
    {
        if (_bt == null || _btInfoHex == null) { SpeedBadge.IsVisible = false; return; }
        SpeedLabel.Text = FormatSpeed(_bt.GetDownloadSpeed(_btInfoHex));
    }

    private static string FormatSpeed(long bps) =>
        bps >= 1048576 ? $"{bps / 1048576.0:F1} MB/s"
        : bps >= 1024 ? $"{bps / 1024.0:F0} KB/s"
        : $"{bps} B/s";

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("title", out var t) && t is string title) _item.Title = title;
        if (query.TryGetValue("sourceKey", out var sk) && sk is string sourceKey)
        {
            _site.Key = sourceKey;
            _item.SourceKey = sourceKey;
        }
        if (query.TryGetValue("type", out var tp) && tp is string typeStr && int.TryParse(typeStr, out var type))
            _site.Type = type;
        if (query.TryGetValue("api", out var apiObj) && apiObj is string api) _site.Api = api;
        if (query.TryGetValue("itemId", out var idObj) && idObj is string itemId) _item.Id = itemId;
        if (query.TryGetValue("cover", out var cv) && cv is string cover && cover.Length > 0) _item.Cover = cover;
        if (query.TryGetValue("year", out var y) && y is string year && year.Length > 0) _item.Year = year;
        if (query.TryGetValue("remarks", out var r) && r is string remarks && remarks.Length > 0) _item.Remarks = remarks;
        if (query.TryGetValue("desc", out var d) && d is string desc && desc.Length > 0) _item.Description = desc;

        // 播放历史跳转携带的续看定位（选集名 + 上次位置）
        if (query.TryGetValue("resumeEp", out var re) && re is string resumeEp && resumeEp.Length > 0)
            _resumeEpisodeName = resumeEp;
        if (query.TryGetValue("pos", out var posObj) && posObj is string posStr &&
            double.TryParse(posStr, System.Globalization.CultureInfo.InvariantCulture, out var pos) && pos > 0)
            _resumePosition = pos;
        // 上次线路（续看优先恢复该线路；找不到则回退第一条）
        if (query.TryGetValue("route", out var rt) && rt is string routeName && routeName.Length > 0)
            _resumeRouteName = routeName;

        WatchLog($"[args] title={_item.Title} sourceKey={_item.SourceKey} itemId={_item.Id} " +
                 $"route={_resumeRouteName ?? "<null>"} ep={_resumeEpisodeName ?? "<null>"} pos={_resumePosition:F0}");

        TitleLabel.Text = _item.Title;
        TopBarTitle.Text = _item.Title;

        // 徽章：清晰度 / 年份 / 分类（无则隐藏）
        SetBadge(RemarksBadge, RemarksBadgeLabel, _item.Remarks);
        SetBadge(YearBadge, YearBadgeLabel, _item.Year);
        SetBadge(CategoryBadge, CategoryBadgeLabel, _item.Category);
        _argsReady.TrySetResult();

        MetaLabel.Text = $"来源：{_site.Name}{(_site.Name.Length > 0 ? " · " : "")}{_site.Key}";
        _descFull = _item.Description is { Length: > 0 } raw ? CleanDesc(raw) : "";
        _descExpanded = false;
        DescToggle.IsVisible = false;
        DescLabel.MaxLines = 2;
        DescLabel.Text = _descFull.Length > 0 ? _descFull : "暂无简介";

        // 简介排版诊断（临时）：记录原始/清洗后文本的不可见字符分布，定位空隙根因后移除
        try
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CatClawVideo");
            Directory.CreateDirectory(dir);
            var rawDesc = _item.Description ?? "";
            var odd = string.Concat(rawDesc.Where(c => c == '\n' || c == '\r' || c == '\u00a0' || c == '\u3000' || c == '\u200b' || c == '\ufeff')
                .Select(c => $"U+{(int)c:X4} "));
            File.WriteAllText(System.IO.Path.Combine(dir, "desc-debug.log"),
                $"[{DateTime.Now:HH:mm:ss}] 原始长度={rawDesc.Length} 清洗后长度={DescLabel.Text.Length} 特殊字符=[{odd}]\n");
        }
        catch { }

        // 回显收藏状态（收藏表查重）
        _ = LoadFavoriteStateAsync();
    }

    private static void SetBadge(Border badge, Label label, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) { badge.IsVisible = false; return; }
        label.Text = text;
        badge.IsVisible = true;
    }

    /// <summary>
    /// 播放器高度自适应：全屏 = 撑满整页（此时顶栏隐藏、ContentStack padding 为 0）；
    /// 常规 = 按播放器实际宽度取 16:9（clamp 200~560）。
    ///
    /// ⚠️ 必须能被显式调用：进入/退出全屏本身**不改变页面尺寸**（Android 基准方向即横屏，
    /// 全屏不再切方向），所以 OnSizeAllocated 不会触发 —— 只靠它会导致全屏后播放器仍是
    /// 16:9 高度、屏幕底部留出大片背景色（全屏没铺满）。
    /// </summary>
    private void ApplyPlayerHeight()
    {
        if (_isFullscreen)
        {
            PlayerHost.HeightRequest = Math.Max(200, Height);
            return;
        }
        // 页面宽 - 左右 padding(48) - 选集栏(300) - 列间距(16)
        var playerWidth = Width - 48 - 300 - 16;
        if (playerWidth > 100)
            PlayerHost.HeightRequest = Math.Clamp(playerWidth * 9.0 / 16.0, 200, 560);
    }

    /// <summary>布局完成/窗口缩放时重算播放器高度</summary>
    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        ApplyPlayerHeight();
    }

    /// <summary>简介清洗：HTML 实体解码 + &nbsp; 空段/连续空白折叠为单空格</summary>
    private static string CleanDesc(string text)
    {
        var decoded = System.Net.WebUtility.HtmlDecode(text);
        var cleaned = System.Text.RegularExpressions.Regex.Replace(decoded, @"[\s\u00a0\u3000]+", " ").Trim();
        return cleaned;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
#if WINDOWS
        HookEscKey(attach: true);
        // 顶栏拖拽区（SetTitleBar 指定元素；延迟到 Handler 就绪后再挂）
        AttachTitleBarDragArea();
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(250), () => { AttachTitleBarDragArea(); return false; });
#endif
        if (!_loaded)
        {
            _loaded = true;
            _ = LoadSourcesAsync();
        }
        else if (_playing)
        {
            Player.Play();
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
#if WINDOWS
        HookEscKey(attach: false);
        // 离开本页恢复系统默认标题栏（主页面用 AppWindow 拖拽矩形机制）；
        // 延迟到导航完成后重算，否则主页面的拖拽矩形会一直处于被清空状态
        App.SetTitleBarDragElement(null);
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(350), () =>
        {
            ((App)Application.Current!).RefreshTitleBarDragRegion();
            return false;
        });
#endif
        if (_isFullscreen) SetFullscreen(false);
        Player.Pause();
        _playing = false;
        UpdatePlayIcon();
        _speedTimer?.Stop();
        SpeedBadge.IsVisible = false;

        // 播放历史落库（观看页会话收尾）
        try { _playback.EndSession(Player.Position.TotalSeconds, Player.Duration.TotalSeconds); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Watch] 历史记录失败: {ex.Message}"); }
    }

    /// <summary>拉播放线路与选集（真数据），默认播第一线路第一集</summary>
    private async Task LoadSourcesAsync()
    {
        BufferingIndicator.IsVisible = true;
        try
        {
            _sources = await _provider.GetPlaySourcesAsync(_site, _item);

            // 兜底：旧源（v1 静态快照，id 为哈希）与现行 web 源（id 为文章 URL）的
            // id 方案不兼容，历史/收藏卡跳转会解析为空 → 按标题跨分类找回影片，
            // 用新 id 重新定位线路（源切换后的老记录自愈）
            if (_sources.Count == 0 && !string.IsNullOrWhiteSpace(_item.Title))
            {
                var found = await FindItemByTitleAsync(_item.Title);
                if (found is not null)
                {
                    _item = found;
                    _sources = await _provider.GetPlaySourcesAsync(_site, _item);
                }
            }

            // 线路芯片（右侧选集栏上方）：点击切换线路并重载选集；多线路时加"线路"前缀提示可切换
            LinesHost.Children.Clear();
            _lineChips.Clear();
            if (_sources.Count > 1)
                LinesHost.Children.Add(new Label
                {
                    Text = "线路",
                    FontSize = 10.5,
                    TextColor = (Color)Application.Current!.Resources["TextHintColor"],
                    VerticalOptions = LayoutOptions.Center,
                });
            for (int i = 0; i < _sources.Count; i++)
            {
                var index = i;
                var chip = new Border
                {
                    StrokeThickness = 0,
                    StrokeShape = new RoundRectangle { CornerRadius = 8 },
                    Padding = new Thickness(12, 5),
                    BackgroundColor = i == 0 ? (Color)Application.Current!.Resources["PrimaryColor"] : (Color)Application.Current!.Resources["ChipInactiveColor"],
                    VerticalOptions = LayoutOptions.Center,
                    Content = new Label
                    {
                        Text = DisplayNameFor(_sources[i].Name),
                        FontSize = 11.5,
                        TextColor = i == 0 ? Colors.White : (Color)Application.Current!.Resources["TextSecondaryColor"],
                    },
                };
                var tap = new TapGestureRecognizer();
                tap.Tapped += (_, _) => _ = SelectSourceAsync(index);
                chip.GestureRecognizers.Add(tap);
                LinesHost.Children.Add(chip);
                _lineChips.Add(chip);
            }

            if (_sources.Count > 0)
            {
                // 等参数就绪再决定续看（顺序无关；2s 兜底防死等）
                await Task.WhenAny(_argsReady.Task, Task.Delay(2000));

                WatchLog($"[load] sources={string.Join(",", _sources.Select(s => s.Name))} " +
                         $"before: route={_resumeRouteName ?? "<null>"} ep={_resumeEpisodeName ?? "<null>"} pos={_resumePosition:F0}");

                // 续看上下文（线路/集/位置）必须在选中线路之前解析：
                // 否则会先默认播线路1并把历史里的线路覆盖掉（2026-09-11 用户实测）
                if (_resumeRouteName is null && _resumeEpisodeName is null && _resumePosition <= 0)
                    await ApplyHistoryResumeAsync();

                // 续看优先恢复上次线路（按线路名匹配；找不到回退第一条）
                int startIndex = 0;
                if (_resumeRouteName is { Length: > 0 })
                {
                    var idx = _sources.FindIndex(s => string.Equals(s.Name.Trim(), _resumeRouteName.Trim(), StringComparison.Ordinal));
                    if (idx >= 0) startIndex = idx;
                }
                WatchLog($"[pick] startIndex={startIndex} name={_sources[startIndex].Name} applyResume=true");
                await SelectSourceAsync(startIndex, applyResume: true);
            }
            else
                await ShowTipAsync("该影片暂无可播放线路");
        }
        catch
        {
            await ShowTipAsync("线路加载失败");
        }
        finally
        {
            BufferingIndicator.IsVisible = false;
        }
    }

    /// <summary>
    /// 按标题跨分类查找影片（历史/收藏的旧 id 与现行源 id 方案不兼容时兜底）。
    /// 每分类最多翻 3 页，找到即返回；找不到返回 null。
    /// </summary>
    private async Task<VodItem?> FindItemByTitleAsync(string title)
    {
        try
        {
            var key = title.Replace(" ", "");
            foreach (var cat in await _provider.GetCategoriesAsync(_site))
            {
                for (var page = 1; page <= 3; page++)
                {
                    var items = await _provider.GetItemsAsync(_site, cat, page);
                    if (items.Count == 0) break;
                    var hit = items.FirstOrDefault(x =>
                        x.Title.Replace(" ", "").Contains(key, StringComparison.OrdinalIgnoreCase));
                    if (hit is not null) return hit;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>切换播放线路：重建右侧选集栏并播第一集</summary>
    /// <param name="applyResume">true = 页面首次加载：套用续看集/位置；
    /// false = 用户手动切线路：从该线路第一集起播，且不套用其它线路的续看进度。</param>
    private async Task SelectSourceAsync(int index, bool applyResume = false)
    {
        if (index < 0 || index >= _sources.Count) return;
        _currentSourceIndex = index;
        var source = _sources[index];

        // 切换线路立即同步到播放记录（线路名随下次落库生效，不依赖是否起播成功）
        _playback.SetRouteName(source.Name);

        // 线路芯片高亮（_lineChips 与 _sources 索引一一对应）
        for (int i = 0; i < _lineChips.Count; i++)
        {
            var chip = _lineChips[i];
            chip.BackgroundColor = i == index
                ? (Color)Application.Current!.Resources["PrimaryColor"]
                : (Color)Application.Current.Resources["ChipInactiveColor"];
            if (chip.Content is Label l)
                l.TextColor = i == index ? Colors.White : (Color)Application.Current.Resources["TextSecondaryColor"];
        }

        // 重建选集模型：全量行模型各挂一次高亮回调，可视件按当前页渲染（一页 20 集，2 列）
        EpisodeListHost.Children.Clear();
        _episodeRows.Clear();
        _episodePage = 0;
        for (int i = 0; i < source.Episodes.Count; i++)
        {
            var row = new EpisodeRow(i, source.Episodes[i].Name);
            row.CurrentChanged += () => ApplyRowHighlight(row);
            _episodeRows.Add(row);
        }
        RenderEpisodePage();

        EpisodeCountLabel.Text = source.Episodes.Count > 1
            ? $"共 {source.Episodes.Count} 集 · {DisplayNameFor(source.Name)}"
            : DisplayNameFor(source.Name);

        if (source.Episodes.Count > 0)
        {
            // 手动切线路：作废续看上下文（进度属于原线路的集），从新线路第一集起播
            if (!applyResume)
            {
                _resumeEpisodeName = null;
                _resumePosition = 0;
            }

            var resumeRow = applyResume && _resumeEpisodeName is { Length: > 0 }
                ? _episodeRows.FirstOrDefault(r =>
                      string.Equals(r.Name.Trim(), _resumeEpisodeName.Trim(), StringComparison.Ordinal))
                : null;
            PlayEpisodeByRow(resumeRow ?? _episodeRows[0]);
            if (applyResume && resumeRow == null)
            {
                // 找不到续看集（换线路选集名不同）：作废续看位置，避免误 seek
                _resumeEpisodeName = null;
                _resumePosition = 0;
            }
        }
        else
            _ = ShowTipAsync("该线路暂无选集");
    }

    /// <summary>选集行当前态样式（底色/文字色/序号块）</summary>
    private void HighlightRow(Border border, Label name, Border num, EpisodeRow row)
    {
        border.BackgroundColor = row.IsCurrent
            ? (Color)Application.Current!.Resources["PrimaryColor"]
            : (Color)Application.Current.Resources["CardBackgroundColor"];
        name.TextColor = row.IsCurrent ? Colors.White : (Color)Application.Current.Resources["TextSecondaryColor"];
        num.BackgroundColor = row.IsCurrent
            ? (Color)Application.Current.Resources["PrimaryColor"]
            : Color.FromArgb("#14FFFFFF");
        if (num.Content is Label numLabel)
            numLabel.TextColor = row.IsCurrent ? Colors.White : (Color)Application.Current.Resources["TextSecondaryColor"];
    }

    /// <summary>渲染当前分页的选集格（列数按集数自适应：1-3 列 × 10 行）并刷新翻页条可见性</summary>
    private void RenderEpisodePage()
    {
        if (_currentSourceIndex < 0 || _currentSourceIndex >= _sources.Count) return;
        var source = _sources[_currentSourceIndex];
        int count = source.Episodes.Count;
        int cols = EpisodeColumnsFor(count);
        int perPage = PerPageFor(count);
        int totalPages = (int)Math.Ceiling(count / (double)perPage);

        EpisodeListHost.Children.Clear();
        EpisodeListHost.RowDefinitions.Clear();
        EpisodeListHost.ColumnDefinitions.Clear();
        for (int c = 0; c < cols; c++)
            EpisodeListHost.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        _pageVisuals.Clear();

        int start = _episodePage * perPage;
        int end = Math.Min(count, start + perPage);
        for (int i = start; i < end; i++)
        {
            var row = _episodeRows[i];
            int slot = i - start;
            int r = slot / cols, c = slot % cols;
            if (c == 0) EpisodeListHost.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var (cell, border, name, num) = BuildEpisodeCell(i, source.Episodes[i], row);
            EpisodeListHost.Add(cell, c, r);
            _pageVisuals.Add((border, name, num, row));
            HighlightRow(border, name, num, row);
        }

        // 翻页条：≤1 页整条隐藏；首页无上一页、末页无下一页
        bool multi = totalPages > 1;
        EpisodePager.IsVisible = multi;
        if (multi)
        {
            PagerPrev.IsVisible = _episodePage > 0;
            PagerNext.IsVisible = _episodePage < totalPages - 1;
            PagerLabel.Text = $"{_episodePage + 1}/{totalPages}";
        }
    }

    /// <summary>构建单个选集格：卡片（序号块 + 集名，点击播放）；
    /// 磁力资源额外附"▶ 播放 / ⬇ 下载"按钮行（按钮在卡片手势区外，避免与卡片点击冲突）</summary>
    private (VisualElement Cell, Border Border, Label Name, Border Num) BuildEpisodeCell(
        int index, VodEpisode episode, EpisodeRow row)
    {
        var isMagnet = episode.Url?.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) == true;

        var border = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            Padding = new Thickness(8, 8),
            BindingContext = row,
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitionCollection
        {
            new ColumnDefinition(28), new ColumnDefinition(GridLength.Star),
        }, ColumnSpacing = 8 };
        var num = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 6 },
            BackgroundColor = Color.FromArgb("#14FFFFFF"),
            WidthRequest = 26, HeightRequest = 26, VerticalOptions = LayoutOptions.Center,
        };
        num.Content = new Label
        {
            Text = (index + 1).ToString(),
            FontSize = 11,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalOptions = LayoutOptions.Center,
            TextColor = (Color)Application.Current!.Resources["TextSecondaryColor"],
        };
        var nameLabel = new Label
        {
            FontSize = 12.5,
            VerticalOptions = LayoutOptions.Center,
            LineBreakMode = LineBreakMode.TailTruncation,
            Text = episode.Name,
            TextColor = (Color)Application.Current.Resources["TextSecondaryColor"],
        };
        grid.Add(num, 0);
        grid.Add(nameLabel, 1);
        border.Content = grid;
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => PlayEpisodeByRow(row);
        border.GestureRecognizers.Add(tap);

        VisualElement cell = border;

        // 磁力资源：卡片下方附"播放 / 下载"按钮行（在卡片手势识别区外，点击互不干扰）
        if (isMagnet)
        {
            var actions = new HorizontalStackLayout { Spacing = 6, HorizontalOptions = LayoutOptions.Center };
            actions.Add(BuildCellChip("▶", "播放", icPlay: true, () => PlayEpisodeByRow(row)));
            actions.Add(BuildCellChip("⬇", "下载", icPlay: false, () => _ = DownloadEpisodeAsync(episode)));
            var wrapper = new VerticalStackLayout { Spacing = 2 };
            wrapper.Add(border);
            wrapper.Add(actions);
            cell = wrapper;
        }

        return (cell, border, nameLabel, num);
    }

    /// <summary>选集格内的小操作 chip（图标 + 文字）</summary>
    private Border BuildCellChip(string icon, string text, bool icPlay, Action onTap)
    {
        var chip = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            BackgroundColor = (Color)Application.Current!.Resources["ChipInactiveColor"],
            Padding = new Thickness(10, 3),
            HorizontalOptions = LayoutOptions.Center,
            Content = new HorizontalStackLayout { Spacing = 4 },
        };
        var stack = (HorizontalStackLayout)chip.Content!;
        stack.Children.Add(new Image
        {
            Source = icPlay ? "ic_play.png" : "ic_download_white.png",
            WidthRequest = 10, HeightRequest = 10,
            VerticalOptions = LayoutOptions.Center,
        });
        stack.Children.Add(new Label
        {
            Text = text,
            FontSize = 10.5,
            TextColor = (Color)Application.Current.Resources["TextSecondaryColor"],
            VerticalOptions = LayoutOptions.Center,
        });
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => onTap();
        chip.GestureRecognizers.Add(tap);
        return chip;
    }

    /// <summary>磁力资源下载：入队下载管理器整包下载该磁力</summary>
    private async Task DownloadEpisodeAsync(VodEpisode episode)
    {
        if (_downloads == null)
        {
            await ShowTipAsync("下载管理器不可用");
            return;
        }
        var name = string.IsNullOrWhiteSpace(_item.Title) ? episode.Name : $"{_item.Title} {episode.Name}";
        _downloads.EnqueueMagnet(episode.Url!, name);
        await ShowTipAsync($"「{episode.Name}」已加入下载队列（设置 → 下载管理 查看）");
    }

    /// <summary>行高亮刷新入口：仅当前页已渲染的行有可视件（不在本页的行事件静默）</summary>
    private void ApplyRowHighlight(EpisodeRow row)
    {
        foreach (var v in _pageVisuals)
        {
            if (!ReferenceEquals(v.Row, row)) continue;
            HighlightRow(v.Border, v.Name, v.Num, row);
            return;
        }
    }

    private void OnPrevPageTapped(object? sender, EventArgs e)
    {
        if (_episodePage > 0)
        {
            _episodePage--;
            RenderEpisodePage();
        }
    }

    private void OnNextPageTapped(object? sender, EventArgs e)
    {
        if (_currentSourceIndex < 0 || _currentSourceIndex >= _sources.Count) return;
        int totalPages = (int)Math.Ceiling(_sources[_currentSourceIndex].Episodes.Count / (double)PerPageFor(_sources[_currentSourceIndex].Episodes.Count));
        if (_episodePage < totalPages - 1)
        {
            _episodePage++;
            RenderEpisodePage();
        }
    }

    /// <summary>选集行点击</summary>
    private async void PlayEpisodeByRow(EpisodeRow row)
    {
        if (_currentSourceIndex < 0 || _currentSourceIndex >= _sources.Count) return;
        var episodes = _sources[_currentSourceIndex].Episodes;
        if (row.Index < 0 || row.Index >= episodes.Count) return;

        // 行高亮切换
        foreach (var r in _episodeRows) r.IsCurrent = false;
        row.IsCurrent = true;
        _currentEpisodeIndex = row.Index;

        // 当前集不在本页时自动翻页（重渲染时按 IsCurrent 应用高亮）
        int target = row.Index / PerPageFor(episodes.Count);
        if (target != _episodePage)
        {
            _episodePage = target;
            RenderEpisodePage();
        }

        await PlayEpisodeAsync(episodes[row.Index]);
    }

    /// <summary>小窗播放指定集：统一走 ResolvePlayUrlAsync（web 源实时解析直链/BT 流式代理）</summary>
    private async Task PlayEpisodeAsync(VodEpisode episode)
    {
        // 播放历史续看：只有播的正是续看集才应用上次位置；换集则作废
        if (_resumeEpisodeName is { Length: > 0 } &&
            !string.Equals(episode.Name.Trim(), _resumeEpisodeName.Trim(), StringComparison.Ordinal))
        {
            _resumeEpisodeName = null;
            _resumePosition = 0;
        }

        _currentEpisode = episode;
        var generation = ++_playGeneration;
        ShowBuffering(true);
        try
        {
            var play = await _provider.ResolvePlayUrlAsync(_site, episode);
            if (generation != _playGeneration) return; // 已被后续点击取代，丢弃过期解析
            _resolvedPlay = play;
            Player.Source = play.Url;
            Player.Play();
            _playing = true;

            // 起播即落库一次：最后播放的影片立刻置顶历史第一位
            _playback.SaveProgress(0, 0);
            StartProgressAutoSave();

            // 起播后唤出控制层；鼠标离开播放框（或手指离开）即 3s 后自动隐藏
            ShowControls();
            RestartControlsHideTimer();

            // 播放历史落库（BT 代理地址是会话内瞬态链接，重启后失效，不落库）。
            // 带上来源定位与集名：历史卡才能跳回观看页详情并自动选中该集续看。
            // 标题只存影片名（不含集名）——历史按影片合并，集名单独落 EpisodeName 列。
            if (!play.Url.Contains("/stream/", StringComparison.OrdinalIgnoreCase))
                _playback.BeginSession(_item.Title, play.Url, _item.Cover,
                    sourceKey: _site.Key, itemType: _site.Type, itemApi: _site.Api,
                    itemId: _item.Id, episodeName: episode.Name,
                    category: _item.Category, year: _item.Year,
                    remarks: _item.Remarks, description: _item.Description,
                    routeName: _sources[_currentSourceIndex].Name);
        }
        catch (NotSupportedException ex)
        {
            if (generation == _playGeneration) await ShowTipAsync(ex.Message);
        }
        catch
        {
            if (generation == _playGeneration) await ShowTipAsync("该集解析失败，请换集或换线路");
        }
        finally
        {
            if (generation == _playGeneration)
            {
                ShowBuffering(false);
                UpdatePlayIcon();
            }
        }
    }

    private void ShowBuffering(bool on) => BufferingIndicator.IsVisible = on;

    /// <summary>线路展示名兼容映射：存量静态源数据里的"磁力下载"统一显示为"磁力播放"（点击即 BT 流式播放）</summary>
    private static string DisplayNameFor(string name) =>
        name == "磁力下载" ? "磁力播放" : name;

    private void OnBackTapped(object? sender, TappedEventArgs e) => Shell.Current.GoToAsync("..");

    private DateTime _lastSurfaceTapTime = DateTime.MinValue;
    private System.Threading.CancellationTokenSource? _singleTapCts;

    private void OnSurfaceTapped(object? sender, TappedEventArgs e)
    {
        // 单击 = 唤出控制层（延迟 300ms 执行，给双击留判定窗口）；
        // 双击 = 取消单击动作，切换播放/暂停（2026-09-11 用户要求：
        // 原先单击即切播放，翻控制层时总误触暂停）。
        var now = DateTime.Now;
        if ((now - _lastSurfaceTapTime).TotalMilliseconds <= 300)
        {
            _lastSurfaceTapTime = DateTime.MinValue;
            _singleTapCts?.Cancel();
            _singleTapCts = null;
            OnPlayPauseClicked(sender, e);
            return;
        }

        _lastSurfaceTapTime = now;
        _singleTapCts?.Cancel();
        _singleTapCts = new System.Threading.CancellationTokenSource();
        var cts = _singleTapCts;
        Task.Run(async () =>
        {
            try { await Task.Delay(300, cts.Token); }
            catch (TaskCanceledException) { return; }
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (cts.IsCancellationRequested) return;
                ShowControls();
                RestartControlsHideTimer();
            });
        });
    }

    private IDispatcherTimer? _progressSaveTimer;

    /// <summary>播放中每 10s 落库一次进度（异常退出/杀进程也能续播上次位置）</summary>
    private void StartProgressAutoSave()
    {
        _progressSaveTimer ??= Dispatcher.CreateTimer();
        _progressSaveTimer.Interval = TimeSpan.FromSeconds(10);
        _progressSaveTimer.Tick -= OnProgressSaveTick;
        _progressSaveTimer.Tick += OnProgressSaveTick;
        _progressSaveTimer.Start();
    }

    private void OnProgressSaveTick(object? sender, EventArgs e)
    {
        if (!_playing) return;
        _playback.SaveProgress(Player.Position.TotalSeconds, Player.Duration.TotalSeconds);
    }

    private void OnPlayPauseClicked(object? sender, EventArgs e)
    {
        if (_playing)
        {
            Player.Pause();
            _playing = false;
            // 暂停即落库：切走/杀进程也不丢进度
            _playback.SaveProgress(Player.Position.TotalSeconds, Player.Duration.TotalSeconds);
        }
        else
        {
            Player.Play();
            _playing = true;
        }
        UpdatePlayIcon();

        // 动作后控制层保持可见：暂停时常驻，播放中则 3s 后隐藏
        ShowControls();
        RestartControlsHideTimer();
    }

    /// <summary>简介展开/收起</summary>
    private void OnDescToggleTapped(object? sender, TappedEventArgs e)
    {
        _descExpanded = !_descExpanded;
        DescLabel.MaxLines = _descExpanded ? int.MaxValue : 2;
        DescToggle.Text = _descExpanded ? "收起 ▴" : "展开 ▾";
    }

    /// <summary>收藏/取消收藏（VideoDatabase favorites 表，按 sourceKey+itemId 查重）</summary>
    private async void OnFavoriteClicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_item.SourceKey) || string.IsNullOrEmpty(_item.Id))
        {
            await ShowTipAsync("该影片暂不支持收藏");
            return;
        }
        try
        {
            var existing = await _db.FindFavoriteAsync(_item.SourceKey, _item.Id);
            if (existing != null)
            {
                await _db.RemoveFavoriteAsync(existing);
                FavoriteButton.Text = "收藏";
                await ShowTipAsync("已取消收藏");
            }
            else
            {
                await _db.AddFavoriteAsync(_item);
                FavoriteButton.Text = "已收藏";
                await ShowTipAsync("已加入收藏");
            }
        }
        catch
        {
            await ShowTipAsync("收藏操作失败");
        }
    }

    /// <summary>进入页面时回显收藏状态</summary>
    private async Task LoadFavoriteStateAsync()
    {
        if (string.IsNullOrEmpty(_item.SourceKey) || string.IsNullOrEmpty(_item.Id)) return;
        try
        {
            var existing = await _db.FindFavoriteAsync(_item.SourceKey, _item.Id);
            FavoriteButton.Text = existing != null ? "已收藏" : "收藏";
        }
        catch { }
    }

    /// <summary>分享当前播放链接</summary>
    private async void OnShareClicked(object? sender, EventArgs e)
    {
        var url = _currentEpisode?.Url ?? "";
        if (url.Length == 0) return;
        try
        {
            await Microsoft.Maui.ApplicationModel.DataTransfer.Share.RequestAsync(
                new Microsoft.Maui.ApplicationModel.DataTransfer.ShareTextRequest
                {
                    Title = _item.Title,
                    Text = $"{_item.Title} · {_currentEpisode?.Name}\n{url}",
                });
        }
        catch { }
    }

    private async Task ShowTipAsync(string message)
    {
        try
        {
            await DisplayAlertAsync("提示", message, "确定");
        }
        catch { }
    }

    /// <summary>全屏切换：同一播放器实例原地放大铺满（不新开页面、不重新拉流、进度天然连续）</summary>
    private void OnToggleFullscreenClicked(object? sender, EventArgs e) => SetFullscreen(!_isFullscreen);

    /// <summary>选集面板高度对齐播放框：MaximumHeightRequest 取播放框（Border）的
    /// **实际渲染高度**——名义 340 会因圆角/描边/测量差异留下 ~20dp 落差（2026-09-11 真机实测）。</summary>
    private void SyncSidebarHeight()
    {
        try
        {
            if (PlayerHost.Parent is VisualElement box && box.Height > 0)
                SidebarPanel.MaximumHeightRequest = box.Height;
        }
        catch { }
    }

    /// <summary>原地全屏：隐藏顶栏/选集/信息区，播放器铺满整页；窗口切 FullScreen（Win）/ 横屏（Android）</summary>
    private void SetFullscreen(bool on)
    {
        _isFullscreen = on;
        TopBarGrid.IsVisible = !on;
        SidebarPanel.IsVisible = !on;
        InfoArea.IsVisible = !on;
        RecommendHint.IsVisible = !on;
        MainArea.ColumnDefinitions = on
            ? new ColumnDefinitionCollection { new ColumnDefinition(GridLength.Star) }
            : new ColumnDefinitionCollection { new ColumnDefinition(GridLength.Star), new ColumnDefinition(300) };
        ContentStack.Padding = on ? new Thickness(0) : NormalContentPadding;
        FullScreenButton.Source = on ? "ic_fullscreen_exit.png" : "ic_fullscreen.png";
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(120), () => { SyncSidebarHeight(); return false; });
        if (on) _ = ContentScroll.ScrollToAsync(0, 0, false);
#if WINDOWS
        App.SetWindowFullscreen(on);
#elif ANDROID
        // Android 基准方向已是横屏（MainActivity ScreenOrientation=SensorLandscape），
        // 全屏进出都保持横屏 → 此处不再改方向。
        // （原先退全屏调 ReleaseLandscape() 会切 SensorPortrait，基准改横屏后会把整个 App 掰成竖屏；
        //   切竖屏是播放页旋转按钮的职责。）
        // 全屏时隐藏系统栏（状态栏 + 导航栏），退出全屏恢复
        CatClawVideo.Maui.MainActivity.SetImmersive(on);
#endif

        // 显式重算播放器高度：进/退全屏不改变页面尺寸，OnSizeAllocated 不会触发，
        // 否则全屏后播放器仍停留在 16:9 高度、底部留出大片背景色。
        ApplyPlayerHeight();

        // 切换全屏后唤出控制层；鼠标移出播放框即按 3s 倒计时隐藏
        ShowControls();
    }

#if WINDOWS
    /// <summary>Esc 退出全屏：挂到原生窗口 Content 根元素（与 MainPage 键盘导航同通道）</summary>
    private void HookEscKey(bool attach)
    {
        try
        {
            var native = (Window?.Handler?.PlatformView as Microsoft.UI.Xaml.Window)
                ?? (Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Microsoft.UI.Xaml.Window);
            if (native?.Content is Microsoft.UI.Xaml.UIElement root)
            {
                root.KeyDown -= OnPlatformKeyDown;
                if (attach) root.KeyDown += OnPlatformKeyDown;
            }
        }
        catch { }
    }

    private void OnPlatformKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && _isFullscreen)
        {
            e.Handled = true;
            MainThread.BeginInvokeOnMainThread(() => SetFullscreen(false));
        }
    }
#endif

    private void UpdatePlayIcon()
    {
        var icon = _playing ? "ic_pause.png" : "ic_play.png";
        CenterPlayButton.Source = icon;
        SmallPlayButton.Source = icon;
    }

    private void UpdateProgress()
    {
        if (_seeking) return;
        var total = Player.Duration.TotalSeconds;
        PositionLabel.Text = FormatTime(Player.Position);
        DurationLabel.Text = total > 0 ? FormatTime(Player.Duration) : "--:--";
        if (total > 0)
            ProgressBar.Value = Player.Position.TotalSeconds / total * 100;
    }

    private void OnSeekStarted(object? sender, EventArgs e)
    {
        // 拖动进度中：控制层常驻，倒计时作废
        _seeking = true;
        _controlsHideTimer?.Stop();
    }

    private void OnSeekCompleted(object? sender, EventArgs e)
    {
        _seeking = false;
        var total = Player.Duration.TotalSeconds;
        if (total > 0)
            Player.Seek(TimeSpan.FromSeconds(ProgressBar.Value / 100 * total));

        // 松手后重新开始 3s 倒计时
        RestartControlsHideTimer();
    }

    // ════════════════ 控制层自动隐藏 ════════════════

    /// <summary>显示控制层并取消倒计时（鼠标仍在播放框内时保持常亮）</summary>
    private void ShowControls()
    {
        _controlsHideTimer?.Stop();
        SetControlsVisible(true);
    }

    /// <summary>从当前时刻起重新计时 3s（鼠标移出播放框 / 手指离开 / 拖动结束时调用）</summary>
    private void RestartControlsHideTimer()
    {
        _controlsHideTimer?.Stop();
        if (CanAutoHideControls()) _controlsHideTimer?.Start();
    }

    /// <summary>仅"播放中且非拖动"才自动隐藏——暂停/拖动时常驻，否则进度条与时长都看不见</summary>
    private bool CanAutoHideControls() => _playing && !_seeking;

    private void HideControlsIfIdle()
    {
        if (CanAutoHideControls()) SetControlsVisible(false);
    }

    /// <summary>
    /// 控制层显隐：只切换子元素，ControlsOverlay 本身常驻不隐藏。
    /// 该层承载指针手势与画面点击，若整体隐藏则手势随之失效，控件层将无法被再次唤出。
    /// </summary>
    private void SetControlsVisible(bool on)
    {
        ControlBar.IsVisible = on;
        CenterPlayButton.IsVisible = on;
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

    /// <summary>右侧选集栏行模型（IsCurrent 驱动高亮）</summary>
    public sealed class EpisodeRow(int index, string name) : INotifyPropertyChanged
    {
        public int Index { get; } = index;
        public string Name { get; } = name;

        private bool _isCurrent;
        public bool IsCurrent
        {
            get => _isCurrent;
            set
            {
                if (_isCurrent == value) return;
                _isCurrent = value;
                CurrentChanged?.Invoke();
            }
        }

        /// <summary>高亮样式刷新回调（MAUI 无双向样式绑定，代码后置挂接）</summary>
        public event Action? CurrentChanged;
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
