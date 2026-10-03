using CatClawVideo.Maui.Controls;
using OpenTK.Graphics.OpenGL;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CatClawVideo.Maui.Platforms.Windows.Mpv;

/// <summary>
/// libmpv 播放后端（Windows）：杜比视界/HDR 片源的正确出路。
/// FFmpegInteropX 不解析 DV 的 RPU 动态元数据，P5 基础层按错误色彩空间直出
/// ⇒ 发灰泛紫；mpv（gpu 渲染器 + FFmpeg RPU 解析 + tone-mapping）能把 DV/HDR10
/// 正确压到 SDR 显示。渲染宿主见 <see cref="MpvRenderControl"/>（GL→D3D11 互操作，
/// 无 ANGLE）。本类实现 <see cref="IVideoPlayerImplementation"/>，由 Handler 在
/// 检测到 DV 片源时代管播放。
/// </summary>
public unsafe sealed class MpvVideoBackend : IVideoPlayerImplementation, IDisposable
{
    private readonly VideoPlayerView _view;
    private readonly MpvRenderControl _renderControl;

    private IntPtr _mpv;
    private IntPtr _renderCtx;
    private Thread? _eventPump;
    private volatile bool _pumpStop;
    private bool _renderCtxReady;

    private double _pendingSpeed = 1.0;
    private double _pendingVolume = -1;
    private IReadOnlyDictionary<string, string>? _headers;
    /// <summary>当前源是否杜比视界（决定 hwdec 软解/硬解，initialize 前定案）。</summary>
    private bool _dvSource;

    /// <summary>必须在 Initialize() 之前调用：DV 源强制软解（hwdec=no），RPU 才能到达 mpv 做 tone-map。</summary>
    public void SetDolbyVision(bool dv)
    {
        _dvSource = dv;
        if (_mpv != IntPtr.Zero)
            MpvLib.SetPropertyString(_mpv, "hwdec", dv ? "no" : "auto-copy");
        BtLog($"[mpv] DV 源={dv} → hwdec={(dv ? "no（软解，保 RPU）" : "auto-copy")}");
    }

    public MpvVideoBackend(VideoPlayerView view, MpvRenderControl renderControl)
    {
        _view = view;
        _renderControl = renderControl;
    }

    /// <summary>初始化 mpv 实例 + 渲染上下文。失败抛异常（Handler 回落 FFmpeg 路径）。</summary>
    public void Initialize()
    {
        _mpv = MpvLib.mpv_create();
        if (_mpv == IntPtr.Zero) throw new InvalidOperationException("mpv_create 失败");

        // 关键选项：全部在 initialize 之前设置
        MpvLib.SetPropertyString(_mpv, "vo", "libmpv");
        // 默认硬解 copy 模式（解码帧拷回内存再进 GL，规避 GL-DX interop 硬解的驱动差异）。
        // ⚠ DV 源必须软解：d3d11va 等 hwaccel 的 HEVC 输出 GPU surface，**不携带 RPU**，
        //   mpv 拿不到 DV 元数据 → P5 的 IPT 色彩被当普通 YCbCr 解读 → 发绿发紫（12:56 实测）。
        //   是否 DV 由 Handler 探测后经 SetDolbyVision 下发（initialize 前必须定案）。
        MpvLib.SetPropertyString(_mpv, "hwdec", _dvSource ? "no" : "auto-copy");
        MpvLib.SetPropertyString(_mpv, "keep-open", "no");
        MpvLib.SetPropertyString(_mpv, "idle", "yes");
        MpvLib.SetPropertyString(_mpv, "force-window", "no");
        MpvLib.SetPropertyString(_mpv, "audio-display", "no");
        MpvLib.SetPropertyString(_mpv, "input-default-bindings", "no");
        MpvLib.SetPropertyString(_mpv, "osc", "no");
        // DV/HDR → SDR tone-map：默认 auto（HDR10 用 bt.2390，DV 走 RPU 指导），这里显式钉住
        MpvLib.SetPropertyString(_mpv, "tone-mapping", "bt.2446a");
        // 网络流：mpv 自带缓存（默认 demuxer-max-bytes 150MiB 已够），Range 直连可 seek
        MpvLib.SetPropertyString(_mpv, "cache", "yes");
        MpvLib.SetPropertyString(_mpv, "demuxer-max-bytes", "64MiB");
        MpvLib.SetPropertyString(_mpv, "demuxer-readahead-secs", "10");
        // 网络超时与重试：跟播放场景对齐（长超时，别秒断）
        MpvLib.SetPropertyString(_mpv, "network-timeout", "30");
        MpvLib.SetPropertyString(_mpv, "stream-lavf-o", "reconnect=1,reconnect_streamed=1,reconnect_delay_max=10");

        // DV 日志：详细日志由 wait_event 的 LOG_MESSAGE 事件透出（见 EventPump）

        var rc = MpvLib.mpv_initialize(_mpv);
        if (rc < 0) throw new InvalidOperationException($"mpv_initialize 失败: {MpvErrorStr(rc)}");

        // 属性观察（事件泵据此推 View 层状态；位置由 View 层定时器轮询 GetPosition，不走事件）
        Observe("track-list", MpvLib.MpvFormatString);

        // 渲染上下文：等 RenderControl 就绪（GL 上下文已 current）后再建
        _renderControl.Render += OnRenderFrame;

        // mpv 内部日志桥：这套 mpv-2.dll 构建没导出 mpv_request_log_level（入口点缺失，
        // 13:12 实测把整个 Initialize 炸了）——改用 log-file 写文件（独立于事件机制）。
        try
        {
            var mpvLog = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CatClawVideo.debug", "logs", "mpv.log");
            MpvLib.SetPropertyString(_mpv, "log-file", mpvLog);
            MpvLib.SetPropertyString(_mpv, "log-append", "yes");
        }
        catch { /* 日志文件失败不致命 */ }

        _pumpStop = false;
        _eventPump = new Thread(EventPump) { IsBackground = true, Name = "mpv-events" };
        _eventPump.Start();

        BtLog("[mpv] 初始化完成（hwdec=auto-copy, tone-mapping=bt.2446a）");
    }

    private static string MpvErrorStr(int code) => $"code={code}";

    private void Observe(string name, int format)
    {
        var n = MpvLib.ToUtf8Z(name);
        fixed (byte* np = n)
            MpvLib.mpv_observe_property(_mpv, 0, np, format);
    }

    private static void BtLog(string msg) =>
        Maui.Services.BtFileLog.Write(msg);

    // ═══════════ 渲染 ═══════════

    private void OnRenderFrame(TimeSpan frameDelta)
    {
        if (_mpv == IntPtr.Zero) return;
        EnsureRenderContext();
        if (!_renderCtxReady || _renderControl.FrameBuffer is null) return;

        var fb = _renderControl.FrameBuffer;
        var fbo = new MpvLib.MpvOpenGLFbo
        {
            Fbo = fb.GLFrameBufferHandle,
            W = fb.BufferWidth,
            H = fb.BufferHeight,
            InternalFormat = 0,
        };
        var flip = 0;   // GL FBO 渲染：mpv 按 GL 惯例（左下原点）输出，不翻转（参考实现 flipY=0；=1 会画面倒置）

        // ⚠ 参数只给 [Fbo, FlipY, 终止符]——绝不能传 BlockForTargetTime 且 Data=null：
        //   该 param 的数据类型是 int*，mpv 内部直接解引用 ⇒ null ⇒ AV（12:43/12:52 两次闪退根因）。
        MpvLib.MpvRenderParam* pars = stackalloc MpvLib.MpvRenderParam[3];
        {
            pars[0] = new(MpvLib.RenderParamOpenGLFbo, &fbo);
            pars[1] = new(MpvLib.RenderParamFlipY, &flip);
            pars[2] = default; // 终止符 MPV_RENDER_PARAM_INVALID
            var rc = MpvLib.mpv_render_context_render(_renderCtx, pars);
            if (!_firstFrameLogged)
            {
                _firstFrameLogged = true;
                BtLog($"[mpv] 首帧渲染调用完成 rc={rc} fbo={fb.GLFrameBufferHandle} {fb.BufferWidth}x{fb.BufferHeight}");
            }
            else if (rc < 0 && _lastRenderRc >= 0)
            {
                BtLog($"[mpv] 渲染开始报错 rc={rc}（此前正常）");
            }
            _lastRenderRc = rc;
        }
        MpvLib.mpv_render_context_report_swap(_renderCtx);
    }

    private bool _firstFrameLogged;
    private int _lastRenderRc;

    /// <summary>在 GL 上下文当前线程上创建 render context（RenderControl.Ready 已 MakeCurrent）。</summary>
    private void EnsureRenderContext()
    {
        if (_renderCtxReady) return;

        var apiType = MpvLib.ToUtf8Z(MpvLib.RenderApiTypeOpenGL);
        MpvLib.MpvOpenGLInitParams initParams = default;
        initParams.GetProcAddress = &GetProcAddressThunk;
        MpvLib.MpvRenderParam* pars = stackalloc MpvLib.MpvRenderParam[3];
        fixed (byte* api = apiType)
        {
            pars[0] = new(MpvLib.RenderParamApiType, api);
            pars[1] = new(MpvLib.RenderParamOpenGLInitParams, &initParams);
            pars[2] = default;
            IntPtr ctx;
            var rc = MpvLib.mpv_render_context_create(&ctx, _mpv, pars);
            if (rc < 0)
            {
                BtLog($"[mpv] render_context_create 失败: {rc}");
                _renderCtxReady = false;
                return;
            }
            _renderCtx = ctx;
        }
        _renderCtxReady = true;
        BtLog("[mpv] 渲染上下文就绪（OpenGL → D3D11 互操作）");
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void* GetProcAddressThunk(void* ctx, byte* name) =>
        (void*)MpvRenderContext.GetProcAddress(MpvLib.FromUtf8(name));

    // ═══════════ 事件泵 ═══════════

    private void EventPump()
    {
        while (!_pumpStop && _mpv != IntPtr.Zero)
        {
            var ev = MpvLib.mpv_wait_event(_mpv, 0.5);
            if (ev == IntPtr.Zero) continue;
            var e = System.Runtime.InteropServices.Marshal.PtrToStructure<MpvLib.MpvEvent>(ev);
            switch (e.EventId)
            {
                case MpvLib.EventShutdown:
                    return;
                case MpvLib.EventLogMessage:
                    // [prefix] level: text —— mpv 内部日志（GL 初始化错误、fallback、AV 前的最后遗言）
                    var log = System.Runtime.InteropServices.Marshal.PtrToStructure<MpvLib.MpvEventLogMessage>((IntPtr)e.Data);
                    var prefix = MpvLib.FromUtf8(log.Prefix);
                    var level = MpvLib.FromUtf8(log.Level);
                    var text = MpvLib.FromUtf8(log.Text).TrimEnd();
                    BtLog($"[mpv:{prefix}/{level}] {text}");
                    break;
                case MpvLib.EventFileLoaded:
                    // 媒体就绪：View 层据此触发 MediaOpened（含 ShouldAutoPlay 的自动 Play）。
                    // 事件泵在后台线程，View/WinUI 访问必须回 UI 线程。
                    DispatchOnUi(() => _view.RaiseMediaOpened());
                    break;
                case MpvLib.EventEndFile:
                    var endFile = System.Runtime.InteropServices.Marshal.PtrToStructure<MpvLib.MpvEventEndFile>((IntPtr)e.Data);
                    var reason = endFile.Reason;
                    if (reason == MpvLib.EndFileReasonEof)
                        DispatchOnUi(() => _view.RaiseMediaEnded());
                    else if (reason == MpvLib.EndFileReasonError)
                        DispatchOnUi(() => _view.RaiseMediaFailed("播放失败: mpv 解码/网络错误"));
                    break;
            }
        }
    }

    private void DispatchOnUi(Action action)
    {
        try
        {
            var dispatcher = _view.Dispatcher;
            if (dispatcher is null) { action(); return; }
            dispatcher.Dispatch(() => { try { action(); } catch { } });
        }
        catch { try { action(); } catch { } }
    }

    // ═══════════ IVideoPlayerImplementation ═══════════

    void IVideoPlayerImplementation.SetSource(string? url, IReadOnlyDictionary<string, string>? headers)
    {
        if (_mpv == IntPtr.Zero) return;
        _headers = headers;

        if (string.IsNullOrEmpty(url))
        {
            MpvLib.Command(_mpv, "stop");
            return;
        }

        // 防盗链头（TVBox 源 Referer/UA）→ mpv http 选项
        if (headers is not null)
        {
            var pairs = new List<string>();
            foreach (var kv in headers)
                pairs.Add($"{kv.Key}: {kv.Value}");
            MpvLib.SetPropertyString(_mpv, "http-header-fields", string.Join(",", pairs));
        }

        var rc = MpvLib.Command(_mpv, "loadfile", url, "replace");
        BtLog($"[mpv] loadfile rc={rc}: {url}");
        if (rc >= 0 && Math.Abs(_pendingSpeed - 1.0) > 0.001)
            MpvLib.SetPropertyString(_mpv, "speed", _pendingSpeed.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        if (rc >= 0 && _pendingVolume >= 0)
            MpvLib.SetPropertyString(_mpv, "volume", (_pendingVolume * 100).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
    }

    void IVideoPlayerImplementation.Play() { if (_mpv != IntPtr.Zero) MpvLib.SetPropertyString(_mpv, "pause", "no"); }
    void IVideoPlayerImplementation.Pause() { if (_mpv != IntPtr.Zero) MpvLib.SetPropertyString(_mpv, "pause", "yes"); }
    void IVideoPlayerImplementation.Stop() { if (_mpv != IntPtr.Zero) MpvLib.Command(_mpv, "stop"); }

    void IVideoPlayerImplementation.Seek(TimeSpan position)
    {
        if (_mpv == IntPtr.Zero) return;
        MpvLib.Command(_mpv, "seek", position.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "absolute");
    }

    void IVideoPlayerImplementation.SetVolume(double volume)
    {
        _pendingVolume = volume;
        if (_mpv != IntPtr.Zero)
            MpvLib.SetPropertyString(_mpv, "volume", Math.Clamp(volume * 100, 0, 100).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
    }

    void IVideoPlayerImplementation.SetAspect(VideoAspect aspect)
    {
        if (_mpv == IntPtr.Zero) return;
        switch (aspect)
        {
            case VideoAspect.Original:
                // 原始比例：1:1 像素不缩放（窗口小于视频则裁边，大于则居中留边）
                MpvLib.SetPropertyString(_mpv, "video-unscaled", "yes");
                MpvLib.SetPropertyString(_mpv, "video-aspect-override", "no");
                MpvLib.SetPropertyString(_mpv, "panscan", "0");
                break;
            case VideoAspect.AspectFit:
                MpvLib.SetPropertyString(_mpv, "video-unscaled", "no");
                MpvLib.SetPropertyString(_mpv, "video-aspect-override", "no");
                MpvLib.SetPropertyString(_mpv, "panscan", "0");
                break;
            case VideoAspect.AspectFill:
                MpvLib.SetPropertyString(_mpv, "video-unscaled", "no");
                MpvLib.SetPropertyString(_mpv, "video-aspect-override", "no");
                MpvLib.SetPropertyString(_mpv, "panscan", "1");
                break;
            case VideoAspect.Fill:
                MpvLib.SetPropertyString(_mpv, "video-unscaled", "no");
                MpvLib.SetPropertyString(_mpv, "video-aspect-override", "display");
                MpvLib.SetPropertyString(_mpv, "panscan", "0");
                break;
        }
    }

    void IVideoPlayerImplementation.KeepScreenOn(bool keep) { /* Windows 无意义 */ }

    void IVideoPlayerImplementation.SetSpeed(double speed)
    {
        _pendingSpeed = speed;
        if (_mpv != IntPtr.Zero)
            MpvLib.SetPropertyString(_mpv, "speed", Math.Clamp(speed, 0.1, 4.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
    }

    void IVideoPlayerImplementation.SetExternalSubtitle(string? path, string? mime, double offsetSeconds)
    {
        if (_mpv == IntPtr.Zero) return;
        if (string.IsNullOrEmpty(path))
        {
            MpvLib.Command(_mpv, "sub-remove");
            return;
        }
        var rc = MpvLib.Command(_mpv, "sub-add", path, "auto", "CatClawExt");
        if (rc < 0) BtLog($"[mpv] sub-add 失败 rc={rc}（外挂字幕加载失败）");
        MpvLib.SetPropertyString(_mpv, "sub-delay", (-offsetSeconds).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
    }

    void IVideoPlayerImplementation.SetDecoderMode(VideoDecoderMode mode)
    {
        if (_mpv == IntPtr.Zero) return;
        MpvLib.SetPropertyString(_mpv, "hwdec", mode switch
        {
            VideoDecoderMode.Hardware => "auto",
            VideoDecoderMode.Software => "no",
            _ => "auto-copy",
        });
    }

    IReadOnlyList<VideoTrackInfo> IVideoPlayerImplementation.GetTracks(VideoTrackKind kind)
    {
        if (_mpv == IntPtr.Zero) return Array.Empty<VideoTrackInfo>();
        var json = MpvLib.GetPropertyString(_mpv, "track-list");
        if (string.IsNullOrEmpty(json)) return Array.Empty<VideoTrackInfo>();
        try
        {
            var arr = System.Text.Json.JsonDocument.Parse(json).RootElement;
            var kindStr = kind switch { VideoTrackKind.Audio => "audio", VideoTrackKind.Subtitle => "sub", _ => "video" };
            var list = new List<VideoTrackInfo>();
            foreach (var t in arr.EnumerateArray())
            {
                if (!t.TryGetProperty("type", out var tp) || tp.GetString() != kindStr) continue;
                var id = t.TryGetProperty("id", out var idv) ? idv.GetInt64().ToString() : "?";
                var title = t.TryGetProperty("title", out var tv) ? tv.GetString() : null;
                var lang = t.TryGetProperty("lang", out var lv) ? lv.GetString() : null;
                var codec = t.TryGetProperty("codec", out var cv) ? cv.GetString() : null;
                var selected = t.TryGetProperty("selected", out var sv) && sv.GetBoolean();
                var display = title ?? lang ?? (codec != null ? $"{kindStr} {id} ({codec})" : $"轨道 {id}");
                list.Add(new VideoTrackInfo(id, display, selected));
            }
            return list;
        }
        catch { return Array.Empty<VideoTrackInfo>(); }
    }

    void IVideoPlayerImplementation.SelectTrack(VideoTrackKind kind, string? id)
    {
        if (_mpv == IntPtr.Zero) return;
        var prop = kind switch { VideoTrackKind.Audio => "aid", VideoTrackKind.Subtitle => "sid", _ => "vid" };
        MpvLib.SetPropertyString(_mpv, prop, string.IsNullOrEmpty(id) ? "no" : id);
    }

    TimeSpan IVideoPlayerImplementation.GetPosition() =>
        _mpv == IntPtr.Zero ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Max(0, MpvLib.GetPropertyDouble(_mpv, "time-pos")));

    TimeSpan IVideoPlayerImplementation.GetDuration() =>
        _mpv == IntPtr.Zero ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Max(0, MpvLib.GetPropertyDouble(_mpv, "duration")));

    TimeSpan IVideoPlayerImplementation.GetBufferedPosition() =>
        _mpv == IntPtr.Zero ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Max(0, MpvLib.GetPropertyDouble(_mpv, "demuxer-cache-time")));

    bool IVideoPlayerImplementation.IsBufferedPositionReliable => _mpv != IntPtr.Zero;

    bool IVideoPlayerImplementation.IsWaitingForData =>
        _mpv != IntPtr.Zero && MpvLib.GetPropertyDouble(_mpv, "paused-for-cache") > 0.5;

    public void Dispose()
    {
        BtLog($"[mpv] Dispose 开始（renderCtx={_renderCtx != IntPtr.Zero} mpv={_mpv != IntPtr.Zero}）");
        _pumpStop = true;
        _renderControl.Render -= OnRenderFrame;
        if (_renderCtx != IntPtr.Zero)
        {
            // 文档要求：render_context_free 必须在创建它的线程、GL 上下文 current 时调用
            //（本类生命周期全部在 UI 线程，共享 GL 上下文也常驻 current 于 UI 线程）
            try
            {
                MpvLib.mpv_render_context_free(_renderCtx);
                BtLog("[mpv] render context 已释放");
            }
            catch (Exception ex)
            {
                BtLog($"[mpv] render context 释放异常: {ex.GetType().Name}");
            }
            _renderCtx = IntPtr.Zero;
        }
        if (_mpv != IntPtr.Zero)
        {
            MpvLib.mpv_wakeup(_mpv);
            if (_eventPump is { } pump && !pump.Join(TimeSpan.FromSeconds(3)))
                BtLog("[mpv] 警告：事件泵 3s 未退出，仍继续销毁");
            MpvLib.mpv_terminate_destroy(_mpv);
            _mpv = IntPtr.Zero;
            BtLog("[mpv] 实例已销毁");
        }
    }
}
