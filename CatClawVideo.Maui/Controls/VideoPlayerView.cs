namespace CatClawVideo.Maui.Controls;

/// <summary>画面比例</summary>
public enum VideoAspect
{
    /// <summary>保持比例，完整显示（默认）</summary>
    AspectFit,
    /// <summary>保持比例，填满裁切</summary>
    AspectFill,
    /// <summary>拉伸填满（变形）</summary>
    Fill,
}

/// <summary>播放器状态</summary>
public enum VideoPlayerState
{
    /// <summary>未加载/初始</summary>
    Idle,
    /// <summary>加载中</summary>
    Preparing,
    /// <summary>缓冲中</summary>
    Buffering,
    /// <summary>播放中</summary>
    Playing,
    /// <summary>暂停</summary>
    Paused,
    /// <summary>停止（含自然播完）</summary>
    Stopped,
    /// <summary>失败</summary>
    Failed,
}

/// <summary>
/// 平台播放器实现契约（由各平台 Handler 提供并注入回 View）。
/// View 层驱动平台实现，事件由实现层推送回 View 层。
/// </summary>
public interface IVideoPlayerImplementation
{
    /// <summary>加载媒体地址（headers 供 TVBox 源 Referer/UA 防盗链使用，Windows 平台暂忽略）</summary>
    void SetSource(string? url, IReadOnlyDictionary<string, string>? headers);

    void Play();
    void Pause();
    void Stop();
    void Seek(TimeSpan position);
    void SetVolume(double volume);
    void SetAspect(VideoAspect aspect);
    void KeepScreenOn(bool keep);

    TimeSpan GetPosition();
    TimeSpan GetDuration();
}

/// <summary>
/// 跨平台视频播放器控件：Android 用 Media3 ExoPlayer，Windows 用 WinUI MediaPlayer。
/// 相比框架/Toolkit 的 MediaElement，支持自定义请求头（TVBox 源防盗链必需）与完全自绘控制层。
/// </summary>
public partial class VideoPlayerView : View
{
    // ═══════════════════ BindableProperty ═══════════════════

    public static readonly BindableProperty SourceProperty =
        BindableProperty.Create(nameof(Source), typeof(string), typeof(VideoPlayerView), null,
            propertyChanged: OnSourceChanged);

    public static readonly BindableProperty ShouldAutoPlayProperty =
        BindableProperty.Create(nameof(ShouldAutoPlay), typeof(bool), typeof(VideoPlayerView), true);

    public static readonly BindableProperty VolumeProperty =
        BindableProperty.Create(nameof(Volume), typeof(double), typeof(VideoPlayerView), 1.0,
            coerceValue: CoerceVolume,
            propertyChanged: OnVolumeChanged);

    public static readonly BindableProperty AspectProperty =
        BindableProperty.Create(nameof(Aspect), typeof(VideoAspect), typeof(VideoPlayerView),
            VideoAspect.AspectFit, propertyChanged: OnAspectChanged);

    public static readonly BindableProperty ShouldKeepScreenOnProperty =
        BindableProperty.Create(nameof(ShouldKeepScreenOn), typeof(bool), typeof(VideoPlayerView), true,
            propertyChanged: OnKeepScreenOnChanged);

    /// <summary>请求头（Referer / User-Agent，TVBox 源防盗链）</summary>
    public static readonly BindableProperty HeadersProperty =
        BindableProperty.Create(nameof(Headers), typeof(IReadOnlyDictionary<string, string>), typeof(VideoPlayerView), null,
            propertyChanged: OnHeadersChanged);

    // ═══════════════════ 公开属性 ═══════════════════

    /// <summary>媒体地址（http/https 直链 m3u8/mp4）</summary>
    public string? Source
    {
        get => (string?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>加载完成后自动播放</summary>
    public bool ShouldAutoPlay
    {
        get => (bool)GetValue(ShouldAutoPlayProperty);
        set => SetValue(ShouldAutoPlayProperty, value);
    }

    /// <summary>音量 0.0 ~ 1.0</summary>
    public double Volume
    {
        get => (double)GetValue(VolumeProperty);
        set => SetValue(VolumeProperty, value);
    }

    /// <summary>画面比例</summary>
    public VideoAspect Aspect
    {
        get => (VideoAspect)GetValue(AspectProperty);
        set => SetValue(AspectProperty, value);
    }

    /// <summary>播放期间保持屏幕常亮</summary>
    public bool ShouldKeepScreenOn
    {
        get => (bool)GetValue(ShouldKeepScreenOnProperty);
        set => SetValue(ShouldKeepScreenOnProperty, value);
    }

    /// <summary>自定义请求头（加载前设置）</summary>
    public IReadOnlyDictionary<string, string>? Headers
    {
        get => (IReadOnlyDictionary<string, string>?)GetValue(HeadersProperty);
        set => SetValue(HeadersProperty, value);
    }

    // ═══════════════════ 运行时状态（由平台层回写） ═══════════════════

    /// <summary>当前播放状态</summary>
    public VideoPlayerState CurrentState { get; private set; } = VideoPlayerState.Idle;

    /// <summary>是否正在播放</summary>
    public bool IsPlaying => CurrentState == VideoPlayerState.Playing;

    /// <summary>当前播放位置</summary>
    public TimeSpan Position { get; private set; }

    /// <summary>总时长（未加载为 0）</summary>
    public TimeSpan Duration { get; private set; }

    /// <summary>失败原因（Failed 状态时有效）</summary>
    public string? ErrorMessage { get; private set; }

    // ═══════════════════ 事件 ═══════════════════

    public event EventHandler? MediaOpened;
    public event EventHandler? MediaFailed;
    public event EventHandler? MediaEnded;
    public event EventHandler? StateChanged;
    public event EventHandler? PositionChanged;

    /// <summary>平台实现（Handler 连接时注入）</summary>
    internal IVideoPlayerImplementation? Implementation { get; set; }

    /// <summary>位置轮询定时器（播放期间 250ms 刷新 Position/Duration）</summary>
    private IDispatcherTimer? _positionTimer;

    /// <summary>当前媒体是否已成功打开（用于 MediaOpened 只触发一次）</summary>
    private bool _mediaOpened;

    public VideoPlayerView()
    {
        try
        {
            _positionTimer = Dispatcher.CreateTimer();
            _positionTimer.Interval = TimeSpan.FromMilliseconds(250);
            _positionTimer.Tick += (_, _) => PollPosition();
        }
        catch { /* Dispatcher 尚不可用时延后（首次 Play 再建） */ }
    }

    // ═══════════════════ 播放命令 ═══════════════════

    public void Play() => Implementation?.Play();

    public void Pause() => Implementation?.Pause();

    public void Stop() => Implementation?.Stop();

    public void Seek(TimeSpan position) => Implementation?.Seek(position);

    // ═══════════════════ 属性变更路由 ═══════════════════

    private static void OnSourceChanged(BindableObject bindable, object oldValue, object newValue)
        => ((VideoPlayerView)bindable).ApplySource();

    private static void OnHeadersChanged(BindableObject bindable, object oldValue, object newValue)
        => ((VideoPlayerView)bindable).ApplySource();

    private static void OnVolumeChanged(BindableObject bindable, object oldValue, object newValue)
        => ((VideoPlayerView)bindable).Implementation?.SetVolume((double)newValue);

    private static void OnAspectChanged(BindableObject bindable, object oldValue, object newValue)
        => ((VideoPlayerView)bindable).Implementation?.SetAspect((VideoAspect)newValue);

    private static void OnKeepScreenOnChanged(BindableObject bindable, object oldValue, object newValue)
        => ((VideoPlayerView)bindable).Implementation?.KeepScreenOn((bool)newValue);

    private static object CoerceVolume(BindableObject bindable, object value)
        => Math.Clamp((double)value, 0.0, 1.0);

    /// <summary>应用媒体源：重置状态并交给平台层加载</summary>
    private void ApplySource()
    {
        _mediaOpened = false;
        ErrorMessage = null;
        Position = TimeSpan.Zero;
        Duration = TimeSpan.Zero;
        UpdateState(VideoPlayerState.Idle);
        StopPositionTimer();

        var url = Source;
        if (string.IsNullOrEmpty(url))
        {
            Implementation?.SetSource(null, Headers);
            return;
        }

        UpdateState(VideoPlayerState.Preparing);
        Implementation?.SetSource(url, Headers);
    }

    // ═══════════════════ 平台层回调 ═══════════════════

    /// <summary>平台层上报状态变化（由 Handler 调用）</summary>
    internal void RaiseStateChanged(VideoPlayerState state, string? error = null)
    {
        ErrorMessage = state == VideoPlayerState.Failed
            ? (error ?? "播放失败，请检查网络或链接是否有效")
            : null;

        UpdateState(state);

        // 播放中开轮询，其他状态停轮询
        if (state == VideoPlayerState.Playing) StartPositionTimer();
        else if (state != VideoPlayerState.Buffering) StopPositionTimer();
    }

    /// <summary>平台层上报媒体打开成功（仅首个 READY 时由 Handler 触发一次）</summary>
    internal void RaiseMediaOpened()
    {
        if (_mediaOpened) return;
        _mediaOpened = true;

        Duration = Implementation?.GetDuration() ?? TimeSpan.Zero;
        MediaOpened?.Invoke(this, EventArgs.Empty);

        if (ShouldAutoPlay)
        {
            Implementation?.KeepScreenOn(ShouldKeepScreenOn);
            Implementation?.Play();
        }
    }

    /// <summary>平台层上报播放结束（自然播完）</summary>
    internal void RaiseMediaEnded()
    {
        StopPositionTimer();
        PollPosition();
        UpdateState(VideoPlayerState.Stopped);
        MediaEnded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>平台层上报失败</summary>
    internal void RaiseMediaFailed(string message)
    {
        StopPositionTimer();
        ErrorMessage = string.IsNullOrEmpty(message)
            ? "无法加载该地址，请检查网络或链接是否有效"
            : message;
        UpdateState(VideoPlayerState.Failed);
        MediaFailed?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateState(VideoPlayerState state)
    {
        if (CurrentState == state) return;
        CurrentState = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ═══════════════════ 位置轮询 ═══════════════════

    private void StartPositionTimer()
    {
        if (_positionTimer == null)
        {
            try
            {
                _positionTimer = Dispatcher.CreateTimer();
                _positionTimer.Interval = TimeSpan.FromMilliseconds(250);
                _positionTimer.Tick += (_, _) => PollPosition();
            }
            catch { return; }
        }
        if (!_positionTimer.IsRunning) _positionTimer.Start();
    }

    private void StopPositionTimer()
    {
        if (_positionTimer?.IsRunning == true) _positionTimer.Stop();
    }

    private void PollPosition()
    {
        if (Implementation == null) return;
        var pos = Implementation.GetPosition();
        var dur = Implementation.GetDuration();
        if (pos != Position || dur != Duration)
        {
            Position = pos;
            if (dur > Duration) Duration = dur;
            PositionChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
