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
/// 解码模式（对位 TVBox 的 <c>IJK_CODEC</c>「ijk硬解/ijk软解」与 <c>PLAY_TYPE</c> 内核选择）。
/// <para>为什么值得暴露给用户：某些源的 H.265/AC3 在设备硬解上会花屏或无声，而软解能放；
/// 反过来低端机软解会卡成幻灯片。TVBox 是靠换内核解决，我们两端各只有一个内核，
/// 所以抓手是「同内核内切换硬/软解码器」。</para>
/// </summary>
public enum VideoDecoderMode
{
    /// <summary>自动（默认）：优先硬件，失败时平台自行回落。</summary>
    Auto,
    /// <summary>强制硬件解码。</summary>
    Hardware,
    /// <summary>强制软件解码（兼容优先，功耗高）。</summary>
    Software,
}

/// <summary>轨道类别（对位 TVBox 播放面板的 音轨 / 字幕 / 视频轨 三个入口）。</summary>
public enum VideoTrackKind { Audio, Subtitle, Video }

/// <summary>
/// 一条可切换轨道的中间表示。
/// <para><b>为什么要自己造一层</b>：两端轨道模型根本不同 —— Android 是 Media3 的
/// <c>Tracks.TrackGroup/Track</c>（按 group+track 两级索引选），Windows 是 FFmpegInteropX 的
/// <c>{Audio,Video,Subtitle}Streams</c> 集合（按流对象自身标志选）。UI 只能面对一个统一形状，
/// 否则每加一端就要重写一遍面板。</para>
/// </summary>
/// <param name="Id">平台内定位用的稳定标识（Android 是 "组:轨"，Windows 是流下标）。</param>
/// <param name="Display">展示名（语言/标题/编解码，缺省时由平台层兜底成「轨道 N」）。</param>
/// <param name="Active">当前是否被选中/显示。</param>
public sealed record VideoTrackInfo(string Id, string Display, bool Active);

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

    /// <summary>设置播放速率（1.0 = 常速）。平台不支持时应静默忽略。</summary>
    void SetSpeed(double speed);

    /// <summary>
    /// 外挂字幕。<paramref name="path"/> 为本地文件路径或 http 地址，<b>null = 关闭字幕</b>。
    /// <paramref name="mime"/> 取 <c>SubtitleSupport.Mime*</c> 常量（播放器按 MIME 选解析器，
    /// 给错值的表现是「字幕静默不显示」而不是报错）。
    /// <paramref name="offsetSeconds"/> 是时间轴偏移：Windows 走原生延迟，
    /// Android 无该 API 故由平台层把字幕重写一份再加载。
    /// </summary>
    void SetExternalSubtitle(string? path, string? mime, double offsetSeconds);

    /// <summary>
    /// 解码模式。<b>切换会重建播放器/媒体源</b>（解码器在创建期决定，运行期改不了），
    /// 所以平台层要保住播放位置；不重建就无效的那些平台应静默忽略。
    /// </summary>
    void SetDecoderMode(VideoDecoderMode mode);

    /// <summary>
    /// 列出某类轨道。拿不到（未就绪/平台不支持）时返回空表，不抛 —— UI 据此把入口置灰。
    /// </summary>
    IReadOnlyList<VideoTrackInfo> GetTracks(VideoTrackKind kind);

    /// <summary>
    /// 选择轨道；<paramref name="id"/> 为 null = 关闭该类轨道（字幕可关，音轨/视频轨关了就无声无画，
    /// 平台层可自行回落到「自动」）。
    /// </summary>
    void SelectTrack(VideoTrackKind kind, string? id);

    TimeSpan GetPosition();
    TimeSpan GetDuration();

    /// <summary>
    /// 已缓冲到的位置（用于进度轴的缓冲条）。
    /// 无法获知时返回当前位置（表现为「不画缓冲」），不要返回 0 —— 那会让缓冲条倒退。
    /// </summary>
    TimeSpan GetBufferedPosition();

    /// <summary>
    /// 上面返回的缓冲位置是否**可信**（即「从当前点到该位置是连续可播的」）。
    ///
    /// <para><b>为什么需要</b>：磁力边下边播时平台拿不到真正的缓冲区间（Windows 的 MF 对
    /// FFmpegInteropX 包装的非自适应流不报 <c>BufferedRanges</c>），只能回落到
    /// 「已下载字节比例 × 总时长」。而 P2P 是**乱序下载**——已下载 95% 并不代表当前位置往后
    /// 连续可播，这个值会严重虚高。宿主据此算「缓冲百分比」会直接跳到 100% 卡死。</para>
    ///
    /// <para>返回 false 时宿主改用时长的经验估算，避免显示误导性的数字。</para>
    /// </summary>
    bool IsBufferedPositionReliable { get; }

    /// <summary>
    /// 播放器是否**正在等数据**（读到已缓冲范围之外 = 真饥饿）。
    ///
    /// <para><b>这是磁力路上唯一权威的缓冲信号</b>：磁力流经宿主的读前缓存代理
    /// （<c>QemuStreamProxy</c>），代理确切知道「读者有没有顶到缓存前沿等数据」——
    /// 不需要任何估算。直链路没有代理，返回 false（调用方回落位置推进判断）。</para>
    /// </summary>
    bool IsWaitingForData { get; }
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

    /// <summary>
    /// 解码模式（自动/强制硬解/强制软解）。改它会由平台层重建播放器或媒体源并保住位置 ——
    /// 解码器是创建期决定的，运行期改不动。
    /// </summary>
    public VideoDecoderMode DecoderMode
    {
        get => (VideoDecoderMode)GetValue(DecoderModeProperty);
        set => SetValue(DecoderModeProperty, value);
    }

    public static readonly BindableProperty DecoderModeProperty =
        BindableProperty.Create(nameof(DecoderMode), typeof(VideoDecoderMode), typeof(VideoPlayerView),
            VideoDecoderMode.Auto, propertyChanged: OnDecoderModeChanged);

    /// <summary>最近一次解码模式切换的失败原因（null = 正常）。</summary>
    public string? DecoderError { get; private set; }

    private static void OnDecoderModeChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var view = (VideoPlayerView)bindable;
        view.DecoderError = null;
        try { view.Implementation?.SetDecoderMode((VideoDecoderMode)newValue); }
        catch (Exception ex) { view.DecoderError = "切换解码模式失败：" + ex.Message; }
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

    /// <summary>已缓冲到的位置（进度轴缓冲条用；不支持时为当前位置。
    /// ⚠ 已被抬到不低于 <see cref="Position"/>，用于绘制进度条；要算「缓冲进度」请用
    /// <see cref="BufferedFrontier"/>）</summary>
    public TimeSpan BufferedPosition { get; private set; }

    /// <summary>
    /// 已缓冲前沿（**未**与播放位置取 max 的原始值）。
    ///
    /// <para><b>为什么单独暴露</b>：快进到未缓冲区域时，播放器的 <see cref="Position"/> 会立刻跳到
    /// 目标点，而真正下载到的前沿还停在后面 —— 「还要缓冲到这里」的百分比必须用这个原始值；
    /// 用 <see cref="BufferedPosition"/>（已抬到 position）会恒等于 100%，指示器立刻消失。</para>
    /// </summary>
    public TimeSpan BufferedFrontier { get; private set; }

    /// <summary>
    /// <see cref="BufferedFrontier"/> 是否可信（连续可播）。磁力边下边播时平台只能按
    /// 「已下载字节比例」估算，P2P 乱序下载会让该值虚高 → 宿主不用它算百分比。
    /// </summary>
    public bool IsBufferedPositionReliable { get; private set; }

    /// <summary>
    /// 播放器是否正在等数据（权威信号，仅磁力路有效）。
    /// 磁力流经 <c>QemuStreamProxy</c>，代理确切知道读者是否顶到缓存前沿 —— 无需估算。
    /// </summary>
    public bool IsWaitingForData { get; private set; }

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

    /// <summary>播放速率（1.0 = 常速）。切换源后会自动重新应用。</summary>
    public double Speed
    {
        get => _speed;
        set
        {
            var v = Math.Clamp(value, MinSpeed, MaxSpeed);
            if (Math.Abs(v - _speed) < 0.001) return;
            _speed = v;
            try { Implementation?.SetSpeed(v); } catch { }
        }
    }

    private double _speed = 1.0;

    /// <summary>可选速率档位（遥控器/触屏都在这一组里循环）。</summary>
    public static readonly double[] SpeedPresets = [0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0];
    private const double MinSpeed = 0.25, MaxSpeed = 4.0;

    // ═══════════════════ 外挂字幕 ═══════════════════

    /// <summary>字幕文件路径或地址；<b>null = 关闭</b>。赋值后立即生效（平台层重建媒体项并保持播放位置）。</summary>
    public string? SubtitlePath
    {
        get => _subtitlePath;
        set
        {
            if (_subtitlePath == value) return;
            _subtitlePath = value;
            ApplySubtitle();
        }
    }

    private string? _subtitlePath;

    /// <summary>字幕时间轴偏移秒数（正数 = 字幕延后出现）。对位 TVBox 字幕面板的 ±0.5s 步进。</summary>
    public double SubtitleOffsetSeconds
    {
        get => _subtitleOffset;
        set
        {
            if (Math.Abs(value - _subtitleOffset) < 0.0005) return;
            _subtitleOffset = Math.Clamp(value, -600, 600);
            ApplySubtitle();
        }
    }

    private double _subtitleOffset;

    /// <summary>最近一次字幕操作的失败原因（null = 正常）。UI 用它提示而不是弹异常。</summary>
    public string? SubtitleError { get; private set; }

    void ApplySubtitle()
    {
        if (string.IsNullOrEmpty(_subtitlePath))
        {
            SubtitleError = null;
            try { Implementation?.SetExternalSubtitle(null, null, 0); } catch { }
            return;
        }
        var mime = Core.Services.SubtitleSupport.InferMime(_subtitlePath);
        if (mime is null)
        {
            SubtitleError = "不支持的字幕后缀（可用 .srt / .vtt / .ass / .ssa / .ttml）";
            return;
        }
        SubtitleError = null;
        try { Implementation?.SetExternalSubtitle(_subtitlePath, mime, _subtitleOffset); }
        catch (Exception ex) { SubtitleError = "字幕加载失败：" + ex.Message; }
    }

    // ═══════════════════ 轨道选择 ═══════════════════

    /// <summary>
    /// 列出某类轨道。未就绪或平台拿不到时返回**空表**（不抛）—— UI 据此把入口置灰，
    /// 而不是让用户点了之后面对一个空弹窗。
    /// </summary>
    public IReadOnlyList<VideoTrackInfo> GetTracks(VideoTrackKind kind)
    {
        try { return Implementation?.GetTracks(kind) ?? Array.Empty<VideoTrackInfo>(); }
        catch { return Array.Empty<VideoTrackInfo>(); }
    }

    /// <summary>切到指定轨道。<paramref name="id"/> 为 null = 关闭（字幕）或恢复自动（音/视频轨）。</summary>
    public void SelectTrack(VideoTrackKind kind, string? id)
    {
        try { Implementation?.SelectTrack(kind, id); } catch { }
    }

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

        // 换源/换集后重新套用**速率与音量**：换媒体会把它们重置回默认值，
        // 不补的话用户会觉得「切了一集倍速就丢了 / 静音自己解除了」。
        try { Implementation?.SetSpeed(_speed); } catch { }
        try { Implementation?.SetVolume(Volume); } catch { }

        // 字幕同理：平台层的媒体项是按「一集」重建的，不补挂的话换集字幕就静默消失
        if (!string.IsNullOrEmpty(_subtitlePath)) ApplySubtitle();
    }

    // ═══════════════════ 平台层回调 ═══════════════════

    /// <summary>平台层上报状态变化（由 Handler 调用）</summary>
    internal void RaiseStateChanged(VideoPlayerState state, string? error = null)
    {
        ErrorMessage = state == VideoPlayerState.Failed
            ? (error ?? "播放失败，请检查网络或链接是否有效")
            : null;

        UpdateState(state);

        // 播放中 / 缓冲中都开轮询：
        //   - 播放中：刷新 Position/Duration，驱动进度条
        //   - 缓冲中：Position 不动但**缓冲前沿在推进**，缓冲百分比要靠它刷新
        //     （此前缓冲态不轮询，宿主拿不到前沿变化 → 百分比不会动）
        if (state is VideoPlayerState.Playing or VideoPlayerState.Buffering) StartPositionTimer();
        else StopPositionTimer();
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

        // 缓冲位置单独取：个别平台/源拿不到准确值，失败时退化为当前位置（不画缓冲条）
        TimeSpan buf;
        try { buf = Implementation.GetBufferedPosition(); }
        catch { buf = pos; }

        // 原始前沿先留档（算缓冲百分比用），再做「不低于播放位置」的抬升（画进度条用）
        var frontier = buf;
        if (buf < pos) buf = pos;   // 缓冲绝不落后于播放位置，否则进度条会「倒退」

        // 可信度：由平台层判定（磁力按字节估算时为 false）
        bool reliable;
        try { reliable = Implementation.IsBufferedPositionReliable; }
        catch { reliable = false; }

        bool waiting;
        try { waiting = Implementation.IsWaitingForData; }
        catch { waiting = false; }

        Position = pos;
        if (dur > Duration) Duration = dur;
        BufferedPosition = buf;
        BufferedFrontier = frontier;
        IsBufferedPositionReliable = reliable;
        IsWaitingForData = waiting;

        // **无条件**每 250ms 上报一次（不再按「值有变化」过滤）：
        //   ① 卡住时 Position/前沿都不变，按变化上报会让宿主的缓冲指示器僵死；
        //   ② 磁力流 MF 可能一边正常播放一边把 PlaybackState 报成 Buffering，
        //      宿主只能靠「位置有没有推进」判断真假缓冲 —— 那需要连续的采样点。
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }
}
