using System.Drawing;

namespace StreamProbe;

/// <summary>
/// H.264（Annex-B）解码门面：<b>Media Foundation 定稿路线</b>（N2-1 二选一定稿）。
///
/// <para>对外接口：每收到一个访问单元调 <see cref="TryDecode"/>，
/// 出帧返回 true 并给出位图（由 <c>ViewWindow.SubmitBitmap</c> 显示）。</para>
///
/// <para>选型理由（<see cref="MediaFoundationH264Decoder"/>）：
/// Windows 原生（Microsoft H264 Video Decoder MFT）、零外部依赖、无 ffmpeg.exe 分发负担。
/// ffmpeg.exe 兜底路线已在定稿时删除（N2-P1 任务书要求二选一，不要两条都留）。</para>
/// </summary>
public sealed class H264Decoder
{
    private readonly MediaFoundationH264Decoder _impl;
    private readonly bool _verbose;
    private long _count;

    public H264Decoder(bool verbose, int? widthHint = null, int? heightHint = null)
    {
        _verbose = verbose;
        _impl = new MediaFoundationH264Decoder(widthHint, heightHint);   // 失败直接抛：调用方展示初始化错误
    }

    /// <summary>当前后端说明（握手行打印一次）。</summary>
    public string DescribeBackend() => "Media Foundation（Microsoft H264 Decoder MFT）";

    /// <summary>诊断统计（离线判据结束后打印）。</summary>
    public string DiagStats() => _impl.DiagStats();

    /// <summary>排空解码器（诊断用）。</summary>
    public List<Bitmap> DrainAll(bool sendDrain = true) => _impl.DrainAll(sendDrain);

    /// <summary>
    /// 尝试解码一个 Annex-B 访问单元。
    /// 出帧返回 true 并给出该帧位图；无帧（还在攒参考帧/PPS 缺失期）返回 false。
    /// </summary>
    public bool TryDecode(ReadOnlyMemory<byte> annexB, out Bitmap? frame)
    {
        frame = null;
        try
        {
            return _impl.TryDecode(annexB, out frame);
        }
        catch (Exception ex)
        {
            var n = Interlocked.Increment(ref _count);
            if (_verbose && n % 100 == 1)
                Console.WriteLine($"[h264] 解码异常：{ex.Message}——本帧丢弃，链路不断");
            return false;
        }
    }
}
