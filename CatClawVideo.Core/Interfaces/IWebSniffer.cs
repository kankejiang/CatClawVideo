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
    /// 超时/无直链 → 抛 NotSupportedException。
    /// </summary>
    Task<PlayRequest> SniffAsync(string pageUrl, IReadOnlyDictionary<string, string>? extraHeaders, CancellationToken ct = default);
}
