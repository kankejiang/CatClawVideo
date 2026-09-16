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
    private FFmpegInteropX.FFmpegMediaSource? _interop;
    private int _sourceGen;   // 源切换代数：丢弃慢创建的过期 FFmpeg 源
    private static bool _ffmpegLogInit;

    /// <summary>FFmpeg 内部日志桥到 bt.log（只警告以上；排音画/ demux 问题时改 Info）。</summary>
    private static void EnsureFfmpegLogging()
    {
        if (_ffmpegLogInit) return;
        _ffmpegLogInit = true;
        try
        {
            FFmpegInteropX.FFmpegInteropLogging.SetLogLevel(FFmpegInteropX.LogLevel.Debug);
            FFmpegInteropX.FFmpegInteropLogging.SetLogProvider(new FfmpegLogBridge());
        }
        catch { }
    }

    private sealed class FfmpegLogBridge : FFmpegInteropX.ILogProvider
    {
        public void Log(FFmpegInteropX.LogLevel level, string message) =>
            Maui.Services.BtFileLog.Write($"[ffmpeg:{level}] {message}");
    }

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
        CloseInterop();

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
            CloseInterop();
            if (string.IsNullOrEmpty(url))
            {
                PlatformView.Source = null;
                return;
            }

            // 首选 FFmpeg 解码：磁力源的 MKV 音轨多为 E-AC3/DTS，Media Foundation 无对应解码器
            // （表现为视频正常出画、全程无声，实测 dyg7 源 A_EAC3）。音频强制走 FFmpeg 软解，
            // 视频保持默认（D3D11 硬解优先）。FFmpeg 打不开/超时 → 回落原生 MF 路径。
            // 创建是异步的（元数据级解析），用代数守卫丢弃过期的结果，不阻塞 UI 线程。
            var gen = ++_sourceGen;
            var uri = url;
            _ = SetSourceInteropAsync(view, uri, gen);
        }
        catch (Exception ex)
        {
            view.RaiseMediaFailed($"加载失败: {ex.Message}");
        }
    }

    /// <summary>异步创建 FFmpeg 媒体源并挂到播放器；任何失败回落 MediaSource.CreateFromUri（原生 MF）。</summary>
    private async System.Threading.Tasks.Task SetSourceInteropAsync(VideoPlayerView view, string url, int gen)
    {
        EnsureFfmpegLogging();
        Maui.Services.BtFileLog.Write($"[player] FFmpeg 打开源：{url}");
        try
        {
            // 音频默认策略即满足需求：AAC/MP3 走系统解码器，E-AC3/DTS 等系统没有的编解码自动落到
            // FFmpeg（这正是无声问题的解）；视频保持默认（D3D11 硬解优先）。
            // 内置读前缓冲兜底网络抖动，减少播放器饥饿。
            var config = new FFmpegInteropX.MediaSourceConfig();
            config.General.ReadAheadBufferEnabled = true;
            // ★ 初始 demux 提速：引擎媒体口按下载节奏节流（~1MB/s），FFmpeg 默认 probesize 会顺序
            //   拉几十 MB，CreateFromUri 要等 1-2 分钟。MKV（H264/AAC）从 Tracks 头即可识别，
            //   砍小 probesize/analyzeduration 让打开秒级完成；eac3 解码不受影响（音轨信息在头部）。
            config.FFmpegOptions["probesize"] = "3145728";
            config.FFmpegOptions["analyzeduration"] = "2000000";
            // ★ 强制 http 可 seek（默认 -1 探测会误判为不可 seek → 对 Cues 的 seek 退化成
            //   「Soft-seeking by draining 1.9GB」顺序丢读，永远开不了播，实测）。
            config.FFmpegOptions["seekable"] = "1";
            // ★ 绝不能对 CreateFromUri 设超时：数据供给被引擎节流，初始化就是要几十秒；
            //   超时放弃的实例仍在后台解码，但 Source 已换成别的——表现为黑屏。等它完成即可。
            var interop = await FFmpegInteropX.FFmpegMediaSource.CreateFromUriAsync(url, config)
                .AsTask().ConfigureAwait(true);
            if (gen != _sourceGen)
            {
                // 期间用户已切源：丢弃过期结果
                try { interop.Dispose(); } catch { }
                Maui.Services.BtFileLog.Write("[player] FFmpeg 源已过期，丢弃");
                return;
            }
            _interop = interop;
            // 用官方推荐的 MediaPlaybackItem 路径（GetMediaStreamSource + CreateFromMediaStreamSource
            // 在部分版本组合下会 E_INVALIDARG）。MediaPlaybackItem 本身就是合法的播放器源。
            PlatformView.Source = interop.CreateMediaPlaybackItem();
            Maui.Services.BtFileLog.Write("[player] FFmpeg MSS 就绪，已挂到播放器");
        }
        catch (Exception ex)
        {
            if (gen != _sourceGen) return;
            Maui.Services.BtFileLog.Write($"[player] FFmpeg 源创建失败，回落原生 MF：{ex.GetType().Name}: {ex.Message}");
            try { PlatformView.Source = global::Windows.Media.Core.MediaSource.CreateFromUri(new Uri(url)); } catch (Exception ex2) { view.RaiseMediaFailed($"加载失败: {ex2.Message}"); return; }
            Maui.Services.BtFileLog.Write("[player] 已回落原生 MF 源");
        }
    }

    /// <summary>关闭并释放当前 FFmpeg 媒体源（切源/断开 Handler 时调用）。
    /// ★ 必须先解绑 PlatformView.Source：旧 MediaPlaybackItem 还挂在播放器上时直接 Dispose
    /// interop 会与之冲突，换集时 CreateMediaPlaybackItem 抛 COMException（0x80070057，实测）。</summary>
    private void CloseInterop()
    {
        _sourceGen++;
        var old = _interop;
        _interop = null;
        try { PlatformView.Source = null; } catch { }
        try { old?.Dispose(); } catch { }
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
        Maui.Services.BtFileLog.Write("[player] MediaOpened");
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
        Maui.Services.BtFileLog.Write($"[player] MediaFailed: {args.Error} {msg} extended={args.ExtendedErrorCode?.HResult}");
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

