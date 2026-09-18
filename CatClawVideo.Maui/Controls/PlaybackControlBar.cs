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

        var mainCtrls = new HorizontalStackLayout
        {
            Spacing = 6,
            VerticalOptions = LayoutOptions.Center,
            Children = { _prev, _play, _next },
        };

        var seekTools = new HorizontalStackLayout
        {
            Spacing = 6,
            VerticalOptions = LayoutOptions.Center,
            Children = { _rewind, _forward },
        };

        var tools = new HorizontalStackLayout
        {
            Spacing = 6,
            VerticalOptions = LayoutOptions.Center,
            Children = { _speed, _mute, _episodes },
        };

        var upper = new Grid
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

        upper.Add(infoHolder, 0, 0);
        upper.Add(mainCtrls, 1, 0);
        upper.Add(seekTools, 2, 0);
        upper.Add(tools, 3, 0);

        var lower = new Grid
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
        lower.Add(_posLabel, 0, 0);
        lower.Add(_seek, 1, 0);
        lower.Add(_durLabel, 2, 0);
        lower.Add(_fullscreen, 3, 0);

        var stack = new VerticalStackLayout { Spacing = 10, Children = { upper, lower } };

        return new Border
        {
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 18 },
            Stroke = new SolidColorBrush(Color.FromArgb("#1AFFFFFF")),
            BackgroundColor = Color.FromArgb("#E6100D22"),
            Padding = new Thickness(18, 14, 18, 12),
            Margin = new Thickness(24, 0, 24, 20),
            VerticalOptions = LayoutOptions.End,
            Content = stack,
        };
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
