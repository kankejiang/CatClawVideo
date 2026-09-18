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
            // ★ 绝不能开 ReadAheadBuffer：它会包一层 FFmpegInteropX 自带的 AVIOContext，
            //   其 seek 回调不完整 → avio 判「不可 seek」→ 一切 seek（恢复进度/拖进度条）都退化成
            //   「Soft-seeking by draining」顺序丢读整个未下载区间 → 卡死黑屏 + 反复重开（实测 15:36 会话）。
            //   读前缓冲由宿主 QemuStreamProxy（64MB 窗口）承担，这层是冗余且有害的。
            config.General.ReadAheadBufferEnabled = false;
            // ★ 初始 demux 提速：引擎媒体口按下载节奏节流（~1MB/s），FFmpeg 默认 probesize 会顺序
            //   拉几十 MB，CreateFromUri 要等 1-2 分钟。MKV（H264/AAC）从 Tracks 头即可识别，
            //   砍小 probesize/analyzeduration 让打开秒级完成；eac3 解码不受影响（音轨信息在头部）。
            config.FFmpegOptions["probesize"] = "3145728";
            config.FFmpegOptions["analyzeduration"] = "2000000";
            // ★ 强制 http 可 seek（默认 -1 探测会误判为不可 seek → 对 Cues 的 seek 退化成
            //   「Soft-seeking by draining 1.9GB」顺序丢读，永远开不了播，实测）。
            config.FFmpegOptions["seekable"] = "1";
            // ★ 短距离 seek 阈值必须设小值：FFmpeg 8.1 http.c 的
            //   「remaining(uint64) <= ffurl_get_short_seek(h)」在底层无 get_short_seek 回调时
            //   返回负数 ENOSYS，被宽化成 uint64 巨数 → 条件恒真 → 任意距离的 seek 都走
            //   「Soft-seeking drain」（把中间所有字节顺序读掉）而不是发新 Range 请求 —— 拖动
            //   seek 也会变成 331MB 级别的假死读。设为 4KB 后大距离 seek 强制重连发 Range。
            config.FFmpegOptions["short_seek_size"] = "4096";
            // ★ 必须开 FastSeek：磁力流 MKV 没有 Cues 索引（文件尾未下载），普通 seek 走
            //   av_seek_frame → matroska_read_seek 无索引直接失败（无 avio 请求、位置弹回）——
            //   实测 16:45 会话：Seek 请求 1415.9s 后零 Range 请求。FastSeek 走
            //   avformat_seek_file 的通用二分搜索（用 Cluster 时间戳 + avio seek 探测），
            //   无索引也能 seek —— ffmpeg CLI 实测同一条流 seek 成功（Range 57MB 处）。
            config.General.FastSeek = true;
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
            ReapplySpeed();      // 挂上新源会重置速率 → 立即补回
            Maui.Services.BtFileLog.Write("[player] FFmpeg MSS 就绪，已挂到播放器");
        }
        catch (Exception ex)
        {
            if (gen != _sourceGen) return;
            Maui.Services.BtFileLog.Write($"[player] FFmpeg 源创建失败，回落原生 MF：{ex.GetType().Name}: {ex.Message}");
            try
            {
                PlatformView.Source = global::Windows.Media.Core.MediaSource.CreateFromUri(new Uri(url));
                ReapplySpeed();
            }
            catch (Exception ex2) { view.RaiseMediaFailed($"加载失败: {ex2.Message}"); return; }
            Maui.Services.BtFileLog.Write("[player] 已回落原生 MF 源");
        }
    }

    /// <summary>把 View 层当前的倍速补回播放器（换源后 MF 会重置为 1.0）。</summary>
    private void ReapplySpeed()
    {
        var target = VirtualView?.Speed ?? 1.0;
        if (Math.Abs(target - 1.0) < 0.001) return;
        ((IVideoPlayerImplementation)this).SetSpeed(target);
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
        Maui.Services.BtFileLog.Write($"[player] Seek 请求：{position.TotalSeconds:F1}s（session={_mediaPlayer?.PlaybackSession != null}）");
        if (_mediaPlayer?.PlaybackSession != null)
            _mediaPlayer.PlaybackSession.Position = position;
    }

    /// <summary>播放速率：MF 的 <c>PlaybackSession.PlaybackRate</c>（0.25~4.0 有效）。
    /// 换源后 MF 会重置回 1.0，故 View 层在 SetSource 后会再调一次本方法。
    /// <para>⚠ 这里**回读校验**并落日志：部分媒体源（如 FFmpegInteropX 的 MediaStreamSource）
    /// MF 可能不接受倍速并静默保持 1.0，不校验的话用户只会看到「点了没反应」而查不到原因。</para></summary>
    void IVideoPlayerImplementation.SetSpeed(double speed)
    {
        var session = _mediaPlayer?.PlaybackSession;
        if (session == null) return;
        try
        {
            var want = Math.Clamp(speed, 0.25, 4.0);
            session.PlaybackRate = want;

            var actual = session.PlaybackRate;
            if (Math.Abs(actual - want) > 0.01)
                Maui.Services.BtFileLog.Write(
                    $"[player] 倍速未生效：请求 {want:0.##}×，实际 {actual:0.##}×（该媒体源可能不支持变速）");
            else
                Maui.Services.BtFileLog.Write($"[player] 倍速已设为 {actual:0.##}×");
        }
        catch (Exception ex)
        {
            Maui.Services.BtFileLog.Write($"[player] 设置倍速失败（{speed}）：{ex.Message}");
        }
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

    /// <summary>
    /// 已缓冲位置：取 <c>GetBufferedRanges()</c> 中**包含当前播放位置**的那一段的右端。
    /// 直接取所有区间的 Max 会得到「已下载到片尾」这类超前值（磁力边下边播常见），
    /// 让缓冲条虚高；取包含当前播放点的那段才符合用户直觉。
    /// </summary>
    TimeSpan IVideoPlayerImplementation.GetBufferedPosition()
    {
        var session = _mediaPlayer?.PlaybackSession;
        if (session == null) return TimeSpan.Zero;

        var pos = session.Position;
        try
        {
            var ranges = session.GetBufferedRanges();
            var best = pos;
            foreach (var range in ranges)
            {
                // 该区间覆盖当前播放点 → 取它的右端（WinRT 投射属性为 Start / End）
                if (range.Start <= pos && pos <= range.End && range.End > best)
                    best = range.End;
            }
            if (best > pos) return best;
        }
        catch { }

        // MF 未报告缓冲区间（磁力流经 FFmpegInteropX 包装，非自适应流不给 BufferedRanges）
        // → 用宿主读前缓存代理的**连续可播量**（不是整片下载比例！见 ReaderAheadBytes）
        //   换算成时间。这个值是诚实的：引擎供不上数据时它停住不动。
        try
        {
            var proxy = CatClawVideo.Core.Services.QemuThunder.QemuStreamProxy.Current;
            if (proxy is not null && proxy.TotalBytes > 0)
            {
                var dur = session.NaturalDuration;
                if (dur > TimeSpan.Zero)
                {
                    var ahead = dur * (proxy.ReaderAheadBytes / (double)proxy.TotalBytes);
                    var byBudget = pos + ahead;
                    if (byBudget > pos) return byBudget;
                }
            }
        }
        catch { }

        return pos;
    }

    /// <summary>缓冲位置可信度：仅当 MF 真的报告了包含当前点的缓冲区间时为 true。
    /// 走「已下载字节比例」回退（磁力边下边播）时不可信 —— 该值会严重虚高。</summary>
    bool IVideoPlayerImplementation.IsBufferedPositionReliable
    {
        get
        {
            var session = _mediaPlayer?.PlaybackSession;
            if (session == null) return false;
            try
            {
                var pos = session.Position;
                foreach (var range in session.GetBufferedRanges())
                {
                    if (range.Start <= pos && pos <= range.End && range.End > pos)
                        return true;
                }
            }
            catch { }
            return false;
        }
    }

    /// <summary>
    /// 播放器是否在等数据：**磁力路走代理的真实饥饿信号**（读者顶到缓存前沿 = 确凿地在等），
    /// 直链路没有代理（<c>Current</c> 为 null）→ 返回 false，由宿主用位置推进判断。
    ///
    /// <para>这比看 MF 的 <c>PlaybackState</c> 或推算缓冲位置都可靠：代理知道每一个读者
    /// 读到了哪个偏移、缓存前沿在哪，判定是精确的。</para>
    /// </summary>
    bool IVideoPlayerImplementation.IsWaitingForData
    {
        get
        {
            try
            {
                var proxy = CatClawVideo.Core.Services.QemuThunder.QemuStreamProxy.Current;
                return proxy?.ReaderStarving ?? false;
            }
            catch { return false; }
        }
    }

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

