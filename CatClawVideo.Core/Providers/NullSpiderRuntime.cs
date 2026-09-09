using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;

namespace CatClawVideo.Core.Providers;

/// <summary>占位爬虫运行时：平台不可用（如 Windows 上的 jar/dex）时抛出明确异常</summary>
public class NullSpiderRuntime : ISpiderRuntime
{
    public string Id { get; }
    public bool IsSupported => false;

    public NullSpiderRuntime(string id = "null") => Id = id;

    private static Exception Unsupported() =>
        new NotSupportedException("该爬虫源依赖的运行时在当前平台不可用");

    public Task<string> HomeContentAsync(VodSiteInfo site, CancellationToken ct = default) => throw Unsupported();
    public Task<string> CategoryContentAsync(VodSiteInfo site, string tid, string pg, CancellationToken ct = default) => throw Unsupported();
    public Task<string> DetailContentAsync(VodSiteInfo site, string id, CancellationToken ct = default) => throw Unsupported();
    public Task<string> SearchContentAsync(VodSiteInfo site, string keyword, string pg, CancellationToken ct = default) => throw Unsupported();
    public Task<string> PlayerContentAsync(VodSiteInfo site, string flag, string id, CancellationToken ct = default) => throw Unsupported();
}
