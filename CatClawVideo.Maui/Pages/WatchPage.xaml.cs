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
public partial class WatchPage : ContentPage, IQueryAttributable, IRemoteKeyHandler, Services.IWindowDragArea
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

    /// <summary>最近一次 MediaOpened 的时刻（过滤"起播瞬间的伪 ended"，见 MediaEnded 守卫）</summary>
    private DateTime _mediaOpenedUtc;

    /// <summary>线路芯片（SelectSource 高亮用；LinesHost 首位可能是"线路"前缀标签）</summary>
    private readonly List<Border> _lineChips = [];

    /// <summary>播放历史会话（观看页播放也落库，海报墙封面来自 _item.Cover）</summary>
    private readonly VideoPlaybackManager _playback;

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

    public WatchPage(IVodSourceProvider provider, VideoDatabase db, VideoPlaybackManager playback)
    {
        InitializeComponent();
        _provider = provider;
        _db = db;
        _playback = playback;

        // 控件条随播放框尺寸自适应：手机端内嵌小窗里，桌面尺寸的按钮会占掉大半个画面。
        // 用控件条自身宽度（= 播放框宽度）推算，缩放不会反向影响宽度，故不会触发循环。
        ControlBar.SizeChanged += (_, _) =>
            ControlBar.UiScale = Controls.PlaybackControlBar.ScaleFor(ControlBar.Width);

#if ANDROID
        // 顶栏避开状态栏：Edge-to-Edge 下页面从 y=0 起绘，返回按钮会顶进状态栏
        // （2026-09-11 真机实测）。状态栏高度动态取自 SafeAreaHelper；
        // 原地全屏隐藏 TopBarGrid 时边距随之消失，不留缝。
        ApplyTopBarInset();
        SafeAreaHelper.SafeAreaChanged += (_, _) =>
            MainThread.BeginInvokeOnMainThread(ApplyTopBarInset);
#endif

        ContentStack.Padding = NormalContentPadding;

        // 控件条（方案 B）：接线到本页已有的操作，保持行为不变
        ControlBar.ShowEpisodesButton = true;
        ControlBar.ShowEpisodeStepButtons = true;
        ControlBar.PlayPauseRequested += (_, _) => OnPlayPauseClicked(this, EventArgs.Empty);
        ControlBar.PrevEpisodeRequested += (_, _) => SkipEpisode(-1);
        ControlBar.NextEpisodeRequested += (_, _) => SkipEpisode(1);
        ControlBar.RewindRequested += (_, _) => SeekRelative(-10);
        ControlBar.ForwardRequested += (_, _) => SeekRelative(10);
        ControlBar.EpisodesRequested += (_, _) => ToggleFullscreenForEpisodes();
        ControlBar.FullscreenRequested += (_, _) => OnToggleFullscreenClicked(this, EventArgs.Empty);
        ControlBar.SeekStarted += (_, _) => OnSeekStarted(this, EventArgs.Empty);
        ControlBar.SeekCompleted += (_, _) => OnSeekCompleted(this, EventArgs.Empty);
        ControlBar.SeekRequested += (_, seconds) => OnSeekRequested(seconds);
        ControlBar.SpeedRequested += (_, _) => CycleSpeed();
        ControlBar.MuteChanged += (_, muted) => ApplyMute(muted);

        Player.PositionChanged += (_, _) => MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateProgress();
            TickBufferingIndicator();   // 先在「真的在播」时收起指示器（MF 会误报 Buffering）
            UpdateBufferProgress();     // 仍在缓冲：刷新百分比
        });

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
            _mediaOpenedUtc = DateTime.UtcNow;
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
        Player.StateChanged += (_, _) => MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdatePlayIcon();

            // 缓冲态变化：进入缓冲显示指示器，恢复播放收起。
            var state = Player.CurrentState;
            if (state == VideoPlayerState.Buffering)
            {
                // ⚠ MF 对磁力流会在**正常播放时反复报 Buffering**：位置已在推进的话
                //   （_playbackAdvancing）不得重新唤起指示器，否则会「收起→又弹出」闪烁。
                if (!_playbackAdvancing) BeginBuffering();
            }
            else if (state == VideoPlayerState.Playing)
            {
                ShowBuffering(false);
            }
            else if (state is VideoPlayerState.Paused or VideoPlayerState.Stopped)
            {
                _playbackAdvancing = false;     // 暂停/停止：下次缓冲仍需重新判定
            }
        });
        // 自然播完 → 自动连播下一集。⚠ 提示不能用 ShowTipAsync（模态弹窗会卡住流程，
        // 用户点确定才换集——2026-09-17 用户实测反馈），静默 3s 后直接换，期间手动换集则放弃。
        // ⚠ 换集/重开瞬间会收到旧流拆除的残留 MediaEnded（实测引发连环跳集）：
        //   播放位置离片尾超过 15s 的 "ended" 一律忽略。
        // ⚠ Duration 未知（≤0）也忽略：MP4 的 moov / MKV 的 Cues 在文件尾，起播探测拿不到时
        //   FFmpeg frames:0 → 播放器报时长 0 → 刚挂上就 "ended"（进度条满格 + 秒跳下一集，实测）。
        // ⚠ 起播后 10s 内的 ended 一律忽略（2026-09-17 实测真凶）：Cues 探测失败被代理回 416 时
        //   FFmpeg 把 MKV 解封装视作 EOF（"File ended prematurely"）→ MediaEnded 立刻到达，
        //   而 Position/Duration 都是小值、守卫 `Position >= Duration-15s` 天然成立 → 每 30s 跳一集连环换集。
        //   自然播完绝不可能发生在起播 10s 内，故以此为准入门槛。
        Player.MediaEnded += (_, _) => MainThread.BeginInvokeOnMainThread(async () =>
        {
            // ⚠ 先判守卫再改状态：此前无条件 `_playing = false; UpdatePlayIcon();` 写在守卫之前，
            //   导致被忽略的伪 MediaEnded 仍把 UI 钉在暂停态、进度条停在假片尾（2026-09-17 压测实测）。
            if (Player.Duration <= TimeSpan.Zero) return;          // 时长未知：起播失败态，绝非自然播完
            if (_mediaOpenedUtc != default && (DateTime.UtcNow - _mediaOpenedUtc).TotalSeconds < 10)
            {
                BtFileLog.Write($"[player] 忽略起播 {(DateTime.UtcNow - _mediaOpenedUtc).TotalSeconds:F1}s 的伪 MediaEnded（Position={Player.Position.TotalSeconds:F1}s Duration={Player.Duration.TotalSeconds:F1}s）");
                return;
            }
            if (Player.Position < Player.Duration - TimeSpan.FromSeconds(15))
                return;   // 残留事件：位置远未到片尾
            _playing = false;
            UpdatePlayIcon();
            if (_currentSourceIndex < 0 || _currentSourceIndex >= _sources.Count) return;
            var endedIndex = _currentEpisodeIndex;
            var next = endedIndex + 1;
            if (next < 0 || next >= _sources[_currentSourceIndex].Episodes.Count)
            {
                try { await ShowTipAsync("已经是最后一集了"); } catch { }
                return;
            }
            await Task.Delay(3000);
            if (_currentEpisodeIndex != endedIndex || _currentSourceIndex < 0 || _currentSourceIndex >= _sources.Count) return;
            SkipEpisode(1);
        });
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

        // 内置 BT 已移除 → 网速徽章暂无数据源（磁力改走迅雷引擎；引擎本身有 speed 上报，后续接上即可）
        _btInfoHex = null;
        SpeedBadge.IsVisible = false;
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

    /// <summary>
    /// 本页自管窗口拖拽区（<c>App.SyncTitleBarDrag</c> 在每次导航后调用）。
    ///
    /// <para>返回 <c>true</c> = 已接管。不接管的话 App 会把拖拽区**清零** ——
    /// 播放页不在它的默认分支里（那里只认主页），所以在 OnAppearing 里设是白设。</para>
    ///
    /// <para>⚠ 本方法**不能**放进 <c>#if WINDOWS</c>：接口是全平台编译的，
    /// Android 下缺实现会直接编译失败（2026-09-19 实测）。平台判定放在方法体里。</para>
    /// </summary>
    public bool ApplyWindowDragArea()
    {
#if WINDOWS
        // 全屏选集模式下顶栏被隐藏：此时不声明拖拽区，交回 App 默认规则
        if (!TopBarVisible) return false;

        AttachTitleBarDragArea();
        return true;
#else
        return false;
#endif
    }

#if WINDOWS
    /// <summary>把顶栏的空白段声明为窗口拖拽区（整条顶栏按横向补集切段）：
    /// 只有空白参与拖拽，返回键/标题/线路芯片照常可点，顶栏保持沉浸式。</summary>
    private void AttachTitleBarDragArea()
    {
        try
        {
            // 整条顶栏的空白段都可拖（含返回按钮上方、线路芯片上下的留白），
            // 控件本身（返回 / 标题 / 线路芯片）仍归客户区、照常可点。
            Services.WindowDragHelper.AttachStrip(
                TopBarGrid?.Handler?.PlatformView as Microsoft.UI.Xaml.FrameworkElement,
                TitleBarDragArea?.Handler?.PlatformView as Microsoft.UI.Xaml.FrameworkElement,
                BackButtonHost?.Handler?.PlatformView as Microsoft.UI.Xaml.FrameworkElement,
                TopBarTitle?.Handler?.PlatformView as Microsoft.UI.Xaml.FrameworkElement,
                LinesHost?.Handler?.PlatformView as Microsoft.UI.Xaml.FrameworkElement);
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
            var dir = CatClawVideo.Core.AppPaths.DataRoot;
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

    private void SeekRelative(double deltaSeconds)
    {
        var from = Player.Position.TotalSeconds;
        var target = Player.Position + TimeSpan.FromSeconds(deltaSeconds);
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (Player.Duration > TimeSpan.Zero && target > Player.Duration)
            target = Player.Duration - TimeSpan.FromSeconds(1);
        if (target < TimeSpan.Zero) return;

        // 前进到**未缓冲**区域才提示缓冲进度（后退/已在缓冲区内由播放器瞬时完成，
        // 弹指示器反而闪一下）；判断用未抬升的原始前沿。
        var targetSec = target.TotalSeconds;
        if (deltaSeconds > 0 && targetSec > Player.BufferedFrontier.TotalSeconds + 0.5)
        {
            _playbackAdvancing = false;     // 跳到新区间：需要重新判定是否已在播
            BeginBuffering(from, targetSec);
        }

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
        // 网速数据源（内置 BT）已移除：该位置改用于**倍速提示**（非 1.0 时常驻）。
        SpeedBadge.IsVisible = _playing && Math.Abs(Player.Speed - 1.0) > 0.01;
        if (SpeedBadge.IsVisible) SpeedLabel.Text = $"{Player.Speed:0.0#}× 播放";
    }

    /// <summary>
    /// 循环切换播放速率（0.5 → 0.75 → 1.0 → 1.25 → 1.5 → 2.0 → 3.0 → 0.5…）。
    ///
    /// <para>用「按钮点击循环」而不是弹菜单：遥控器上左右键即可调，无需进入二级面板
    /// （见设置页那套遥控优先原则）。速率显示在按钮上（倍速时同时浮一个徽章提示）。</para>
    /// </summary>
    private void CycleSpeed()
    {
        var presets = VideoPlayerView.SpeedPresets;
        var idx = Array.FindIndex(presets, p => Math.Abs(p - Player.Speed) < 0.01);
        var next = presets[(idx + 1) % presets.Length];   // idx = -1（非档位值）→ 回到首档

        Player.Speed = next;
        ControlBar.SpeedValue = next;

        // 非 1.0 时浮一下倍数提示，让用户确认已生效
        if (_playing && next != 1.0) ShowSpeedBadge(next);
        else HideSpeedBadge();
    }

    /// <summary>
    /// 应用静音（音量归零 / 恢复）。
    /// <para>记住静音前的音量：直接置 0 再置 1.0 会丢失用户原本的音量设置，
    /// 且从静音恢复时若原本就是 0 会「恢复后仍然没声音」。</para>
    /// </summary>
    private void ApplyMute(bool muted)
    {
        if (muted)
        {
            if (Player.Volume > 0) _volumeBeforeMute = Player.Volume;
            Player.Volume = 0;
        }
        else
        {
            Player.Volume = _volumeBeforeMute > 0 ? _volumeBeforeMute : 1.0;
        }
        ControlBar.IsMuted = muted;   // 回填，保证按钮文案与实际一致
    }

    /// <summary>静音前的音量（恢复用）。</summary>
    private double _volumeBeforeMute = 1.0;

    /// <summary>显示倍速徽章（如「1.5×」）。</summary>
    private void ShowSpeedBadge(double speed)
    {
        SpeedLabel.Text = $"{speed:0.0#}× 播放";
        SpeedBadge.IsVisible = true;
    }

    private void HideSpeedBadge() => SpeedBadge.IsVisible = false;

    private static string FormatSpeed(long bps) =>
        bps >= 1048576 ? $"{bps / 1048576.0:F1} MB/s"
        : bps >= 1024 ? $"{bps / 1024.0:F0} KB/s"
        : $"{bps} B/s";

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("title", out var t) && t is string title) _item.Title = title;
        // ⚠️ 路由只带 Key/Type/Api 三个字段，而 spider 站点能否被路由**取决于
        // VodSiteInfo.SpiderKind + Jar**（SpiderVodProvider.CanHandle）。用路由参数重拼
        // 站点对象会让所有 type=3 爬虫站恒判定为「无可用适配器」，详情页必然报
        // 「线路加载失败」——所以优先从站点仓库取回订阅解析出的**完整**站点对象。
        // 取不到（订阅被删/历史残留）才退回路由参数拼装。
        if (query.TryGetValue("sourceKey", out var sk) && sk is string sourceKey && sourceKey.Length > 0)
        {
            var fromRegistry = SiteRegistry.Find(sourceKey);
            if (fromRegistry is not null)
                _site = fromRegistry;
            else
            {
                _site.Key = sourceKey;
                if (query.TryGetValue("type", out var tp0) && tp0 is string ts0 && int.TryParse(ts0, out var t0))
                    _site.Type = t0;
                if (query.TryGetValue("api", out var api0) && api0 is string a0) _site.Api = a0;
            }
            _item.SourceKey = sourceKey;
        }
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
        ControlBar.Title = _item.Title;

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
            var dir = CatClawVideo.Core.AppPaths.DataRoot;
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
    /// <summary>上次已应用的播放器高度（避免同值重复赋值触发无谓的布局往返）。</summary>
    private double _appliedPlayerHeight = -1;

    /// <summary>播放器下方信息区占用的垂直空间估算（顶栏 + 信息区 + 各段间距）。</summary>
    private const double ReservedVerticalSpace = 300;

    private void ApplyPlayerHeight()
    {
        double target;
        if (_isFullscreen)
        {
            target = Math.Max(200, Height);
        }
        else
        {
            // 页面宽 - 左右 padding(48) - 选集栏(300) - 列间距(16)
            var playerWidth = Width - 48 - 300 - 16;
            if (playerWidth <= 100) return;                     // 尺寸还没就绪：保持原值

            // ⚠ 这里**不能**用固定上限（原为 clamp(…, 200, 560)，2026-09-19 用户实测
            //   「窗口最大化后比例很怪」）：1920 宽时播放器可用宽约 1556，16:9 理想高度
            //   应为 ~875，被压到 560 后 AspectFit 只能按高度适配 —— 视频缩成中间窄条、
            //   左右各留一大片黑边，看着就是「比例不对」。
            //
            //   正确做法：优先保证 16:9（无黑边），只在「窗口太矮、放不下」时才收缩；
            //   放不下时页面本来就可滚动，不必为了塞进视口而牺牲画面比例。
            var ideal = playerWidth * 9.0 / 16.0;
            var available = Height - ReservedVerticalSpace;
            target = available > 240
                ? Math.Clamp(ideal, 200, available)     // 视口够：取 16:9，但不超出可视高度
                : ideal;                                // 视口还没量准 / 窗口很矮：直接 16:9（靠滚动查看）
        }

        // ⚠ 同值不重复赋值：HeightRequest 变化会反过来触发 OnSizeAllocated → 再次进入本方法，
        //   不加这道守卫就可能形成「布局 → 改高度 → 再布局」的抖动循环。这类窗口/画面帧的
        //   持续抖动会被 GPU 覆盖层（NVIDIA 等）当作全屏状态变化，导致其提示浮窗反复闪烁
        //   （2026-09-19 用户实测）。
        if (Math.Abs(target - _appliedPlayerHeight) < 0.5) return;
        _appliedPlayerHeight = target;
        PlayerHost.HeightRequest = target;

        // 播放器高度变了 → 选集栏必须跟着等高，否则窗口放大后选集框还是旧高度、明显不齐。
        // 延后一拍：此刻新高度尚未完成布局，Border 的 Height 还是旧值。
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(50), () => { SyncSidebarHeight(); return false; });
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

        // 接管方向键（本页是整窗推送页，键盘栈顶只它一个消费者）
        RemoteKeyRouter.Push(this);

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
        RemoteKeyRouter.Pop(this);

#if WINDOWS
        HookEscKey(attach: false);
        // 离开本页：先解绑本页拖拽元素，再延迟按当前页面重设
        //（返回主页后要交回主页顶栏的空白段；延迟是为了等导航真正完成）
        Services.WindowDragHelper.Detach();
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(350), () =>
        {
            ((App)Application.Current!).SyncTitleBarDrag();
            return false;
        });
#endif
        if (_isFullscreen) SetFullscreen(false);
        EpisodesOverlay.IsVisible = false;   // 离开页面：浮层复位（页面实例可能被复用）
        Player.Pause();
        _playing = false;
        UpdatePlayIcon();
        _speedTimer?.Stop();
        SpeedBadge.IsVisible = false;

        // 播放历史落库（观看页会话收尾）
        try { _playback.EndSession(Player.Position.TotalSeconds, Player.Duration.TotalSeconds); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Watch] 历史记录失败: {ex.Message}"); }

        // 退出播放页 = 本次播放结束：通知磁力引擎收尾。
        // 迅雷 P2SP 任务交完播放地址后仍会自己下个不停，宿主只能靠「页面退出」这个信号叫停；
        // QEMU 引擎收到后冻结 VM（下载立刻停，任务与已下载数据保留，回来点同一剧可续）。
        // 地址不匹配（非磁力播放 / 已被别的页面接管）时引擎内部会自行忽略。
        try
        {
            (MagnetEngines.Thunder as IPlaybackSessionLease)?.ReleasePlaybackSession(_resolvedPlay?.Url);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Watch] 磁力会话收尾失败: {ex.Message}"); }
    }

    /// <summary>拉播放线路与选集（真数据），默认播第一线路第一集。
    /// <para>磁力站走**流式**加载：先上屏未展开选集，随后每条磁力展开完成再刷新，
    /// 避免「8 条打包磁力全串行探测 ~8s 期间选集栏空白」的慢感。</para></summary>
    private async Task LoadSourcesAsync()
    {
        BufferingIndicator.IsVisible = true;
        try
        {
            if (_provider is IProgressiveVodSourceProvider progressive)
            {
                await LoadSourcesProgressiveAsync(progressive);
                return;
            }

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

            await ApplySourcesAsync();
        }
        catch (Exception ex)
        {
            // 不要静默：这里的异常通常是「站点不可播 / 爬虫运行时缺失 / 详情解析炸了」，
            // 吞掉后只剩一句「线路加载失败」，无法判断是哪一类（2026-09-14 真机排查成本极高）。
            WatchLog($"[load-fail] site={_site.Key} type={_site.Type} api={_site.Api} " +
                     $"spiderKind={_site.SpiderKind} jar={(string.IsNullOrEmpty(_site.Jar) ? "<null>" : "有")} " +
                     $"item={_item.Id} → {ex.GetType().Name}: {ex.Message}\n{ex}");
            await ShowTipAsync($"线路加载失败：{ex.Message}");
        }
        finally
        {
            BufferingIndicator.IsVisible = false;
        }
    }

    /// <summary>
    /// 磁力站流式加载：首个批次立即上屏并起播，后续批次只刷新选集栏。
    /// 用户几百毫秒内就能看到选集并听到声音，剩余打包磁力在后台继续展开。
    /// </summary>
    private async Task LoadSourcesProgressiveAsync(IProgressiveVodSourceProvider progressive)
    {
        var first = true;
        await foreach (var batch in progressive.StreamPlaySourcesAsync(_site, _item))
        {
            if (batch.Count == 0 && first)
            {
                await ShowTipAsync("该影片暂无可播放线路");
                return;
            }
            if (batch.Count == 0) return;

            _sources = batch;
            if (first)
            {
                first = false;
                await ApplySourcesAsync();          // 建线路芯片 + 续看 + 起播
        }
            else
            {
                RefreshEpisodesKeepPlayback();      // 只更新集名（打包名 → 真实文件名）
            }
        }
        if (first) await ShowTipAsync("该影片暂无可播放线路");
    }

    /// <summary>重建线路芯片、应用续看上下文并选中线路（两条加载路径共用）。</summary>
    private async Task ApplySourcesAsync()
    {
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

        if (_sources.Count == 0)
        {
            await ShowTipAsync("该影片暂无可播放线路");
            return;
        }

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

    /// <summary>
    /// 流式加载的后续批次：只把当前线路的集名刷新掉（打包名 → 种子内真实文件名），
    /// **不打断正在进行的播放** —— 按集名匹配找回当前集，找不到则维持原高亮。
    /// </summary>
    private void RefreshEpisodesKeepPlayback()
    {
        if (_currentSourceIndex < 0 || _currentSourceIndex >= _sources.Count) return;

        var currentName = _currentEpisodeIndex >= 0 && _currentEpisodeIndex < _episodeRows.Count
            ? _episodeRows[_currentEpisodeIndex].Name
            : null;

        var source = _sources[_currentSourceIndex];
        _episodeRows.Clear();
        for (int i = 0; i < source.Episodes.Count; i++)
        {
            var row = new EpisodeRow(i, source.Episodes[i].Name);
            row.CurrentChanged += () => ApplyRowHighlight(row);
            _episodeRows.Add(row);
        }

        // 找回当前集（集名可能从打包名变成了真实文件名，故用「包含」松散匹配）
        int keep = currentName is null ? -1 : _episodeRows.FindIndex(r =>
            string.Equals(r.Name.Trim(), currentName.Trim(), StringComparison.Ordinal));
        if (keep < 0 && currentName is not null)
            keep = _episodeRows.FindIndex(r =>
                r.Name.Contains(currentName, StringComparison.OrdinalIgnoreCase) ||
                currentName.Contains(r.Name, StringComparison.OrdinalIgnoreCase));

        if (keep >= 0)
        {
            _episodeRows[keep].IsCurrent = true;
            _currentEpisodeIndex = keep;
        }
        else if (_currentEpisodeIndex >= 0 && _currentEpisodeIndex < _episodeRows.Count)
        {
            _episodeRows[_currentEpisodeIndex].IsCurrent = true;
        }

        // 当前集可能翻到了别的页，跟着切过去
        _episodePage = _currentEpisodeIndex >= 0
            ? _currentEpisodeIndex / PerPageFor(source.Episodes.Count)
            : 0;
        RenderEpisodePage();

        EpisodeCountLabel.Text = source.Episodes.Count > 1
            ? $"共 {source.Episodes.Count} 集 · {DisplayNameFor(source.Name)}"
            : DisplayNameFor(source.Name);
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

        // ⚠️ 仅在**手动切线路**时记忆/落库：首次加载（applyResume=true）不得落库，
        // 否则进页面瞬间会把历史记录覆盖成「默认集 @ 0s」，续看还没来得及应用。
        int keepIndex = -1;
        double keepPos = 0;
        if (!applyResume)
        {
            // 不同线路是**同一集的不同源**，位置仍然有效
            keepIndex = _currentEpisodeIndex;
            keepPos = _playing ? Player.Position.TotalSeconds : 0;

            // 立即落库（新线路 + 同序号集 + 原位置）：切完就走也不会留下旧线路
            _playback.SetRouteName(source.Name);
            _playback.SetEpisodeName(keepIndex >= 0 && keepIndex < source.Episodes.Count
                ? source.Episodes[keepIndex].Name
                : source.Episodes.FirstOrDefault()?.Name ?? string.Empty);
            _playback.SaveProgress(keepPos, Player.Duration.TotalSeconds);
        }

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

        // 控件条副标题：线路名（集名在换集时另行更新，见 UpdateControlBarSubtitle）
        ControlBar.Subtitle = DisplayNameFor(source.Name);

        if (source.Episodes.Count > 0)
        {
            if (applyResume)
            {
                // 首次加载：套用续看集与位置
                var resumeRow = _resumeEpisodeName is { Length: > 0 }
                    ? _episodeRows.FirstOrDefault(r =>
                          string.Equals(r.Name.Trim(), _resumeEpisodeName.Trim(), StringComparison.Ordinal))
                    : null;
                PlayEpisodeByRow(resumeRow ?? _episodeRows[0]);
                if (resumeRow == null)
                {
                    // 找不到续看集（换线路选集名不同）：作废续看位置，避免误 seek
                    _resumeEpisodeName = null;
                    _resumePosition = 0;
                }
            }
            else
            {
                // 切线路：优先播同序号的集，并沿用上一个线路的播放位置（同一集的不同源）
                var sameIndexRow = keepIndex >= 0 && keepIndex < _episodeRows.Count
                    ? _episodeRows[keepIndex]
                    : null;
                _resumeEpisodeName = null;
                _resumePosition = keepPos > 10 ? keepPos : 0;
                PlayEpisodeByRow(sameIndexRow ?? _episodeRows[0]);
            }
        }
        else
            _ = ShowTipAsync("该线路暂无选集");
    }

    /// <summary>
    /// 选集行样式：底色 = 「正在播放的那一集」，描边环 = 「遥控器焦点」，两者互不覆盖
    /// （焦点可能停在别的集上，此时两格要能同时区分出来）。
    /// </summary>
    private void HighlightRow(Border border, Label name, Border num, EpisodeRow row)
    {
        var res = Application.Current!.Resources;
        bool focused = _episodeFocusEngaged && row.Index == _episodeFocusIndex;

        border.BackgroundColor = row.IsCurrent
            ? (Color)res["PrimaryColor"]
            : (Color)res["CardBackgroundColor"];
        name.TextColor = row.IsCurrent ? Colors.White : (Color)res["TextSecondaryColor"];
        num.BackgroundColor = row.IsCurrent
            ? (Color)res["PrimaryColor"]
            : Color.FromArgb("#14FFFFFF");
        if (num.Content is Label numLabel)
            numLabel.TextColor = row.IsCurrent ? Colors.White : (Color)res["TextSecondaryColor"];

        border.Stroke = focused ? (Color)res["PrimaryColor"] : Colors.Transparent;
        border.StrokeThickness = focused ? 2.5 : 0;
    }

    // ═══════════════════════ 遥控器焦点（选集栏） ═══════════════════════

    /// <summary>
    /// 本页的焦点区。几何顺序与界面一致：控制条（画面下方）→ 收藏/分享（信息区）→ 选集栏（右侧）。
    /// 方向键在区内移动，越界则按这个顺序换区，不会出现「怎么按都没反应」的死角。
    /// </summary>
    private enum WatchZone { None, TopBar, Controls, Episodes, Actions }

    private WatchZone _zone = WatchZone.None;

    /// <summary>焦点是否在选集栏（<see cref="HighlightRow"/> 据此决定是否画焦点环）。</summary>
    private bool _episodeFocusEngaged => _zone == WatchZone.Episodes;

    /// <summary>收藏 / 分享 的焦点下标（0 = 收藏，1 = 分享）。</summary>
    private int _actionIndex;

    /// <summary>顶栏焦点下标：0 = 返回按钮，1..N = 线路芯片。</summary>
    private int _topBarIndex;

    /// <summary>顶栏是否可见（全屏选集模式下顶栏被隐藏，此时不能把焦点送过去）。</summary>
    private bool TopBarVisible => TopBarGrid.IsVisible;

    /// <summary>顶栏（返回 / 线路芯片）的焦点环。</summary>
    private void RenderTopBarFocus()
    {
        var primary = Application.Current!.Resources["PrimaryColor"] as Color ?? Colors.Purple;
        bool on = _zone == WatchZone.TopBar;

        void Apply(Border b, bool focused)
        {
            b.Stroke = focused ? primary : Colors.Transparent;
            b.StrokeThickness = focused ? 2.5 : 0;
            b.Scale = focused ? 1.08 : 1.0;
        }

        Apply(BackButtonHost, on && _topBarIndex == 0);
        for (int i = 0; i < _lineChips.Count; i++)
            Apply(_lineChips[i], on && _topBarIndex == i + 1);
    }

    /// <summary>顶栏内左右移动（返回 ↔ 线路芯片）。返回 false = 到头。</summary>
    private bool MoveTopBarFocus(int dir)
    {
        int next = _topBarIndex + dir;
        if (next < 0 || next >= 1 + _lineChips.Count) return false;

        _topBarIndex = next;
        RenderTopBarFocus();
        return true;
    }

    /// <summary>焦点所在集（全局下标，对应 <c>_episodeRows</c>）。</summary>
    private int _episodeFocusIndex = -1;

    /// <summary>外层把焦点送进来（顶栏 ↓ 等）：落在选集栏正在播放的那一集上。</summary>
    public void FocusContent() => SetZone(WatchZone.Episodes);

    /// <summary>外层把焦点收走：全页熄灯（正在播放那一集的底色保留）。</summary>
    public void BlurContent() => SetZone(WatchZone.None);

    /// <summary>
    /// 切换焦点区：先给旧区「熄灯」，再点亮新区。
    /// 所有换区都走这里，保证任何时刻只有一个区亮着（顶栏与内容双高亮就是这个页面踩过的坑）。
    /// </summary>
    private void SetZone(WatchZone zone)
    {
        var old = _zone;
        _zone = zone;   // 先切换：下面重画时旧区自然不再点亮

        if (old == WatchZone.Episodes) RefreshEpisodeVisuals();
        if (old == WatchZone.Controls) ControlBar.Blur();
        if (old == WatchZone.Actions) RenderActionFocus();
        if (old == WatchZone.TopBar) RenderTopBarFocus();

        switch (zone)
        {
            case WatchZone.TopBar:
                _topBarIndex = Math.Clamp(_topBarIndex, 0, _lineChips.Count);
                RenderTopBarFocus();
                break;

            case WatchZone.Controls:
                ControlBar.FocusFirst();
                break;

            case WatchZone.Episodes:
                if (_episodeRows.Count == 0) { SetZone(WatchZone.Controls); return; }
                FocusEpisodes(_episodeFocusIndex >= 0 ? _episodeFocusIndex : _currentEpisodeIndex);
                break;

            case WatchZone.Actions:
                _actionIndex = Math.Clamp(_actionIndex, 0, 2);
                RenderActionFocus();
                break;
        }
    }

    /// <summary>操作按钮的焦点环：换源 / 收藏 / 分享（Button 用描边 + 微放大）。</summary>
    private void RenderActionFocus()
    {
        var primary = Application.Current!.Resources["PrimaryColor"] as Color ?? Colors.Purple;
        bool on = _zone == WatchZone.Actions;

        void Apply(Button b, bool focused)
        {
            b.BorderColor = focused ? primary : Colors.Transparent;
            b.BorderWidth = focused ? 2.5 : 0;
            b.Scale = focused ? 1.06 : 1.0;
        }

        Apply(SwitchSourceButton, on && _actionIndex == 0);
        Apply(FavoriteButton, on && _actionIndex == 1);
        Apply(ShareButton, on && _actionIndex == 2);
    }

    /// <summary>把焦点送进选集栏（本方法只做定位，「换区」由 <see cref="SetZone"/> 负责）。</summary>
    private void FocusEpisodes(int index)
    {
        if (_episodeRows.Count == 0) return;

        _episodeFocusIndex = Math.Clamp(index < 0 ? 0 : index, 0, _episodeRows.Count - 1);
        EnsureEpisodePageOf(_episodeFocusIndex);
        RefreshEpisodeVisuals();
        ScrollEpisodeIntoView();
    }

    /// <summary>重画当前页全部选集格（焦点切换时整页状态都可能变）。</summary>
    private void RefreshEpisodeVisuals()
    {
        foreach (var v in _pageVisuals) HighlightRow(v.Border, v.Name, v.Num, v.Row);
    }

    /// <summary>焦点所在集不在当前页时先翻页（选集分页显示，跨页必须跟着翻）。</summary>
    private void EnsureEpisodePageOf(int index)
    {
        int perPage = PerPageFor(_episodeRows.Count);
        if (perPage <= 0) return;

        int page = index / perPage;
        if (page == _episodePage) return;
        _episodePage = page;
        RenderEpisodePage();
    }

    /// <summary>把焦点格滚进可视区（每页 10 行，不滚会跑到视野外）。</summary>
    private void ScrollEpisodeIntoView()
    {
        try
        {
            foreach (var v in _pageVisuals)
            {
                if (v.Row.Index != _episodeFocusIndex) continue;
                _ = EpisodeScroll.ScrollToAsync(v.Border, ScrollToPosition.MakeVisible, animated: false);
                return;
            }
        }
        catch { }
    }

    /// <summary>方向键移动焦点。返回 <c>false</c> = 该方向已到头（交还外层）。</summary>
    private bool MoveEpisodeFocus(RemoteKey dir)
    {
        int total = _episodeRows.Count;
        if (total == 0) return false;

        int cols = Math.Max(1, EpisodeColumnsFor(total));
        int perPage = Math.Max(1, PerPageFor(total));
        int start = _episodePage * perPage;
        int slot = _episodeFocusIndex - start;
        if (slot < 0) { FocusEpisodes(start); return true; }

        int? target = dir switch
        {
            RemoteKey.Left => slot % cols == 0 ? null : _episodeFocusIndex - 1,
            RemoteKey.Right => (slot % cols == cols - 1 || _episodeFocusIndex >= total - 1) ? null : _episodeFocusIndex + 1,
            RemoteKey.Up => _episodeFocusIndex - cols < 0 ? null : _episodeFocusIndex - cols,
            RemoteKey.Down => _episodeFocusIndex + cols > total - 1 ? null : _episodeFocusIndex + cols,
            _ => null,
        };
        if (target is not { } idx) return false;

        _episodeFocusIndex = idx;
        EnsureEpisodePageOf(idx);
        RefreshEpisodeVisuals();
        ScrollEpisodeIntoView();
        return true;
    }

    public bool Handle(RemoteKey key)
    {
        switch (key)
        {
            case RemoteKey.Left:
            case RemoteKey.Right:
            case RemoteKey.Up:
            case RemoteKey.Down:
                return MoveZone(key);

            case RemoteKey.Enter:
                return ActivateZone();

            case RemoteKey.Back:
                // 先退焦点，再按一次才返回上一页（避免误触把播放页关掉）
                if (_zone != WatchZone.None) { SetZone(WatchZone.None); return true; }
                return false;
        }
        return false;
    }

    /// <summary>
    /// 方向键：区内移动，越界按**界面几何顺序**换区
    /// （控制条 → 收藏/分享 → 选集栏），所以任何一个方向都不会按了没反应。
    /// </summary>
    private bool MoveZone(RemoteKey dir)
    {
        switch (_zone)
        {
            case WatchZone.None:
                // 第一下就有反应：↓/→ 落控制条，↑ 落顶栏（返回 / 线路）
                if (dir is RemoteKey.Down or RemoteKey.Right) { SetZone(WatchZone.Controls); return true; }
                if (dir == RemoteKey.Up && TopBarVisible) { SetZone(WatchZone.TopBar); return true; }
                return false;

            case WatchZone.TopBar:
                if (dir is RemoteKey.Left or RemoteKey.Right)
                    return MoveTopBarFocus(dir == RemoteKey.Right ? 1 : -1);
                if (dir == RemoteKey.Down) { SetZone(WatchZone.Controls); return true; }
                return false;   // ↑ 已在最顶：交还外层

            case WatchZone.Controls:
                if (dir is RemoteKey.Left or RemoteKey.Right)
                {
                    if (ControlBar.MoveFocus(dir == RemoteKey.Right ? 1 : -1)) return true;
                    // 右端出头 → 进右侧选集栏；左端到头 → 交还外层
                    if (dir == RemoteKey.Right) { SetZone(WatchZone.Episodes); return true; }
                    return false;
                }
                if (dir == RemoteKey.Down) { SetZone(WatchZone.Actions); return true; }
                // ↑ 回顶栏（返回 / 线路）；顶栏不可见（全屏选集）则退出焦点
                if (dir == RemoteKey.Up)
                {
                    if (TopBarVisible) { SetZone(WatchZone.TopBar); return true; }
                    SetZone(WatchZone.None);
                    return false;
                }
                return false;

            case WatchZone.Actions:
                if (dir is RemoteKey.Left or RemoteKey.Right)
                {
                    _actionIndex = Math.Clamp(_actionIndex + (dir == RemoteKey.Right ? 1 : -1), 0, 2);
                    RenderActionFocus();
                    return true;
                }
                if (dir == RemoteKey.Up) { SetZone(WatchZone.Controls); return true; }
                if (dir == RemoteKey.Down) { SetZone(WatchZone.Episodes); return true; }
                return false;

            case WatchZone.Episodes:
                if (MoveEpisodeFocus(dir)) return true;
                // 到边界：左 → 控制条；下 → 收藏/分享；上 → 顶栏（返回 / 线路）
                if (dir == RemoteKey.Left) { SetZone(WatchZone.Controls); return true; }
                if (dir == RemoteKey.Down) { SetZone(WatchZone.Actions); return true; }
                if (dir == RemoteKey.Up && TopBarVisible) { SetZone(WatchZone.TopBar); return true; }
                return false;
        }
        return false;
    }

    /// <summary>回车：按当前区触发（控制条按钮 / 收藏分享 / 播放该集）。</summary>
    private bool ActivateZone()
    {
        switch (_zone)
        {
            case WatchZone.TopBar:
                if (_topBarIndex == 0) OnBackTapped(this, new TappedEventArgs(null));
                else if (_topBarIndex - 1 < _lineChips.Count) _ = SelectSourceAsync(_topBarIndex - 1);
                return true;

            case WatchZone.Controls:
                ControlBar.ActivateFocus();
                return true;

            case WatchZone.Actions:
                if (_actionIndex == 0) OnSwitchSourceClicked(this, EventArgs.Empty);
                else if (_actionIndex == 1) OnFavoriteClicked(this, EventArgs.Empty);
                else OnShareClicked(this, EventArgs.Empty);
                return true;

            case WatchZone.Episodes:
                if (_episodeFocusIndex >= 0 && _episodeFocusIndex < _episodeRows.Count)
                    PlayEpisodeByRow(_episodeRows[_episodeFocusIndex]);
                return true;

            default:
                SetZone(WatchZone.Controls);   // 无焦点时回车 = 从控制条开始
                return true;
        }
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
            var (border, name, num) = BuildEpisodeCell(i, source.Episodes[i], row, cols);
            EpisodeListHost.Add(border, c, r);
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

    /// <summary>构建单个选集格：卡片（序号块 + 集名，**点卡片即播放**）。
    /// <paramref name="cols"/> 用于按列数收敛字号，避免文案横向溢出压到邻列。</summary>
    private (Border Border, Label Name, Border Num) BuildEpisodeCell(
        int index, VodEpisode episode, EpisodeRow row, int cols)
    {
        var border = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            Padding = new Thickness(8, 8),
            BindingContext = row,
        };
        // 宽列（1~2 列）用 28dp 序号块；3 列时每列仅约 88dp，序号块收窄给集名留出空间
        var numWidth = cols >= 3 ? 22 : 28;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitionCollection
        {
            new ColumnDefinition(numWidth), new ColumnDefinition(GridLength.Star),
        }, ColumnSpacing = cols >= 3 ? 6 : 8 };
        var numSize = cols >= 3 ? 22 : 26;
        var num = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 6 },
            BackgroundColor = Color.FromArgb("#14FFFFFF"),
            WidthRequest = numSize, HeightRequest = numSize, VerticalOptions = LayoutOptions.Center,
        };
        num.Content = new Label
        {
            Text = (index + 1).ToString(),
            FontSize = cols >= 3 ? 10 : 11,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalOptions = LayoutOptions.Center,
            TextColor = (Color)Application.Current!.Resources["TextSecondaryColor"],
        };
        var nameLabel = new Label
        {
            FontSize = cols >= 3 ? 11.5 : 12.5,
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

        // ⚠ 2026-09-22：磁力行原先在卡片下方再挂一枚「▶ 播放」chip（更早还有「⬇ 下载」），
        //   与「点卡片即播放」（见上面的 tap → PlayEpisodeByRow）功能完全重复，按用户要求移除。
        //   连带去掉当时为放 chip 而包的那层 VerticalStackLayout wrapper（故不再需要 Cell 别名）。
        return (border, nameLabel, num);
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
        UpdateControlBarSubtitle(episode);
        var generation = ++_playGeneration;
        // ★ 换集立即掐灭旧画面（2026-09-17 用户反馈）：Stop 暂停旧会话 + Source=null 卸载
        //   FFmpeg interop（画面立即变黑），解析期间不再继续播旧视频、也不再白白拉旧流数据。
        //   buffering 转圈立刻可见，新流就绪后自动接管。
        try { Player.Stop(); } catch { }
        try { Player.Headers = null; Player.Source = null; } catch { }
        // 解析阶段还拿不到时长/缓冲前沿 → 用旋转弧表示「在忙」，
        // 起播（Buffering 且时长已知）后自动切到圆环百分比。
        ShowBufferingIndeterminate(true);
        try
        {
            var play = await _provider.ResolvePlayUrlAsync(_site, episode);
            if (generation != _playGeneration) return; // 已被后续点击取代，丢弃过期解析
            _resolvedPlay = play;
            UpdateControlBarSubtitle(episode, play.Title);
            // 防盗链头透传播放器（spider header 全量；此前 Referer/UA 在此被丢弃导致部分源 403）
            Player.Headers = play.Headers ??
                (play.Referer is { Length: > 0 } || play.UserAgent is { Length: > 0 }
                    ? PlayRequestHeaders(play)
                    : null);
            Player.Source = play.Url;
            Player.Play();
            _playing = true;

            // 起播即落库一次：最后播放的影片立刻置顶历史第一位。
            // 带续播位置时先记该位置（避免 seek 生效前退出被记成 0s）
            _playback.SaveProgress(_resumePosition > 0 ? _resumePosition : 0, 0);
            StartProgressAutoSave();

            // 起播后唤出控制层；鼠标离开播放框（或手指离开）即 3s 后自动隐藏
            ShowControls();
            RestartControlsHideTimer();

            // 同步控制条状态（换集会重建媒体，音量可能被重置）
            ControlBar.IsMuted = Player.Volume <= 0;
            ControlBar.SpeedValue = Player.Speed;

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
            // 这类异常的消息本身就是给人看的（如「BT 引擎未初始化」「该集为电驴链接」），
            // 原样提示；同时落盘，方便事后对照桥日志查因。
            DiagLog.Write($"[播放] 不支持 {_item.Title} / {episode.Name}: {ex.Message}");
            if (generation == _playGeneration) await ShowTipAsync(ex.Message);
            _ = MaybeAutoSwitchSourceAsync();
        }
        catch (Exception ex)
        {
            // ⚠ 绝不能静默吞：只显示一句「解析失败」的话，Guard 包缺类、BT 无节点、
            // 嗅探失败、防盗链 403 全都长得一模一样，排障只能靠猜
            // （2026-09-15 实测：「新6V 剧集解析失败」真因是 playerContent 抛
            //   ClassNotFoundException: android.view.View$OnTouchListener，全被这里吃掉）。
            DiagLog.Write($"[播放] 解析失败 {_item.Title} / {episode.Name}"
                          + $"（源 {_site.Name} / 线路 {episode.Flag}）: {ex.GetType().Name}: {ex.Message}");
            if (generation == _playGeneration)
                await ShowTipAsync("该集解析失败：" + ReasonOf(ex.Message));
            _ = MaybeAutoSwitchSourceAsync();
        }
        finally
        {
            if (generation == _playGeneration)
            {
                // 不要无条件收起：解析成功后播放器已进入 Buffering/Preparing，
                // 此时正是「起播缓冲」阶段，指示器应由 StateChanged 接管；
                // 只有解析失败（没有 Source）才收起。
                if (string.IsNullOrEmpty(Player.Source)) ShowBuffering(false);
                UpdatePlayIcon();
            }
        }
    }

    // ═══════════ 磁力兜底：自动换源 ═══════════

    private bool _autoSwitching;

    /// <summary>当前影片的全部剧集都是磁力/电驴（即「磁力站」）</summary>
    private bool CurrentItemIsMagnetOnly() =>
        _sources.SelectMany(s => s.Episodes).Any()
        && _sources.SelectMany(s => s.Episodes).All(e =>
            e.Url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ||
            e.Url.StartsWith("ed2k:", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 磁力兜底：磁力站的剧集起播失败时，用标题在**同订阅的其他可播站点**里找同名内容，
    /// 找到一个「有非磁力剧集」的站点/条目后直接跳过去续播同集。
    /// 给最终用户用的：不要求任何账号、零配置 —— 磁力起不来就自动落到能播的源上。
    /// <para>只对「整部片都是磁力」的影片触发；混合线路里换一条线路就够了。</para>
    /// </summary>
    private async Task MaybeAutoSwitchSourceAsync()
    {
        if (_autoSwitching) return;
        if (!CurrentItemIsMagnetOnly()) return;

        _autoSwitching = true;
        try
        {
            var title = CleanTitleForMatch(_item.Title);
            if (title.Length < 2) return;

            DiagLog.Write($"[换源] 磁力站起播失败，开始跨站搜索：{title}");
            int tried = 0;
            foreach (var site in SiteRegistry.Playable)
            {
                if (site.Key == _site.Key || tried >= 10) continue;
                tried++;

                List<VodItem> hits;
                try { hits = await _provider.SearchAsync(site, title); }
                catch { continue; }

                var match = hits.FirstOrDefault(h => TitlesMatch(h.Title, title));
                if (match is null) continue;

                // 必须确认有非磁力剧集，否则换过去还是播不了
                List<VodPlaySource> srcs;
                try { srcs = await _provider.GetPlaySourcesAsync(site, match); }
                catch { continue; }
                var hasDirect = srcs.SelectMany(s => s.Episodes).Any(e =>
                    !e.Url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) &&
                    !e.Url.StartsWith("ed2k:", StringComparison.OrdinalIgnoreCase));
                if (!hasDirect) continue;

                var ep = _currentEpisode?.Name;
                DiagLog.Write($"[换源] 命中 {site.Name}/{site.Key}《{match.Title}》，跳转续播 {ep}");
                var q = "watch?sourceKey=" + Uri.EscapeDataString(site.Key)
                      + "&itemId=" + Uri.EscapeDataString(match.Id)
                      + "&title=" + Uri.EscapeDataString(match.Title);
                if (!string.IsNullOrEmpty(match.Cover)) q += "&cover=" + Uri.EscapeDataString(match.Cover);
                if (!string.IsNullOrEmpty(ep)) q += "&resumeEp=" + Uri.EscapeDataString(ep);
                await Shell.Current.GoToAsync(q);
                return;
            }
            DiagLog.Write("[换源] 其他站点没有找到可播的同名内容");
            await ShowTipAsync("磁力源不可用，其他站点也没有同名可播内容");
        }
        catch (Exception ex)
        {
            DiagLog.Write($"[换源] 失败：{ex.GetType().Name}: {ex.Message}");
        }
        finally { _autoSwitching = false; }
    }

    // ═══════════════════════ 换源（跨站点搜索同名片） ═══════════════════════

    private bool _switchingSource;

    /// <summary>
    /// 「换源」：拿当前片名去**其它站点**搜一遍，列出搜到的站点供选择；
    /// 选中后跳一个新的 watch 路由（新实例）用那个站点的资源播放。
    ///
    /// <para>为什么用 GoToAsync 而不是在本页就地替换 <c>_site</c>/<c>_item</c>：
    /// 本页的状态全绑在「当前影片 + 当前线路 + 当前集」上（<c>_sources</c> / <c>_episodeRows</c> /
    /// <c>_argsReady</c> 一次性信号 / <c>_loaded</c> 标志 / 播放器实例），
    /// 就地换片要手工重置这些，漏一个就是诡异状态。新开一个实例最干净 ——
    /// 项目里既有的自动换源（<see cref="MaybeAutoSwitchSourceAsync"/>）走的也是这条路。</para>
    /// </summary>
    private async void OnSwitchSourceClicked(object? sender, EventArgs e)
    {
        if (_switchingSource) return;

        var title = (TitleLabel.Text ?? string.Empty).Trim();
        if (title.Length == 0) { await ShowTipAsync("还没有片名，无法换源"); return; }

        _switchingSource = true;
        try
        {
            // 直接跳搜索页做跨站搜索（URL 里带 q，页面会自动开搜）。
            //
            // 为什么不自建搜索 + 选站点弹窗：搜索页本来就有跨站并行搜索、站点筛选条、
            // 结果分站显示、点结果即进观看页 —— 一套能力重写一遍只会更差，
            // 而且用户对那个界面已经熟（2026-09-19 用户明确要求「还不如直接跳转搜索页」）。
            await Shell.Current.GoToAsync($"search?q={Uri.EscapeDataString(title)}");
        }
        catch (Exception ex)
        {
            await ShowTipAsync($"打开搜索失败：{ex.Message}");
        }
        finally
        {
            _switchingSource = false;
        }
    }

    /// <summary>标题清洗：去括号备注/年份/更新集数等，只留正题名（自动换源的标题匹配用）</summary>
    private static string CleanTitleForMatch(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim();
        int cut = s.IndexOfAny(['(', '（', '【']);
        if (cut > 1) s = s[..cut];
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\b(19|20)\d{2}\b", "").Trim();
        s = System.Text.RegularExpressions.Regex.Replace(s, @"更新至.*$|第.*季$|[4kK][hl]?$", "").Trim();
        return s.Trim(' ', '-', '—', '·', '｜', '|');
    }

    /// <summary>标题匹配：去空白/标点后忽略大小写互含</summary>
    private static bool TitlesMatch(string a, string b)
    {
        static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());
        var na = Norm(a); var nb = Norm(b);
        if (na.Length < 2 || nb.Length < 2) return false;
        return na.Contains(nb, StringComparison.OrdinalIgnoreCase)
            || nb.Contains(na, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把异常消息压成一行可读原因（VerifyError 之类自带多行字节码 dump，不能整段进提示）</summary>
    private static string ReasonOf(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "请换集或换线路";
        var line = message.Split('\n')[0].Trim();
        return line.Length > 80 ? line[..80] + "…" : line;
    }

    /// <summary>把 Referer/UA 兜底成播放器请求头字典（Headers 未提供时）</summary>
    private static Dictionary<string, string>? PlayRequestHeaders(PlayRequest play)
    {
        var h = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(play.Referer)) h["Referer"] = play.Referer;
        if (!string.IsNullOrEmpty(play.UserAgent)) h["User-Agent"] = play.UserAgent;
        return h.Count > 0 ? h : null;
    }

    private void ShowBuffering(bool on) => BufferingIndicator.IsVisible = on;

    // ═══════════ 缓冲进度指示（圆环 + 中央百分比） ═══════════
    //
    // 语义（用户确认）：百分比 = 「从起点到目标位置」的缓冲完成度。
    //   · 快进到未缓冲区域：起点 = 快进前的位置，目标 = 快进后的位置，
    //     前沿（BufferedFrontier，**未**与播放位置取 max 的原始值）从起点往后追 → 0%→100%。
    //     若用 BufferedPosition（已抬到 position）会恒等 100%，指示器立刻消失，故必须用前沿。
    //   · 进入页面/起播：没有明确的「目标位置」（播放器自己的缓冲策略决定何时开播），
    //     按「起播所需的典型缓冲量」推进；这个是启发式，见 StartupBufferSeconds。
    //   · 解析中 / 时长未知 / 拿不到前沿：退化为不确定态旋转弧（原转圈行为）。

    /// <summary>本次缓冲的开始时刻（用于「等太久就切旋转弧」的判定）。</summary>
    private DateTime _bufferStartUtc = DateTime.UtcNow;

    /// <summary>起播缓冲的参考量：进度环的分母（只用于显示，不影响播放时机）。</summary>
    private const double StartupBufferSeconds = 8.0;

    /// <summary>超过这个时长且连续可播量几乎没涨 → 切回旋转弧，避免挂着不动的数字像死机。</summary>
    private const double LongWaitSeconds = 25.0;

    /// <summary>上次采样的播放位置（判断「是否真的在播」）。</summary>
    private double _bufferLastPos = -1;

    /// <summary>已确认播放位置在推进（画面真的在播）。用于压制 MF 对磁力流的 Buffering 误报。</summary>
    private bool _playbackAdvancing;

    /// <summary>把缓冲指示切到不确定态（解析中 / 时长未知：画旋转弧、不显示百分比）。</summary>
    private void ShowBufferingIndeterminate(bool on)
    {
        if (on) BufferRing.IsIndeterminate = true;
        ShowBuffering(on);
    }

    /// <summary>声明「即将开始一次缓冲」：显示指示器并把进度归零重新计时。</summary>
    private void BeginBuffering(double anchorSeconds = -1, double targetSeconds = -1)
    {
        _ = anchorSeconds;
        _ = targetSeconds;
        _bufferStartUtc = DateTime.UtcNow;
        _playbackAdvancing = false;
        _bufferLastPos = -1;          // 重置采样哨兵：重新积累「位置推进」证据
        BufferRing.IsIndeterminate = false;
        BufferRing.Progress = 0;
        ShowBuffering(true);
    }

    /// <summary>
    /// 裁决缓冲指示器的显隐。
    ///
    /// <para><b>判据一（磁力路，权威）</b>：<see cref="VideoPlayerView.IsWaitingForData"/> ——
    /// 磁力流经宿主的读前缓存代理，代理精确知道「有没有读者顶到缓存前沿等数据」。这是真正
    /// 的「在缓冲」，不需要任何估算。</para>
    ///
    /// <para><b>判据二（兜底）</b>：位置推进。直链路没有代理信号；且磁力路上 MF 的
    /// <c>PlaybackState</c> 会在**画面正常播放时仍报 Buffering**（2026-09-18 用户实测：
    /// 视频在播、中央却一直挂「91%」），所以不能用它。</para>
    /// </summary>
    private void TickBufferingIndicator()
    {
        var pos = Player.Position.TotalSeconds;
        var dur = Player.Duration.TotalSeconds;

        // 判据一（权威，磁力路）：代理说读者顶到缓存前沿等数据 = 确凿地在缓冲。
        // ⚠ 这一条必须**无条件**检查（不能因为指示器当前隐藏就跳过）：播放中途断粮
        //   正是磁力最常见的情形，那时指示器需要被**重新唤起**。
        var waiting = Player.IsWaitingForData;

        // 判据二（兜底）：位置真的往前走了（且不是拖动引起的跳变）→ 已经在播。
        // ⚠ `_bufferLastPos >= 0` 不可省：初值是 -1（哨兵），否则首帧 pos=0 会被当成
        //   「从 -1 推进到 0」而误判为已在播放，起播指示器瞬间消失。
        var advancing = dur > 0 && _bufferLastPos >= 0
                        && pos > _bufferLastPos + 0.15
                        && pos - _bufferLastPos < 5.0      // 排除 seek 跳变
                        && !_seeking;
        _bufferLastPos = pos;

        if (waiting)
        {
            _playbackAdvancing = false;
            if (!BufferingIndicator.IsVisible) BeginBuffering();   // 中途断粮 → 重新显示
            return;
        }

        if (advancing)
        {
            _playbackAdvancing = true;          // 压制 MF 的 Buffering 误报
            if (BufferingIndicator.IsVisible)
            {
                BufferRing.IsIndeterminate = false;
                BufferRing.Progress = 1;
                ShowBuffering(false);
            }
        }
    }

    /// <summary>
    /// 刷新缓冲百分比 = 「播放点往后还有多少**连续可播**数据」朝起播所需量推进。
    ///
    /// <para><b>为什么是这个量（2026-09-18~19 用户实测，连续踩了三个坑）</b>：</para>
    /// <list type="number">
    /// <item>最初用「已下载字节比例 × 总时长」——磁力是**乱序 P2P 下载**，整片下到 95%
    ///   也不代表当前位置往后连续可播，实测快进后百分比直接跳到 100% 卡死。</item>
    /// <item>改用「等待时长」估算——数字在动但与实际无关，仅供观感。</item>
    /// <item>最终：改用读前缓存代理的 <c>ReaderAheadBytes</c>（从最慢读者位置到缓存前沿的
    ///   连续字节数，见 <see cref="VideoPlayerView.BufferedFrontier"/>）。
    ///   它是**诚实且单调**的：引擎供得上就涨，供不上就停 —— 这才是「还要缓冲多久」的真答案。
    ///   直链路也走同一公式（MF 的 BufferedRanges 同样是连续可播末端）。</item>
    /// </list>
    ///
    /// <para>等太久（>25s）说明引擎抢不到数据，再挂个不动的百分比只会像死机 → 切旋转弧。</para>
    /// </summary>
    private void UpdateBufferProgress()
    {
        if (!BufferingIndicator.IsVisible) return;

        var dur = Player.Duration.TotalSeconds;
        if (dur <= 0)
        {
            BufferRing.IsIndeterminate = true;   // 还没读到元数据
            return;
        }

        var pos = Player.Position.TotalSeconds;
        var frontier = Player.BufferedFrontier.TotalSeconds;

        // 播放点往后的连续可播时长（magnet 走 ReaderAheadBytes、直链走 BufferedRanges，
        // 平台层已统一成「连续可播末端」，这里不区分）
        var ahead = frontier - pos;
        if (ahead < 0) ahead = 0;

        // 等太久且几乎没有前进 → 引擎抢不到数据，切旋转弧（别挂着不动的数字）
        var waited = (DateTime.UtcNow - _bufferStartUtc).TotalSeconds;
        if (waited > LongWaitSeconds && ahead < 1.0)
        {
            BufferRing.IsIndeterminate = true;
            return;
        }

        // 分母：起播所需典型缓冲量（只用于显示，不影响播放时机）
        var need = Math.Max(1.0, Math.Min(dur, StartupBufferSeconds));
        BufferRing.IsIndeterminate = false;
        BufferRing.Progress = Math.Clamp(ahead / need, 0, 0.99);
    }

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

    /// <summary>播放中每 5s 兜底落库一次（切线路/切集/拖进度条/暂停均已即时保存）</summary>
    private void StartProgressAutoSave()
    {
        _progressSaveTimer ??= Dispatcher.CreateTimer();
        _progressSaveTimer.Interval = TimeSpan.FromSeconds(5);
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
    /// **实际渲染高度**——名义 340 会因圆角/描边/测量差异留下 ~20dp 落差（2026-09-11 真机实测）。
    /// <para>窗口尺寸变化（最大化/拉伸）后播放器高度会变，必须重新对齐，否则选集栏会停在旧高度
    /// （2026-09-19 用户实测：播放器铺满后选集框还是矮矮一条）。</para></summary>
    private void SyncSidebarHeight()
    {
        try
        {
            // ⚠ 判据用「面板当前的真实宿主」而不是 _sidebarHost 字段：AttachSidebar 内部会先搬移、
            //   再（末尾）更新字段，用字段判断时搬回主区的这一次调用会因字段仍是 Overlay 而被跳过，
            //   导致 MaximumHeightRequest 停在浮层用的「无限高」，选集栏被集列表撑到很高。
            if (SidebarPanel.Parent is null || ReferenceEquals(SidebarPanel.Parent, EpisodesOverlayHost))
                return;   // 在浮层里（或不属于任何宿主）：高度不限，不受播放框约束

            if (PlayerHost.Parent is VisualElement box && box.Height > 0)
                SidebarPanel.MaximumHeightRequest = box.Height;
        }
        catch { }
    }

    /// <summary>选集栏的宿主容器（在「主区右列」与「全屏浮层」之间切换）。</summary>
    private enum SidebarHost { Inline, Overlay }

    private SidebarHost _sidebarHost = SidebarHost.Inline;

    /// <summary>
    /// 把选集栏挂到指定宿主。同一个 <see cref="SidebarPanel"/> 实例在两处之间搬移，
    /// 因此集列表、翻页位置、当前集高亮全部原样保留，不重建、不丢状态。
    /// <para>只负责**搬移**，不改变浮层显隐 —— 浮层开关由调用方决定（进入全屏应是干净画面，
    /// 若在这里顺手打开，用户每次进全屏都会被选集栏挡住）。</para>
    /// </summary>
    private void AttachSidebar(SidebarHost host)
    {
        if (_sidebarHost == host) return;
        try
        {
            if (SidebarPanel.Parent is Layout from)
                from.Remove(SidebarPanel);

            if (host == SidebarHost.Overlay)
            {
                // ⚠ 无限制必须写 PositiveInfinity（MAUI 的默认值），**不能写 -1**：
                //   -1 会被当作「最大高度 ≤ -1」把面板压成 0 高度，浮层打开却什么都看不见。
                SidebarPanel.MaximumHeightRequest = double.PositiveInfinity;
                // ⚠ 必须显式设 0 列：XAML 里它是 Grid.Column="1"，搬进浮层宿主（单列 Grid）
                //   后列索引越界 → 元素不参与布局而"消失"。
                Grid.SetColumn(SidebarPanel, 0);
                Grid.SetRow(SidebarPanel, 0);
                EpisodesOverlayHost.Add(SidebarPanel, 0, 0);
            }
            else
            {
                MainArea.Add(SidebarPanel, 1, 0);   // Add 会同时设 Row/Column 附加属性
                SyncSidebarHeight();
            }
            _sidebarHost = host;
        }
        catch (Exception ex)
        {
            DiagLog.Write($"[选集] 全屏浮层挂载失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>退出全屏时把选集栏搬回主区右列并收起浮层。</summary>
    private void RestoreSidebarInline()
    {
        EpisodesOverlay.IsVisible = false;
        AttachSidebar(SidebarHost.Inline);
    }

    /// <summary>点击浮层空白处：关闭选集浮层（保持全屏，不退出）。</summary>
    private void OnEpisodesOverlayBackdropTapped(object? sender, TappedEventArgs e)
    {
        if (_sidebarHost != SidebarHost.Overlay) return;
        EpisodesOverlay.IsVisible = false;
        ShowControls();
    }

    /// <summary>原地全屏：隐藏顶栏/选集/信息区，播放器铺满整页；窗口切 FullScreen（Win）/ 横屏（Android）</summary>
    private void SetFullscreen(bool on)
    {
        _isFullscreen = on;
        TopBarGrid.IsVisible = !on;
        InfoArea.IsVisible = !on;
        RecommendHint.IsVisible = !on;
        ContentStack.Padding = on ? new Thickness(0) : NormalContentPadding;
        ControlBar.IsFullscreen = on;

        // 全屏时禁用滚动：播放器被撑到整屏高（ApplyPlayerHeight），而它外面套着 ScrollView，
        // 内容高度略超视口时滚轮/手指就能把它滚出去，露出视频以外的空白
        //（2026-09-19 用户实测「全屏还能滚」）。全屏下没有任何需要滚动的内容。
        if (on)
        {
            ContentScroll.Orientation = ScrollOrientation.Neither;
            _ = ContentScroll.ScrollToAsync(0, 0, false);
        }
        else
        {
            ContentScroll.Orientation = ScrollOrientation.Vertical;
        }

        if (on)
        {
            // 进入全屏：**先把选集栏从 MainArea 摘出去**，再收窄列定义 —— 顺序不能反。
            //   MainArea 收窄成单列后，仍留在其中的 SidebarPanel（Grid.Column=1）列索引越界，
            //   MAUI 会把它落到第 0 列 → 选集栏铺满整个播放区（2026-09-19 用户实测「进全屏
            //   直接糊满选集」）。摘到浮层宿主里待命，此时浮层隐藏，画面干净。
            EpisodesOverlay.IsVisible = false;
            AttachSidebar(SidebarHost.Overlay);
            MainArea.ColumnDefinitions =
                new ColumnDefinitionCollection { new ColumnDefinition(GridLength.Star) };
        }
        else
        {
            // 退出全屏：先恢复双列，再把选集栏搬回右列（等布局尺寸稳定后再量高度）
            MainArea.ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(300),
            };
            Dispatcher.StartTimer(TimeSpan.FromMilliseconds(120), () =>
            {
                RestoreSidebarInline();
                return false;
            });
        }
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
        ControlBar.IsPlaying = _playing;
        // 中央大键与控件条播放键互斥：播放中隐藏中央键
        CenterPlayButton.IsVisible = !_playing;
    }

    private void UpdateProgress()
    {
        if (_seeking) return;
        ControlBar.SetProgress(Player.Position.TotalSeconds, Player.Duration.TotalSeconds,
            Player.BufferedPosition.TotalSeconds);
    }

    /// <summary>控件条请求跳转（拖拽实时 + 点击）。</summary>
    private void OnSeekRequested(double seconds)
    {
        var total = Player.Duration.TotalSeconds;
        if (total > 0) seconds = Math.Clamp(seconds, 0, total - 1);
        if (seconds < 0) return;

        // 跳向未缓冲区域 → 显示缓冲进度（拖拽中会持续重设锚点，松手后开始推进）
        var from = Player.Position.TotalSeconds;
        if (seconds > Player.BufferedFrontier.TotalSeconds + 0.5)
            BeginBuffering(from, seconds);

        Player.Seek(TimeSpan.FromSeconds(seconds));
        ControlBar.SetProgress(seconds, total);
    }

    /// <summary>
    /// 选集按钮。
    /// <para>全屏态：切换**选集浮层**显隐（此前只是退出全屏，等于全屏下无法选集 —— 2026-09-19 用户反馈）。
    /// 非全屏态：选集栏本就常驻，这里只把控制层唤出，不做多余动作。</para>
    /// </summary>
    private void ToggleFullscreenForEpisodes()
    {
        if (_isFullscreen)
        {
            if (EpisodesOverlay.IsVisible)
            {
                EpisodesOverlay.IsVisible = false;   // 已开 → 收起
            }
            else
            {
                // 顺序要紧：**先**让浮层可见，**再**把面板搬进来。
                // 反过来（先搬进隐藏容器）宿主尺寸为 0，面板会以 0 高度完成测量而不可见。
                EpisodesOverlay.IsVisible = true;
                AttachSidebar(SidebarHost.Overlay);
            }
        }
        ShowControls();
    }

    /// <summary>
    /// 控件条副标题：集名 · 线路名（无集名时只显示线路）。
    /// <paramref name="resolvedTitle"/> 为解析出的**真实文件名**（磁力时是种子内文件名，
    /// 与站点给的打包名不同）；传入时以它为准，让用户看到正在播的具体文件。
    /// </summary>
    private void UpdateControlBarSubtitle(VodEpisode? episode, string? resolvedTitle = null)
    {
        var route = _currentSourceIndex >= 0 && _currentSourceIndex < _sources.Count
            ? DisplayNameFor(_sources[_currentSourceIndex].Name)
            : string.Empty;

        var ep = !string.IsNullOrWhiteSpace(resolvedTitle) ? resolvedTitle!.Trim() : episode?.Name?.Trim();

        ControlBar.Subtitle = string.IsNullOrEmpty(ep)
            ? route
            : string.IsNullOrEmpty(route) ? ep : $"{ep} · {route}";
    }

    private void OnSeekStarted(object? sender, EventArgs e)
    {
        // 拖动进度中：控制层常驻，倒计时作废
        _seeking = true;
        _controlsHideTimer?.Stop();
    }

    private void OnSeekCompleted(object? sender, EventArgs e)
    {
        // 拖动结束**立即落库**（不等 5s 定时）；seek 已在 SeekRequested 中实时完成
        _playback.SaveProgress(Player.Position.TotalSeconds, Player.Duration.TotalSeconds);
        _seeking = false;

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
        // 中央大键与控件条播放键互斥：播放中不显示中央键（否则出现两个「暂停」）
        CenterPlayButton.IsVisible = on && !_playing;
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
