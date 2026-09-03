using Android.Content;
using Android.Views;
using CatClawVideo.Maui.Controls;
using Microsoft.Maui.Handlers;

#if ANDROID
using AndroidX.Media3.Common;
using AndroidX.Media3.DataSource;
using AndroidX.Media3.ExoPlayer;
using AndroidX.Media3.ExoPlayer.Source;
using AndroidX.Media3.UI;
using SimpleExoPlayer = AndroidX.Media3.ExoPlayer.SimpleExoPlayer;

namespace CatClawVideo.Maui.Platforms.Android;

/// <summary>
/// Android 播放器 Handler：Media3 ExoPlayer + PlayerView。
/// DefaultHttpDataSource 支持自定义请求头（Referer/UA），HLS 模块随包引入自动识别 m3u8。
/// 命名空间/接口名按 Xamarin.AndroidX.Media3 1.10.1 绑定规则（DataSource 大写 S、IPlayerListener）。
/// </summary>
public class VideoPlayerViewHandler : ViewHandler<VideoPlayerView, PlayerView>, IVideoPlayerImplementation
{
    private SimpleExoPlayer? _player;

    /// <summary>媒体源工厂（持有引用以便换源时切换数据源工厂，实现按源自定义请求头）</summary>
    private DefaultMediaSourceFactory? _mediaSourceFactory;

    // Media3 播放状态常量：1=Idle 2=Buffering 3=Ready 4=Ended
    private const int StateIdle = 1;
    private const int StateBuffering = 2;
    private const int StateReady = 3;
    private const int StateEnded = 4;

    // PlayerView.ResizeMode 常量（AspectRatioFrameLayout）：Fit=0 Fill=3 Zoom=4
    private const int ResizeFit = 0;
    private const int ResizeFill = 3;
    private const int ResizeZoom = 4;

    private const string UserAgent = "CatClawVideo/0.1 (Android; Linux)";

    public VideoPlayerViewHandler() : base(ViewHandler.ViewMapper)
    {
    }

    protected override PlayerView CreatePlatformView()
    {
        var context = Context;
        var playerView = new PlayerView(context ?? throw new InvalidOperationException("Context 不可用"))
        {
            UseController = false,
            LayoutParameters = new ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent),
        };
        return playerView;
    }

    protected override void ConnectHandler(PlayerView platformView)
    {
        base.ConnectHandler(platformView);
        var context = Context ?? platformView.Context;

        // HTTP 数据源：支持自定义请求头 + 跨协议重定向（http→https 跳转常见）
        var httpFactory = BuildHttpFactory(null);
        _mediaSourceFactory = new DefaultMediaSourceFactory(httpFactory);

        _player = new SimpleExoPlayer.Builder(context)
            .SetMediaSourceFactory(_mediaSourceFactory)
            .Build();
        _player.AddListener(new PlayerListener(this));

        platformView.Player = _player;

        VirtualView.Implementation = this;

        // 应用 View 层已设置的属性（Source 可能已在 Handler 连接前赋值）
        var impl = (IVideoPlayerImplementation)this;
        impl.SetVolume(VirtualView.Volume);
        impl.SetAspect(VirtualView.Aspect);
        impl.KeepScreenOn(VirtualView.ShouldKeepScreenOn);
        if (!string.IsNullOrEmpty(VirtualView.Source))
            impl.SetSource(VirtualView.Source, VirtualView.Headers);
    }

    protected override void DisconnectHandler(PlayerView platformView)
    {
        VirtualView.Implementation = null;
        platformView.Player = null;
        try { _player?.Release(); } catch { }
        _player = null;
        base.DisconnectHandler(platformView);
    }

    // ═══════════════════ IVideoPlayerImplementation ═══════════════════

    void IVideoPlayerImplementation.SetSource(string? url, IReadOnlyDictionary<string, string>? headers)
    {
        if (_player == null) return;

        try
        {
            _player.Stop();
            _player.ClearMediaItems();

            if (string.IsNullOrEmpty(url)) return;

            // 请求头按源切换（TVBox 源防盗链：Referer / User-Agent）
            _mediaSourceFactory?.SetDataSourceFactory(BuildHttpFactory(headers));

            _player.SetMediaItem(MediaItem.FromUri(global::Android.Net.Uri.Parse(url)));
            _player.Prepare();
        }
        catch (Exception ex)
        {
            VirtualView?.RaiseMediaFailed($"加载失败: {ex.Message}");
        }
    }

    /// <summary>构建 HTTP 数据源工厂（跨协议重定向 + 自定义请求头）</summary>
    private static DefaultHttpDataSource.Factory BuildHttpFactory(IReadOnlyDictionary<string, string>? headers)
    {
        var factory = new DefaultHttpDataSource.Factory()
            .SetAllowCrossProtocolRedirects(true)
            .SetConnectTimeoutMs(10_000)
            .SetReadTimeoutMs(20_000)
            .SetUserAgent(UserAgent);
        if (headers is { Count: > 0 })
            factory.SetDefaultRequestProperties(new Dictionary<string, string>(headers));
        return factory;
    }

    void IVideoPlayerImplementation.Play() => _player?.Play();

    void IVideoPlayerImplementation.Pause() => _player?.Pause();

    void IVideoPlayerImplementation.Stop()
    {
        if (_player == null) return;
        _player.Pause();
        _player.SeekTo(0);
    }

    void IVideoPlayerImplementation.Seek(TimeSpan position) => _player?.SeekTo((long)position.TotalMilliseconds);

    void IVideoPlayerImplementation.SetVolume(double volume) { if (_player != null) _player.Volume = (float)volume; }

    void IVideoPlayerImplementation.SetAspect(VideoAspect aspect)
    {
        // AspectFit/AspectFill/Fill → PlayerView.ResizeMode（int 常量）
        if (PlatformView == null) return;
        PlatformView.ResizeMode = aspect switch
        {
            VideoAspect.Fill => ResizeFill,
            VideoAspect.AspectFill => ResizeZoom,
            _ => ResizeFit,
        };
    }

    void IVideoPlayerImplementation.KeepScreenOn(bool keep)
    {
        PlatformView?.KeepScreenOn = keep;
    }

    TimeSpan IVideoPlayerImplementation.GetPosition() =>
        _player == null ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Math.Max(0, _player.CurrentPosition));

    TimeSpan IVideoPlayerImplementation.GetDuration() =>
        _player == null || _player.Duration < 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(_player.Duration);

    // ═══════════════════ 播放事件桥 ═══════════════════

    /// <summary>ExoPlayer 事件 → VideoPlayerView（Xamarin.AndroidX.Media3 1.10.1 把 Player.IListener 绑定为 IPlayerListener）</summary>
    private sealed class PlayerListener : Java.Lang.Object, IPlayerListener
    {
        private readonly VideoPlayerViewHandler _handler;
        private bool _opened;

        public PlayerListener(VideoPlayerViewHandler handler) => _handler = handler;

        public void OnPlaybackStateChanged(int playbackState)
        {
            var view = _handler.VirtualView;
            if (view == null) return;

            switch (playbackState)
            {
                case StateBuffering:
                    view.RaiseStateChanged(VideoPlayerState.Buffering);
                    break;
                case StateReady:
                    if (!_opened)
                    {
                        // 首次 READY = 媒体打开成功（是否自动播放由 View 层决定）
                        _opened = true;
                        view.RaiseMediaOpened();
                        if (!view.ShouldAutoPlay)
                            view.RaiseStateChanged(VideoPlayerState.Paused);
                    }
                    else if (_handler._player?.PlayWhenReady == true)
                    {
                        view.RaiseStateChanged(VideoPlayerState.Playing);
                    }
                    else
                    {
                        view.RaiseStateChanged(VideoPlayerState.Paused);
                    }
                    break;
                case StateEnded:
                    view.RaiseMediaEnded();
                    break;
                case StateIdle:
                    view.RaiseStateChanged(VideoPlayerState.Idle);
                    break;
            }
        }

        public void OnIsPlayingChanged(bool isPlaying)
        {
            var view = _handler.VirtualView;
            if (view == null) return;
            view.RaiseStateChanged(isPlaying ? VideoPlayerState.Playing : VideoPlayerState.Paused);
        }

        public void OnPlayerError(PlaybackException? error)
        {
            var view = _handler.VirtualView;
            if (view == null) return;
            var msg = error?.LocalizedMessage ?? "播放出错";
            view.RaiseMediaFailed(msg);
        }
    }
}
#endif
