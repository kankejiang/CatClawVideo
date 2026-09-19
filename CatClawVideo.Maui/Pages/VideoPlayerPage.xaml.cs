using CatClawVideo.Maui.Controls;
using CatClawVideo.Maui.Services;
using CatClawVideo.Maui.ViewModels;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 视频播放页：自研 VideoPlayerView + 自定义控制层（播放/暂停、进度拖动、横竖屏、自动隐藏）。
/// 播放状态由 ViewModel 承载，播放器实例由页面持有（平台控件不可进 VM）。
/// </summary>
public partial class VideoPlayerPage : ContentPage, IQueryAttributable
{
    private readonly VideoPlayerViewModel _vm;
    private readonly VideoPlaybackManager _playback;

    /// <summary>控制层自动隐藏计时器（播放中 3.5s 无操作隐藏）</summary>
    private IDispatcherTimer? _hideTimer;

    /// <summary>断点续播起始位置（秒）：观看页全屏入口携带，MediaOpened 后 seek（0 = 从头播）</summary>
    private double _startPosition;

    // ─────────── 缓冲进度指示（圆环 + 中央百分比；与 WatchPage 同一套语义） ───────────

    /// <summary>本次缓冲的开始时刻（「等太久就切旋转弧」的判定用）。</summary>
    private DateTime _bufferStartUtc = DateTime.UtcNow;

    /// <summary>起播缓冲的参考量：仅作进度环分母，不参与任何播放时机决策。</summary>
    private const double StartupBufferSeconds = 8.0;

    /// <summary>超过这个时长且连续可播量几乎没涨 → 切回旋转弧。</summary>
    private const double LongWaitSeconds = 25.0;

    /// <summary>上次采样的播放位置（判断「是否真的在播」）。</summary>
    private double _bufferLastPos = -1;

    /// <summary>已确认播放位置在推进。用于压制 MF 对磁力流的 Buffering 误报。</summary>
    private bool _playbackAdvancing;

    public VideoPlayerPage(VideoPlayerViewModel vm, VideoPlaybackManager playback)
    {
        InitializeComponent();
        _vm = vm;
        _playback = playback;
        BindingContext = _vm;

        // 控件条随播放区尺寸自适应（手机端再压一档，见 PlaybackControlBar.ScaleFor）
        ControlBar.SizeChanged += (_, _) =>
            ControlBar.UiScale = Controls.PlaybackControlBar.ScaleFor(ControlBar.Width);

        // Android 显示横竖屏切换按钮
#if ANDROID
        RotateButton.IsVisible = true;
#endif

        // 播放器事件 → VM 状态
        Player.MediaOpened += OnMediaOpened;
        Player.PositionChanged += OnPositionChanged;
        Player.StateChanged += OnStateChanged;
        Player.MediaFailed += OnMediaFailed;
        Player.MediaEnded += OnMediaEnded;

        // 控件条事件 → 播放器操作（方案 B）
        ControlBar.ShowEpisodesButton = false;   // 全屏播放页无选集栏
        ControlBar.ShowEpisodeStepButtons = false;
        ControlBar.PlayPauseRequested += (_, _) => OnPlayPauseClicked(this, EventArgs.Empty);
        ControlBar.RewindRequested += (_, _) => SeekRelative(-10);
        ControlBar.ForwardRequested += (_, _) => SeekRelative(10);
        ControlBar.FullscreenRequested += (_, _) => ToggleLandscape();
        ControlBar.SeekStarted += (_, _) => OnSeekStarted(this, EventArgs.Empty);
        ControlBar.SeekCompleted += (_, _) => OnSeekCompleted(this, EventArgs.Empty);
        ControlBar.SeekRequested += (_, seconds) => OnSeekRequested(seconds);
        ControlBar.SpeedRequested += (_, _) => CycleSpeed();
        ControlBar.MuteChanged += (_, muted) => ApplyMute(muted);

        _hideTimer = Dispatcher.CreateTimer();
        _hideTimer.Interval = TimeSpan.FromSeconds(3.5);
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => OnHideTimerTick();
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("title", out var titleObj) && titleObj is string title)
            _vm.Title = title;
        if (query.TryGetValue("url", out var urlObj) && urlObj is string url)
            _vm.Url = url;
        if (query.TryGetValue("pos", out var posObj) && posObj is string posStr &&
            double.TryParse(posStr, System.Globalization.CultureInfo.InvariantCulture, out var pos) && pos > 0)
            _startPosition = pos;
        if (query.TryGetValue("cover", out var coverObj) && coverObj is string cover && cover.Length > 0)
            _vm.Cover = cover;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
#if ANDROID
        // 本页即全屏播放页：隐藏系统栏（状态栏 + 导航栏）
        MainActivity.SetImmersive(true);
#endif
        if (!string.IsNullOrEmpty(_vm.Url))
        {
            StartPlayback();
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _hideTimer?.Stop();

#if ANDROID
        // 恢复系统栏
        MainActivity.SetImmersive(false);

        // 退出播放页恢复 App 基准横屏：旋转按钮切的竖屏只应在播放页内生效，
        // 否则返回后整个 App 会停在竖屏，与基准横屏不一致。
        (Application.Current as App)?.ForceLandscape();
#endif

        try
        {
            _playback.EndSession(_vm.PositionSeconds, _vm.DurationSeconds);
            Player.Stop();
            Player.Source = null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Player] 退出清理失败: {ex.Message}");
        }

        // 退出播放页 = 本次播放结束：通知磁力引擎收尾（QEMU 引擎会冻结 VM，
        // 让迅雷侧下载立刻停下；任务与已下数据保留，回来续播不必冷启动）。
        try
        {
            (CatClawVideo.Core.Interfaces.MagnetEngines.Thunder
                as CatClawVideo.Core.Interfaces.IPlaybackSessionLease)?.ReleasePlaybackSession(_vm.Url);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Player] 磁力会话收尾失败: {ex.Message}");
        }
    }

    /// <summary>加载并开始播放</summary>
    private void StartPlayback()
    {
        _vm.IsFailed = false;
        _vm.IsBuffering = true;
        _vm.PositionSeconds = 0;
        _vm.DurationSeconds = 0;
        _vm.ControlsVisible = true;

        // 加载阶段拿不到时长/前沿 → 先用旋转弧表达「在忙」，
        // MediaOpened 后由 BeginBuffering 切到圆环百分比。
        BufferRing.IsIndeterminate = true;
        BufferRing.Progress = 0;

        ControlBar.Title = _vm.Title;
        ControlBar.SetProgress(0, 0);
        SyncControlsVisibility();

        Player.Source = _vm.Url;
        _playback.BeginSession(_vm.Title, _vm.Url, _vm.Cover);
        RestartHideTimer();
    }

    // ════════════════ 播放器事件 ════════════════

    private void OnMediaOpened(object? sender, EventArgs e)
    {
        if (Player.Duration != TimeSpan.Zero)
            _vm.DurationSeconds = Player.Duration.TotalSeconds;

        // 元数据就绪 → 起播缓冲改用圆环百分比（此前是旋转弧）
        BeginBuffering(Player.Position.TotalSeconds, Player.Position.TotalSeconds);
        UpdateBufferProgress();

        // 断点续播：媒体就绪（时长已知）后一次性 seek；越界（接近片尾）则放弃从头播
        if (_startPosition > 0)
        {
            var target = _startPosition;
            _startPosition = 0;
            if (Player.Duration == TimeSpan.Zero || target < Player.Duration.TotalSeconds - 1)
            {
                BeginBuffering(0, target);      // 续播跳到未缓冲处 → 显示缓冲进度
                Player.Seek(TimeSpan.FromSeconds(target));
                _vm.PositionSeconds = target;
            }
        }
    }

    private void OnPositionChanged(object? sender, EventArgs e)
    {
        if (_vm.IsSeeking) return;
        _vm.PositionSeconds = Player.Position.TotalSeconds;
        if (Player.Duration != TimeSpan.Zero)
            _vm.DurationSeconds = Player.Duration.TotalSeconds;

        SyncControlBarProgress();
        TickBufferingIndicator();   // 先在「真的在播」时收起指示器（MF 会误报 Buffering）
        UpdateBufferProgress();     // 仍在缓冲：刷新百分比
    }

    // ─────────── 缓冲进度 ───────────

    /// <summary>声明「即将开始一次缓冲」：显示指示器并重新计时。</summary>
    private void BeginBuffering(double anchorSeconds = -1, double targetSeconds = -1)
    {
        _ = anchorSeconds;
        _ = targetSeconds;
        _bufferStartUtc = DateTime.UtcNow;
        _playbackAdvancing = false;
        _bufferLastPos = -1;          // 重置采样哨兵
        BufferRing.IsIndeterminate = false;
        BufferRing.Progress = 0;
    }

    /// <summary>
    /// 裁决缓冲指示器的显隐。判据一（磁力路，权威）：<see cref="VideoPlayerView.IsWaitingForData"/>
    /// —— 代理精确知道读者是否顶到缓存前沿等数据；判据二（兜底）：播放位置是否推进
    /// （磁力路上 MF 会在画面正常播放时仍报 Buffering，不能用它）。
    /// </summary>
    private void TickBufferingIndicator()
    {
        var pos = Player.Position.TotalSeconds;
        var dur = Player.Duration.TotalSeconds;

        // 判据一（权威，磁力路）：代理说读者顶到缓存前沿等数据。
        // 必须**无条件**检查 —— 中途断粮时需要把指示器重新唤起来。
        if (Player.IsWaitingForData)
        {
            _playbackAdvancing = false;
            if (!_vm.IsBuffering) BeginBuffering();
            _vm.IsBuffering = true;            // 覆盖 MF 的漏报
            return;
        }

        // `_bufferLastPos >= 0` 不可省：初值是 -1（哨兵），否则首帧 pos=0 会被当成「推进」
        var advancing = dur > 0 && _bufferLastPos >= 0
                        && pos > _bufferLastPos + 0.15
                        && pos - _bufferLastPos < 5.0      // 排除 seek 跳变
                        && !_vm.IsSeeking;
        _bufferLastPos = pos;

        if (advancing)
        {
            _playbackAdvancing = true;         // 压制 MF 的 Buffering 误报
            if (_vm.IsBuffering)
            {
                BufferRing.IsIndeterminate = false;
                BufferRing.Progress = 1;
                _vm.IsBuffering = false;
            }
        }
    }

    /// <summary>缓冲百分比 =「播放点往后的连续可播量」朝起播所需量推进（与 WatchPage 同源）。</summary>
    private void UpdateBufferProgress()
    {
        if (!_vm.IsBuffering) return;

        var dur = Player.Duration.TotalSeconds;
        if (dur <= 0)
        {
            BufferRing.IsIndeterminate = true;
            return;
        }

        var pos = Player.Position.TotalSeconds;
        var ahead = Player.BufferedFrontier.TotalSeconds - pos;
        if (ahead < 0) ahead = 0;

        var waited = (DateTime.UtcNow - _bufferStartUtc).TotalSeconds;
        if (waited > LongWaitSeconds && ahead < 1.0)
        {
            BufferRing.IsIndeterminate = true;   // 抢不到数据：别挂着不动的数字
            return;
        }

        var need = Math.Max(1.0, Math.Min(dur, StartupBufferSeconds));
        BufferRing.IsIndeterminate = false;
        BufferRing.Progress = Math.Clamp(ahead / need, 0, 0.99);
    }

    /// <summary>把进度推给控件条（三层进度轴由它自绘，含已缓冲区间）。</summary>
    private void SyncControlBarProgress() =>
        ControlBar.SetProgress(_vm.PositionSeconds, _vm.DurationSeconds,
            Player.BufferedPosition.TotalSeconds);

    private void OnStateChanged(object? sender, EventArgs e)
    {
        _vm.IsPlaying = Player.IsPlaying;
        _vm.IsBuffering = Player.CurrentState
            is VideoPlayerState.Preparing or VideoPlayerState.Buffering;

        // 图标切换（中央大键与控件条播放键同源）
        var icon = _vm.IsPlaying ? "ic_pause.png" : "ic_play.png";
        CenterPlayButton.Source = icon;
        ControlBar.IsPlaying = _vm.IsPlaying;

        // 中央大键与底部播放键互斥：播放中隐藏中央键，避免出现两个「暂停」
        CenterPlayButton.IsVisible = _vm.ControlsVisible && !_vm.IsPlaying;

        // 缓冲指示：进入缓冲 → 显示（位置已在推进的话不重复唤起，避免闪烁）；恢复播放 → 收起
        var state = Player.CurrentState;
        if (state == VideoPlayerState.Buffering && !_playbackAdvancing)
            BeginBuffering();
        else if (state == VideoPlayerState.Playing)
            _playbackAdvancing = false;

        if (_vm.IsPlaying) RestartHideTimer();
    }

    private void OnMediaFailed(object? sender, EventArgs e)
    {
        _vm.IsFailed = true;
        _vm.IsBuffering = false;
        _vm.ErrorMessage = Player.ErrorMessage ?? "无法加载该地址，请检查网络或链接是否有效";
    }

    private void OnMediaEnded(object? sender, EventArgs e)
    {
        _vm.IsPlaying = false;
        _vm.PositionSeconds = _vm.DurationSeconds;
        CenterPlayButton.Source = "ic_play.png";
        ControlBar.IsPlaying = false;
        CenterPlayButton.IsVisible = true;
        _vm.ControlsVisible = true;
    }

    /// <summary>静音前的音量（恢复用）。</summary>
    private double _volumeBeforeMute = 1.0;

    /// <summary>应用静音（音量归零 / 恢复）；记住静音前音量，避免恢复时丢失。</summary>
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

    /// <summary>循环切换播放速率（档位见 VideoPlayerView.SpeedPresets）。</summary>
    private void CycleSpeed()
    {
        var presets = CatClawVideo.Maui.Controls.VideoPlayerView.SpeedPresets;
        var idx = Array.FindIndex(presets, p => Math.Abs(p - Player.Speed) < 0.01);
        var next = presets[(idx + 1) % presets.Length];
        Player.Speed = next;
        ControlBar.SpeedValue = next;
    }

    /// <summary>±10s / 拖动进度：相对跳转（控件条只报绝对秒数，这里做边界收口）。</summary>
    private void OnSeekRequested(double seconds)
    {
        if (_vm.DurationSeconds > 0)
            seconds = Math.Clamp(seconds, 0, _vm.DurationSeconds - 1);
        if (seconds < 0) return;

        // 跳向未缓冲区域 → 显示缓冲进度（判断用未抬升的原始前沿）
        var from = Player.Position.TotalSeconds;
        if (seconds > Player.BufferedFrontier.TotalSeconds + 0.5)
            BeginBuffering(from, seconds);

        Player.Seek(TimeSpan.FromSeconds(seconds));
        _vm.PositionSeconds = seconds;
        SyncControlBarProgress();
    }

    private void SeekRelative(double deltaSeconds)
    {
        var from = Player.Position.TotalSeconds;
        var target = Player.Position.TotalSeconds + deltaSeconds;
        var max = _vm.DurationSeconds > 0 ? _vm.DurationSeconds - 1 : double.MaxValue;
        target = Math.Clamp(target, 0, max);

        // 只在前进到未缓冲区域时提示（后退通常已缓冲，瞬时完成）
        if (deltaSeconds > 0 && target > Player.BufferedFrontier.TotalSeconds + 0.5)
            BeginBuffering(from, target);

        Player.Seek(TimeSpan.FromSeconds(target));
        _vm.PositionSeconds = target;
        SyncControlBarProgress();
        RestartHideTimer();
    }

    /// <summary>底栏全屏键：Android 走横竖屏切换（本页已全屏，语义等同）。</summary>
    private void ToggleLandscape()
    {
        _vm.ToggleLandscapeCommand.Execute(null);
    }

    // ════════════════ 控制层交互 ════════════════

    private void OnSurfaceTapped(object? sender, TappedEventArgs e)
    {
        _vm.ToggleControlsCommand.Execute(null);
        SyncControlsVisibility();
        if (_vm.ControlsVisible) RestartHideTimer();
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("..");
    }

    private void OnRetryClicked(object? sender, EventArgs e)
    {
        if (!string.IsNullOrEmpty(_vm.Url)) StartPlayback();
    }

    private void OnPlayPauseClicked(object? sender, EventArgs e)
    {
        if (_vm.IsFailed) return;
        if (_vm.IsPlaying)
        {
            Player.Pause();
            _vm.ControlsVisible = true; // 暂停时常驻控制层
            _hideTimer?.Stop();
        }
        else
        {
            Player.Play();
            RestartHideTimer();
        }
    }

    private void OnSeekStarted(object? sender, EventArgs e)
    {
        _vm.IsSeeking = true;
        _hideTimer?.Stop();
    }

    private void OnSeekCompleted(object? sender, EventArgs e)
    {
        // 拖动过程中已通过 SeekRequested 实时 seek，这里只收尾状态
        _vm.IsSeeking = false;
        SyncControlBarProgress();
        RestartHideTimer();
    }

    // ════════════════ 自动隐藏 ════════════════

    private void RestartHideTimer()
    {
        if (!_vm.IsPlaying || _vm.IsSeeking || _vm.IsFailed) return;
        _hideTimer?.Stop();
        _hideTimer?.Start();
    }

    private void OnHideTimerTick()
    {
        if (_vm.IsPlaying && !_vm.IsSeeking)
            _vm.ControlsVisible = false;
    }

    /// <summary>控件层显隐：中央大键只在「暂停」时出现（与控件条播放键互斥）。</summary>
    private void SyncControlsVisibility()
    {
        CenterPlayButton.IsVisible = _vm.ControlsVisible && !_vm.IsPlaying;
    }
}
