namespace StreamProbe;

/// <summary>
/// H.264（Annex-B）解码 —— <b>Media Foundation 桩</b>。
///
/// <para>当前状态：jpeg 链路完整可用（系统自带 GDI+/System.Drawing 解码）；
/// h264 链路在**联调阶段补**（T3 服务端就绪后才有真实流可验）。桩行为：
/// 每收到一个访问单元计数 + 每 100 帧提示一次「h264 解码未实现」，**不崩不断**。</para>
///
/// <para>实现计划（TODO）：</para>
/// <list type="number">
/// <item>优先 <c>MFCreateTransform</c>（MFT：Microsoft H264 Video Decoder MFT），
///   P/Invoke：MFStartup → MFCreateTransform → IMFTransform.ProcessInput/ProcessOutput →
///  <IMFDXGIDeviceManager 可选>；输出 NV12 → 转 RGB。</item>
/// <item>兜底：<c>ffmpeg.exe</c> 子进程（<c>ffmpeg -i - -f rawvideo -pix_fmt bgra -</c> 管道），
///   需在 README 声明外部依赖。</item>
/// </list>
/// </summary>
public sealed class H264Decoder
{
    private long _count;
    private readonly bool _verbose;

    public H264Decoder(bool verbose) => _verbose = verbose;

    /// <summary>说明当前解码能力（握手 codec=h264 时打印一次）。</summary>
    public const string StubNotice = "H.264 解码为 Media Foundation 桩（未实现）—— jpeg 链路完整可用；" +
                                     "真实 h264 流联调在 T3 就绪后补（实现计划见 H264Decoder.cs 头注释）";

    /// <summary>
    /// 尝试解码一个 Annex-B 访问单元。
    /// 桩：返回 false（未解码）。真实实现后返回 true 并给出该帧位图。
    /// </summary>
    public bool TryDecode(ReadOnlyMemory<byte> annexB, out System.Drawing.Bitmap? frame)
    {
        frame = null;
        var n = Interlocked.Increment(ref _count);
        if (_verbose && n % 100 == 1)
            Console.WriteLine($"[h264] 收到访问单元 #{n}（{annexB.Length}B）—— {StubNotice}");
        return false;
    }
}
