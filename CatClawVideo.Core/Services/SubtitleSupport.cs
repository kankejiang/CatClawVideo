using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CatClawVideo.Core.Services;

/// <summary>
/// 字幕相关的纯逻辑（MIME 推断 + 时间轴偏移重写），双端共用、可脱离播放器单测。
///
/// <para><b>为什么要自己重写时间轴</b：Android 侧的 Media3 绑定<b>没有</b>对外挂字幕做延迟的公开 API
/// （<c>DefaultSubtitleRendererFactory</c> 那层没投影出来），而 Windows 侧 FFmpegInteropX 有
/// <c>SetSubtitleDelay</c>。为了让「字幕提前/延后 0.5s」在两端行为一致，Android 走
/// 「把字幕文件按偏移重写一份到缓存目录，再交给播放器」这条路。</para>
///
/// <para><b>MIME 常量的来源</b>：取自本项目实际链接的 <c>media3-extractor 1.10.1</c> 里
/// <c>MimeTypes</c> 的字段值（<c>APPLICATION_SUBRIP</c>=<c>application/x-subrip</c>、
/// <c>TEXT_VTT</c>=<c>text/vtt</c>、<c>TEXT_SSA</c>=<c>text/x-ssa</c>），
/// 与 <c>DefaultSubtitleParserFactory</c> 的分派表一一对应 —— 写错字符串会让字幕静默不显示
/// （播放器按 MIME 选解析器，选不到就当没有这条轨）。</para>
/// </summary>
public static class SubtitleSupport
{
    public const string MimeSubrip = "application/x-subrip";
    public const string MimeVtt = "text/vtt";
    public const string MimeSsa = "text/x-ssa";
    public const string MimeTtml = "application/ttml+xml";

    /// <summary>按扩展名推断字幕 MIME；非字幕扩展名返回 null（调用方按「不支持」处理）。</summary>
    public static string? InferMime(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path;
        var q = p.IndexOfAny(['?', '#']);
        if (q >= 0) p = p[..q];   // 远程地址常带 query
        return Path.GetExtension(p).ToLowerInvariant() switch
        {
            ".srt" => MimeSubrip,
            ".vtt" => MimeVtt,
            ".ass" or ".ssa" => MimeSsa,
            ".ttml" or ".dfxp" => MimeTtml,
            _ => null,
        };
    }

    /// <summary>该扩展名是否支持偏移重写（ASS 的时间轴格式不同，本函数不处理）。</summary>
    public static bool SupportsOffsetRewrite(string? mime) =>
        mime is MimeSubrip or MimeVtt;

    /// <summary>
    /// 把字幕时间轴整体平移。<b>只改含 <c>--></c> 的 cue 行</b>，正文里的数字一律不碰
    /// （SRT 正文出现 "1:20" 这种时间样式并不罕见，全局替换会把台词改坏）。
    /// </summary>
    /// <param name="text">字幕原文。</param>
    /// <param name="seconds">偏移秒数，正数=字幕延后出现（对位 TVBox <c>SubtitleDialog</c> 的 ±0.5s 步进）。</param>
    /// <returns>偏移后的文本；偏移为 0 或解析失败时原样返回。</returns>
    public static string ApplyOffset(string text, double seconds)
    {
        if (string.IsNullOrEmpty(text) || Math.Abs(seconds) < 0.0005) return text;
        var deltaMs = (long)Math.Round(seconds * 1000);

        var sb = new StringBuilder(text.Length);
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Contains("-->", StringComparison.Ordinal))
            {
                var shifted = CueRegex.Replace(line, m => ShiftToken(m.Value, deltaMs));
                sb.Append(shifted);
            }
            else sb.Append(line);
            sb.Append('\n');
        }
        // 原文件末尾没有换行时别多送一个（部分解析器按行数算 cue）
        var result = sb.ToString();
        return !text.EndsWith('\n') && result.EndsWith('\n') ? result[..^1] : result;
    }

    // cue 时间戳：可选小时段，分隔符是 SRT 的逗号或 WebVTT 的点
    static readonly Regex CueRegex = new(@"(\d{1,2}:)?\d{1,2}:\d{2}[.,]\d{1,3}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>平移单个时间戳，保持原有格式（有无小时段、逗号还是点、毫秒位数）。</summary>
    static string ShiftToken(string token, long deltaMs)
    {
        var sepIdx = token.IndexOfAny([',', '.']);
        if (sepIdx < 0) return token;
        var hasHours = token.Count(c => c == ':') == 2;
        var millisText = token[(sepIdx + 1)..];

        if (!TryParseClock(token[..sepIdx], out var secMs)) return token;
        // 原时间戳自带的毫秒必须一起加上：只加偏移会把 "02,500" 当成 "02,000" 处理（台架 S4 抓到过）
        var ownMs = long.Parse(millisText.PadRight(3, '0')[..3], CultureInfo.InvariantCulture);
        var moved = Math.Max(0, secMs + ownMs + deltaMs);

        var h = moved / 3_600_000;
        var m = moved / 60_000 % 60;
        var s = moved / 1000 % 60;
        var milli = moved % 1000;

        var sb = new StringBuilder();
        if (hasHours) sb.Append(h.ToString("D2", CultureInfo.InvariantCulture)).Append(':');
        sb.Append(m.ToString("D2", CultureInfo.InvariantCulture)).Append(':')
            .Append(s.ToString("D2", CultureInfo.InvariantCulture))
            .Append(token[sepIdx]);
        // 毫秒位数照原样（3 位最常见，但 WebVTT 允许 1~3 位）
        sb.Append(milli.ToString("D" + millisText.Length, CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>解析 <c>HH:MM:SS</c> 或 <c>MM:SS</c> 为毫秒。</summary>
    static bool TryParseClock(string clock, out long ms)
    {
        ms = 0;
        var parts = clock.Split(':');
        if (parts.Length is not (2 or 3)) return false;
        long total = 0;
        foreach (var p in parts)
        {
            if (!int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return false;
            total = total * 60 + v;
        }
        ms = total * 1000;
        return true;
    }
}
