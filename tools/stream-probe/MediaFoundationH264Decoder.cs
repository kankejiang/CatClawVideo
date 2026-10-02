using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace StreamProbe;

/// <summary>
/// Media Foundation H.264 软解码：Microsoft H264 Video Decoder MFT（异步 MFT）。
///
/// <para><b>为什么用裸 vtable 调用（Marshal.GetDelegateForFunctionPointer 按槽位直调）
/// 而不是 [ComImport] 接口：</b>.NET 的 RCW cast 与原生 QI 行为不一致（实测：
/// MFTEnumEx 激活对象原生 QI IMFTransform 成功，RCW cast 同一 IID 却 E_NOINTERFACE）。
/// 裸 vtable 按槽位号调用，零 COM interop 魔法，行为完全确定。</para>
///
/// <para><b>槽位表依据（2026-10-02 定稿，N2-1）</b>：Windows SDK MIDL 生成头
/// （mftransform.h，tpn/winsdk-10 镜像）逐槽核对；IMFAttributes 31 法（含
/// DeleteItem/DeleteAllItems，夹在 SetItem 与 SetUINT32 之间）与 Wine mfobjects.idl
/// 一致，且旧实现用 21/8/24 三个属性槽可交叉验证。此前按记忆拼的表
/// （GetAttributes=17、SetInputType=14…）全数错位 —— 症状：GetInputCurrentType
/// 冒充 GetAttributes 报 0xC00D36B3、GetOutputAvailableType 冒充 SetInputType 报
/// ASYNC_LOCKED、GetItemByIndex 冒充 ActivateObject 直接 AV。</para>
///
/// <para>异步 MFT 用法：GetAttributes(8) → SetUINT32(MF_TRANSFORM_ASYNC_UNLOCK=1) 解锁 →
/// SetInputType(H264)/SetOutputType(NV12) → 事件驱动 NeedInput(601)（喂 AU）/
/// HaveOutput(602)（取 NV12→BGRA）。PTS 按到达顺序打记号（仅排序用），出帧即时返回，
/// 不按解码顺序排队 ⇒ 无累积延迟（N2-1 判据）。</para>
/// </summary>
internal sealed class MediaFoundationH264Decoder : IDisposable
{
    // ── 常量 ─────────────────────────────────────────────────────────────
    private const int MF_VERSION = (2 << 16) | 70;
    private const int COINIT_MULTITHREADED = 0x0;
    private const uint CLSCTX_INPROC_SERVER = 0x1;
    private const int MF_EVENT_FLAG_NO_WAIT = 0x2;
    private const int S_OK = 0, S_FALSE = 1;
    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private const int MF_E_NO_EVENTS_AVAILABLE = unchecked((int)0xC00D3E80);
    private const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    private const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    private const int MF_E_NOTACCEPTING = unchecked((int)0xC00D6D71);
    private const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);

    private const int METransformNeedInput = 601;
    private const int METransformHaveOutput = 602;

    private const long MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000;
    private const long MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;

    private static readonly Guid CLSID_CMSH264DecoderMFT_Win11 = new("62CE7E72-4C71-4D20-B15D-452831A87D9D");
    private static readonly Guid CLSID_CMSH264DecoderMFT = new("62CE7E72-4C71-4D20-B15D-4522A53CB4A6");
    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8006-a20b786e4948");
    private static readonly Guid IID_IMFTransform_V2 = new("bf94c121-5b05-4e6f-8000-ba598961414d");
    private static readonly Guid IID_ICodecAPI = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
    private static readonly Guid IID_IMFMediaEventGenerator = new("2CD0BD52-BCD5-4B89-B62C-EADC0C031E7D");
    private static Guid _iidEventGen = IID_IMFMediaEventGenerator;
    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00AA00389B71");
    private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e6cc-453c-a889-b6d7dfebb48a");
    private static readonly Guid MF_TRANSFORM_ASYNC_UNLOCK = new("e5666e60-3422-4eb6-a421-da7db1f8e207");
    private static readonly Guid CODECAPI_AVLowLatencyMode = new("9C27891A-ED7A-40E1-88E8-B227D2780C4C");
    private static readonly Guid MFT_CATEGORY_VIDEO_DECODER = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");
    private static readonly Guid MFT_TRANSFORM_CLSID_Attr = new("68abd1c0-a7fb-4767-95c7-4b8c68f9cdcb");
    private static readonly Guid MFT_FRIENDLY_NAME_Attr = new("314ff86e-09c8-4e11-9e5a-c952c1e1a2ed");
    private const uint MFT_ENUM_FLAG_ALL = 0x3F;
    private const uint MFT_ENUM_FLAG_SORTANDFILTER = 0x20;
    private const int MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x100;

    private static bool _mfStarted;
    private static readonly object MfGate = new();

    // ── 委托（StdCall，与 COM vtable 一致）───────────────────────────────
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetOutputStreamInfoD(IntPtr self, int streamId, out MFT_OUTPUT_STREAM_INFO info);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetAttributesD(IntPtr self, out IntPtr attrs);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetOutputCurrentTypeD(IntPtr self, int streamId, out IntPtr type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetInputTypeD(IntPtr self, int streamId, IntPtr type, int flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetOutputTypeD(IntPtr self, int streamId, IntPtr type, int flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetOutputAvailableTypeD(IntPtr self, int streamId, int index, out IntPtr type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ProcessMessageD(IntPtr self, long msg, IntPtr param);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ProcessInputD(IntPtr self, int streamId, IntPtr sample, int flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ProcessOutputD(IntPtr self, int flags, int count, ref MFT_OUTPUT_DATA_BUFFER buffers, out int status);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetUINT32D(IntPtr self, ref Guid key, out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetUINT32D(IntPtr self, ref Guid key, int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetUINT64D(IntPtr self, ref Guid key, out long value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetUINT64D(IntPtr self, ref Guid key, long value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetGUIDD(IntPtr self, ref Guid key, ref Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetGUIDD(IntPtr self, ref Guid key, out Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetCountD(IntPtr self, out int count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int LockD(IntPtr self, out IntPtr ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int UnlockD(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetCurrentLengthD(IntPtr self, uint cbCurrentLength);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetCurrentLengthD(IntPtr self, out uint cbCurrentLength);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetSampleTimeD(IntPtr self, out long hnsSampleTime);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AddBufferD(IntPtr self, IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetBufferCountD(IntPtr self, out int count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetSampleTimeD(IntPtr self, long hnsSampleTime);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetSampleDurationD(IntPtr self, long hnsSampleDuration);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ConvertToContiguousD(IntPtr self, out IntPtr ppBuffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetEventD(IntPtr self, int dwFlags, out IntPtr ppEvent);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetEventTypeD(IntPtr self, out int met);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ActivateObjectD(IntPtr self, ref Guid riid, out IntPtr ppv);

    [StructLayout(LayoutKind.Sequential)]
    private struct MFT_INPUT_STREAM_INFO
    {
        public long hnsMaxLatency;
        public int dwFlags;
        public int cbSize;
        public int cbMaxLookahead;
        public int cbAlignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MFT_OUTPUT_STREAM_INFO
    {
        public long hnsMaxLatency;
        public int dwFlags;
        public int cbSize;
        public int cbMaxLookahead;
        public int cbAlignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MFT_OUTPUT_DATA_BUFFER
    {
        public int dwStreamID;
        public IntPtr pSample;
        public int dwStatus;
        public IntPtr pEvents;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MFT_REGISTER_TYPE_INFO
    {
        public Guid guidMajorType;
        public Guid guidSubtype;
    }

    // ── 裸 vtable 工具 ───────────────────────────────────────────────────
    private static T Slot<T>(IntPtr obj, int slot) where T : Delegate
    {
        var vtbl = Marshal.ReadIntPtr(obj);
        var fn = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer(fn, typeof(T)) as T
            ?? throw new InvalidOperationException("vtable 委托转换失败");
    }

    private static IntPtr QI(IntPtr unk, Guid iid)
    {
        var g = iid;
        return Marshal.QueryInterface(unk, ref g, out var p) == S_OK ? p : IntPtr.Zero;
    }

    // ═══ 槽位常量（IUnknown 占 0..2；依据见类注释）═══

    // IMFTransform：SDK mftransform.h MIDL 序，槽 3..25
    private const int T_GETSTREAMCOUNT = 4;          // GetStreamCount
    private const int T_GETSTREAMIDS = 5;            // GetStreamIDs
    private const int T_GETOUTPUTSTREAMINFO = 7;     // GetOutputStreamInfo
    private const int T_GETATTRIBUTES = 8;           // GetAttributes ← 异步解锁钥匙
    private const int T_GETINPUTSTREAMATTR = 9;      // GetInputStreamAttributes
    private const int T_GETOUTPUTSTREAMATTR = 10;    // GetOutputStreamAttributes
    private const int T_GETOUTPUTAVAILABLETYPE = 14; // GetOutputAvailableType
    private const int T_GETOUTPUTCURRENTTYPE = 18;   // GetOutputCurrentType
    private const int T_SETINPUTTYPE = 15;           // SetInputType
    private const int T_SETOUTPUTTYPE = 16;          // SetOutputType
    private const int T_PROCESSMESSAGE = 23;         // ProcessMessage
    private const int T_PROCESSINPUT = 24;           // ProcessInput
    private const int T_PROCESSOUTPUT = 25;          // ProcessOutput

    // IMFAttributes：30 法（含 DeleteItem/DeleteAllItems，夹在 SetItem 与 SetUINT32 之间），槽 3..32
    private const int ATTR_GETUINT32 = 7;
    private const int ATTR_GETUINT64 = 8;
    private const int ATTR_GETGUID = 10;
    private const int ATTR_SETUINT32 = 21;
    private const int ATTR_SETUINT64 = 22;
    private const int ATTR_SETGUID = 24;
    private const int ATTR_GETCOUNT = 30;            // 完备性哨兵（31=GetItemByIndex，会报 E_POINTER）

    // IMFSample（继承 IMFAttributes 到 32，槽 33 起）：
    // GetSampleFlags=33,SetSampleFlags=34,GetSampleTime=35,SetSampleTime=36,
    // GetSampleDuration=37,SetSampleDuration=38,GetBufferCount=39,GetBufferByIndex=40,
    // ConvertToContiguousBuffer=41,AddBuffer=42
    private const int SAMPLE_GETSAMPLETIME = 35;
    private const int SAMPLE_GETBUFFERCOUNT = 39;
    private const int SAMPLE_SETSAMPLETIME = 36;
    private const int SAMPLE_SETSAMPLEDURATION = 38;
    private const int SAMPLE_CONVERTTOCONTIGUOUS = 41;
    private const int SAMPLE_ADDBUFFER = 42;

    // IMFMediaBuffer（直接继承 IUnknown）：Lock=3,Unlock=4,GetCurrentLength=5,SetCurrentLength=6,GetMaxLength=7
    private const int BUF_LOCK = 3, BUF_UNLOCK = 4, BUF_GETCURRENTLENGTH = 5, BUF_SETCURRENTLENGTH = 6;

    // IMFMediaEventGenerator（直接继承 IUnknown）：GetEvent=3,BeginGetEvent=4,EndGetEvent=5,QueueEvent=6
    private const int EVG_GETEVENT = 3;

    // IMFMediaEvent（继承 IMFAttributes 到 32，槽 33 起）：GetType=33
    private const int EV_GETTYPE = 33;

    // IMFActivate（继承 IMFAttributes 到 32，槽 33 起）：ActivateObject=33
    private const int ACT_ACTIVATEOBJECT = 33;

    // ── 状态 ─────────────────────────────────────────────────────────────
    private readonly LinkedList<byte[]> _pending = new();
    private Bitmap? _last;
    private long _pts100ns;
    private int _width, _height;
    private byte[] _nv12 = Array.Empty<byte>();
    private bool _disposed;
    private long _needMoreInput;
    private long _fedCount;
    private long _processInputCalls;
    private long _notAccepting;
    private long _outInfoLogs;
    private long _outCalls;
    private IntPtr _outSample = IntPtr.Zero;   // 复用的输出样本
    private readonly List<Bitmap> _frameQueue = new();   // 解出的帧队列（连发帧不丢失）

    private IntPtr _t = IntPtr.Zero;      // IMFTransform*
    private IntPtr _eg = IntPtr.Zero;     // IMFMediaEventGenerator*（有 = 异步 MFT）

    private readonly int? _widthHint, _heightHint;   // 握手行给的尺寸 → 输入类型带 FRAME_SIZE

    /// <summary>输入样本固定块大小（GetInputStreamInfo 报 FIXED_SAMPLE_SIZE cbSize=4096）。</summary>
    private static uint InputBufferSize(int auLength) => auLength > 4096 ? (uint)((auLength + 4095) & ~4095) : 4096u;

    // ═══════════════════════ 初始化 ═══════════════════════

    /// <param name="widthHint">服务端握手行分辨率（喂给输入类型 MF_MT_FRAME_SIZE，
    /// 避免解码器按 1920x1080 默认值协商输出、流尺寸不符时静默丢帧）。</param>
    public MediaFoundationH264Decoder(int? widthHint = null, int? heightHint = null)
    {
        _widthHint = widthHint;
        _heightHint = heightHint;
        EnsureMfStarted();
        if (!Wireup(CreateDecoderT()))
            throw new MfException("没有可用的 H264 解码 MFT（所有候选接线失败）");
    }

    private static void EnsureMfStarted()
    {
        lock (MfGate)
        {
            if (_mfStarted) return;
            int hr = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
            if (hr != S_OK && hr != S_FALSE && hr != RPC_E_CHANGED_MODE)
                throw new MfException($"CoInitializeEx 失败: 0x{hr:X8}");
            hr = MFStartup(MF_VERSION, 0);   // 完整启动（LITE 模式下部分 MFT 的属性表不可用）
            if (hr != S_OK && hr != S_FALSE)
                throw new MfException($"MFStartup 失败: 0x{hr:X8}");
            _mfStarted = true;
        }
    }

    /// <summary>对一个候选对象做完整接线：解锁 → 类型协商 → 起流。失败返回 false。</summary>
    private bool Wireup(IntPtr t)
    {
        try
        {
            _t = t;

            // ── 哨兵：槽 4 必须是 GetStreamCount（S_OK 且 1 进 1 出）——验证对象与槽位表对齐，
            //    避免拿错对象时把后续调用打进野方法。
            if (Slot<GetStreamCountD>(_t, T_GETSTREAMCOUNT)(_t, out var nIn, out var nOut) != S_OK
                || nIn != 1 || nOut != 1)
            {
                return Fail($"GetStreamCount 哨兵失败（in={nIn}, out={nOut}）—— 对象/槽位表不匹配");
            }

            // ── 模式判定：先探事件生成器（异步 MFT 的标志）──
            int hrQI = Marshal.QueryInterface(_t, ref _iidEventGen, out var egProbe);
            if (hrQI == S_OK && egProbe != IntPtr.Zero)
            {
                _eg = egProbe;
                if (!TryUnlockAsync())
                    return Fail("异步 MFT 解锁失败");
                Console.WriteLine($"[h264] 异步 MFT（事件驱动，QI hr=0x{hrQI:X8}）");
            }
            else
            {
                Console.WriteLine($"[h264] QI(IMFMediaEventGenerator) hr=0x{hrQI:X8} —— 无事件生成器，尝试同步模式（不解锁，直调）");
                // 同步模式也开低延迟（否则输出被扣在内部队列，DRAIN 才吐）
                SetLowLatencyMode();
            }

            // ── IMFSample 槽位自检：AddBuffer(42) → GetBufferCount(39) 回读必须 =1 ──
            VerifySampleSlots();

            // ── 类型协商：H264 进 / NV12 出（输出类型从 GetOutputAvailableType 枚举里挑，
            //    裸建 NV12 会缺解码器要求的属性 → MF_E_ATTRIBUTENOTFOUND）──
            var inType = CreateType(MFVideoFormat_H264);
            int hr = Slot<SetInputTypeD>(_t, T_SETINPUTTYPE)(_t, 0, inType, 0);
            Marshal.Release(inType);
            if (hr != S_OK) return Fail($"SetInputType(H264) 失败 0x{hr:X8}");

            var outType = PickOutputTypeNv12();
            hr = Slot<SetOutputTypeD>(_t, T_SETOUTPUTTYPE)(_t, 0, outType, 0);
            Marshal.Release(outType);
            if (hr != S_OK) return Fail($"SetOutputType(NV12) 失败 0x{hr:X8}");

            // ── 起流（异步 MFT 才有意义；同步 MFT 对这些消息通常也宽容）──
            // ⚠ 先显式声明无 D3D 管理器（纯软件路径）——实测不声明显式软件模式时
            //   msmpeg2vdec 会周期性重试 DXVA 初始化（表现：约 500ms 才出第一帧且后续全丢）
            int hrD3D = Slot<ProcessMessageD>(_t, T_PROCESSMESSAGE)(_t, 2 /*MFT_MESSAGE_SET_D3D_MANAGER*/, IntPtr.Zero);
            Console.WriteLine($"[h264] MFT_MESSAGE_SET_D3D_MANAGER(NULL) hr=0x{hrD3D:X8}");
            hr = Slot<ProcessMessageD>(_t, T_PROCESSMESSAGE)(_t, MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
            if (hr != S_OK && hr != E_NOTIMPL) return Fail($"NOTIFY_BEGIN_STREAMING 失败 0x{hr:X8}");
            hr = Slot<ProcessMessageD>(_t, T_PROCESSMESSAGE)(_t, MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero);
            if (hr != S_OK && hr != E_NOTIMPL) return Fail($"NOTIFY_START_OF_STREAM 失败 0x{hr:X8}");

            // ── vtable 尾部探针：验证 19/20/24 槽位身份 ──
            int hrIS = Slot<GetInputStatusD>(_t, 19)(_t, 0, out var isFlags);
            Console.WriteLine($"[h264] 探针 GetInputStatus@19 hr=0x{hrIS:X8} flags={isFlags}");
            int hrOS = Slot<GetOutputStatusD>(_t, 20)(_t, out var osFlags);
            Console.WriteLine($"[h264] 探针 GetOutputStatus@20 hr=0x{hrOS:X8} flags={osFlags}");
            int hrNull = Slot<ProcessInputD>(_t, T_PROCESSINPUT)(_t, 0, IntPtr.Zero, 0);    // 真 ProcessInput → E_POINTER
            Console.WriteLine($"[h264] 探针 ProcessInput@24(NULL) hr=0x{hrNull:X8}（E_POINTER=0x80070057 则槽位正确）");
            int hrII = Slot<GetInputStreamInfoD>(_t, 6)(_t, 0, out var inInfo);
            Console.WriteLine($"[h264] 探针 GetInputStreamInfo@6 hr=0x{hrII:X8} flags=0x{inInfo.dwFlags:X} cbSize={inInfo.cbSize} cbAlignment={inInfo.cbAlignment}");
            int hrOO6 = Slot<GetInputStreamInfoD>(_t, 6)(_t, 0, out var ooInfo6);
            Console.WriteLine($"[h264] 探针 [对照] 槽6 再次调用 hr=0x{hrOO6:X8} flags=0x{ooInfo6.dwFlags:X} cbSize={ooInfo6.cbSize}");
            int hrO7 = Slot<GetInputStreamInfoD>(_t, 7)(_t, 0, out var ooInfo7);
            Console.WriteLine($"[h264] 探针 槽7 as GetOutputStreamInfo hr=0x{hrO7:X8} flags=0x{ooInfo7.dwFlags:X} cbSize={ooInfo7.cbSize}");
            return true;
        }
        catch (Exception ex)
        {
            return Fail("接线异常 " + ex.Message);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetStreamCountD(IntPtr self, out int inputs, out int outputs);

    /// <summary>同步模式下也开 AVLowLatencyMode（优先 ICodecAPI，属性表兜底）。</summary>
    private void SetLowLatencyMode()
    {
        // ① ICodecAPI::SetValue（正规途径）
        var codecApi = QI(_t, IID_ICodecAPI);
        if (codecApi != IntPtr.Zero)
        {
            try
            {
                unsafe
                {
                    var guid = CODECAPI_AVLowLatencyMode;
                    var guidPtr = Marshal.AllocCoTaskMem(16);
                    var varPtr = Marshal.AllocCoTaskMem(24);
                    try
                    {
                        Marshal.Copy(guid.ToByteArray(), 0, guidPtr, 16);
                        Marshal.WriteInt16(varPtr, 0, 3);          // VT_I4
                        Marshal.WriteInt32(varPtr, 8, 1);          // lVal = 1
                        // ICodecAPI vtable：GetParameterCount=3,GetParameterInfo=4,GetDefaultValue=5,
                        // GetValue=6,SetValue=7
                        int hr = Slot<SetValue2D>(codecApi, 7)(codecApi, guidPtr, varPtr);
                        Console.WriteLine($"[h264] ICodecAPI.SetValue(AVLowLatencyMode=1) hr=0x{hr:X8}");
                    }
                    finally { Marshal.FreeCoTaskMem(guidPtr); Marshal.FreeCoTaskMem(varPtr); }
                }
            }
            finally { Marshal.Release(codecApi); }
        }
        else
        {
            Console.WriteLine("[h264] ICodecAPI 不可用（QI 失败）");
        }

        // ② 属性表兜底
        int hrAttr = Slot<GetAttributesD>(_t, T_GETATTRIBUTES)(_t, out var attrs);
        if (hrAttr != S_OK || attrs == IntPtr.Zero) return;
        try
        {
            var llKey = CODECAPI_AVLowLatencyMode;
            int hr = Slot<SetUINT32D>(attrs, ATTR_SETUINT32)(attrs, ref llKey, 1);
            Console.WriteLine($"[h264] AVLowLatencyMode(属性表) set hr=0x{hr:X8}");
        }
        finally { Marshal.Release(attrs); }
    }

    /// <summary>GetAttributes(8) 拿属性表 → GetCount(30) 验真 → SetUINT32(21) 解锁。</summary>
    private bool TryUnlockAsync()
    {
        int hrAttr = Slot<GetAttributesD>(_t, T_GETATTRIBUTES)(_t, out var attrs);
        if (hrAttr != S_OK || attrs == IntPtr.Zero)
        {
            Console.WriteLine($"[h264] GetAttributes(8) hr=0x{hrAttr:X8} —— 视为无全局属性表");
            return false;
        }
        try
        {
            // 哨兵：真属性表必须能报数（伪指针/错误槽位会 AV 或非 S_OK）
            int hrCount = Slot<GetCountD>(attrs, ATTR_GETCOUNT)(attrs, out var n);
            if (hrCount != S_OK)
            {
                Console.WriteLine($"[h264] GetAttributes 返回的对象不是属性表（GetCount hr=0x{hrCount:X8}）");
                return false;
            }
            var unlockKey = MF_TRANSFORM_ASYNC_UNLOCK;
            int hr = Slot<SetUINT32D>(attrs, ATTR_SETUINT32)(attrs, ref unlockKey, 1);
            // 低延迟模式：不开会把输出扣在内部队列（实测 DRAIN 才吐 32 帧）
            var llKey = CODECAPI_AVLowLatencyMode;
            int hrLL = Slot<SetUINT32D>(attrs, ATTR_SETUINT32)(attrs, ref llKey, 1);
            int hrLL2 = Slot<GetUINT32D>(attrs, ATTR_GETUINT32)(attrs, ref llKey, out var llVal);
            Console.WriteLine($"[h264] 异步解锁 hr=0x{hr:X8}；AVLowLatencyMode set=0x{hrLL:X8} get=0x{hrLL2:X8} val={llVal}（属性表 {n} 项）");
            return hr == S_OK;
        }
        finally { Marshal.Release(attrs); }
    }

    /// <summary>IMFSample/IMFMediaBuffer 槽位自检：AddBuffer→GetBufferCount、
    /// SetCurrentLength→GetCurrentLength、SetSampleTime→GetSampleTime 全回读验证。</summary>
    private static unsafe void VerifySampleSlots()
    {
        Check(MFCreateSample(out var sample));
        Check(MFCreateMemoryBuffer(64, out var buf));
        try
        {
            Check(Slot<AddBufferD>(sample, SAMPLE_ADDBUFFER)(sample, buf));
            Console.WriteLine("[h264] 自检步1：AddBuffer OK");

            // ── IMFMediaBuffer 槽位探针梯（4..7；槽3=Lock 不安全，跳过）──
            for (int s = 4; s <= 7; s++)
            {
                int hrG = Slot<GetCurrentLengthD>(buf, s)(buf, out var v1);
                Console.WriteLine($"[h264] buf槽{s} as GetCurrentLength: hr=0x{hrG:X8} v={v1}");
            }
            int hrS6 = Slot<SetCurrentLengthD>(buf, 6)(buf, 16);
            Console.WriteLine($"[h264] buf槽6 as SetCurrentLength(16): hr=0x{hrS6:X8}");
            int hrG5 = Slot<GetCurrentLengthD>(buf, 5)(buf, out var v5);
            Console.WriteLine($"[h264] buf槽5 回读: hr=0x{hrG5:X8} v={v5}");
            int hrG7 = Slot<GetMaxLengthProbeD>(buf, 7)(buf, out var v7);
            Console.WriteLine($"[h264] buf槽7 as GetMaxLength: hr=0x{hrG7:X8} v={v7}");

            // sample：SetSampleTime(0xABCDEF) → GetSampleTime 必须回读
            Check(Slot<SetSampleTimeD>(sample, SAMPLE_SETSAMPLETIME)(sample, 0xABCDEF));
            Console.WriteLine("[h264] 自检步4：SetSampleTime OK");
            int hrST = Slot<GetSampleTimeD>(sample, SAMPLE_GETSAMPLETIME)(sample, out var sampleTime);
            Console.WriteLine($"[h264] 自检步5：GetSampleTime hr=0x{hrST:X8} t=0x{sampleTime:X}");
            if (hrST == S_OK && sampleTime != 0xABCDEF)
                throw new MfException($"IMFSample 槽位自检失败：SetSampleTime 后 GetSampleTime=0x{sampleTime:X}（预期 0xABCDEF）");

            Check(Slot<GetBufferCountD>(sample, SAMPLE_GETBUFFERCOUNT)(sample, out var n));
            Console.WriteLine($"[h264] 自检步6：GetBufferCount={n}");
            if (n != 1)
                throw new MfException($"IMFSample 槽位自检失败：AddBuffer 后 GetBufferCount={n}（预期 1）");
            if (hrST != S_OK)
                throw new MfException($"槽位回读异常（GetSampleTime hr=0x{hrST:X8}）");
            Console.WriteLine("[h264] IMFSample/Buffer 槽位自检通过（AddBuffer=42 / GetBufferCount=39 / SetCurrentLength=6 / SetSampleTime=36）");
        }
        finally
        {
            Marshal.Release(buf);
            Marshal.Release(sample);
        }
    }

    /// <summary>枚举 GetOutputAvailableType(14)，挑 subtype=NV12 的那个（带全解码器要求的属性）。</summary>
    private IntPtr PickOutputTypeNv12()
    {
        for (int i = 0; i < 32; i++)
        {
            int hr = Slot<GetOutputAvailableTypeD>(_t, T_GETOUTPUTAVAILABLETYPE)(_t, 0, i, out var mt);
            if (hr != S_OK || mt == IntPtr.Zero)
                break;                       // 枚举完（MF_E_NO_MORE_TYPES）
            try
            {
                var key = MF_MT_SUBTYPE;
                if (Slot<GetGUIDD>(mt, ATTR_GETGUID)(mt, ref key, out var subtype) == S_OK)
                {
                    Console.WriteLine($"[h264] 可用输出类型 #{i}: {subtype:B}");
                    if (subtype == MFVideoFormat_NV12)
                        return mt;           // 所有权转移给调用方
                }
            }
            catch { Marshal.Release(mt); throw; }
            Marshal.Release(mt);
        }
        throw new MfException("GetOutputAvailableType 未枚举到 NV12 输出类型");
    }

    private static bool Fail(string msg)
    {
        Console.WriteLine("[h264] MF 接线失败: " + msg);
        return false;
    }

    private IntPtr CreateType(Guid subtype)
    {
        Check(MFCreateMediaType(out var mt));
        try
        {
            var k1 = MF_MT_MAJOR_TYPE; var v1 = MFMediaType_Video;
            Check(Slot<SetGUIDD>(mt, ATTR_SETGUID)(mt, ref k1, ref v1));
            var k2 = MF_MT_SUBTYPE; var v2 = subtype;
            Check(Slot<SetGUIDD>(mt, ATTR_SETGUID)(mt, ref k2, ref v2));
            if (subtype == MFVideoFormat_H264)
            {
                // ⚠ 必须显式声明逐行：Mixed 模式下解码器按隔行语义攒场扣帧
                //   （实测：扣住 33 帧，DRAIN 才吐）。T3 投屏流恒为逐行。
                var kInterlace = MF_MT_INTERLACE_MODE;
                Check(Slot<SetUINT32D>(mt, ATTR_SETUINT32)(mt, ref kInterlace, 2 /*MFVideoInterlace_Progressive*/));
            }
            if (subtype == MFVideoFormat_H264 && _widthHint is > 0 && _heightHint is > 0)
            {
                var k3 = MF_MT_FRAME_SIZE;
                long size = ((long)_widthHint.Value << 32) | (uint)_heightHint.Value;
                Check(Slot<SetUINT64D>(mt, ATTR_SETUINT64)(mt, ref k3, size));
            }
            return mt;
        }
        catch { Marshal.Release(mt); throw; }
    }

    /// <summary>
    /// 找可用的 H264 解码 MFT：① CoCreateInstance 硬编码 CLSID（Win11 新 + 经典）；
    /// ② MFTEnumEx 枚举（激活对象 / CLSID 属性）。每个候选做完整接线探测。
    /// </summary>
    private IntPtr CreateDecoderT()
    {
        // ① MFTEnumEx（Firefox 同款 SORTANDFILTER——排除 CoCreateInstance 实例的配置差异）
        try
        {
            var t1 = CreateDecoderTViaEnum(MFT_ENUM_FLAG_SORTANDFILTER);
            if (t1 is { } tt1 && tt1 != IntPtr.Zero) return tt1;
        }
        catch (Exception ex) { Console.WriteLine("[h264] MFTEnumEx(SORTANDFILTER) 路线失败：" + ex.Message); }

        // ② CoCreateInstance 硬编码 CLSID（Win11 新 + 经典）
        foreach (var clsid in (Guid[])[CLSID_CMSH264DecoderMFT_Win11, CLSID_CMSH264DecoderMFT])
        {
            var c = clsid;
            var iidUnk = IID_IUnknown;
            if (CoCreateInstance(ref c, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iidUnk, out var unk)
                is S_OK or S_FALSE)
            {
                Console.WriteLine($"[h264] CoCreateInstance 成功（CLSID={clsid}）");
                if (Wireup(unk)) return unk;
                Marshal.Release(unk);
            }
        }

        // ③ MFTEnumEx 全量标志兜底
        var t3 = CreateDecoderTViaEnum(MFT_ENUM_FLAG_ALL);
        return t3 ?? throw new MfException("所有 MFT 候选（MFTEnumEx/CoCreateInstance）都不可用");
    }

    private IntPtr? CreateDecoderTViaEnum(uint flags)
    {
        var inInfo = new MFT_REGISTER_TYPE_INFO { guidMajorType = MFMediaType_Video, guidSubtype = MFVideoFormat_H264 };
        int hr = MFTEnumEx(MFT_CATEGORY_VIDEO_DECODER, flags, ref inInfo,
            IntPtr.Zero, out var arr, out var n);
        if (hr != S_OK || n == 0)
            throw new MfException($"MFTEnumEx 未找到 H264 解码器（hr=0x{hr:X8}, n={n}）——系统缺媒体功能？");
        Console.WriteLine($"[h264] MFTEnumEx(flags=0x{flags:X}) 返回 {n} 个候选");

        try
        {
            for (int i = 0; i < n; i++)
            {
                var activate = Marshal.ReadIntPtr(arr, i * IntPtr.Size);
                string why;
                try
                {
                    // 打印候选身份：枚举 activate 属性表全部条目
                    DumpAttrStore(activate, $"候选 #{i}");

                    // ActivateObject（槽 33）拿 MFT 实例：先直接要 IMFTransform（Firefox 同款），IUnknown 兜底
                    IntPtr unk = IntPtr.Zero;
                    foreach (var iid in (Guid[])[IID_IMFTransform, IID_IMFTransform_V2, IID_IUnknown])
                    {
                        var iidAct = iid;
                        hr = Slot<ActivateObjectD>(activate, ACT_ACTIVATEOBJECT)(activate, ref iidAct, out var p);
                        if ((hr == S_OK || hr == S_FALSE) && p != IntPtr.Zero)
                        {
                            Console.WriteLine($"[h264] 候选 #{i}：ActivateObject 成功（riid={iid:B}）");
                            unk = p;
                            break;
                        }
                    }

                    if (unk != IntPtr.Zero)
                    {
                        if (Wireup(unk))
                        {
                            Console.WriteLine($"[h264] 候选 #{i} 经 ActivateObject 激活");
                            return unk;
                        }
                        Marshal.Release(unk);
                        why = "ActivateObject 后接线失败";
                    }
                    else
                    {
                        why = $"ActivateObject 全部 IID 失败（最后 hr=0x{hr:X8}）";
                    }
                }
                catch (Exception ex)
                {
                    why = "候选处理异常 " + ex.Message;
                }
                Console.WriteLine($"[h264] MFTEnumEx 候选 #{i} 跳过：{why}");
                Marshal.Release(activate);
            }
        }
        finally
        {
            if (arr != IntPtr.Zero) Marshal.FreeCoTaskMem(arr);
        }
        return null;
    }

    /// <summary>枚举属性表全部条目（GetCount@30 + GetItemByIndex@31）。</summary>
    private static unsafe void DumpAttrStore(IntPtr attrs, string tag)
    {
        try
        {
            if (Slot<GetCountD>(attrs, ATTR_GETCOUNT)(attrs, out var cnt) != S_OK)
            {
                Console.WriteLine($"[h264] {tag}：属性表 GetCount 失败");
                return;
            }
            Console.WriteLine($"[h264] {tag}：属性表 {cnt} 项");
            for (uint idx = 0; idx < cnt; idx++)
            {
                var guidBuf = Marshal.AllocCoTaskMem(16);
                var pvBuf = Marshal.AllocCoTaskMem(64);   // PROPVARIANT 简化缓冲
                try
                {
                    int hr = Slot<GetItemByIndexD>(attrs, 31)(attrs, idx, guidBuf, pvBuf);
                    if (hr != S_OK) { Console.WriteLine($"[h264] {tag}：项#{idx} hr=0x{hr:X8}"); continue; }
                    var key = new byte[16];
                    Marshal.Copy(guidBuf, key, 0, 16);
                    var g = new Guid(BitConverter.ToInt32(key, 0), BitConverter.ToInt16(key, 4),
                        BitConverter.ToInt16(key, 6), key[8], key[9], key[10], key[11], key[12], key[13], key[14], key[15]);
                    // PROPVARIANT: vt@0(2B)，值@8（GUID 情况下是指针 or 内嵌）——只打印 vt 和尽量可读内容
                    ushort vt = (ushort)Marshal.ReadInt16(pvBuf);
                    string val = "";
                    if (vt == 19) // VT_LPWSTR
                    {
                        var p = Marshal.ReadIntPtr(pvBuf, 8);
                        val = Marshal.PtrToStringUni(p) ?? "";
                    }
                    else if (vt == 3) val = Marshal.ReadInt32(pvBuf, 8).ToString();
                    else if (vt == 72) // VT_CLSID → ptr to GUID
                    {
                        var p = Marshal.ReadIntPtr(pvBuf, 8);
                        if (p != IntPtr.Zero) val = new Guid(Marshal.ReadInt32(p), Marshal.ReadInt16(p, 4),
                            Marshal.ReadInt16(p, 6), Marshal.ReadByte(p, 8), Marshal.ReadByte(p, 9), Marshal.ReadByte(p, 10),
                            Marshal.ReadByte(p, 11), Marshal.ReadByte(p, 12), Marshal.ReadByte(p, 13), Marshal.ReadByte(p, 14),
                            Marshal.ReadByte(p, 15)).ToString("B");
                    }
                    else val = "(vt=" + vt + ")";
                    Console.WriteLine($"[h264] {tag}：[{idx}] {g:B} = {val}");
                }
                finally { Marshal.FreeCoTaskMem(guidBuf); Marshal.FreeCoTaskMem(pvBuf); }
            }
        }
        catch (Exception ex) { Console.WriteLine($"[h264] {tag}：属性表枚举失败 {ex.Message}"); }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetItemByIndexD(IntPtr self, uint index, IntPtr guidKey, IntPtr pValue);

    private static bool TryGetGuidAttr(IntPtr attrs, Guid key, out Guid value)
    {
        var g = key;
        return Slot<GetGUIDD>(attrs, ATTR_GETGUID)(attrs, ref g, out value) == S_OK;
    }

    /// <summary>读字符串属性（GetStringLength@11 → GetString@12）。</summary>
    private static unsafe bool TryGetStringAttr(IntPtr attrs, Guid key, out string value)
    {
        var g = key;
        value = "";
        try
        {
            if (Slot<GetUINT32D>(attrs, 11)(attrs, ref g, out var len) != S_OK || len == 0) return false;
            var buf = Marshal.AllocCoTaskMem((len + 1) * 2);
            try
            {
                if (Slot<GetStringD>(attrs, 12)(attrs, ref g, buf, len + 1, out _) != S_OK) return false;
                value = Marshal.PtrToStringUni(buf) ?? "";
                return true;
            }
            finally { Marshal.FreeCoTaskMem(buf); }
        }
        catch { return false; }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetStringD(IntPtr self, ref Guid key, IntPtr buf, int cch, out int written);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetInputStatusD(IntPtr self, int streamId, out uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetOutputStatusD(IntPtr self, out uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetInputStreamInfoD(IntPtr self, int streamId, out MFT_INPUT_STREAM_INFO info);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetValue2D(IntPtr self, IntPtr guidPtr, IntPtr variantPtr);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetMaxLengthProbeD(IntPtr self, out uint flags);

    // ═══════════════════════ 解码主流程 ═══════════════════════

    /// <summary>诊断统计（离线判据结束后打印）。</summary>
    public string DiagStats() =>
        $"ProcessInput 调用={_processInputCalls} NOTACCEPTING={_notAccepting} 已喂={_fedCount} " +
        $"NEED_MORE_INPUT={_needMoreInput} 输出尺寸={_width}x{_height}";

    /// <summary>排空解码器：发 DRAIN 后连取所有余帧（诊断用）。</summary>
    public List<Bitmap> DrainAll(bool sendDrain = true)
    {
        var frames = new List<Bitmap>();
        if (sendDrain)
        {
            // MFT_MESSAGE_COMMAND_DRAIN = 1
            int hrD = Slot<ProcessMessageD>(_t, T_PROCESSMESSAGE)(_t, 1, IntPtr.Zero);
            Console.WriteLine($"[h264] DRAIN hr=0x{hrD:X8}");
        }
        for (int i = 0; i < 5000; i++)
        {
            ProduceOutput();
            if (_last is not null)
            {
                frames.Add(_last);
                _last = null;
            }
            else if (i > 100 && frames.Count == 0) break;
            else if (i > 200) break;
        }
        return frames;
    }

    public bool TryDecode(ReadOnlyMemory<byte> annexB, out Bitmap? frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        frame = null;
        _pending.AddLast(annexB.ToArray());

        // ① 队列里还有上一轮连发的帧 → 直接出队返回（不喂新数据）
        if (_frameQueue.Count > 0)
        {
            frame = _frameQueue[0];
            _frameQueue.RemoveAt(0);
            return true;
        }

        // ⚠ 等待窗必须远小于帧间隔（33ms@30fps）：收流循环同步调这里，
        //   阻塞过久会让后续 AU 积压 → 累积延迟。出不了的帧由下一个 AU 到达时再泵出。
        var deadline = Environment.TickCount64 + 10;
        while (true)
        {
            if (_eg == IntPtr.Zero)
            {
                // 同步 MFT：无事件，直接喂 + 取
                FeedOne();
                ProduceOutput();
            }
            else PumpEvents();

            if (_last is not null)
            {
                _frameQueue.Add(_last);
                _last = null;
            }
            if (_frameQueue.Count > 0)
            {
                frame = _frameQueue[0];
                _frameQueue.RemoveAt(0);
                return true;
            }
            if (Environment.TickCount64 >= deadline)
                return false;
            Thread.Sleep(1);
        }
    }

    private void PumpEvents()
    {
        while (true)
        {
            int hr = Slot<GetEventD>(_eg, EVG_GETEVENT)(_eg, MF_EVENT_FLAG_NO_WAIT, out var ev);
            if (hr != S_OK || ev == IntPtr.Zero)
                break;                       // 无事件（MF_E_NO_EVENTS_AVAILABLE）或出错
            int met;
            try { Slot<GetEventTypeD>(ev, EV_GETTYPE)(ev, out met); }
            finally { Marshal.Release(ev); }

            if (met == METransformNeedInput) FeedOne();
            else if (met == METransformHaveOutput) ProduceOutput();
        }
    }

    private void FeedOne()
    {
        if (_pending.Count == 0)
            return;
        var au = _pending.First!.Value;
        _pending.RemoveFirst();
        Interlocked.Increment(ref _fedCount);

        Check(MFCreateMemoryBuffer(InputBufferSize(au.Length), out var buf));
        Check(MFCreateSample(out var sample));
        try
        {
            Check(Slot<LockD>(buf, BUF_LOCK)(buf, out var ptr, out _, out _));
            Marshal.Copy(au, 0, ptr, au.Length);
            // ⚠ 该 MFT 报 FIXED_SAMPLE_SIZE cbSize=4096：缓冲容量必须 ≥4096；
            //   但 CurrentLength 必须是真实数据长度（把长度也填到 4096 会让尾部零
            //   被当成破损 NAL，整帧静默丢弃）。
            Check(Slot<SetCurrentLengthD>(buf, BUF_SETCURRENTLENGTH)(buf, (uint)au.Length));
            Check(Slot<UnlockD>(buf, BUF_UNLOCK)(buf));

            Check(Slot<AddBufferD>(sample, SAMPLE_ADDBUFFER)(sample, buf));
            _pts100ns += 333_333;                        // 30fps 记号（解码器只用它排序）
            Check(Slot<SetSampleTimeD>(sample, SAMPLE_SETSAMPLETIME)(sample, _pts100ns));
            Check(Slot<SetSampleDurationD>(sample, SAMPLE_SETSAMPLEDURATION)(sample, 333_333));

            int hr = Slot<ProcessInputD>(_t, T_PROCESSINPUT)(_t, 0, sample, 0);
            Interlocked.Increment(ref _processInputCalls);
            if (hr != S_OK && hr != S_FALSE)
            {
                if (hr == MF_E_NOTACCEPTING)
                {
                    _notAccepting++;
                    _pending.AddFirst(au);               // 没收下：塞回队首下次再喂
                }
                else
                    throw new MfException($"ProcessInput 失败: 0x{hr:X8}");
            }
            else if (_processInputCalls <= 30)
            {
                int hrOS = Slot<GetOutputStatusD>(_t, 20)(_t, out var osFlags);
                int hrII = Slot<GetInputStatusD>(_t, 19)(_t, 0, out var isFlags);
                Console.WriteLine($"[h264] 喂#{_processInputCalls}({au.Length}B): ProcessInput=0x{hr:X8} OutputStatus=0x{hrOS:X8}/{osFlags} InputStatus=0x{hrII:X8}/{isFlags}");
            }
        }
        finally
        {
            Marshal.Release(buf);
            Marshal.Release(sample);
        }
    }

    private void ProduceOutput()
    {
        ProduceOutputCore();
        // 解码器连发多帧时全部收进队列（直接覆盖 _last 会丢帧）
        while (_last is not null)
        {
            _frameQueue.Add(_last);
            _last = null;
            ProduceOutputCore();
        }
    }

    private void ProduceOutputCore()
    {
        Slot<GetOutputStreamInfoD>(_t, T_GETOUTPUTSTREAMINFO)(_t, 0, out var info);
        bool provides = (info.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;
        int n = (int)++_outInfoLogs;
        if (n <= 12)
            Console.WriteLine($"[h264] 出帧#{n} 流信息：flags=0x{info.dwFlags:X} cbSize={info.cbSize} provides={provides} w={_width} h={_height}");

        // 输出样本复用：首次分配，之后每次 ProcessOutput 复用同一 IMFSample
        if (!provides && _outSample == IntPtr.Zero)
        {
            Check(MFCreateSample(out var own));
            uint sz = info.cbSize > 0 ? (uint)info.cbSize : 16 * 1024 * 1024;
            Check(MFCreateMemoryBuffer(sz, out var buf));
            Check(Slot<AddBufferD>(own, SAMPLE_ADDBUFFER)(own, buf));
            Marshal.Release(buf);
            _outSample = own;
        }

        var odb = new MFT_OUTPUT_DATA_BUFFER
        {
            dwStreamID = 0,
            pSample = _outSample,
            dwStatus = 0,
            pEvents = IntPtr.Zero,
        };
        int hr = Slot<ProcessOutputD>(_t, T_PROCESSOUTPUT)(_t, 0, 1, ref odb, out _);

        try
        {
            int cn = (int)++_outCalls;
            if (cn <= 12)
                Console.WriteLine($"[h264] 出帧#{cn} ProcessOutput hr=0x{hr:X8} dwStatus=0x{odb.dwStatus:X} pSample={odb.pSample:x}");
            if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT)
            {
                if (++_needMoreInput % 500 == 1)
                    Console.WriteLine($"[h264] ProcessOutput NEED_MORE_INPUT ×{_needMoreInput}（累计已喂 {Interlocked.Read(ref _fedCount)} AU）");
                return;
            }
            if (hr == MF_E_TRANSFORM_STREAM_CHANGE)
            {
                Console.WriteLine($"[h264] ProcessOutput STREAM_CHANGE dwStatus=0x{odb.dwStatus:X} —— 重协商输出类型");
                UpdateFrameSize();
                Console.WriteLine($"[h264] 当前输出尺寸 {_width}x{_height}");
                // 同步 MFT 在类型变更后必须重设输出类型再取
                try
                {
                    var t2 = PickOutputTypeNv12();
                    int hr2 = Slot<SetOutputTypeD>(_t, T_SETOUTPUTTYPE)(_t, 0, t2, 0);
                    Marshal.Release(t2);
                    Console.WriteLine($"[h264] 重设输出类型 hr=0x{hr2:X8}");
                }
                catch (Exception ex2) { Console.WriteLine("[h264] 重设输出类型失败：" + ex2.Message); }
                return;                                  // 下轮再取
            }
            if (hr != S_OK)
                throw new MfException($"ProcessOutput 失败: 0x{hr:X8}");
            if (odb.pSample == IntPtr.Zero)
                return;

            UpdateFrameSize();
            if (_width <= 0 || _height <= 0 || _nv12.Length < _width * _height * 3 / 2)
                return;

            if (Slot<ConvertToContiguousD>(odb.pSample, SAMPLE_CONVERTTOCONTIGUOUS)(odb.pSample, out var buf2) != S_OK
                || buf2 == IntPtr.Zero)
                return;
            try
            {
                if (Slot<LockD>(buf2, BUF_LOCK)(buf2, out var ptr, out _, out var len) == S_OK)
                {
                    try
                    {
                        if (len > (uint)_nv12.Length) len = (uint)_nv12.Length;
                        Marshal.Copy(ptr, _nv12, 0, (int)len);
                    }
                    finally { Slot<UnlockD>(buf2, BUF_UNLOCK)(buf2); }
                    _last = Nv12ToBgra(_nv12, _width, _height);
                }
            }
            finally { Marshal.Release(buf2); }
        }
        finally
        {
            if (odb.pEvents != IntPtr.Zero) Marshal.Release(odb.pEvents);
        }
    }

    private void UpdateFrameSize()
    {
        int hr = Slot<GetOutputCurrentTypeD>(_t, T_GETOUTPUTCURRENTTYPE)(_t, 0, out var mt);
        if (hr != S_OK || mt == IntPtr.Zero) return;
        try
        {
            var key = MF_MT_FRAME_SIZE;
            if (Slot<GetUINT64D>(mt, ATTR_GETUINT64)(mt, ref key, out var size) != S_OK) return;
            int w = (int)(size >> 32), h = (int)(size & 0xFFFFFFFF);
            if (w != _width || h != _height)
            {
                _width = w; _height = h;
                _nv12 = new byte[w * h * 3 / 2];
            }
        }
        finally { Marshal.Release(mt); }
    }

    private static Bitmap Nv12ToBgra(byte[] nv12, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        var rect = new Rectangle(0, 0, w, h);
        var bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            unsafe
            {
                fixed (byte* src = nv12)
                {
                    byte* y = src;
                    byte* uv = src + w * h;
                    for (int row = 0; row < h; row++)
                    {
                        byte* yRow = y + row * w;
                        byte* uvRow = uv + (row >> 1) * w;
                        byte* d = (byte*)bd.Scan0 + row * bd.Stride;
                        for (int col = 0; col < w; col++)
                        {
                            int c = yRow[col] - 16;
                            int u = uvRow[col & ~1] - 128;
                            int v = uvRow[(col & ~1) + 1] - 128;
                            int r = (298 * c + 409 * v + 128) >> 8;
                            int g = (298 * c - 100 * u - 208 * v + 128) >> 8;
                            int b = (298 * c + 516 * u + 128) >> 8;
                            d[col * 4 + 0] = Clamp(b);
                            d[col * 4 + 1] = Clamp(g);
                            d[col * 4 + 2] = Clamp(r);
                            d[col * 4 + 3] = 255;
                        }
                    }
                }
            }
        }
        finally { bmp.UnlockBits(bd); }
        return bmp;
    }

    private static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // COM 对象生命周期进程级；MFShutdown 不反初始化（可能还有别的用户）
    }

    private sealed class MfException(string msg) : Exception(msg);

    private static void Check(int hr)
    {
        if (hr != S_OK && hr != S_FALSE)
            throw new MfException($"MF 调用失败: 0x{hr:X8}");
    }

    // ═══════════════════════ P/Invoke ═══════════════════════

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, int coInit);
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, IntPtr unkOuter, uint clsctx, ref Guid iid, out IntPtr ppv);
    [DllImport("mfplat.dll")] private static extern int MFStartup(int version, int flags);
    [DllImport("mfplat.dll")] private static extern int MFShutdown();
    [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out IntPtr mt);
    [DllImport("mfplat.dll")] private static extern int MFCreateMemoryBuffer(uint cbMaxSize, out IntPtr buf);
    [DllImport("mfplat.dll")] private static extern int MFCreateSample(out IntPtr sample);
    [DllImport("mfplat.dll", EntryPoint = "MFTEnumEx")]
    private static extern int MFTEnumEx(Guid category, uint flags, ref MFT_REGISTER_TYPE_INFO inputType,
        IntPtr outputType, out IntPtr ppMFTActivate, out int pnumMFTActivate);
}
