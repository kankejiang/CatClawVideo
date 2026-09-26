using CatClawVideo.Core.Models;

namespace CatClawVideo.Core.Interfaces;

/// <summary>
/// 网页嗅探抽象（Core 不引 MAUI，WebView 由平台实现注入）。
/// 对照 TVBox OSC PlayFragment.loadWebView + SysWebClient.shouldInterceptRequest：
/// 加载页面 → 拦截全部子请求 → 视频正则命中即返回直链。
/// </summary>
public interface IWebSniffer
{
    /// <summary>
    /// 加载 webUrl 并嗅探视频直链。
    /// extraHeaders：请求页面的附加头（UA/Referer，来自 spider playerContent 或 parses ext.header）。
    /// subscriptionKey：订阅名，用来取该订阅下发的 <c>rules[].host/rule/filter</c> 嗅探规则
    /// （对位 TVBox <c>VideoParseRuler</c> 的 per-host 表）；null = 只用通用正则。
    /// 超时/无直链 → 抛 NotSupportedException。
    /// </summary>
    Task<PlayRequest> SniffAsync(string pageUrl, IReadOnlyDictionary<string, string>? extraHeaders,
        string? subscriptionKey, CancellationToken ct = default);
}
