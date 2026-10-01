using System.Text.RegularExpressions;
using QRCoder;

namespace CatClawVideo.Core.Services;

/// <summary>
/// URL/文本 → 二维码 PNG：<b>码由宿主生成</b>，爬虫只给一个串。
///
/// <para>为什么要它：TVBox 的契约里二维码从来不是爬虫画的
/// （<c>app/…/ui/activity/PushActivity.java:59</c>、<c>ApiDialog.java:190</c>、
/// <c>RemoteDialog.java:33</c> 全是 <c>QRCodeGen.generateBitmap(url, …)</c>），
/// 而 <c>Spider.action(String)</c> 的返回值 app 也只读 <c>msg</c>
/// （<c>GridFragment.java:430</c> / <c>UserFragment.java:270</c>）。
/// 我们宿主此前<b>完全没有</b>出码能力（全仓 grep 无 QR 生成），所以只能反过来去抠 jar 画的位图——
/// 那套 Android 绘制仿真已于 2026-10-01 删除。这个类就是补上契约里该有的那一环。</para>
/// </summary>
public static class QrPng
{
    private static readonly Regex UrlRx = new("https?://[^\\s\"'<>\u4e00-\u9fff]+", RegexOptions.Compiled);

    /// <summary>
    /// 生成二维码 PNG 字节。<c>px</c> 是期望边长，实际按模块数取整数倍
    /// （缩到非整数倍会让模块边缘发灰，手机解码率明显下降）。空文本返回 null。
    /// </summary>
    public static byte[]? FromText(string? text, int px = 512)
    {
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        try
        {
            using var data = new QRCodeGenerator().CreateQrCode(s, QRCodeGenerator.ECCLevel.M);
            using var code = new PngByteQRCode(data);
            // QRCoder 1.8 的 QRCodeData 不暴露 ModuleCount，直接按固定倍数取整：
            // 倍数必须是整数（非整数缩放会让模块边缘发灰，手机解码率明显下降）。
            return code.GetGraphic(Math.Max(2, Math.Min(12, px / 140)));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>从任意文本（action 的 msg、jar 回给我们的 HTML/JSON）里挑第一个 http(s) 链接。</summary>
    public static string? FirstUrl(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = UrlRx.Match(text);
        return m.Success ? m.Value.TrimEnd('。', '，', ',', ';', '；', ')', '）', ']', '\'', '"') : null;
    }
}
