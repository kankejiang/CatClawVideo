using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CatClawVideo.Core.Live;

/// <summary>
/// 直播源解析器：<c>com.github.tvbox.osc.util.live.TxtSubscribe</c> 的忠实移植（含
/// <c>ApiConfig.loadLives</c> 的字段契约）。把三种格式统一成 TVBox 规范化 JSON：
/// <code>[ { "group": "组名", "channels": [ { "name":…, "urls":[…], "logo":…, "epg":…, … } ] } ]</code>
/// 支持：
/// <list type="bullet">
/// <item>JSON 数组（社区规范结构，含 channels/channel 两种键名）</item>
/// <item>M3U（<c>#EXTM3U</c>：group-title / tvg-* / #EXTHTTP / #EXTVLCOPT / #KODIPROP / catchup）</item>
/// <item>TXT（<c>组名,#genre#</c> 分组；<c>频道名,url1#url2</c>；<c>ua=</c> 等设置行挂到紧随其后的频道）</item>
/// </list>
/// </summary>
public static class LiveParser
{
    public const string DefaultGroupName = "直播";
    private const string LegacyDefaultGroupName = "Ungrouped";

    private static readonly Regex NamePattern = new(@".*,(.+?)$", RegexOptions.Compiled);
    private static readonly Regex GroupPattern = new("group-title=\"(.*?)\"", RegexOptions.Compiled);
    private static readonly Regex TvgChnoPattern = new("tvg-chno=\"(.*?)\"", RegexOptions.Compiled);
    private static readonly Regex TvgLogoPattern = new("tvg-logo=\"(.*?)\"", RegexOptions.Compiled);
    private static readonly Regex TvgNamePattern = new("tvg-name=\"(.*?)\"", RegexOptions.Compiled);
    private static readonly Regex TvgUrlPattern = new("tvg-url=\"(.*?)\"", RegexOptions.Compiled);
    private static readonly Regex TvgIdPattern = new("tvg-id=\"(.*?)\"", RegexOptions.Compiled);
    private static readonly Regex HttpUserAgentPattern = new("http-user-agent=\"(.*?)\"", RegexOptions.Compiled);
    private static readonly Regex CatchupPattern = new("catchup=\"(.*?)\"", RegexOptions.Compiled);
    private static readonly Regex CatchupSourcePattern = new("catchup-source=\"(.*?)\"", RegexOptions.Compiled);
    private static readonly Regex CatchupReplacePattern = new("catchup-replace=\"(.*?)\"", RegexOptions.Compiled);

    // ═══════════════════ 入口 ═══════════════════

    /// <summary>任意内容 → 规范化 JSON 数组（对应 parseToJsonArray：JSON 数组 / M3U / TXT 三态）。</summary>
    public static JsonArray ParseToNormalizedArray(string? str)
    {
        var result = new JsonArray();
        if (string.IsNullOrWhiteSpace(str)) return result;
        str = str.Trim();

        // 1) 直接是 JSON 数组
        if (str.StartsWith('['))
        {
            try
            {
                var node = JsonNode.Parse(str);
                if (node is JsonArray arr) return NormalizeJsonArray(arr);
            }
            catch
            {
                // 落到文本解析
            }
        }

        // 2) M3U
        if (str.StartsWith("#EXTM3U", StringComparison.Ordinal)) return ParseM3uToJsonArray(str);

        // 3) TXT
        return ParseTxtToJsonArray(str);
    }

    /// <summary>
    /// 规范化 JSON → 直播分组模型（对应 ApiConfig.loadLives 的字段契约：
    /// 组名 <c>组名_密码</c> 拆分、urls 的 <c>url$线路名</c> 拆分、同名频道合并线路、全局频道号）。
    /// </summary>
    public static List<LiveChannelGroup> BuildGroups(JsonArray normalized)
    {
        var list = new List<LiveChannelGroup>();
        int groupIndex = 0;
        int channelNum = 0;
        foreach (var groupNode in normalized)
        {
            if (groupNode is not JsonObject groupObj) continue;
            var group = new LiveChannelGroup { GroupIndex = groupIndex++ };
            var rawGroupName = Str(groupObj["group"]).Trim();
            var splitName = rawGroupName.Split('_', 2);
            group.GroupName = splitName[0];
            group.GroupPassword = splitName.Length > 1 ? splitName[1] : "";

            int channelIndex = 0;
            if (groupObj["channels"] is JsonArray channels)
            {
                foreach (var channelNode in channels)
                {
                    if (channelNode is not JsonObject obj) continue;
                    var item = new LiveChannelItem
                    {
                        ChannelName = Str(obj["name"]).Trim(),
                        ChannelLogo = Str(obj["logo"]),
                        ChannelEpg = Str(obj["epg"]),
                        ChannelUa = Str(obj["ua"]),
                        ChannelClick = Str(obj["click"]),
                        ChannelFormat = Str(obj["format"]),
                        ChannelOrigin = Str(obj["origin"]),
                        ChannelReferer = Str(obj["referer"]),
                        ChannelTvgId = Str(obj["tvg-id"]),
                        ChannelTvgName = Str(obj["tvg-name"]),
                    };
                    if (obj["parse"] is JsonNode parseNode && int.TryParse(parseNode.ToString(), out var parseVal))
                        item.ChannelParse = parseVal;

                    // catchup：对象直接用；字符串 → {type, source, replace}
                    if (obj["catchup"] is JsonObject catchupObj)
                    {
                        item.CatchupType = Str(catchupObj["type"]);
                        item.CatchupSource = Str(catchupObj["source"]);
                        item.CatchupReplace = Str(catchupObj["replace"]);
                    }
                    else if (obj["catchup"] is JsonValue catchupVal)
                    {
                        item.CatchupType = Str(catchupVal);
                        item.CatchupSource = Str(obj["catchup-source"]);
                        item.CatchupReplace = Str(obj["catchup-replace"]);
                    }

                    if (obj["header"] is JsonObject headerObj)
                    {
                        foreach (var kv in headerObj)
                            item.ChannelHeader[kv.Key] = Str(kv.Value);
                    }

                    if (obj["urls"] is JsonArray urls)
                    {
                        int sourceIndex = 1;
                        foreach (var urlNode in urls)
                        {
                            var url = Str(urlNode).Trim();
                            var splitUrl = url.Split('$', 2);
                            item.SourceUrls.Add(splitUrl[0]);
                            item.SourceNames.Add(splitUrl.Length > 1 ? splitUrl[1] : "源" + sourceIndex);
                            sourceIndex++;
                        }
                    }

                    if (MergeLiveChannel(group.Channels, item))
                    {
                        item.ChannelIndex = channelIndex++;
                        item.ChannelNum = ++channelNum;
                    }
                }
            }
            list.Add(group);
        }
        return list;
    }

    /// <summary>从 M3U 头部行提取 EPG 地址（对应 ApiConfig.extractLiveTextEpg：x-tvg-url → tvg-url → url-tvg）。</summary>
    public static string ExtractLiveTextEpg(string? content)
    {
        if (string.IsNullOrEmpty(content)) return "";
        var text = content.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('\ufeff')) line = line[1..].Trim();
            if (!line.StartsWith("#EXTM3U", StringComparison.Ordinal)) continue;
            var epg = ExtractQuotedAttr(line, "x-tvg-url");
            if (epg.Length == 0) epg = ExtractQuotedAttr(line, "tvg-url");
            if (epg.Length == 0) epg = ExtractQuotedAttr(line, "url-tvg");
            return epg;
        }
        return "";
    }

    private static string ExtractQuotedAttr(string line, string key)
    {
        var token = key + "=\"";
        var start = line.IndexOf(token, StringComparison.Ordinal);
        if (start < 0) return "";
        start += token.Length;
        var end = line.IndexOf('"', start);
        if (end < 0) return "";
        return line[start..end].Trim();
    }

    /// <summary>直播地址白名单（对应 isUrl：http / rtp / rtsp / rtmp）。</summary>
    public static bool IsLiveUrl(string? url) =>
        !string.IsNullOrEmpty(url) &&
        (url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
         || url.StartsWith("rtp", StringComparison.OrdinalIgnoreCase)
         || url.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase)
         || url.StartsWith("rtmp", StringComparison.OrdinalIgnoreCase));

    /// <summary>组名归一化（对应 normalizeGroupName：空 / "Ungrouped" → "直播"）。</summary>
    public static string NormalizeGroupName(string? name)
    {
        if (name == null) return DefaultGroupName;
        name = name.Trim();
        if (name.Length == 0 || string.Equals(name, LegacyDefaultGroupName, StringComparison.OrdinalIgnoreCase))
            return DefaultGroupName;
        return name;
    }

    // ═══════════════════ JSON 规范化 ═══════════════════

    private static JsonArray NormalizeJsonArray(JsonArray groups)
    {
        var result = new JsonArray();
        foreach (var node in groups)
        {
            if (node is not JsonObject groupObj) continue;
            var outGroup = new JsonObject();
            var groupName = Str(groupObj["group"]);
            if (groupName.Length == 0) groupName = Str(groupObj["name"]);
            if (groupName.Length == 0) groupName = DefaultGroupName;
            outGroup["group"] = NormalizeGroupName(groupName);

            JsonArray? channels = groupObj["channels"] as JsonArray ?? groupObj["channel"] as JsonArray;
            if (channels != null)
            {
                foreach (var channelNode in channels)
                {
                    if (channelNode is not JsonObject channelObj) continue;
                    var outChannel = new JsonObject();
                    foreach (var key in ChannelKeys)
                        CopyIfExists(channelObj, outChannel, key);
                    AddChannel(outGroup, outChannel);
                }
            }
            if (outGroup["channels"] is not JsonArray) outGroup["channels"] = new JsonArray();
            result.Add(outGroup);
        }
        return result;
    }

    private static readonly string[] ChannelKeys =
    [
        "name", "urls", "logo", "epg", "ua", "click", "format", "origin", "referer",
        "tvg-id", "tvg-name", "tvg-chno", "parse", "header", "catchup", "catchup-source", "catchup-replace",
    ];

    private static void CopyIfExists(JsonObject src, JsonObject dst, string key)
    {
        if (src[key] is JsonNode node) dst[key] = node.DeepClone();
    }

    // ═══════════════════ M3U ═══════════════════

    private static JsonArray ParseM3uToJsonArray(string str)
    {
        var result = new JsonArray();
        JsonObject? currentGroup = null;
        JsonObject? pendingChannel = null;
        var pendingMeta = new JsonObject();
        try
        {
            var text = str.Replace("\r\n", "\n").Replace('\r', '\n');
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith("#EXTM3U", StringComparison.Ordinal))
                {
                    MergeMeta(pendingMeta, BuildMeta(line));
                    continue;
                }
                if (IsSetting(line))
                {
                    MergeMeta(pendingMeta, BuildSetting(line));
                    continue;
                }
                if (line.StartsWith("#EXTINF", StringComparison.Ordinal) || line.Contains("#EXTINF", StringComparison.Ordinal))
                {
                    var groupName = NormalizeGroupName(Get(line, GroupPattern));
                    currentGroup = FindOrCreateGroup(result, groupName);
                    pendingChannel = new JsonObject { ["name"] = Get(line, NamePattern) };
                    MergeMeta(pendingChannel, BuildMeta(line));
                    MergeMeta(pendingChannel, pendingMeta);
                    pendingMeta = new JsonObject();
                    continue;
                }
                if (line.StartsWith('#')) continue;

                currentGroup ??= FindOrCreateGroup(result, DefaultGroupName);
                pendingChannel ??= new JsonObject();

                var parts = line.Split('|', 2);
                var url = parts[0].Trim();
                if (!IsLiveUrl(url)) continue;
                if (parts.Length > 1) MergeMeta(pendingMeta, ParseHeaderString(parts[1]));
                MergeMeta(pendingChannel, pendingMeta);

                if (pendingChannel["urls"] is not JsonArray urls)
                {
                    urls = new JsonArray();
                    pendingChannel["urls"] = urls;
                }
                if (!ContainsUrl(urls, url)) urls.Add(url);

                AddChannel(currentGroup, pendingChannel);
                pendingMeta = new JsonObject();
            }
        }
        catch
        {
            // 与 Java 版一致：解析异常吞掉，返回已解析部分
        }
        return result;
    }

    // ═══════════════════ TXT ═══════════════════

    private static JsonArray ParseTxtToJsonArray(string str)
    {
        var result = new JsonArray();
        JsonObject? currentGroup = null;
        var pendingMeta = new JsonObject();
        try
        {
            var text = str.Replace("\r\n", "\n").Replace('\r', '\n');
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith('#'))
                {
                    if (IsSetting(line)) MergeMeta(pendingMeta, BuildSetting(line));
                    continue;
                }
                if (line.Contains("#genre#"))
                {
                    var groupName = line.Split(',', 2)[0].Trim();
                    currentGroup = FindOrCreateGroup(result, groupName);
                    pendingMeta = new JsonObject();
                    continue;
                }
                var split = line.Split(',', 2);
                if (split.Length < 2) continue;
                currentGroup ??= FindOrCreateGroup(result, DefaultGroupName);

                var channel = new JsonObject { ["name"] = split[0].Trim() };
                MergeMeta(channel, pendingMeta);

                var urls = new List<string>();
                foreach (var part in split[1].Trim().Split('#'))
                {
                    var url = part.Trim();
                    if (IsLiveUrl(url) && !urls.Contains(url)) urls.Add(url);
                }
                if (urls.Count == 0) continue;   // ⚠ 注意：此处不清 pendingMeta（与 Java 一致）

                var urlArray = new JsonArray();
                foreach (var url in urls) urlArray.Add(url);
                channel["urls"] = urlArray;
                AddChannel(currentGroup, channel);
                pendingMeta = new JsonObject();
            }
        }
        catch
        {
        }
        return result;
    }

    // ═══════════════════ 元数据 / 设置行 ═══════════════════

    private static JsonObject ParseHeaderString(string text)
    {
        var wrapper = new JsonObject();
        var obj = new JsonObject();
        foreach (var param in text.Split('&'))
        {
            if (!param.Contains('=')) continue;
            var a = param.Split('=', 2);
            obj[a[0].Trim().Replace("\"", "")] = a[1].Trim().Replace("\"", "");
        }
        if (obj.Count > 0) wrapper["header"] = obj;
        return wrapper;
    }

    private static JsonObject BuildMeta(string line)
    {
        var obj = new JsonObject();
        Put(obj, "logo", Get(line, TvgLogoPattern));
        Put(obj, "epg", Get(line, TvgUrlPattern));
        Put(obj, "tvg-id", Get(line, TvgIdPattern));
        Put(obj, "tvg-name", Get(line, TvgNamePattern));
        Put(obj, "tvg-chno", Get(line, TvgChnoPattern));
        Put(obj, "ua", Get(line, HttpUserAgentPattern));
        var catchup = Get(line, CatchupPattern);
        var source = Get(line, CatchupSourcePattern);
        var replace = Get(line, CatchupReplacePattern);
        if (catchup.Length > 0 || source.Length > 0 || replace.Length > 0)
        {
            var catchupObj = new JsonObject();
            Put(catchupObj, "type", catchup);
            Put(catchupObj, "source", source);
            Put(catchupObj, "replace", replace);
            obj["catchup"] = catchupObj;
        }
        return obj;
    }

    private static JsonObject BuildSetting(string line)
    {
        var obj = new JsonObject();
        if (line.StartsWith("ua")) Put(obj, "ua", GetValue(line, "ua"));
        if (line.StartsWith("parse")) Put(obj, "parse", GetValue(line, "parse"));
        if (line.StartsWith("click")) Put(obj, "click", GetValue(line, "click"));
        if (line.StartsWith("header"))
        {
            var value = GetValue(line, "header");
            if (value.Length > 0)
            {
                try { obj["header"] = JsonNode.Parse(value)?.DeepClone(); } catch { }
            }
        }
        if (line.StartsWith("format")) Put(obj, "format", GetValue(line, "format"));
        if (line.StartsWith("origin")) Put(obj, "origin", GetValue(line, "origin"));
        if (line.StartsWith("referer")) Put(obj, "referer", GetValue(line, "referer"));
        if (line.StartsWith("#EXTHTTP:"))
        {
            try { obj["header"] = JsonNode.Parse(line.Split("#EXTHTTP:")[1].Trim())?.DeepClone(); } catch { }
        }
        if (line.StartsWith("#EXTVLCOPT:"))
        {
            if (line.Contains("http-user-agent")) Put(obj, "ua", GetValue(line, "http-user-agent"));
            if (line.Contains("http-origin")) Put(obj, "origin", GetValue(line, "http-origin"));
            if (line.Contains("http-referrer")) Put(obj, "referer", GetValue(line, "http-referrer"));
        }
        if (line.StartsWith("#KODIPROP:") && line.Contains("manifest_type="))
            Put(obj, "format", GetValue(line, "manifest_type"));
        return obj;
    }

    private static bool IsSetting(string line) =>
        line.StartsWith("ua") || line.StartsWith("parse") || line.StartsWith("click") ||
        line.StartsWith("player") || line.StartsWith("header") || line.StartsWith("format") ||
        line.StartsWith("origin") || line.StartsWith("referer") || line.StartsWith("#EXTHTTP:") ||
        line.StartsWith("#EXTVLCOPT:") || line.StartsWith("#KODIPROP:");

    // ═══════════════════ 通用工具 ═══════════════════

    private static JsonObject FindOrCreateGroup(JsonArray result, string name)
    {
        name = NormalizeGroupName(name);
        foreach (var node in result)
        {
            if (node is JsonObject group && Str(group["group"]) == name) return group;
        }
        var newGroup = new JsonObject { ["group"] = name, ["channels"] = new JsonArray() };
        result.Add(newGroup);
        return newGroup;
    }

    private static void AddChannel(JsonObject group, JsonObject channel)
    {
        if (group["channels"] is not JsonArray channels)
        {
            channels = new JsonArray();
            group["channels"] = channels;
        }
        var name = Str(channel["name"]);
        var exists = name.Length == 0 ? null : FindChannel(channels, name);
        if (exists == null) channels.Add(channel);
        else MergeChannel(exists, channel);
    }

    private static JsonObject? FindChannel(JsonArray channels, string name)
    {
        foreach (var node in channels)
        {
            if (node is JsonObject channel && Str(channel["name"]) == name) return channel;
        }
        return null;
    }

    private static void MergeChannel(JsonObject dst, JsonObject src)
    {
        MergeUrls(dst, src);
        foreach (var kv in src)
        {
            if (kv.Key == "urls") continue;
            if (dst[kv.Key] is not JsonNode existing || IsEmptyValue(existing))
                dst[kv.Key] = kv.Value?.DeepClone();
        }
    }

    private static void MergeUrls(JsonObject dst, JsonObject src)
    {
        if (src["urls"] is not JsonArray srcUrls) return;
        if (dst["urls"] is not JsonArray dstUrls)
        {
            dstUrls = new JsonArray();
            dst["urls"] = dstUrls;
        }
        foreach (var node in srcUrls)
        {
            if (node is not JsonValue) continue;
            var url = Str(node).Trim();
            if (IsLiveUrl(url) && !ContainsUrl(dstUrls, url)) dstUrls.Add(url);
        }
    }

    private static bool IsEmptyValue(JsonNode? node) => node switch
    {
        null => true,
        JsonValue value => Str(value).Trim().Length == 0,
        JsonArray arr => arr.Count == 0,
        JsonObject obj => obj.Count == 0,
        _ => false,
    };

    private static bool ContainsUrl(JsonArray urls, string url)
    {
        foreach (var node in urls)
        {
            if (Str(node) == url) return true;
        }
        return false;
    }

    private static void MergeMeta(JsonObject dst, JsonObject src)
    {
        foreach (var kv in src) dst[kv.Key] = kv.Value?.DeepClone();
    }

    private static string Get(string line, Regex pattern)
    {
        var m = pattern.Match(line);
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    private static string GetValue(string line, string key)
    {
        var index = line.IndexOf(key + "=", StringComparison.Ordinal);
        if (index == -1) return "";
        return line[(index + key.Length + 1)..].Trim().Replace("\"", "");
    }

    private static void Put(JsonObject obj, string key, string value)
    {
        if (!string.IsNullOrEmpty(value)) obj[key] = value;
    }

    /// <summary>JsonNode → 字符串（字符串节点取原文，其余取文本表示）。</summary>
    private static string Str(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v => v.TryGetValue<string>(out var s) ? s : v.ToString(),
        _ => node.ToJsonString(),
    };

    // ═══════════════════ ApiConfig 侧合并逻辑 ═══════════════════

    private static bool MergeLiveChannel(List<LiveChannelItem> items, LiveChannelItem newItem)
    {
        var old = items.FirstOrDefault(i => i.ChannelName == newItem.ChannelName);
        if (old == null)
        {
            items.Add(newItem);
            return true;
        }
        MergeLiveChannelUrls(old, newItem);
        return false;
    }

    private static void MergeLiveChannelUrls(LiveChannelItem oldItem, LiveChannelItem newItem)
    {
        for (var i = 0; i < newItem.SourceUrls.Count; i++)
        {
            var url = newItem.SourceUrls[i];
            if (oldItem.SourceUrls.Contains(url)) continue;
            oldItem.SourceUrls.Add(url);
            oldItem.SourceNames.Add(i < newItem.SourceNames.Count ? newItem.SourceNames[i] : "源" + (oldItem.SourceNames.Count + 1));
        }
    }
}
