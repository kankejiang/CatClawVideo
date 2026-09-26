using Android.Content;
using Android.Views;
using CatClawVideo.Maui.Controls;
using TrackLang = CatClawVideo.Maui.Services.TrackLang;
using Microsoft.Maui.Handlers;

#if ANDROID
using AndroidX.Media3.Common;
using AndroidX.Media3.DataSource;
using AndroidX.Media3.ExoPlayer;
using AndroidX.Media3.ExoPlayer.MediaCodec;
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
    private AndroidX.Media3.ExoPlayer.IExoPlayer? _player;

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
        // 播放器统一由 RebuildPlayer 造（解码模式要在 builder 期决定，两处不能各写一份）
        _decoderMode = VirtualView.DecoderMode;
        RebuildPlayer();
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
        try
        {
            if (_player is not null) PlayerHandoff.Detach(_player);
            _player?.Release();
        }
        catch { }
        _player = null;
        base.DisconnectHandler(platformView);
    }

    // ═══════════════════ IVideoPlayerImplementation ═══════════════════

    void IVideoPlayerImplementation.SetSource(string? url, IReadOnlyDictionary<string, string>? headers)
    {
        if (_player == null) return;
        _mediaUrl = url;
        _headers = headers;
        // 换集 = 新的媒体项，外挂字幕要重新挂（View 层会在打开后补调 ApplySubtitle）
        _subtitleUri = null;
        _subtitleMime = null;
        RebuildMediaItem();
    }

    private string? _mediaUrl;
    private IReadOnlyDictionary<string, string>? _headers;
    private global::Android.Net.Uri? _subtitleUri;
    private string? _subtitleMime;

    /// <summary>
    /// 按当前源 + 当前字幕重建媒体项。
    /// <para>必须重建而不能「加一条字幕轨」：Media3 的 <c>SubtitleConfiguration</c> 是
    /// <c>MediaItem</c> 的构造期属性，没有运行期追加 API。重建会回到 Idle，所以要把播放位置
    /// 取出来在 <c>Prepare()</c> 后补 seek —— 否则用户点个字幕就跳回片头。</para>
    /// </summary>
    private void RebuildMediaItem()
    {
        var player = _player;
        if (player == null) return;
        try
        {
            var resumeMs = Math.Max(0, player.CurrentPosition);
            player.Stop();
            player.ClearMediaItems();

            if (string.IsNullOrEmpty(_mediaUrl)) return;

            // 请求头按源切换（TVBox 源防盗链：Referer / User-Agent）
            _mediaSourceFactory?.SetDataSourceFactory(BuildHttpFactory(_headers));

            // 显式指定 HLS：DefaultMediaSourceFactory 只按 **URI 路径后缀** 推断类型，
            // 而本地反代/爬虫给出的地址常是 `/proxy?do=m3u8&url=…%2Findex.m3u8`（真正后缀在 query 里）
            // → 会被判成普通媒体文件走 ProgressiveMediaSource，
            // 报 UnrecognizedInputFormatException（无 extractor 能读 m3u8）。
            // 注：绑定把 Builder 的链式返回标成可空，所以这里逐步调用而不是串起来。
            var builder = new MediaItem.Builder();
            builder.SetUri(global::Android.Net.Uri.Parse(_mediaUrl));
            var mime = InferMime(_mediaUrl);
            if (mime is not null) builder.SetMimeType(mime);

            if (_subtitleUri is not null && _subtitleMime is not null)
            {
                // SELECTION_FLAG_DEFAULT(=1)：Media3 默认**不选**任何文本轨（preferredTextLanguage 为空），
                // 不给这个标志的表现是「字幕文件加载成功但屏幕上什么都没有」。
                var subBuilder = new MediaItem.SubtitleConfiguration.Builder(_subtitleUri);
                subBuilder.SetMimeType(_subtitleMime);
                subBuilder.SetSelectionFlags(1);
                subBuilder.SetLabel("猫爪外挂字幕");
                if (subBuilder.Build() is MediaItem.SubtitleConfiguration sub)
                    builder.SetSubtitleConfigurations(new List<MediaItem.SubtitleConfiguration> { sub });
            }

            player.SetMediaItem(builder.Build());
            player.Prepare();
            if (resumeMs > 1000) player.SeekTo(resumeMs);
        }
        catch (Exception ex)
        {
            VirtualView?.RaiseMediaFailed($"加载失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 外挂字幕。Android 侧没有「运行期改字幕延迟」的公开 API，所以偏移靠
    /// <c>SubtitleSupport.ApplyOffset</c> 把字幕文本重写一份到缓存目录再交给播放器
    /// （Windows 侧有原生 <c>SetSubtitleDelay</c>，两端行为对齐但实现路径不同）。
    /// </summary>
    void IVideoPlayerImplementation.SetExternalSubtitle(string? path, string? mime, double offsetSeconds)
    {
        if (_player == null) return;
        try
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(mime))
            {
                _subtitleUri = null;
                _subtitleMime = null;
            }
            else
            {
                _subtitleMime = mime;
                _subtitleUri = PrepareSubtitleUri(path!, mime, offsetSeconds);
            }
            RebuildMediaItem();
        }
        catch (Exception ex)
        {
            // 字幕失败不该打断播放：只留痕，由 View 层的 SubtitleError 提示
            Maui.Services.BtFileLog.Write($"[player] 字幕加载失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>把字幕准备成播放器可用的 Uri（必要时先做时间轴平移）。</summary>
    private global::Android.Net.Uri? PrepareSubtitleUri(string path, string mime, double offsetSeconds)
    {
        var remote = path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        // 无偏移 → 直接用原文件/原地址，不产生副本
        if (Math.Abs(offsetSeconds) < 0.0005 || !Core.Services.SubtitleSupport.SupportsOffsetRewrite(mime))
            return remote ? global::Android.Net.Uri.Parse(path) : global::Android.Net.Uri.Parse("file://" + path);

        var text = remote
            ? new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) }.GetStringAsync(path).GetAwaiter().GetResult()
            : File.Exists(path) ? File.ReadAllText(path) : null;
        if (string.IsNullOrEmpty(text)) return null;

        var shifted = Core.Services.SubtitleSupport.ApplyOffset(text, offsetSeconds);
        var dir = Context?.CacheDir?.AbsolutePath ?? System.IO.Path.GetTempPath();
        var file = System.IO.Path.Combine(dir, $"subtitle-{Math.Abs(offsetSeconds).ToString("0.###")}.tmp");
        File.WriteAllText(file, shifted);
        return global::Android.Net.Uri.Parse("file://" + file);
    }

    /// <summary>
    /// 从地址推断 MIME（只处理 media3 猜不出来的情形）。
    /// 本地反代地址形如 <c>…/proxy?do=m3u8&amp;url=&lt;百分号编码的真实地址&gt;</c>，
    /// URI 路径后缀是 proxy，但真实资源是 HLS 播放列表 —— 必须显式告诉播放器。
    /// </summary>
    private static string? InferMime(string url)
    {
        try
        {
            if (url.Contains("do=m3u8", StringComparison.OrdinalIgnoreCase))
                return global::AndroidX.Media3.Common.MimeTypes.ApplicationM3u8;
            if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
                return global::AndroidX.Media3.Common.MimeTypes.ApplicationM3u8;
        }
        catch
        {
            // 类型名随 media3 版本变化时忽略：退回默认推断（与改动前行为一致）
        }
        return null;
    }

    /// <summary>构建 HTTP 数据源工厂（跨协议重定向 + 自定义请求头）</summary>
    private static DefaultHttpDataSource.Factory BuildHttpFactory(IReadOnlyDictionary<string, string>? headers)
    {
        var factory = new DefaultHttpDataSource.Factory()
            .SetAllowCrossProtocolRedirects(true)
            .SetConnectTimeoutMs(10_000)
            .SetReadTimeoutMs(20_000);
        // 源自己带了 User-Agent 时**不能再设默认 UA**：Media3 对两者都用 addRequestProperty，
        // HttpURLConnection 会把两个值并成一条 "UA1, UA2" —— 那是非法 UA 串，
        // 夸克这类 CDN 直接回 400 Bad Request（真机 2026-09-26「夸父原1」播放失败
        // Source error / InvalidResponseCodeException: Response code: 400 实测）。
        bool sourceHasUa = headers is not null
            && headers.Keys.Any(k => string.Equals(k, "User-Agent", StringComparison.OrdinalIgnoreCase));
        if (!sourceHasUa) factory.SetUserAgent(UserAgent);
        if (headers is { Count: > 0 })
            factory.SetDefaultRequestProperties(new Dictionary<string, string>(headers));
        return factory;
    }

    /// <summary>
    /// 解码模式切换（Android 路）。解码器工厂是在 <c>SimpleExoPlayer.Builder</c> 期定下的，
    /// 所以这里必须**重建 player**（保住位置与播放态），而不是改个属性。
    /// </summary>
    void IVideoPlayerImplementation.SetDecoderMode(VideoDecoderMode mode)
    {
        if (_decoderMode == mode) return;
        _decoderMode = mode;
        RebuildPlayer();
    }

    private VideoDecoderMode _decoderMode = VideoDecoderMode.Auto;

    private void RebuildPlayer()
    {
        var context = Context;
        if (context is null) return;
        var resumeMs = Math.Max(0, _player?.CurrentPosition ?? 0);
        var wasPlaying = _player?.IsPlaying == true;
        try
        {
            if (PlatformView != null) PlatformView.Player = null;
            _player?.Stop();
            _player?.Release();
        }
        catch { }

        _mediaSourceFactory = new DefaultMediaSourceFactory(BuildHttpFactory(_headers));
        var builder = new ExoPlayerBuilder(context).SetMediaSourceFactory(_mediaSourceFactory);
        if (_decoderMode != VideoDecoderMode.Auto)
        {
            var factory = new DefaultRenderersFactory(context);
            // 软解直接用 Media3 自带的 PREFER_SOFTWARE（上游实现，比自研选择器可靠）；
            // 硬解没有现成常量，只能自己按 MediaCodecInfo.HardwareAccelerated 过滤。
            factory.SetMediaCodecSelector(_decoderMode == VideoDecoderMode.Software
                ? IMediaCodecSelector.PreferSoftware!
                : new HardwareOnlySelector());
            // 绑定把 Builder.setRenderersFactory 投影成了属性（没有同名方法）
            builder.SetRenderersFactory(factory);
        }
        _player = builder.Build();
        // 交给后台播放服务借用（对位 TVBox 的 MusicPlaybackService）：换台会走这里重建，
        // 所以 Attach 必须在这里，不能只在首次创建时做一次
        PlayerHandoff.Attach(_player);
        _player.AddListener(new PlayerListener(this));
        if (PlatformView != null) PlatformView.Player = _player;

        RebuildMediaItem();
        if (resumeMs > 1000 && _player != null) _player.SeekTo(resumeMs);
        if (wasPlaying) _player?.Play();
        Maui.Services.BtFileLog.Write($"[player] 解码模式={_decoderMode} → 已重建 player（位置 {resumeMs / 1000}s）");
    }

    /// <summary>
    /// 只放行硬件解码器的选择器（对位 TVBox 的「ijk硬解」档）。
    /// <para>过滤后一条不剩时退回默认表 —— 宁可照常播，也不要「切个模式直接黑屏」。</para>
    /// </summary>
    private sealed class HardwareOnlySelector : Java.Lang.Object, IMediaCodecSelector
    {
        public System.Collections.Generic.IList<MediaCodecInfo> GetDecoderInfos(
            string mimeType, bool includesSecureDecoders, bool appliesTunnelingModeBlacklist)
        {
            var all = IMediaCodecSelector.Default!.GetDecoderInfos(
                mimeType, includesSecureDecoders, appliesTunnelingModeBlacklist);
            var kept = new List<MediaCodecInfo>();
            if (all is not null)
                foreach (MediaCodecInfo? info in all)
                    if (info is not null && info.HardwareAccelerated) kept.Add(info);
            return kept.Count > 0 ? kept : all!;
        }
    }

    // ═══════════════════ 轨道枚举与切换 ═══════════════════

    static int TrackTypeOf(VideoTrackKind kind) => kind switch
    {
        VideoTrackKind.Audio => AndroidX.Media3.Common.C.TrackTypeAudio,
        VideoTrackKind.Video => AndroidX.Media3.Common.C.TrackTypeVideo,
        _ => AndroidX.Media3.Common.C.TrackTypeText,
    };

    /// <summary>
    /// 取第 index 个轨道组。<c>Tracks.Groups</c> 是 Guava 的 ImmutableList，
    /// 绑定里没有泛型索引器（Count / [i] 都不存在），只能落到非泛型 <c>System.Collections.IList</c>。
    /// </summary>
    static AndroidX.Media3.Common.Tracks.Group? GroupAt(
        AndroidX.Media3.Common.Tracks tracks, int index) =>
        tracks.Groups is System.Collections.IList groups && index >= 0 && index < groups.Count
            ? groups[index] as AndroidX.Media3.Common.Tracks.Group
            : null;

    IReadOnlyList<VideoTrackInfo> IVideoPlayerImplementation.GetTracks(VideoTrackKind kind)
    {
        var list = new List<VideoTrackInfo>();
        if (_player?.CurrentTracks is not { } tracks) return list;
        var type = TrackTypeOf(kind);
        if (tracks.Groups is not System.Collections.IList groups) return list;

        for (var g = 0; g < groups.Count; g++)
        {
            if (GroupAt(tracks, g) is not { } group || group.Type != type) continue;
            for (var t = 0; t < group.Length; t++)
            {
                // Media3 1.10 起 Tracks.Track 已删，组内按序号直接取 Format
                if (group.GetTrackFormat(t) is not { } format) continue;
                list.Add(new VideoTrackInfo(
                    g + ":" + t,
                    Describe(format, kind, list.Count + 1),
                    group.IsTrackSelected(t)));
            }
        }
        return list;
    }

    static string Describe(AndroidX.Media3.Common.Format f, VideoTrackKind kind, int ordinal)
    {
        var bits = new List<string>();
        var name = !string.IsNullOrWhiteSpace(f.Label) ? f.Label : f.Language;
        if (!string.IsNullOrWhiteSpace(name)) bits.Add(TrackLang.NameOrSelf(name!.Trim()));
        if (kind == VideoTrackKind.Audio && f.ChannelCount > 0)
            bits.Add(TrackLang.Channels(f.ChannelCount));
        if (kind == VideoTrackKind.Video && f.Width > 0 && f.Height > 0) bits.Add(f.Width + "×" + f.Height);
        if (f.Bitrate > 0) bits.Add(f.Bitrate / 1000 + " kbps");
        return bits.Count > 0 ? string.Join(" · ", bits) : "轨道 " + ordinal;
    }

    void IVideoPlayerImplementation.SelectTrack(VideoTrackKind kind, string? id)
    {
        var player = _player;
        if (player is null) return;
        var type = TrackTypeOf(kind);
        if (player.TrackSelectionParameters?.BuildUpon()?.ClearOverrides() is not { } builder) return;

        if (string.IsNullOrEmpty(id))
        {
            // 只有字幕能整类关掉。音轨/视频轨关掉就是黑屏无声，所以空 id 对它们是「恢复自动选择」。
            builder.SetTrackTypeDisabled(type, kind == VideoTrackKind.Subtitle);
            player.TrackSelectionParameters = builder.Build();
            return;
        }

        var parts = id.Split(':', 2);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var gi) || !int.TryParse(parts[1], out var ti))
            return;
        if (player.CurrentTracks is not { } tracks || GroupAt(tracks, gi) is not { } group) return;
        if (group.MediaTrackGroup is not { } mediaGroup) return;

        builder.SetTrackTypeDisabled(type, false);
        // override 认的是 TrackGroup 对象 + 组内序号，不是全局扁平序号
        builder.SetOverrideForType(new AndroidX.Media3.Common.TrackSelectionOverride(mediaGroup, ti));
        player.TrackSelectionParameters = builder.Build();
    }

    void IVideoPlayerImplementation.Play()
    {
        _player?.Play();
        MaybeStartPlaybackService();
    }

    /// <summary>
    /// 需要后台继续放时拉起 Media3 的会话服务。
    /// <para>刻意在「点播放」这一刻启动：Android 12+ 不许从后台启前台服务，
    /// 等 <c>OnPause</c> 之后再启就可能直接被拒。</para>
    /// </summary>
    void MaybeStartPlaybackService()
    {
        if (!Maui.Services.BgPlayPrefs.IsOn) return;
        try
        {
            var ctx = Microsoft.Maui.ApplicationModel.Platform.AppContext;
            if (ctx is null || _player is null) return;
            PlayerHandoff.Attach(_player);
            var intent = new Intent(ctx, Java.Lang.Class.FromType(typeof(PlaybackService)));
            ctx.StartForegroundService(intent);
        }
        catch (Exception ex)
        {
            Maui.Services.BtFileLog.Write($"[后台播放] 启动服务失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    void IVideoPlayerImplementation.Pause() => _player?.Pause();

    void IVideoPlayerImplementation.Stop()
    {
        if (_player == null) return;
        _player.Pause();
        _player.SeekTo(0);
    }

    void IVideoPlayerImplementation.Seek(TimeSpan position) => _player?.SeekTo((long)position.TotalMilliseconds);

    void IVideoPlayerImplementation.SetVolume(double volume) { if (_player != null) _player.Volume = (float)volume; }

    /// <summary>播放速率：ExoPlayer 用 <c>PlaybackParameters</c>（音高不变，即变速不变调）。</summary>
    void IVideoPlayerImplementation.SetSpeed(double speed)
    {
        if (_player == null) return;
        try
        {
            var rate = (float)Math.Clamp(speed, 0.25, 4.0);
            _player.PlaybackParameters = new AndroidX.Media3.Common.PlaybackParameters(rate);
        }
        catch (Exception ex)
        {
            Maui.Services.BtFileLog.Write($"[player] 设置倍速失败（{speed}）：{ex.Message}");
        }
    }

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

    /// <summary>已缓冲位置：ExoPlayer 的 BufferedPosition（下载缓冲区的末端）。</summary>
    TimeSpan IVideoPlayerImplementation.GetBufferedPosition() =>
        _player == null ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Math.Max(0, _player.BufferedPosition));

    /// <summary>ExoPlayer 的 BufferedPosition 是真实的连续缓冲末端 —— 可信。</summary>
    bool IVideoPlayerImplementation.IsBufferedPositionReliable => true;

    /// <summary>Android 无宿主流缓存代理（磁力走迅雷 SDK）—— 交回宿主用位置推进判断。</summary>
    bool IVideoPlayerImplementation.IsWaitingForData => false;

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
