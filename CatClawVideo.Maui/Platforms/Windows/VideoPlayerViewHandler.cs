using CatClawVideo.Maui.Controls;
using Microsoft.Maui.Handlers;
using MediaPlayer = global::Windows.Media.Playback.MediaPlayer;
using MediaPlaybackSession = global::Windows.Media.Playback.MediaPlaybackSession;
using MediaPlaybackState = global::Windows.Media.Playback.MediaPlaybackState;
using MediaPlayerError = global::Windows.Media.Playback.MediaPlayerError;

#if WINDOWS
namespace CatClawVideo.Maui.Platforms.Windows;

/// <summary>
/// Windows 播放器 Handler：WinUI MediaPlayerElement + MediaPlayer（支持 HLS/mp4）。
/// 请求头暂忽略（直链场景无防盗链需求；TVBox 源阶段如需再换 HttpClient 流方案）。
/// </summary>
public class VideoPlayerViewHandler : ViewHandler<VideoPlayerView, Microsoft.UI.Xaml.Controls.MediaPlayerElement>,
    IVideoPlayerImplementation
{
    private MediaPlayer? _mediaPlayer;
    private global::Windows.System.Display.DisplayRequest? _displayRequest;

    public VideoPlayerViewHandler() : base(ViewHandler.ViewMapper)
    {
    }

    protected override Microsoft.UI.Xaml.Controls.MediaPlayerElement CreatePlatformView()
    {
        var element = new Microsoft.UI.Xaml.Controls.MediaPlayerElement
        {
            AutoPlay = false,
            AreTransportControlsEnabled = false,
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Stretch,
        };
        return element;
    }

    protected override void ConnectHandler(Microsoft.UI.Xaml.Controls.MediaPlayerElement platformView)
    {
        base.ConnectHandler(platformView);

        _mediaPlayer = new MediaPlayer();
        _mediaPlayer.MediaOpened += OnMediaOpened;
        _mediaPlayer.MediaFailed += OnMediaFailed;
        _mediaPlayer.MediaEnded += OnMediaEnded;
        _mediaPlayer.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;

        platformView.SetMediaPlayer(_mediaPlayer);

        VirtualView.Implementation = this;

        // 应用 View 层已设置的属性（Source 可能已在 Handler 连接前赋值）
        var impl = (IVideoPlayerImplementation)this;
        impl.SetVolume(VirtualView.Volume);
        impl.SetAspect(VirtualView.Aspect);
        impl.KeepScreenOn(VirtualView.ShouldKeepScreenOn);
        if (!string.IsNullOrEmpty(VirtualView.Source))
            impl.SetSource(VirtualView.Source, VirtualView.Headers);
    }

    protected override void DisconnectHandler(Microsoft.UI.Xaml.Controls.MediaPlayerElement platformView)
    {
        VirtualView.Implementation = null;

        if (_mediaPlayer != null)
        {
            _mediaPlayer.MediaOpened -= OnMediaOpened;
            _mediaPlayer.MediaFailed -= OnMediaFailed;
            _mediaPlayer.MediaEnded -= OnMediaEnded;
            if (_mediaPlayer.PlaybackSession != null)
                _mediaPlayer.PlaybackSession.PlaybackStateChanged -= OnPlaybackStateChanged;
            _mediaPlayer.Dispose();
            _mediaPlayer = null;
        }
        ReleaseDisplayRequest();

        base.DisconnectHandler(platformView);
    }

    // ═══════════════════ IVideoPlayerImplementation ═══════════════════

    void IVideoPlayerImplementation.SetSource(string? url, IReadOnlyDictionary<string, string>? headers)
    {
        var view = VirtualView;
        if (view == null || _mediaPlayer == null) return;

        try
        {
            if (string.IsNullOrEmpty(url))
            {
                PlatformView.Source = null;
                return;
            }

            var source = global::Windows.Media.Core.MediaSource.CreateFromUri(new Uri(url));
            PlatformView.Source = source;
        }
        catch (Exception ex)
        {
            view.RaiseMediaFailed($"加载失败: {ex.Message}");
        }
    }

    void IVideoPlayerImplementation.Play() => _mediaPlayer?.Play();

    void IVideoPlayerImplementation.Pause() => _mediaPlayer?.Pause();

    void IVideoPlayerImplementation.Stop()
    {
        if (_mediaPlayer?.PlaybackSession == null) return;
        _mediaPlayer.Pause();
        _mediaPlayer.PlaybackSession.Position = TimeSpan.Zero;
    }

    void IVideoPlayerImplementation.Seek(TimeSpan position)
    {
        if (_mediaPlayer?.PlaybackSession != null)
            _mediaPlayer.PlaybackSession.Position = position;
    }

    void IVideoPlayerImplementation.SetVolume(double volume)
    {
        if (_mediaPlayer != null) _mediaPlayer.Volume = volume;
    }

    void IVideoPlayerImplementation.SetAspect(VideoAspect aspect)
    {
        if (PlatformView == null) return;
        PlatformView.Stretch = aspect switch
        {
            VideoAspect.Fill => Microsoft.UI.Xaml.Media.Stretch.Fill,
            VideoAspect.AspectFill => Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
            _ => Microsoft.UI.Xaml.Media.Stretch.Uniform,
        };
    }

    void IVideoPlayerImplementation.KeepScreenOn(bool keep)
    {
        try
        {
            if (keep)
            {
                _displayRequest ??= new global::Windows.System.Display.DisplayRequest();
                _displayRequest.RequestActive();
            }
            else
            {
                ReleaseDisplayRequest();
            }
        }
        catch { }
    }

    TimeSpan IVideoPlayerImplementation.GetPosition() =>
        _mediaPlayer?.PlaybackSession?.Position ?? TimeSpan.Zero;

    TimeSpan IVideoPlayerImplementation.GetDuration() =>
        _mediaPlayer?.PlaybackSession?.NaturalDuration ?? TimeSpan.Zero;

    // ═══════════════════ 播放事件桥 ═══════════════════

    private void OnMediaOpened(global::Windows.Media.Playback.MediaPlayer sender, object args)
    {
        var view = VirtualView;
        if (view == null) return;
        view.RaiseMediaOpened();
        if (!view.ShouldAutoPlay)
            view.RaiseStateChanged(VideoPlayerState.Paused);
    }

    private void OnMediaFailed(global::Windows.Media.Playback.MediaPlayer sender, global::Windows.Media.Playback.MediaPlayerFailedEventArgs args)
    {
        var view = VirtualView;
        if (view == null) return;
        var msg = args.Error switch
        {
            MediaPlayerError.Unknown => $"播放失败（未知错误）: {args.ErrorMessage}",
            MediaPlayerError.Aborted => "播放被中止",
            MediaPlayerError.NetworkError => "网络错误，无法加载该地址",
            MediaPlayerError.DecodingError => "解码失败，该格式可能不受支持",
            MediaPlayerError.SourceNotSupported => "源不受支持（视频编码或容器格式不兼容）",
            _ => args.ErrorMessage,
        };
        view.RaiseMediaFailed(msg);
    }

    private void OnMediaEnded(global::Windows.Media.Playback.MediaPlayer sender, object args) =>
        VirtualView?.RaiseMediaEnded();

    private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
    {
        var view = VirtualView;
        if (view == null) return;
        view.RaiseStateChanged(sender.PlaybackState switch
        {
            MediaPlaybackState.Playing => VideoPlayerState.Playing,
            MediaPlaybackState.Paused => VideoPlayerState.Paused,
            MediaPlaybackState.Buffering or MediaPlaybackState.Opening
                => VideoPlayerState.Buffering,
            _ => VideoPlayerState.Stopped,
        });
    }

    private void ReleaseDisplayRequest()
    {
        try
        {
            _displayRequest?.RequestRelease();
            _displayRequest = null;
        }
        catch { }
    }
}
#endif

