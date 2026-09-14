using QRCoder;

namespace CatClawVideo.Maui.Services;

/// <summary>
/// 配对二维码：本机地址（+ 设备名）编码进二维码，手机扫码后即可把本机登记为
/// 解析节点提供方 / 遥控端。
///
/// <para>用 QRCoder 的 <see cref="PngByteQRCode"/>：纯托管实现，不依赖 System.Drawing，
/// Android 与 Windows 都能跑。</para>
/// </summary>
internal static class PairQr
{
    /// <summary>配对信息 → 二维码内容（手机端解析 u/n 两个参数）</summary>
    public static string Payload(string host, int port, string? deviceName = null)
    {
        var u = Uri.EscapeDataString($"http://{host}:{port}");
        var n = Uri.EscapeDataString(deviceName ?? DeviceName());
        return $"catclaw://pair?u={u}&n={n}";
    }

    /// <summary>生成二维码 PNG 字节</summary>
    public static byte[] Png(string text, int pixelsPerModule = 8)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data);
        return png.GetGraphic(pixelsPerModule, drawQuietZones: true);
    }

    public static string DeviceName()
    {
        try { return DeviceInfo.Current?.Name ?? "catclaw"; }
        catch { return "catclaw"; }
    }
}
