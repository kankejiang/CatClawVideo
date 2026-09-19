using Microsoft.Maui.Controls.Shapes;

namespace CatClawVideo.Maui.Controls;

/// <summary>
/// 播放控件条（方案 B · 悬浮双行面板）。
///
/// <para>形态：不贴边的圆角玻璃面板，上行 = 「标题信息 + 主控键 + 工具键」，下行 = 「时间 + 进度轴 + 全屏」。
/// 播放页（全屏页）与观看页（内嵌小窗）共用同一份控件，避免两处各写一套。</para>
///
/// <para>本控件只负责<b>展示与用户意图</b>，把动作以事件抛给宿主页面 ——
/// 播放器实例、续看落库、选集栏显隐等页面级逻辑仍留在各自页面。</para>
/// </summary>
public sealed class PlaybackControlBar : ContentView
{
    // ─────────── 事件 ───────────
    public event EventHandler? PlayPauseRequested;
    public event EventHandler? PrevEpisodeRequested;
    public event EventHandler? NextEpisodeRequested;
    public event EventHandler? RewindRequested;
    public event EventHandler? ForwardRequested;
    public event EventHandler? SpeedRequested;
    public event EventHandler? EpisodesRequested;
    public event EventHandler? FullscreenRequested;
    public event EventHandler<bool>? MuteChanged;
    public event EventHandler? SeekStarted;
    public event EventHandler? SeekCompleted;
    /// <summary>请求跳转到指定秒数（拖拽中会持续触发）。</summary>
    public event EventHandler<double>? SeekRequested;

    // ─────────── 子控件（全部在构造函数内初始化：字段初始化器早于构造体执行，
    //              但不能跨字段互相引用，故统一放构造体，避免「先赋值后创建」的空引用）───────────
    private readonly Label _title;
    private readonly Label _subtitle;
    private readonly Controls.IconButton _prev;
    private readonly Controls.IconButton _play;
    private readonly Controls.IconButton _next;
    private readonly Controls.IconButton _rewind;
    private readonly Controls.IconButton _forward;
    private readonly Controls.IconButton _speed;
    private readonly Controls.IconButton _episodes;
    private readonly Controls.IconButton _mute;
    private readonly Controls.IconButton _fullscreen;
    private readonly Label _posLabel;
    private readonly Label _durLabel;
    private readonly SeekBarView _seek;

    private bool _isPlaying;
    private bool _isMuted;
    private double _speedValue = 1.0;

    // 缩放时需要回改的容器/面板引用（在 BuildPanel 里赋值）
    private Border? _panel;
    private VerticalStackLayout? _stack;
    private Grid? _upper;
    private Grid? _lower;
    private HorizontalStackLayout? _mainCtrls;
    private HorizontalStackLayout? _seekTools;
    private HorizontalStackLayout? _tools;

    private double _uiScale = 1.0;

    /// <summary>
    /// 控件条整体缩放（1.0 = 桌面默认）。
    ///
    /// <para><b>为什么需要</b>：按钮与字号原本是写死的桌面尺寸（按钮 38~42、字号 13~14）。
    /// 手机端观看页里播放框本身只有半屏宽，这套尺寸就「吃掉」大半个画面（2026-09-19 实机反馈）。
    /// 由宿主按**播放框尺寸**设置缩放，控件条整体等比缩小。</para>
    /// </summary>
    public double UiScale
    {
        get => _uiScale;
        set
        {
            var v = Math.Clamp(value, 0.55, 1.0);
            if (Math.Abs(v - _uiScale) < 0.02) return;
            _uiScale = v;
            ApplyScale();
        }
    }

    public PlaybackControlBar()
    {
        _title = new Label { Text = "未命名", FontSize = 14, FontFamily = "OpenSansSemibold", TextColor = Colors.White, LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 1 };
        _subtitle = new Label { FontSize = 11, TextColor = Color.FromArgb("#BCC0DD"), Margin = new Thickness(0, 2, 0, 0), LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 1 };
        _posLabel = new Label { Text = "00:00", FontSize = 13, FontFamily = "OpenSansSemibold", TextColor = Colors.White, MinimumWidthRequest = 56, HorizontalTextAlignment = TextAlignment.Center, VerticalOptions = LayoutOptions.Center };
        _durLabel = new Label { Text = "--:--", FontSize = 13, TextColor = Color.FromArgb("#BCC0DD"), MinimumWidthRequest = 56, HorizontalTextAlignment = TextAlignment.Center, VerticalOptions = LayoutOptions.Center };
        _seek = new SeekBarView();

        // 上行按钮一律用**文字**（2026-09-19 用户要求）：符号（‹ ▶ » « ☰）含义不直观，
        // 中文标签一眼可懂。AutoWidth = 宽度随文案自适应，MinWidth 防文案长短切换时整排跳动。
        _prev = new Controls.IconButton("上一集", "上一集", () => PrevEpisodeRequested?.Invoke(this, EventArgs.Empty))
        { Size = 38, AutoWidth = true, MinWidth = 60 };
        _play = new Controls.IconButton("播放", "播放 / 暂停", () => PlayPauseRequested?.Invoke(this, EventArgs.Empty))
        { Size = 42, IsPrimary = true, AutoWidth = true, MinWidth = 62 };
        _next = new Controls.IconButton("下一集", "下一集", () => NextEpisodeRequested?.Invoke(this, EventArgs.Empty))
        { Size = 38, AutoWidth = true, MinWidth = 60 };
        _rewind = new Controls.IconButton("后退10秒", "后退 10 秒", () => RewindRequested?.Invoke(this, EventArgs.Empty))
        { Size = 38, AutoWidth = true, MinWidth = 72 };
        _forward = new Controls.IconButton("前进10秒", "前进 10 秒", () => ForwardRequested?.Invoke(this, EventArgs.Empty))
        { Size = 38, AutoWidth = true, MinWidth = 72 };
        _speed = new Controls.IconButton("1.0×", "播放倍速", () => SpeedRequested?.Invoke(this, EventArgs.Empty))
        { Size = 38, AutoWidth = true, MinWidth = 52, IsText = true };
        _episodes = new Controls.IconButton("选集", "选集", () => EpisodesRequested?.Invoke(this, EventArgs.Empty))
        { Size = 38, AutoWidth = true, MinWidth = 50 };
        _mute = new Controls.IconButton("静音", "静音", ToggleMute)
        { Size = 38, AutoWidth = true, MinWidth = 66 };
        _fullscreen = new Controls.IconButton("⛶", "全屏", () => FullscreenRequested?.Invoke(this, EventArgs.Empty)) { Size = 40 };

        _seek.SeekRequested += (_, seconds) => SeekRequested?.Invoke(this, seconds);
        _seek.SeekStarted += (_, _) => SeekStarted?.Invoke(this, EventArgs.Empty);
        _seek.SeekCompleted += (_, _) => SeekCompleted?.Invoke(this, EventArgs.Empty);

        Content = BuildPanel();
        ApplyScale();   // 用初始 UiScale 校准一遍（宿主随后可改）
    }

    // ─────────── 遥控器焦点（按钮间移动） ───────────

    /// <summary>上行按钮的视觉顺序（进度条不参与：它是拖动条，焦点语义与按钮不同）。</summary>
    private IconButton[] AllButtons => [_prev, _play, _next, _rewind, _forward, _speed, _episodes, _mute, _fullscreen];

    /// <summary>当前**可见**的按钮 —— 隐藏的按钮不能留在焦点序列里（否则焦点会落在看不见的键上）。</summary>
    private List<IconButton> VisibleButtons => AllButtons.Where(b => b.IsVisible).ToList();

    /// <summary>焦点所在按钮下标；<c>-1</c> = 未聚焦。</summary>
    public int FocusIndex { get; private set; } = -1;

    /// <summary>本控件是否持有遥控器焦点。</summary>
    public bool FocusEngaged { get; private set; }

    /// <summary>把焦点交给控制条（落在上次停留的按钮上，没有则第一个）。</summary>
    public void FocusFirst()
    {
        var list = VisibleButtons;
        if (list.Count == 0) return;

        FocusEngaged = true;
        SetIndex(FocusIndex < 0 ? 0 : Math.Min(FocusIndex, list.Count - 1));
    }

    /// <summary>收走焦点（熄掉所有按钮的焦点态）。</summary>
    public void Blur()
    {
        FocusEngaged = false;
        foreach (var b in AllButtons) b.IsFocused = false;
    }

    /// <summary>←/→ 移动焦点。返回 <c>false</c> = 已到两端（宿主播键换区）。</summary>
    public bool MoveFocus(int dir)
    {
        if (!FocusEngaged) return false;

        int next = FocusIndex + dir;
        if (next < 0 || next >= VisibleButtons.Count) return false;
        SetIndex(next);
        return true;
    }

    /// <summary>回车：触发当前按钮（走它自己的点击委托，与鼠标点击同一条路径）。</summary>
    public void ActivateFocus()
    {
        var list = VisibleButtons;
        if (!FocusEngaged || FocusIndex < 0 || FocusIndex >= list.Count) return;
        list[FocusIndex].Invoke();
    }

    private void SetIndex(int index)
    {
        var list = VisibleButtons;
        FocusIndex = Math.Clamp(index, 0, Math.Max(0, list.Count - 1));
        for (int i = 0; i < list.Count; i++) list[i].IsFocused = i == FocusIndex;
    }

    /// <summary>当前焦点按钮的说明（底部提示条用）。</summary>
    public string FocusedTooltip
    {
        get
        {
            var list = VisibleButtons;
            return FocusIndex >= 0 && FocusIndex < list.Count ? list[FocusIndex].Tooltip : string.Empty;
        }
    }

    // ─────────── 可配置项 ───────────

    /// <summary>是否显示「选集」按钮（全屏播放页没有选集栏，应关闭）。</summary>
    public bool ShowEpisodesButton
    {
        get => _episodes.IsVisible;
        set => _episodes.IsVisible = value;
    }

    /// <summary>是否显示「上一集/下一集」（单集视频可关闭）。</summary>
    public bool ShowEpisodeStepButtons
    {
        get => _prev.IsVisible;
        set { _prev.IsVisible = value; _next.IsVisible = value; }
    }

    /// <summary>面板主标题（影片名）。</summary>
    public string Title
    {
        get => _title.Text ?? string.Empty;
        set => _title.Text = value;
    }

    /// <summary>副标题（如「第 12 集 · 线路二」）。</summary>
    public string Subtitle
    {
        get => _subtitle.Text ?? string.Empty;
        set => _subtitle.Text = value;
    }

    // ─────────── 状态同步 ───────────

    /// <summary>播放/暂停状态（切换播放键文案）。</summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (_isPlaying == value) return;
            _isPlaying = value;
            _play.Glyph = value ? "暂停" : "播放";
        }
    }

    /// <summary>倍速显示值（如 1.0 →「1.0×」）。</summary>
    public double SpeedValue
    {
        get => _speedValue;
        set
        {
            _speedValue = value;
            _speed.Glyph = $"{value:0.0}×";
        }
    }

    /// <summary>全屏按钮图标（进入/退出）。</summary>
    public bool IsFullscreen
    {
        get => _isFullscreen;
        set
        {
            _isFullscreen = value;
            _fullscreen.Glyph = value ? "⤡" : "⛶";
            _fullscreen.Tooltip = value ? "退出全屏" : "全屏";
        }
    }
    private bool _isFullscreen;

    /// <summary>一次性更新进度相关的全部状态。</summary>
    public void SetProgress(double positionSeconds, double durationSeconds, double bufferedSeconds = 0)
    {
        _seek.Position = positionSeconds;
        _seek.Duration = durationSeconds;
        _seek.Buffered = bufferedSeconds > 0 ? bufferedSeconds : positionSeconds;
        _posLabel.Text = FormatTime(positionSeconds);
        _durLabel.Text = durationSeconds > 0 ? FormatTime(durationSeconds) : "--:--";
    }

    /// <summary>焦点落在进度轴上（遥控器导航用）。</summary>
    public bool IsSeekFocused
    {
        get => _seek.IsHighlighted;
        set => _seek.IsHighlighted = value;
    }

    /// <summary>把焦点环画在进度轴上（供页面在方向键导航时调用）。</summary>
    public void FocusSeek() => _seek.IsHighlighted = true;

    /// <summary>静音状态（页面可在换集/重开时回填，保证按钮文案与实际音量一致）。</summary>
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (_isMuted == value) return;
            _isMuted = value;
            RenderMute();
        }
    }

    private void ToggleMute()
    {
        _isMuted = !_isMuted;
        RenderMute();
        MuteChanged?.Invoke(this, _isMuted);
    }

    private void RenderMute()
    {
        _mute.Glyph = _isMuted ? "取消静音" : "静音";
        _mute.Tooltip = _isMuted ? "取消静音" : "静音";
    }

    // ─────────── 布局 ───────────

    private View BuildPanel()
    {
        var info = new VerticalStackLayout
        {
            Spacing = 0,
            VerticalOptions = LayoutOptions.Center,
            Children = { _title, _subtitle },
        };

        _mainCtrls = new HorizontalStackLayout
        {
            Spacing = 6,
            VerticalOptions = LayoutOptions.Center,
            Children = { _prev, _play, _next },
        };

        _seekTools = new HorizontalStackLayout
        {
            Spacing = 6,
            VerticalOptions = LayoutOptions.Center,
            Children = { _rewind, _forward },
        };

        _tools = new HorizontalStackLayout
        {
            Spacing = 6,
            VerticalOptions = LayoutOptions.Center,
            Children = { _speed, _mute, _episodes },
        };

        _upper = new Grid
        {
            ColumnSpacing = 16,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
            },
        };
        // 按钮改成中文标签后整行变宽（8 个按钮实测约 560px）。窗口窄/播放框小时，
        // 优先牺牲标题区（Star 列）而不是让按钮溢出被裁 —— 标题本身已有 TailTruncation，
        // 按钮被裁则等于功能不可用。最小宽度 90 保住「至少能看清片名前几个字」。
        var infoHolder = new Grid { MinimumWidthRequest = 90 };
        infoHolder.Add(info);

        _upper.Add(infoHolder, 0, 0);
        _upper.Add(_mainCtrls, 1, 0);
        _upper.Add(_seekTools, 2, 0);
        _upper.Add(_tools, 3, 0);

        _lower = new Grid
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
        _lower.Add(_posLabel, 0, 0);
        _lower.Add(_seek, 1, 0);
        _lower.Add(_durLabel, 2, 0);
        _lower.Add(_fullscreen, 3, 0);

        _stack = new VerticalStackLayout { Spacing = 10, Children = { _upper, _lower } };

        _panel = new Border
        {
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 18 },
            Stroke = new SolidColorBrush(Color.FromArgb("#1AFFFFFF")),
            BackgroundColor = Color.FromArgb("#E6100D22"),
            Padding = new Thickness(18, 14, 18, 12),
            Margin = new Thickness(24, 0, 24, 20),
            VerticalOptions = LayoutOptions.End,
            Content = _stack,
        };
        return _panel;
    }

    /// <summary>
    /// 由播放区尺寸推算控件条缩放（宿主在 SizeChanged 里调用）。
    ///
    /// <list type="bullet">
    /// <item>以**宽度**为基准：900dp 及以上不缩放（桌面窗口就是这个量级，体感正常）；</item>
    /// <item>手机端再压一档（上限 0.85）—— 同一 dp 尺寸在手机的物理屏上体感更大，
    ///   观看页的内嵌小窗尤其明显（2026-09-19 实机反馈「控件太大」）；</item>
    /// <item>下限 0.6 兜住可用性（再小就点不准了）。</item>
    /// </list>
    /// </summary>
    public static double ScaleFor(double width)
    {
        var s = width / 900.0;
#if ANDROID
        s = Math.Min(s, 0.85);
#endif
        return Math.Clamp(s, 0.6, 1.0);
    }

    /// <summary>
    /// 按 <see cref="UiScale"/> 重算所有写死的桌面尺寸（字号 / 按钮 / 间距 / 内边距 / 圆角）。
    /// 基准值保持原样写在上面，缩放只在这里统一施加，改基准时不必到处乘以系数。
    /// </summary>
    private void ApplyScale()
    {
        var s = _uiScale;

        _title.FontSize = 14 * s;
        _subtitle.FontSize = 11 * s;
        _posLabel.FontSize = 13 * s;
        _durLabel.FontSize = 13 * s;
        _posLabel.MinimumWidthRequest = 56 * s;
        _durLabel.MinimumWidthRequest = 56 * s;

        foreach (var b in new[] { _prev, _play, _next, _rewind, _forward, _speed, _episodes, _mute, _fullscreen })
        {
            b.UiScale = s;
            b.ApplyScale();
        }

        if (_mainCtrls is not null) _mainCtrls.Spacing = 6 * s;
        if (_seekTools is not null) _seekTools.Spacing = 6 * s;
        if (_tools is not null) _tools.Spacing = 6 * s;
        if (_stack is not null) _stack.Spacing = 10 * s;
        if (_upper is not null) _upper.ColumnSpacing = 16 * s;
        if (_lower is not null) _lower.ColumnSpacing = 12 * s;

        if (_panel is not null)
        {
            _panel.Padding = new Thickness(18 * s, 14 * s, 18 * s, 12 * s);
            _panel.Margin = new Thickness(24 * s, 0, 24 * s, 20 * s);
            _panel.StrokeShape = new RoundRectangle { CornerRadius = 18 * s };
        }
    }

    internal static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes:D2}:{t.Seconds:D2}";
    }

    /// <summary>主题切换后刷新配色。</summary>
    public void RefreshThemeColors()
    {
        _play.RefreshThemeColors();
        _seek.Invalidate();
    }
}
