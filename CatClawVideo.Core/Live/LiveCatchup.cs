using System.Text;
using System.Text.RegularExpressions;

namespace CatClawVideo.Core.Live;

/// <summary>
/// 直播回看 / 时移地址构造 —— 移植 TVBox <c>LivePlayActivity</c> 的
/// <c>canCurrentChannelCatchup</c> / <c>buildCatchupUrl</c> / <c>formatCatchupUrl</c> /
/// <c>appendCatchupUrl</c> / <c>formatCatchupSource</c> / <c>formatCatchupToken</c>（L1673-1755）。
///
/// <para><b>两级配置</b>：频道级 catchup（m3u 的 <c>catchup</c>/<c>catchup-source</c>/
/// <c>catchup-replace</c> 属性）优先，没有则回退到<b>订阅级</b>（<c>lives[]</c> 条目上的
/// <c>catchup</c> 对象）。判定「有没有」的标准是 <c>source</c> 非空，不是对象存在。</para>
///
/// <para><b>没有任何 catchup 配置时的兜底</b>：URL 里含 <c>/PLTV/</c> 的移动 IPTV 流，
/// 用 <c>?playseek=yyyyMMddHHmmss-yyyyMMddHHmmss</c> + 把 <c>/PLTV/</c> 换成 <c>/TVOD/</c>。
/// 这条兜底是 TVBox 的原样行为，也是国内运营商源最常见的回看形态。</para>
/// </summary>
public static class LiveCatchup
{
    /// <summary>一份 catchup 配置（频道级或订阅级）。</summary>
    public sealed class Config
    {
        /// <summary><c>default</c> = 直接用 source 当完整地址；其它 = 把 source 追加到原地址后。</summary>
        public string Type { get; set; } = "";

        /// <summary>地址模板，含 <c>{utc:...}</c> / <c>{(b0),yyyyMMddHHmmss}</c> 等时间占位。</summary>
        public string Source { get; set; } = "";

        /// <summary>限定哪些频道地址可用（子串或正则）。</summary>
        public string Regex { get; set; } = "";

        /// <summary><c>"pattern,replacement"</c>：对原地址做正则替换后再追加 source。</summary>
        public string Replace { get; set; } = "";

        public bool HasSource => !string.IsNullOrEmpty(Source);
    }

    static readonly Regex TokenPattern = new(@"(\$?\{[^}]*\})", RegexOptions.Compiled);
    static readonly Regex TagPattern = new(@"\{([^}]*)\}", RegexOptions.Compiled);

    /// <summary>频道级优先、订阅级兜底（对位 <c>currentCatchup()</c>）。</summary>
    public static Config? Current(Config? channel, Config? subscription) =>
        channel is { HasSource: true } ? channel : subscription is { HasSource: true } ? subscription : null;

    /// <summary>该频道是否可回看（对位 <c>canCurrentChannelCatchup</c>：有配置看 regex，没配置看 <c>/PLTV/</c>）。</summary>
    public static bool CanCatchup(string url, Config? cfg)
    {
        if (string.IsNullOrEmpty(url)) return false;
        if (cfg is { HasSource: true })
        {
            if (string.IsNullOrEmpty(cfg.Regex)) return true;
            if (url.Contains(cfg.Regex, StringComparison.Ordinal)) return true;
            try { return Regex.IsMatch(url, cfg.Regex); }
            catch { return false; }     // 订阅给的坏正则：按不可回看处理（TVBox 同样 catch → false）
        }
        return url.Contains("/PLTV/", StringComparison.Ordinal);
    }

    /// <summary>
    /// 构造回看地址。返回空串 = 不可回看 / 缺节目时间（调用方据此提示，不要播空地址）。
    /// </summary>
    public static string Build(string url, Config? cfg, DateTimeOffset start, DateTimeOffset end)
    {
        if (string.IsNullOrEmpty(url) || end <= start) return "";

        if (cfg is { HasSource: true })
        {
            var source = FormatSource(cfg.Source, start, end);
            if (string.IsNullOrEmpty(source)) return "";
            return string.Equals(cfg.Type, "default", StringComparison.OrdinalIgnoreCase)
                ? source
                : Append(url, cfg.Replace, source);
        }

        // 无配置：只对 /PLTV/ 形态兜底（playseek 时间区间 + PLTV→TVOD）
        if (!url.Contains("/PLTV/", StringComparison.Ordinal)) return "";
        var playseek = "?playseek=" + FormatTime(start, "yyyyMMddHHmmss") + "-" + FormatTime(end, "yyyyMMddHHmmss");
        return Append(url, "/PLTV/,/TVOD/", playseek);
    }

    /// <summary>替换 + 追加（对位 <c>appendCatchupUrl</c>）。</summary>
    static string Append(string url, string replace, string source)
    {
        var replay = url;
        var parts = (replace ?? "").Split(',', 2);
        if (parts.Length == 2 && parts[0].Length > 0)
        {
            try { replay = Regex.Replace(replay, parts[0], parts[1]); }
            catch { /* 订阅给的坏正则：不替换，继续追加（TVBox 也是 catch 后往下走） */ }
        }
        var q = replay.IndexOf('?');
        // 原地址已有查询串时，把 source 开头的 ? 换成 &，否则会拼出两个 ?
        if (q >= 0 && q < replay.Length - 1) source = source.Replace("?", "&", StringComparison.Ordinal);
        return replay + source;
    }

    /// <summary>展开模板里的 <c>{...}</c> 时间占位（对位 <c>formatCatchupSource</c>）。</summary>
    static string FormatSource(string source, DateTimeOffset start, DateTimeOffset end)
    {
        return TokenPattern.Replace(source, m =>
        {
            var tag = TagPattern.Match(m.Value);
            if (!tag.Success) return "";
            return FormatToken(tag.Groups[1].Value, start, end);
        });
    }

    static string FormatToken(string tag, DateTimeOffset start, DateTimeOffset end)
    {
        if (tag.StartsWith("utcend:", StringComparison.Ordinal)) return ToUnix(end).ToString();
        if (tag.StartsWith("utc:", StringComparison.Ordinal)) return ToUnix(start).ToString();
        var bracket = tag.IndexOf(')');
        if (bracket < 0) return "";
        if (tag.StartsWith("(b", StringComparison.Ordinal)) return FormatTime(start, tag[(bracket + 1)..]);
        if (tag.StartsWith("(e", StringComparison.Ordinal)) return FormatTime(end, tag[(bracket + 1)..]);
        return "";
    }

    static long ToUnix(DateTimeOffset t) => t.ToUnixTimeSeconds();

    /// <summary>
    /// Java <c>SimpleDateFormat</c> 模式到 .NET 的翻译。
    /// <para>只覆盖直播源里实际会出现的字母（<c>yyyyMMddHHmmss</c> 一类）；
    /// 遇到无法对应的字母返回空串 —— 对位 TVBox 捕获 <c>IllegalArgumentException</c> 后给空值，
    /// 宁可不出回看地址，也不要拼出一个错到离谱的时间。</para>
    /// </summary>
    public static string FormatTime(DateTimeOffset t, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return "";
        if (pattern == "timestamp") return ToUnix(t).ToString();
        var sb = new StringBuilder(pattern.Length);
        for (var i = 0; i < pattern.Length;)
        {
            var c = pattern[i];
            if (!char.IsLetter(c)) { sb.Append(c); i++; continue; }
            var run = 1;
            while (i + run < pattern.Length && pattern[i + run] == c) run++;
            if (!TryLetter(c, run, t, out var text)) return "";
            sb.Append(text);
            i += run;
        }
        return sb.ToString();
    }

    static bool TryLetter(char c, int run, DateTimeOffset t, out string text)
    {
        text = "";
        switch (c)
        {
            case 'y': text = run >= 4 ? t.Year.ToString() : (t.Year % 100).ToString("D2"); break;
            case 'M': text = run >= 2 ? t.Month.ToString("D2") : t.Month.ToString(); break;
            case 'd': text = run >= 2 ? t.Day.ToString("D2") : t.Day.ToString(); break;
            case 'H': text = run >= 2 ? t.Hour.ToString("D2") : t.Hour.ToString(); break;
            case 'h': var h12 = t.Hour % 12 == 0 ? 12 : t.Hour % 12; text = run >= 2 ? h12.ToString("D2") : h12.ToString(); break;
            case 'm': text = run >= 2 ? t.Minute.ToString("D2") : t.Minute.ToString(); break;
            case 's': text = run >= 2 ? t.Second.ToString("D2") : t.Second.ToString(); break;
            default: return false;
        }
        return true;
    }
}
