using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;

namespace CatClawVideo.Core.Providers;

/// <summary>
/// TVBox 播放解析决策树（复刻 PlayFragment L1198-1260 / initParse L2049 / doParse L2308 语义）：
///
/// spider playerContent 返回 {parse, jx, playUrl, url, header}：
///   parse=0 且 jx=0 → 直链播放（playUrl 前缀拼接）。
///   parse=1 或 jx=1 → 解析：
///     playUrl "json:接口"      → JSON 解析引擎（type=1 语义）。
///     playUrl "parse:名字"     → parses 里按名查找（type 决定嗅探或 JSON）。
///     playUrl 非空（普通前缀） → 直接嗅探 playUrl + url 页面。
///     playUrl 空 + parses 有该线路 flag 的规则 → 按规则（type1 并发 JSON / type0 嗅探）。
///     playUrl 空 + 无规则      → 默认解析（defaultParse）；连 parses 都没有 → 直接嗅探 url。
/// </summary>
public static class TvBoxPlayPipeline
{
    public static async Task<PlayRequest> ResolveAsync(
        VodSiteInfo site, PlayRequest play, string flag, IWebSniffer? sniffer, CancellationToken ct)
    {
        // 直链：原样返回（headers 已在 ParsePlayRequest 里带上）
        if (!play.NeedsSniff)
            return play;

        var subKey = site.SubscriptionName;
        var pageUrl = play.Url;
        var extraHeaders = play.Headers as IReadOnlyDictionary<string, string>;

        // ── initParse：决定 ParseBean ──
        // 注：TVBox 里 playUrl 是「前缀模板」、url 是「待解析页」；猫爪这层此前把两者混在 play.Url。
        // spider 协议下 playerContent 返回的 url 已含前缀拼接（ParsePlayRequest 处理 playUrl+url），
        // 所以这里的 playUrl 语义只剩 "json:" / "parse:" 两种前缀，需要从 url 里剥出来。
        string url = pageUrl;

        TvBoxConfigStore.ParseRule? rule = null;
        if (url.StartsWith("json:", StringComparison.OrdinalIgnoreCase))
        {
            rule = new TvBoxConfigStore.ParseRule("json:inline", 1, url[5..], null);
            url = "";
        }
        else if (url.StartsWith("parse:", StringComparison.OrdinalIgnoreCase))
        {
            var name = url[6..];
            rule = TvBoxConfigStore.FindByName(subKey, name);
            url = "";
        }

        if (rule is null)
        {
            //线路 flag 匹配 parses（SuperParse.configs 语义）
            var flagRules = TvBoxConfigStore.RulesForFlag(subKey, flag);
            rule = flagRules.FirstOrDefault(r => r.Type is 1 or 2)
                ?? flagRules.FirstOrDefault(r => r.Type == 0)
                ?? TvBoxConfigStore.DefaultRule(subKey);
        }

        // ── doParse ──
        // type=1（或 2/4 的 JSON 部分）：并发 JSON 解析
        if (rule is not null && rule.Type is 1 or 2 or 4 && rule.Url.Length > 0)
        {
            var mixUrl = TvBoxParseEngine.MixUrl(rule.Url, rule.ExtJson);
            var result = await TvBoxParseEngine.ParseJsonParallelAsync(
                [(rule.Name, mixUrl)], url.Length > 0 ? url : pageUrl,
                rule.Headers.Count > 0 ? rule.Headers : extraHeaders, ct);
            if (result is not null)
            {
                if (result.ParseTail)
                {
                    // 二段式：JSON 接口给出中间页 → 继续嗅探
                    return await SniffOrThrowAsync(sniffer, result.Url, MergeHeaders(extraHeaders, result.Headers), subKey, ct);
                }
                return new PlayRequest
                {
                    Title = play.Title,
                    Url = result.Url,
                    Headers = result.Headers.Count > 0 ? result.Headers : null,
                };
            }
            // JSON 解析失败 → 嗅探兜底（TVBox 聚合语义）
        }

        // type=0 嗅探：rule.Url 为前缀（可空）
        if (rule is not null && rule.Type == 0)
        {
            var target = rule.Url + url;
            if (target.Length == 0) target = pageUrl;
            return await SniffOrThrowAsync(sniffer, target, MergeHeaders(extraHeaders, rule.Headers), subKey, ct);
        }

        // 无规则 / JSON 失败兜底：直接嗅探待解析页
        return await SniffOrThrowAsync(sniffer, url.Length > 0 ? url : pageUrl, extraHeaders, subKey, ct);
    }

    private static async Task<PlayRequest> SniffOrThrowAsync(
        IWebSniffer? sniffer, string pageUrl, IReadOnlyDictionary<string, string>? headers,
        string? subscriptionKey, CancellationToken ct)
    {
        if (sniffer is null)
            throw new NotSupportedException(
                "该集需要网页解析（parse=1），当前平台未启用嗅探引擎，请换线路或换源。");
        var req = await sniffer.SniffAsync(pageUrl, headers, subscriptionKey, ct);
        req.Title = string.Empty;   // 调用方只取 url/headers
        return req;
    }

    private static IReadOnlyDictionary<string, string>? MergeHeaders(
        IReadOnlyDictionary<string, string>? a, IReadOnlyDictionary<string, string>? b)
    {
        if (a is null || a.Count == 0) return b;
        if (b is null || b.Count == 0) return a;
        var merged = new Dictionary<string, string>(a, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in b) merged[kv.Key] = kv.Value;
        return merged;
    }
}
