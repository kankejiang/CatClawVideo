namespace CatClawVideo.Core.Services;

/// <summary>
/// 「这条地址能不能喂播放器」的统一判据。
///
/// <para><b>为什么单独提出来</b>：站点没给可播地址时，回落链会把 <c>vod_id</c>／相对路径／内嵌 JSON
/// 原样当播放地址交上来（2026-09-30 磁力链路报告 T3 实测：<c>/dianshiju/guoju/29659.html</c>），
/// 播放器只会抛 <c>Invalid URI: The format of the URI could not be determined.</c> ——
/// 用户看到的是一句 .NET 内部异常，而不是一句人话。判据散在各家 provider 里会各写一份、
/// 也测不到，所以放一个地方、由 UI 与 provider 共用，并让离线台架能直接断言（parity-check 的 A 组）。</para>
///
/// <para><b>判「能播」= 带 scheme 的绝对 http(s)/file URI</b>。<c>magnet:</c> / <c>ed2k://</c>
/// 明确算「不能直接播」：它们各有专用引擎（磁力走迅雷、ed2k 直接拒绝），漏到播放器就是
/// <c>unknown protocol</c>（2026-09-14 真机实测过）。</para>
/// </summary>
public static class PlayAddress
{
    /// <summary>可播即返回 true；<paramref name="reason"/> 给出「为什么不可播」的人话。</summary>
    public static bool IsPlayable(string? url, out string reason)
    {
        reason = "";
        var u = (url ?? "").Trim();
        if (u.Length == 0) { reason = "地址为空"; return false; }
        if (u[0] is '{' or '[') { reason = "拿到的是一段 JSON/数组，不是地址"; return false; }
        if (!Uri.TryCreate(u, UriKind.Absolute, out var parsed))
        {
            reason = "不是完整网址（相对路径或缺少协议头）";
            return false;
        }
        var ok = parsed.Scheme is "http" or "https" or "file";
        if (!ok) reason = $"协议 {parsed.Scheme} 不能直接播放";
        return ok;
    }

    public static bool IsPlayable(string? url) => IsPlayable(url, out _);

    /// <summary>把一条坏地址缩成能安全进文案/日志的一段（去换行、限长，绝不整段回显 JSON）。</summary>
    public static string Brief(string? url, int max = 90)
    {
        var u = (url ?? "").Trim();
        if (u.Length == 0) return "（空）";
        u = u.Replace("\r", " ").Replace("\n", " ");
        return u.Length <= max ? u : u[..max] + "…";
    }
}
