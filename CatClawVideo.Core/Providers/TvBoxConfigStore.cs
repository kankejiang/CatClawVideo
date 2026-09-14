using System.Collections.Concurrent;
using System.Text.Json;

namespace CatClawVideo.Core.Providers;

/// <summary>
/// TVBox 订阅「解析」配置仓：订阅解析时留存 parses/hosts（此前这两个键被整包丢弃），
/// 播放时按线路 flag 匹配 ParseRule 决策：JSON 解析引擎（type=1）/ WebView 嗅探（type=0）/ playUrl 前缀。
/// 语义对照 TVBox OSC：ParseBean.java / SuperParse.java / PlayFragment.initParse(L2049)。
/// </summary>
public static class TvBoxConfigStore
{
    private static readonly ConcurrentDictionary<string, StoreEntry> Stores = new();

    private sealed class StoreEntry
    {
        public List<ParseRule> Parses { get; init; } = [];
        public Dictionary<string, string> Hosts { get; init; } = new(StringComparer.OrdinalIgnoreCase);
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

            Stores[subscriptionKey] = entry;
        }
        catch
        {
            // 留存失败不影响站点加载
        }
    }

    /// <summary>订阅删除/覆盖时清理</summary>
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
}
