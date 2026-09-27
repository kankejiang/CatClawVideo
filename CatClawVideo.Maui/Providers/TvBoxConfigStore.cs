using System.Collections.Concurrent;
using System.Text.Json;
using CatClawVideo.Core.Services;

namespace CatClawVideo.Core.Providers;

/// <summary>
/// TVBox 订阅「解析」配置仓：订阅解析时留存 parses/hosts（此前这两个键被整包丢弃），
/// 播放时按线路 flag 匹配 ParseRule 决策：JSON 解析引擎（type=1）/ WebView 嗅探（type=0）/ playUrl 前缀。
/// 语义对照 TVBox OSC：ParseBean.java / SuperParse.java / PlayFragment.initParse(L2049)。
/// </summary>
public static class TvBoxConfigStore
{
    /// <summary>采集留痕（订阅里到底有没有 rules、采到几条，直接决定嗅探规则消费是否生效）。</summary>
    public static Action<string>? Log;

    private static readonly ConcurrentDictionary<string, StoreEntry> Stores = new();

    private sealed class StoreEntry
    {
        public List<ParseRule> Parses { get; init; } = [];
        public Dictionary<string, string> Hosts { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>嗅探「这是视频」规则组（对位 <c>VideoParseRuler.HOSTS_RULE</c>：一条 = 一组，组内 AND、组间 OR）</summary>
        public List<(string Host, List<string> Group)> RuleGroups { get; } = [];

        /// <summary>嗅探过滤规则（对位 <c>HOSTS_FILTER</c>，同 host 才生效）</summary>
        public List<(string Host, List<string> Group)> FilterGroups { get; } = [];

        /// <summary>播放列表广告正则（对位 <c>HOSTS_REGEX</c>，只收 <c>M3u8.isAd()</c> 命中的那些）</summary>
        public List<(string Host, List<string> Regexes)> AdRegex { get; } = [];

        /// <summary>订阅自带的桌面壁纸地址（对位 TVBox <c>ApiConfig.wallpaper</c> / 设置页「下载壁纸」）。</summary>
        public string? Wallpaper { get; set; }
    }

    /// <summary>单条解析规则（归一化后的 ParseBean）</summary>
    public sealed record ParseRule(
        string Name,
        int Type,          // 0=嗅探 1=json 2=json扩展(按1处理) 4=聚合(按1+嗅探并发处理)
        string Url,
        string? ExtJson)   // ext 原始 JSON 文本（含 flag 列表与 header）
    {
        /// <summary>ext.flag 数组：声明本解析负责哪些线路 flag；空 = 兜底解析</summary>
        public List<string> Flags { get; } = [];

        /// <summary>ext.header 对象：调解析接口时的附加请求头</summary>
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool IsDefault { get; internal set; }
    }

    /// <summary>订阅解析完成后调用：从配置根节点留存 parses/hosts（解析失败静默，不影响站点加载）</summary>
    public static void Capture(string subscriptionKey, JsonElement root)
    {
        try
        {
            var entry = new StoreEntry();

            // 壁纸：<c>Models/TvBoxConfig.cs</c> 那个类全仓从未被反序列化（订阅 JSON 走 JsonElement 直读），
            // 所以捕获点只能落在这里 —— 否则 wallpaper 永远是一行没有消费者的声明。
            if (root.TryGetProperty("wallpaper", out var wp) && wp.ValueKind == JsonValueKind.String)
                entry.Wallpaper = wp.GetString();

            if (root.TryGetProperty("parses", out var parses) && parses.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in parses.EnumerateArray())
                {
                    var name = p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var url = p.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                    if (name.Length == 0 || url.Length == 0) continue;
                    var type = p.TryGetProperty("type", out var t) && t.TryGetInt32(out var tv) ? tv : 0;
                    string? extJson = null;
                    if (p.TryGetProperty("ext", out var e) && e.ValueKind != JsonValueKind.Null)
                        extJson = e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText();

                    var rule = new ParseRule(name, type, url, extJson);
                    if (!string.IsNullOrEmpty(extJson))
                    {
                        try
                        {
                            using var edoc = JsonDocument.Parse(extJson);
                            if (edoc.RootElement.TryGetProperty("flag", out var flags) &&
                                flags.ValueKind == JsonValueKind.Array)
                                foreach (var f in flags.EnumerateArray())
                                    if (f.ValueKind == JsonValueKind.String && f.GetString() is { } fs)
                                        rule.Flags.Add(fs);
                            if (edoc.RootElement.TryGetProperty("header", out var h) &&
                                h.ValueKind == JsonValueKind.Object)
                                foreach (var kv in h.EnumerateObject())
                                    if (kv.Value.ValueKind == JsonValueKind.String)
                                        rule.Headers[kv.Name] = kv.Value.GetString() ?? "";
                        }
                        catch { /* ext 非法时按无 flag/无 header 处理 */ }
                    }
                    entry.Parses.Add(rule);
                }
                // TVBox getDefaultParse：无显式 default 标记时第一个可用者兜底
                var first = entry.Parses.FirstOrDefault();
                if (first is not null) first.IsDefault = true;
            }

            if (root.TryGetProperty("hosts", out var hosts) && hosts.ValueKind == JsonValueKind.Array)
            {
                foreach (var h in hosts.EnumerateArray())
                {
                    if (h.ValueKind != JsonValueKind.String) continue;
                    var s = h.GetString() ?? "";
                    var eq = s.IndexOf('=');
                    if (eq > 0) entry.Hosts[s[..eq].Trim()] = s[(eq + 1)..].Trim();
                }
            }

            // doh：订阅下发的 DoH 服务商列表（全局生效，对位 ApiConfig:935-943 + Hawk DOH_JSON）。
            // 与 TVBox 一致的两点：内容变了就把选择退回「关闭」（上次选的「阿里」在新列表里可能是别家）；
            // 订阅里没有 doh 字段就清空，退回内置三家。
            if (root.TryGetProperty("doh", out var dohEl) && dohEl.ValueKind == JsonValueKind.Array)
            {
                var json = dohEl.GetRawText();
                if (!string.Equals(json, Services.Doh.ConfigJson, StringComparison.Ordinal))
                {
                    var before = Services.Doh.Selector;
                    Services.Doh.ConfigJson = json;
                    Services.Doh.Selector = 0;
                    Log?.Invoke($"[doh] 订阅下发 {Services.Doh.Endpoints.Count} 个服务商，选择已重置"
                        + (before > 0 ? $"（原第 {before} 项）" : ""));
                }
            }
            else if (Services.Doh.ConfigJson.Length > 0)
            {
                Services.Doh.ConfigJson = "";
                Log?.Invoke("[doh] 订阅里没有 doh 字段，退回内置服务商列表");
            }

            // rules：订阅下发的播放规则。此前整包丢弃（模型有、无人采集），
            // 导致 M3u8 去广告的规则级清洗永远拿不到正则。结构照 TVBox ApiConfig L871-931 两形处理。
            if (root.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array)
            {
                foreach (var obj in rules.EnumerateArray())
                {
                    if (obj.ValueKind != JsonValueKind.Object) continue;

                    // 形① {host, rule:[…]} / {host, filter:[…]}
                    if (obj.TryGetProperty("host", out var hostEl) && hostEl.ValueKind == JsonValueKind.String)
                    {
                        var host = hostEl.GetString() ?? "";
                        if (host.Length > 0)
                        {
                            if (ReadStrings(obj, "rule") is { Count: > 0 } rg) entry.RuleGroups.Add((host, rg));
                            if (ReadStrings(obj, "filter") is { Count: > 0 } fg) entry.FilterGroups.Add((host, fg));
                        }
                    }

                    // 形② {hosts:[…], regex:[…]}：isAd 命中的进广告表，其余进嗅探规则组
                    if (obj.TryGetProperty("hosts", out var hostsArr) && hostsArr.ValueKind == JsonValueKind.Array &&
                        obj.TryGetProperty("regex", out var regexArr) && regexArr.ValueKind == JsonValueKind.Array)
                    {
                        var ads = new List<string>();
                        var plain = new List<string>();
                        foreach (var r in regexArr.EnumerateArray())
                        {
                            if (r.ValueKind != JsonValueKind.String) continue;
                            var s = r.GetString() ?? "";
                            if (s.Length == 0) continue;
                            if (M3u8Purifier.IsAd(s)) ads.Add(s);
                            else plain.Add(s);
                        }
                        foreach (var h in hostsArr.EnumerateArray())
                        {
                            if (h.ValueKind != JsonValueKind.String) continue;
                            var host = h.GetString() ?? "";
                            if (host.Length == 0) continue;
                            // addHostRule/addHostRegex 都是「空组不收」
                            if (plain.Count > 0) entry.RuleGroups.Add((host, plain));
                            if (ads.Count > 0) entry.AdRegex.Add((host, ads));
                        }
                    }
                }
            }

            Stores[subscriptionKey] = entry;
            Log?.Invoke($"[配置] 订阅「{subscriptionKey}」采集到 parses={entry.Parses.Count} hosts={entry.Hosts.Count} " +
                        $"嗅探规则组={entry.RuleGroups.Count} 过滤组={entry.FilterGroups.Count} 广告正则组={entry.AdRegex.Count}");
        }
        catch
        {
            // 留存失败不影响站点加载
        }
    }

    /// <summary>订阅删除/覆盖时清理</summary>
    /// <summary>
    /// 取一张订阅壁纸。<b>多订阅并存时壁纸不是全局唯一的</b>（TVBox 只有一份 active config，本仓没有这个概念），
    /// 所以返回第一个非空时<b>连订阅键一起交出去</b> —— UI 必须把它显示出来，
    /// 否则用户换了订阅顺序之后无法理解壁纸为什么变了一张。
    /// </summary>
    public static (string Key, string Url)? AnyWallpaper()
    {
        foreach (var (key, entry) in Stores)
            if (!string.IsNullOrWhiteSpace(entry.Wallpaper)) return (key, entry.Wallpaper!);
        return null;
    }

    public static void Remove(string subscriptionKey) => Stores.TryRemove(subscriptionKey, out _);

    /// <summary>域名映射（hosts "a.com=b.com"）：资源/接口地址镜像替换</summary>
    public static string ApplyHostMap(string subscriptionKey, string url)
    {
        if (url.Length == 0 || !Stores.TryGetValue(subscriptionKey, out var e) || e.Hosts.Count == 0)
            return url;
        foreach (var (from, to) in e.Hosts)
        {
            if (to == "ip" || to.Length == 0) continue;
            if (url.Contains(from, StringComparison.OrdinalIgnoreCase))
                return url.Replace(from, to, StringComparison.OrdinalIgnoreCase);
        }
        return url;
    }

    /// <summary>
    /// 按线路 flag 选解析规则（对照 SuperParse.configs：ext.flag 命中优先）。
    /// 命中列表里 type=1 的进 JSON 引擎、type=0 的进嗅探；顺序即配置顺序。
    /// </summary>
    public static List<ParseRule> RulesForFlag(string subscriptionKey, string flag)
    {
        var rules = Stores.TryGetValue(subscriptionKey, out var e) ? e.Parses : [];
        return rules.Where(r => r.Flags.Contains(flag, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>默认解析（useParse/vipParseFlags 场景；对照 ApiConfig.getDefaultParse）</summary>
    public static ParseRule? DefaultRule(string subscriptionKey)
    {
        var rules = Stores.TryGetValue(subscriptionKey, out var e) ? e.Parses : [];
        return rules.FirstOrDefault(r => r.IsDefault) ?? rules.FirstOrDefault();
    }

    /// <summary>按名字找规则（playUrl "parse:名字" 重定向语义）</summary>
    public static ParseRule? FindByName(string subscriptionKey, string name)
    {
        var rules = Stores.TryGetValue(subscriptionKey, out var e) ? e.Parses : [];
        return rules.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>该订阅是否带 parses 配置</summary>
    public static bool HasParses(string subscriptionKey) =>
        Stores.TryGetValue(subscriptionKey, out var e) && e.Parses.Count > 0;

    /// <summary>
    /// 按播放地址取广告正则（对位 <c>M3u8.getRegex(tsUrlPre)</c>：遍历 host 表，
    /// <b>首个</b>「地址里含该 host」的条目命中即返回，不再看后面的）。
    /// <para>本项目是多订阅并存，所以取的是所有订阅合并后的表；TVBox 单订阅时等价。</para>
    /// </summary>
    public static IReadOnlyList<string>? AdRegexForUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        foreach (var e in Stores.Values)
            foreach (var (host, regexes) in e.AdRegex)
                if (host.Length > 0 && url.Contains(host, StringComparison.OrdinalIgnoreCase))
                    return regexes;
        return null;
    }

    /// <summary>
    /// 某订阅下某 host 的嗅探「这是视频」规则组（对位 <c>VideoParseRuler.getHostRules(host)</c>；
    /// 一条 = 一组，<b>组内 AND、组间 OR</b>）。
    /// <para>返回 null = 该 host 没有专属规则，调用方要回落到 <c>"*"</c> 通配组 —— TVBox 的
    /// <c>checkIsVideoForParse</c> 正是这个顺序，别在采集端提前合并。</para>
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>>? RuleGroupsForHost(string subscriptionKey, string host) =>
        GroupsFor(subscriptionKey, host, forFilter: false);

    /// <summary>
    /// 嗅探过滤规则组（对位 <c>getHostFilters</c>）。
    /// <para>⚠ 与 <see cref="RuleGroupsForHost"/> 有两处刻意的不对称，都是 TVBox 原样：
    /// ① <b>没有</b> <c>"*"</c> 回落；② 命中语义是「这个 URL 不作为嗅探候选」，不是「是视频」。</para>
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>>? FilterGroupsForHost(string subscriptionKey, string host) =>
        GroupsFor(subscriptionKey, host, forFilter: true);

    static IReadOnlyList<IReadOnlyList<string>>? GroupsFor(string subscriptionKey, string host, bool forFilter)
    {
        if (string.IsNullOrEmpty(subscriptionKey) || string.IsNullOrEmpty(host)) return null;
        if (!Stores.TryGetValue(subscriptionKey, out var e)) return null;
        var src = forFilter ? e.FilterGroups : e.RuleGroups;
        List<IReadOnlyList<string>>? all = null;
        foreach (var (h, group) in src)
            if (h.Equals(host, StringComparison.OrdinalIgnoreCase))
                (all ??= []).Add(group);
        return all;
    }

    /// <summary>读一个字符串数组字段（缺字段/非数组返回 null，元素非字符串跳过）。</summary>
    static List<string>? ReadStrings(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var one in el.EnumerateArray())
            if (one.ValueKind == JsonValueKind.String && one.GetString() is { Length: > 0 } s)
                list.Add(s);
        return list.Count > 0 ? list : null;
    }
}
