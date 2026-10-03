using System.Runtime.InteropServices;

namespace CatClawVideo.Maui.Platforms.Windows.Mpv;

/// <summary>
/// libmpv（mpv-2.dll，client API 2.5 / render API 1.x）的最小 P/Invoke 面。
/// 只绑本项目用到的函数；枚举/结构体数值从 mpv 头文件（client.h / render.h / render_gl.h）
/// 逐项核对过（mpv-dev-x86_64-20261002-git-3186d369f9），不要凭记忆改值。
/// </summary>
internal static unsafe class MpvLib
{
    private const string Dll = "mpv-2";

    public delegate void* MpvWakeupCallback(void* d);

    // ── client.h ────────────────────────────────────────────────

    [DllImport(Dll)] public static extern IntPtr mpv_create();
    [DllImport(Dll)] public static extern int mpv_initialize(IntPtr ctx);
    [DllImport(Dll)] public static extern void mpv_terminate_destroy(IntPtr ctx);

    /// <summary>args 为 UTF-8 字符串数组，末位以 null 收尾（见 MpvCommand 封装）。</summary>
    [DllImport(Dll)] public static extern int mpv_command(IntPtr ctx, byte** args);

    [DllImport(Dll)] public static extern int mpv_set_property_string(IntPtr ctx, byte* name, byte* value);
    [DllImport(Dll)] public static extern IntPtr mpv_get_property_string(IntPtr ctx, byte* name);
    [DllImport(Dll)] public static extern int mpv_observe_property(IntPtr ctx, ulong replyUserData, byte* name, int format);
    [DllImport(Dll)] public static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);
    [DllImport(Dll)] public static extern void mpv_wakeup(IntPtr ctx);

    /// <summary>订阅日志事件（LOG_MESSAGE）。msg-level 属性只管终端，libmpv 必须用这个。</summary>
    [DllImport(Dll)] public static extern int mpv_request_log_level(IntPtr ctx, int level);

    // mpv_log_level（client.h 1428-1435）
    public const int LogLevelInfo = 40;
    public const int LogLevelV = 50;

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEvent
    {
        public int EventId;
        public int Error;
        public ulong ReplyUserData;
        public void* Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEventProperty
    {
        public byte* Name;
        public int Format;
        public void* Data;
    }

    /// <summary>mpv_event_end_file 前 2 个字段（reason, error）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEventEndFile
    {
        public int Reason;
        public int Error;
    }

    /// <summary>mpv_event_log_message（client.h：prefix/level/text 均 NUL 结尾，text 自带换行）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEventLogMessage
    {
        public byte* Prefix;
        public byte* Level;
        public byte* Text;
        public int LogLevel;
    }

    // mpv_format
    public const int MpvFormatNone = 0;
    public const int MpvFormatString = 1;
    public const int MpvFormatOsdString = 2;
    public const int MpvFormatFlag = 3;
    public const int MpvFormatInt64 = 4;
    public const int MpvFormatDouble = 5;

    // mpv_event_id（client.h 1250-1367 逐项核对）
    public const int EventNone = 0;
    public const int EventShutdown = 1;
    public const int EventLogMessage = 2;
    public const int EventStartFile = 6;
    public const int EventEndFile = 7;
    public const int EventFileLoaded = 8;
    public const int EventIdle = 11;
    public const int EventVideoReconfig = 17;
    public const int EventSeek = 20;
    public const int EventPlaybackRestart = 21;
    public const int EventPropertyChange = 22;

    // mpv_end_file_reason
    public const int EndFileReasonEof = 0;
    public const int EndFileReasonStop = 2;
    public const int EndFileReasonQuit = 3;
    public const int EndFileReasonError = 4;
    public const int EndFileReasonRedirect = 5;

    // ── render.h / render_gl.h ──────────────────────────────────

    [DllImport(Dll)] public static extern int mpv_render_context_create(IntPtr* res, IntPtr mpv, MpvRenderParam* pars);
    [DllImport(Dll)] public static extern void mpv_render_context_free(IntPtr ctx);
    // set_update_callback 不绑：渲染由 MpvRenderControl 的 CompositionTarget.Rendering 驱动，
    // 每帧无条件 render（省掉 wakeup 回调跨线程 marshaling，参考实现同款做法）。
    [DllImport(Dll)] public static extern int mpv_render_context_render(IntPtr ctx, MpvRenderParam* pars);
    [DllImport(Dll)] public static extern void mpv_render_context_report_swap(IntPtr ctx);
    [DllImport(Dll)] public static extern ulong mpv_render_context_update(IntPtr ctx);

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvRenderParam
    {
        public int Type;
        public void* Data;

        public MpvRenderParam(int type, void* data) { Type = type; Data = data; }
    }

    // mpv_render_param_type（render.h 176-247 逐项核对）
    public const int RenderParamInvalid = 0;
    public const int RenderParamApiType = 1;
    public const int RenderParamOpenGLInitParams = 2;
    public const int RenderParamOpenGLFbo = 3;
    public const int RenderParamFlipY = 4;
    public const int RenderParamDepth = 5;
    public const int RenderParamIccProfile = 6;
    public const int RenderParamAdvancedControl = 10;
    public const int RenderParamNextFrameInfo = 11;
    public const int RenderParamBlockForTargetTime = 12;
    public const int RenderParamSkipRendering = 13;

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvOpenGLInitParams
    {
        public delegate* unmanaged[Cdecl]<void*, byte*, void*> GetProcAddress;
        public void* GetProcAddressCtx;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvOpenGLFbo
    {
        public int Fbo;
        public int W;
        public int H;
        public int InternalFormat;
    }

    /// <summary>MPV_RENDER_API_TYPE_OPENGL = "opengl"（render.h:468）。</summary>
    [DllImport(Dll)] public static extern int MpvRenderApiTypeOpenGl();

    public const string RenderApiTypeOpenGL = "opengl";

    // ── 封装助手 ────────────────────────────────────────────────

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static byte[] ToUtf8Z(string s) => Utf8.GetBytes(s + "\0");

    public static string FromUtf8(byte* p) => p == null ? "" : Marshal.PtrToStringUTF8((IntPtr)p) ?? "";

    /// <summary>mpv_command 的字符串数组封装（args 以 null 结尾）。
    /// ⚠ fixed 必须包住 mpv_command 调用本身：若在循环内 fixed 后出作用域，
    /// 托管数组可能被 GC 移动，bufs 里就是悬空指针（GC 压缩时必炸）。</summary>
    public static int Command(IntPtr ctx, params string[] args)
    {
        var encoded = new byte[args.Length][];
        var bufs = new byte*[args.Length + 1];
        for (var i = 0; i < args.Length; i++)
            encoded[i] = ToUtf8Z(args[i]);
        fixed (byte** argv = bufs)
        {
            for (var i = 0; i < args.Length; i++)
            {
                fixed (byte* p = encoded[i])
                    bufs[i] = p;
            }
            var rc = mpv_command(ctx, argv);
            return rc;
        }
    }

    public static int SetPropertyString(IntPtr ctx, string name, string value)
    {
        var n = ToUtf8Z(name);
        var v = ToUtf8Z(value);
        fixed (byte* np = n, vp = v)
            return mpv_set_property_string(ctx, np, vp);
    }

    public static string? GetPropertyString(IntPtr ctx, string name)
    {
        var n = ToUtf8Z(name);
        fixed (byte* np = n)
        {
            var p = mpv_get_property_string(ctx, np);
            if (p == IntPtr.Zero) return null;
            var s = FromUtf8((byte*)p);
            mpv_free(p);
            return s;
        }
    }

    [DllImport(Dll)] public static extern void mpv_free(IntPtr ptr);

    /// <summary>读数值属性（int64/double/flag 通吃）：优先 int64，回落 double，再回落 string 解析。</summary>
    public static double GetPropertyDouble(IntPtr ctx, string name)
    {
        var n = ToUtf8Z(name);
        fixed (byte* np = n)
        {
            long i = 0;
            if (mpv_get_property(ctx, np, MpvFormatInt64, &i) >= 0) return i;
            double d = 0;
            if (mpv_get_property(ctx, np, MpvFormatDouble, &d) >= 0) return d;
            var s = mpv_get_property_string(ctx, np);
            if (s != IntPtr.Zero)
            {
                var text = FromUtf8((byte*)s);
                mpv_free(s);
                return double.TryParse(text, out var v) ? v : 0;
            }
            return 0;
        }
    }

    [DllImport(Dll)] public static extern int mpv_get_property(IntPtr ctx, byte* name, int format, void* data);
}
