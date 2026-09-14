using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;

namespace CatClawVideo.Core.Providers;

/// <summary>嗅探器占位：平台未注入真实实现时使用，调用即抛出明确异常（与 NullSpiderRuntime 同惯例）。</summary>
public class NullWebSniffer : IWebSniffer
{
    public Task<PlayRequest> SniffAsync(string pageUrl, IReadOnlyDictionary<string, string>? extraHeaders, CancellationToken ct = default) =>
        Task.FromException<PlayRequest>(new NotSupportedException("该集需要网页解析（parse=1），当前平台未启用嗅探引擎，请换线路或换源。"));
}
