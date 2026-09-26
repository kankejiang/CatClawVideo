using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CatClawVideo.Core.Services;

/// <summary>一条弹幕的解析结果（对位 TVBox <c>Danmu.Data</c> + <c>Parser.setCue</c>）。</summary>
public sealed class DanmuCue
{
    public required float TimeSeconds { get; init; }
    /// <summary>XML <c>p</c> 的第 2 段原值：1 右滚 · 4 底部 · 5 顶部 · 6 左滚。</summary>
    public required int Mode { get; init; }
    public required float Size { get; init; }
    /// <summary>0xRRGGBB。</summary>
    public required uint Color { get; init; }
    public required string Text { get; init; }
    /// <summary>轨道动画弹幕（负载是 JSON 数组时解出）；普通弹幕为 null。</summary>
    public DanmuSpecial? Special { get; init; }

    public bool IsFixed => Mode is 4 or 5;
    public bool IsReverse => Mode == 6;

    /// <summary>描边色：TVBox 用「颜色 ≤ 黑就描白」保证深底可读。</summary>
    public uint ShadowColor => Color <= 0x000000 ? 0xFFFFFFu : 0x000000u;
}

/// <summary>轨道弹幕：<c>[x, y, "起-止透明度", 时长秒, "文字", 旋转Z, 旋转Y, 移动X, 移动Y]</c>。</summary>
public sealed class DanmuSpecial
{
    public required float BeginX { get; init; }
    public required float BeginY { get; init; }
    public required float AlphaBegin { get; init; }
    public required float AlphaEnd { get; init; }
    public required float DurationSeconds { get; init; }
    public required float RotateZ { get; init; }
    public required float RotateY { get; init; }
    public required float MoveX { get; init; }
    public required float MoveY { get; init; }
    /// <summary>数组第 5 项：真正要显示的字。</summary>
    public required string Body { get; init; }
}

/// <summary>
/// 弹幕数据层（移植 TVBox <c>bean/Danmu.java</c> + <c>player/danmu/Parser.java</c> 的取数与解析部分）。
/// <para><b>有意不移植</b>原实现里的 trust-all TLS 回落（<c>TRUST_ALL_CERT</c> /
/// <c>executeUnsafeHttp</c>）：弹幕源是订阅里来的第三方地址，跳过证书校验等于把
/// 中间人敞口交给订阅作者。宁可这条弹幕取不到。</para>
/// </summary>
public static class DanmuParser
{
    private static readonly Regex DTag = new(
        "<d\\s+[^>]*\\bp\\s*=\\s*(['\"])(.*?)\\1[^>]*>(.*?)</d>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>解析弹幕 XML（bilibili/ACFun 的 <c>&lt;i&gt;&lt;d p="…"&gt;文字&lt;/d&gt;&lt;/i&gt;</c>）。</summary>
    public static List<DanmuCue> ParseXml(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return [];
        // 先修非法实体：弹幕文本里裸 & 会让严格 XML 解析器整个文档报错，
        // 逐串重扫比整串正则替换准（&#xZZ; 这类看着像合法的也要判掉）。
        var cues = new List<DanmuCue>();
        try { cues = FromPairs(XmlPairs(EscapeIllegalEntities(xml))); }
        catch { cues.Clear(); }   // 文档中途坏掉：XmlReader 是流式的，异常可能在读到第 N 条后才来
        if (cues.Count > 0) return cues;
        return FromPairs(TagPairs(xml));   // 退到按标签硬取（TVBox parseByTag 同语义）
    }

    /// <summary>
    /// 用 <see cref="System.Xml.Linq.XDocument"/> 而不是流式 XmlReader：
    /// <c>ReadInnerXml()</c> 会把 <c>&amp;amp;</c> 原样吐回来（命名字实不解），还会把游标停在
    /// 下一个兄弟元素<b>之后</b>，实测两条弹幕只剩一条。XDocument 一次解全且实体语义正确。
    /// </summary>
    static IEnumerable<(string Param, string Text)> XmlPairs(string xml)
    {
        var settings = new System.Xml.XmlReaderSettings
        { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = System.Xml.XmlReader.Create(new StringReader(xml), settings);
        var doc = System.Xml.Linq.XDocument.Load(reader);
        foreach (var e in doc.Descendants())
        {
            if (e.Name.LocalName != "d") continue;
            var param = e.Attribute("p")?.Value ?? "";
            var text = e.Value;
            if (param.Length > 0 && text.Length > 0) yield return (param, text);
        }
    }

    static IEnumerable<(string Param, string Text)> TagPairs(string xml)
    {
        foreach (Match m in DTag.Matches(xml))
        {
            var param = DecodeXml(m.Groups[2].Value);
            var text = DecodeXml(m.Groups[3].Value);
            if (param.Length > 0 && text.Length > 0) yield return (param, text);
        }
    }

    static List<DanmuCue> FromPairs(IEnumerable<(string Param, string Text)> pairs)
    {
        var list = new List<DanmuCue>();
        foreach (var (param, text) in pairs)
        {
            var cue = ToCue(param, text);
            if (cue is not null) list.Add(cue);
        }
        return list;
    }

    /// <summary>
    /// <c>p="时间,模式,字号,颜色,权重,池,用户ID,时间戳"</c> → 一条弹幕。
    /// <para>TVBox 只要前 4 段就够渲染；解析失败按它的行为丢掉这一条而不是整批失败。</para>
    /// </summary>
    public static DanmuCue? ToCue(string param, string text)
    {
        try
        {
            var v = param.Split(',');
            if (v.Length < 4) return null;
            if (!float.TryParse(v[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var sec)) return null;
            if (!int.TryParse(v[1], out var mode)) return null;
            if (!float.TryParse(v[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var size)) return null;
            if (!long.TryParse(v[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var color)) return null;
            if (text.Length == 0) return null;
            var special = ParseSpecial(text);
            // TVBox：轨道弹幕的正文取数组第 5 项（fillText 覆盖原串）；mode 5 却解不出数组负载 → 丢这条
            if (special is not null) text = special.Body;
            else if (mode == 5 && LooksLikeArray(text)) return null;
            return new DanmuCue
            {
                TimeSeconds = sec,
                Mode = mode,
                Size = size,
                Color = (uint)(color & 0xFFFFFF),
                Text = text,
                Special = special,
            };
        }
        catch { return null; }
    }

    static bool LooksLikeArray(string text)
    {
        var t = text.Trim();
        return t.StartsWith('[') && t.EndsWith(']');
    }

    /// <summary>
    /// 轨道弹幕负载：<c>["100","200","1.0-0.3","2.5","文字",0,0,300,0]</c>。
    /// <para>判据用「负载是不是 ≥5 项且第 5 项非空的 JSON 数组」，而不是比对
    /// <c>BaseDanmaku.TYPE_SPECIAL</c> 常量 —— 那是 danmakuFlameMaster 的内部编号，
    /// 本仓没有那个库，照抄反而会引入一个对不上的数字。</para>
    /// </summary>
    public static DanmuSpecial? ParseSpecial(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith('[') || !trimmed.EndsWith(']')) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
            var a = doc.RootElement;
            if (a.ValueKind != System.Text.Json.JsonValueKind.Array || a.GetArrayLength() < 5) return null;
            string At(int i) => a[i].ValueKind == System.Text.Json.JsonValueKind.String
                ? a[i].GetString() ?? "" : a[i].ToString();
            var body = At(4);
            if (body.Length == 0) return null;
            var alpha = At(2).Split('-');
            if (!float.TryParse(alpha[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a0)) return null;
            var a1 = alpha.Length > 1
                ? float.Parse(alpha[1], NumberStyles.Float, CultureInfo.InvariantCulture) : a0;
            if (!float.TryParse(At(3), NumberStyles.Float, CultureInfo.InvariantCulture, out var dur)) return null;
            return new DanmuSpecial
            {
                BeginX = Num(At(0)),
                BeginY = Num(At(1)),
                AlphaBegin = a0,
                AlphaEnd = a1,
                DurationSeconds = dur,
                RotateZ = a.GetArrayLength() >= 6 ? Num(At(5)) : 0f,
                RotateY = a.GetArrayLength() >= 7 ? Num(At(6)) : 0f,
                MoveX = a.GetArrayLength() >= 8 ? Num(At(7)) : 0f,
                MoveY = a.GetArrayLength() >= 9 ? Num(At(8)) : 0f,
                Body = body,
            };
        }
        catch { return null; }
    }

    static float Num(string s) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0f;

    // ── 实体修复（逐字符，与 TVBox 同判据）──

    static string EscapeIllegalEntities(string xml)
    {
        var sb = new StringBuilder(xml.Length);
        for (int i = 0; i < xml.Length; i++)
        {
            var ch = xml[i];
            if (ch == '&' && !IsLegalEntity(xml, i + 1)) sb.Append("&amp;");
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    static bool IsLegalEntity(string text, int start)
    {
        int end = text.IndexOf(';', start);
        if (end < 0 || end - start > 10) return false;
        var entity = text[start..end];
        if (entity is "amp" or "lt" or "gt" or "quot" or "apos") return true;
        if (entity.StartsWith("#x", StringComparison.OrdinalIgnoreCase)) return IsHex(entity[2..]);
        return entity.StartsWith('#') && IsDecimal(entity[1..]);
    }

    static bool IsDecimal(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);

    static bool IsHex(string s) =>
        s.Length > 0 && s.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));

    static string DecodeXml(string s) => s
        .Replace("&amp;", "&")
        .Replace("&quot;", "\"")
        .Replace("&apos;", "'")
        .Replace("&gt;", ">")
        .Replace("&lt;", "<");

    /// <summary>
    /// 取弹幕源：<c>file</c> 前缀读本地、<c>http</c> 走网络（gzip/deflate 自动解、本机代理超时重试一次）、
    /// 其余当内联 XML 直接用 —— 有的订阅把整份 XML 塞进 <c>do=danmu</c> 的 ext 里。
    /// </summary>
    public static async Task<List<DanmuCue>> LoadAsync(
        string source, HttpClient? http = null, CancellationToken ct = default)
    {
        var content = await ResolveContentAsync(source, http, ct).ConfigureAwait(false);
        return ParseXml(content);
    }

    static async Task<string> ResolveContentAsync(string source, HttpClient? http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source)) return "";
        var s = source.Trim();
        if (s.StartsWith("file", StringComparison.OrdinalIgnoreCase))
        {
            var path = s.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                ? new Uri(s).LocalPath : s;
            return File.Exists(path) ? await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) : "";
        }
        if (!s.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return s;
        if (http is null) return "";

        var local = s.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("http://localhost:", StringComparison.OrdinalIgnoreCase);
        for (int attempt = 0; attempt < (local ? 2 : 1); attempt++)
        {
            try
            {
                using var resp = await http.GetAsync(s, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                return await ReadBodyAsync(resp, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (attempt == 0 && local && !ct.IsCancellationRequested)
            {
                // 本机代理第一次拉起爬虫进程会慢一拍：只对 127.0.0.1 重试一次（TVBox 同策略）
            }
            catch { return ""; }
        }
        return "";
    }

    static async Task<string> ReadBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var xmlish = bytes.Length > 0 && bytes[0] == (byte)'<';
        if (!xmlish && resp.Content.Headers.TryGetValues("Content-Encoding", out var enc))
        {
            var e = enc.FirstOrDefault() ?? "";
            if (e.Equals("gzip", StringComparison.OrdinalIgnoreCase))
                using (var g = new System.IO.Compression.GZipStream(
                    new MemoryStream(bytes), System.IO.Compression.CompressionMode.Decompress))
                    using (var sr = new StreamReader(g))
                        return await sr.ReadToEndAsync(ct).ConfigureAwait(false);
            if (e.Equals("deflate", StringComparison.OrdinalIgnoreCase))
                using (var d = new System.IO.Compression.DeflateStream(
                    new MemoryStream(bytes), System.IO.Compression.CompressionMode.Decompress))
                    using (var sr = new StreamReader(d))
                        return await sr.ReadToEndAsync(ct).ConfigureAwait(false);
        }
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
