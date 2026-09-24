using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace CatClawVideo.Core.Live;

/// <summary>
/// EPG 服务（TVBox <c>util/EpgUtil.java</c> + <c>LivePlayActivity</c> 内联 EPG 逻辑的 C# 移植）：
/// <list type="bullet">
/// <item>台标 / EPG-ID 字典：内嵌 <c>epg_data.json</c>（534 个频道别名 → logo + epgid）</item>
/// <item>EPG 拉取：支持 <c>{name}/{date}</c> 模板、<c>.xml</c>（XMLTV）直链、默认 51zmt 接口</item>
/// <item>双解析：JSON（epg_data/data/list 多形态）与 XMLTV（channel/programme，频道名归一化匹配）</item>
/// <item>时间语义统一 GMT+8；按 <c>频道名_日期</c> 内存缓存（10 分钟）</item>
/// </list>
/// </summary>
public class EpgService
{
    public const string DefaultEpgAddress = "http://epg.51zmt.top:8000/api/diyp/?ch={name}&date={date}";

    private static readonly TimeSpan Zone = TimeSpan.FromHours(8);
    private static readonly HttpClient Http = CreateClient();

    private static Dictionary<string, (string Logo, string EpgId)>? _aliasDict;

    private readonly Dictionary<string, (DateTimeOffset At, List<LiveProgram> Programs)> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    // ═══════════════════ 台标字典 ═══════════════════

    /// <summary>按频道名查台标与 EPG-ID（对应 EpgUtil.getEpgInfo；未命中返回 null）。</summary>
    public static (string Logo, string EpgId)? LookupChannel(string channelName)
    {
        EnsureDict();
        if (_aliasDict == null || channelName.Length == 0) return null;
        return _aliasDict.TryGetValue(channelName, out var v) ? v : null;
    }

    private static void EnsureDict()
    {
        if (_aliasDict != null) return;
        var dict = new Dictionary<string, (string, string)>();
        try
        {
            var asm = typeof(EpgService).Assembly;
            var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("epg_data.json", StringComparison.OrdinalIgnoreCase));
            if (resName != null)
            {
                using var stream = asm.GetManifestResourceStream(resName);
                if (stream != null)
                {
                    using var reader = new StreamReader(stream);
                    var parsed = JsonNode.Parse(reader.ReadToEnd());
                    if (parsed is JsonObject doc && doc["epgs"] is JsonArray epgs)
                    {
                        foreach (var node in epgs)
                        {
                            if (node is not JsonObject obj) continue;
                            var logo = Str(obj["logo"]);
                            var epgId = Str(obj["epgid"]);
                            var name = Str(obj["name"]).Trim();
                            if (name.Length == 0) continue;
                            foreach (var alias in name.Split(','))
                            {
                                var key = alias.Trim();
                                if (key.Length > 0) dict.TryAdd(key, (logo, epgId));
                            }
                        }
                    }
                }
            }
        }
        catch
        {
        }
        _aliasDict = dict;
    }

    // ═══════════════════ 拉取入口 ═══════════════════

    /// <summary>取某频道"当前 / 下一"节目（无数据时返回 null）。</summary>
    public async Task<(LiveProgram? Now, LiveProgram? Next)> GetNowNextAsync(string channelName, string? epgUrl, CancellationToken ct = default)
    {
        var programs = await GetProgramsAsync(channelName, epgUrl, ct);
        var now = DateTimeOffset.UtcNow.ToOffset(Zone);
        LiveProgram? current = programs.FirstOrDefault(p => p.IsNow(now));
        LiveProgram? next;
        if (current != null)
        {
            next = programs.FirstOrDefault(p => p.Start >= current.End);
        }
        else
        {
            next = programs.FirstOrDefault(p => p.Start > now);
        }
        return (current, next);
    }

    /// <summary>取某频道当天节目列表（含缓存与多名字回退）。</summary>
    public async Task<List<LiveProgram>> GetProgramsAsync(string channelName, string? epgUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(channelName)) return new List<LiveProgram>();
        var today = DateTimeOffset.UtcNow.ToOffset(Zone).ToString("yyyy-MM-dd");
        var cacheKey = channelName + "_" + today;
        if (_cache.TryGetValue(cacheKey, out var slot) && DateTimeOffset.Now - slot.At < CacheTtl)
            return slot.Programs;

        var address = string.IsNullOrWhiteSpace(epgUrl) ? DefaultEpgAddress : epgUrl;
        // 查询名回退链：EPG-ID（字典）→ 原名
        var names = new List<string>();
        var meta = LookupChannel(channelName);
        if (meta is { EpgId: { Length: > 0 } id }) names.Add(id);
        if (!names.Contains(channelName)) names.Add(channelName);

        foreach (var queryName in names)
        {
            var url = BuildEpgUrl(address, queryName, today);
            if (url == null) continue;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await Http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) continue;
                var body = await resp.Content.ReadAsStringAsync(ct);
                var programs = ParseEpgBody(body, channelName, today);
                if (programs.Count > 0)
                {
                    _cache[cacheKey] = (DateTimeOffset.Now, programs);
                    return programs;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // 换下一个名字
            }
        }

        _cache[cacheKey] = (DateTimeOffset.Now, new List<LiveProgram>());
        return new List<LiveProgram>();
    }

    /// <summary>URL 组装（对应 buildEpgUrl：模板替换 → .xml 原样 → 追加 ch/date）。</summary>
    private static string? BuildEpgUrl(string address, string name, string date)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        if (address.Contains("{name}") || address.Contains("{date}"))
            return address.Replace("{name}", Uri.EscapeDataString(name)).Replace("{date}", date);
        var pathOnly = address.Split('?')[0];
        if (pathOnly.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) return address;
        var sep = address.Contains('?') ? '&' : '?';
        return $"{address}{sep}ch={Uri.EscapeDataString(name)}&date={date}";
    }

    // ═══════════════════ 响应解析 ═══════════════════

    private static List<LiveProgram> ParseEpgBody(string body, string channelName, string date)
    {
        if (string.IsNullOrWhiteSpace(body)) return new List<LiveProgram>();
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("<tv", StringComparison.OrdinalIgnoreCase)
            || body.Contains("<programme", StringComparison.Ordinal))
            return ParseXmlEpg(body, channelName, date);
        if (body.Contains("epg_data", StringComparison.Ordinal) || trimmed.StartsWith('{') || trimmed.StartsWith('['))
            return ParseJsonEpg(body, date);
        return new List<LiveProgram>();
    }

    // ── JSON ──

    private static List<LiveProgram> ParseJsonEpg(string body, string date)
    {
        var result = new List<LiveProgram>();
        try
        {
            var root = JsonNode.Parse(body);
            JsonArray? arr = root as JsonArray;
            arr ??= FindArray(root, "epg_data");
            arr ??= FindArray(root, "data");
            arr ??= FindArray(root, "list");
            if (arr == null) return result;

            var dateBase = DateTimeOffset.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None).ToOffset(Zone);
            foreach (var node in arr)
            {
                if (node is not JsonObject obj) continue;
                var title = CleanEpgTitle(Pick(obj, "title", "name"));
                var startText = Pick(obj, "start", "start_time", "starttime");
                var endText = Pick(obj, "end", "end_time", "endtime");
                if (title.Length == 0 || startText.Length == 0 || endText.Length == 0) continue;
                if (IsUnavailableEpgText(title)) continue;
                var start = ParseJsonEpgDate(startText, dateBase);
                var end = ParseJsonEpgDate(endText, dateBase);
                if (start == null || end == null) continue;
                if (end <= start) end = end.Value.AddDays(1);
                result.Add(new LiveProgram { Title = title, Start = start.Value, End = end.Value });
            }
        }
        catch
        {
        }
        return Finalize(result, date);
    }

    private static JsonArray? FindArray(JsonNode? node, string key)
    {
        if (node is JsonObject obj && obj[key] is JsonArray arr) return arr;
        if (node is JsonObject o2 && o2["data"] is JsonObject data && data[key] is JsonArray nested) return nested;
        return null;
    }

    private static DateTimeOffset? ParseJsonEpgDate(string text, DateTimeOffset dateBase)
    {
        text = text.Trim();
        if (DateTimeOffset.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var full))
            return full.ToOffset(Zone);
        if (DateTimeOffset.TryParseExact(text, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fullShort))
            return fullShort.ToOffset(Zone);
        // 只有 "HH:mm(:ss)"：用请求日期补全
        if (TimeSpan.TryParse(text.Length == 5 ? text + ":00" : text, CultureInfo.InvariantCulture, out var time))
            return new DateTimeOffset(dateBase.Year, dateBase.Month, dateBase.Day, 0, 0, 0, Zone) + time;
        return null;
    }

    // ── XMLTV ──

    private static List<LiveProgram> ParseXmlEpg(string xml, string channelName, string date)
    {
        var result = new List<LiveProgram>();
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var stringReader = new StringReader(xml);
            using var xmlReader = XmlReader.Create(stringReader, settings);
            var doc = XDocument.Load(xmlReader);

            var target = NormalizeEpgChannelName(channelName);
            var channelIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var channel in doc.Descendants("channel"))
            {
                var id = channel.Attribute("id")?.Value ?? "";
                var matched = NormalizeEpgChannelName(id) == target;
                if (!matched)
                {
                    foreach (var display in channel.Descendants("display-name"))
                    {
                        if (NormalizeEpgChannelName(display.Value) == target) { matched = true; break; }
                    }
                }
                if (matched && id.Length > 0) channelIds.Add(id);
            }

            foreach (var programme in doc.Descendants("programme"))
            {
                var ch = programme.Attribute("channel")?.Value ?? "";
                if (channelIds.Count > 0 && !channelIds.Contains(ch)) continue;
                if (channelIds.Count == 0 && NormalizeEpgChannelName(ch) != target) continue;

                var start = ParseXmlTvDate(programme.Attribute("start")?.Value);
                var stop = ParseXmlTvDate(programme.Attribute("stop")?.Value);
                if (start == null || stop == null) continue;
                if (stop <= start) stop = stop.Value.AddDays(1);

                var title = CleanEpgTitle(programme.Descendants("title").FirstOrDefault()?.Value ?? "");
                if (title.Length == 0 || IsUnavailableEpgText(title)) continue;
                result.Add(new LiveProgram { Title = title, Start = start.Value, End = stop.Value });
            }
        }
        catch
        {
        }
        return Finalize(result, date);
    }

    private static DateTimeOffset? ParseXmlTvDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().Split(' ', 2);
        if (parts[0].Length < 14) return null;
        if (!DateTime.TryParseExact(parts[0][..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return null;
        var offset = Zone;
        if (parts.Length > 1 && parts[1].Length >= 5 && (parts[1][0] == '+' || parts[1][0] == '-'))
        {
            if (int.TryParse(parts[1][1..3], out var hh) && int.TryParse(parts[1][3..5], out var mm))
                offset = new TimeSpan(parts[1][0] == '-' ? -hh : hh, parts[1][0] == '-' ? -mm : mm, 0);
        }
        return new DateTimeOffset(dt, offset);
    }

    /// <summary>频道名归一化（对应 LivePlayActivity.normalizeEpgChannelName：去 - 和空格，CCTV 提取）。</summary>
    public static string NormalizeEpgChannelName(string name)
    {
        var s = name.Trim().ToUpperInvariant().Replace("-", "").Replace(" ", "");
        var m = Regex.Match(s, @"CCTV\d+(\+|K)?");
        return m.Success ? m.Value : s;
    }

    // ═══════════════════ 工具 ═══════════════════

    /// <summary>只保留与目标日期有交集的节目并按开始时间排序。</summary>
    private static List<LiveProgram> Finalize(List<LiveProgram> list, string date)
    {
        var dayStart = DateTimeOffset.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None).ToOffset(Zone);
        var dayEnd = dayStart.AddDays(1);
        return list
            .Where(p => p.End > dayStart && p.Start < dayEnd)
            .OrderBy(p => p.Start)
            .ToList();
    }

    private static string CleanEpgTitle(string title) => title.Replace(" --免费使用", "").Trim();

    private static bool IsUnavailableEpgText(string text) =>
        text.Contains("未提供") || text.Contains("暂无");

    private static string Pick(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            var v = Str(obj[key]);
            if (v.Length > 0) return v.Trim();
        }
        return "";
    }

    private static string Str(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v => v.TryGetValue<string>(out var s) ? s : v.ToString(),
        _ => node.ToJsonString(),
    };

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }
}
