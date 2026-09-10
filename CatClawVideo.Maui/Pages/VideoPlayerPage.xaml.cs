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

    public VideoPlayerPage(VideoPlayerViewModel vm, VideoPlaybackManager playback)
    {
        InitializeComponent();
        _vm = vm;
        _playback = playback;
        BindingContext = _vm;

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
        if (!string.IsNullOrEmpty(_vm.Url))
        {
            StartPlayback();
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _hideTimer?.Stop();

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
    }

    /// <summary>加载并开始播放</summary>
    private void StartPlayback()
    {
        _vm.IsFailed = false;
        _vm.IsBuffering = true;
        _vm.PositionSeconds = 0;
        _vm.DurationSeconds = 0;
        _vm.ControlsVisible = true;

        Player.Source = _vm.Url;
        _playback.BeginSession(_vm.Title, _vm.Url, _vm.Cover);
        RestartHideTimer();
    }

    // ════════════════ 播放器事件 ════════════════

    private void OnMediaOpened(object? sender, EventArgs e)
    {
        if (Player.Duration != TimeSpan.Zero)
            _vm.DurationSeconds = Player.Duration.TotalSeconds;

        // 断点续播：媒体就绪（时长已知）后一次性 seek；越界（接近片尾）则放弃从头播
        if (_startPosition > 0)
        {
            var target = _startPosition;
            _startPosition = 0;
            if (Player.Duration == TimeSpan.Zero || target < Player.Duration.TotalSeconds - 1)
            {
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
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        _vm.IsPlaying = Player.IsPlaying;
        _vm.IsBuffering = Player.CurrentState
            is VideoPlayerState.Preparing or VideoPlayerState.Buffering;

        // 图标切换
        var icon = _vm.IsPlaying ? "ic_pause.svg" : "ic_play.svg";
        CenterPlayButton.Source = icon;
        BottomPlayButton.Source = icon;

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
        CenterPlayButton.Source = "ic_play.svg";
        BottomPlayButton.Source = "ic_play.svg";
        _vm.ControlsVisible = true;
    }

    // ════════════════ 控制层交互 ════════════════

    private void OnSurfaceTapped(object? sender, TappedEventArgs e)
    {
        _vm.ToggleControlsCommand.Execute(null);
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
        // Slider.Value 在拖动中持续变化，完成时即为目标位置
        var target = ProgressBar.Value;
        _vm.PositionSeconds = target;
        Player.Seek(TimeSpan.FromSeconds(target));
        _vm.IsSeeking = false;
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
}
