using CatClawVideo.Core.Models;

namespace CatClawVideo.Core.Interfaces;

/// <summary>
/// TVBox 爬虫运行时抽象：承载 spider jar（Java/dex）与 drpy 脚本（JS）两类后端。
/// 协议即 TVBox Spider 接口——所有方法返回 TVBox 协议 JSON 字符串，
/// 由 <see cref="CatClawVideo.Core.Providers.SpiderVodProvider"/> 解析为统一领域模型。
/// </summary>
public interface ISpiderRuntime
{
    /// <summary>运行时标识（android-dex / jint-drpy）</summary>
    string Id { get; }

    /// <summary>当前平台是否可用（决定 spider 站点是否可播）</summary>
    bool IsSupported { get; }

    /// <summary>
    /// 首页分类（协议 homeContent）。
    /// 返回 {"class":[{"type_id":"1","type_name":"电影"}], "filters":{...}}
    /// </summary>
    Task<string> HomeContentAsync(VodSiteInfo site, CancellationToken ct = default);

    /// <summary>
    /// 分类影片列表（协议 categoryContent）。
    /// 返回 {"list":[{vod_id,vod_name,vod_pic,vod_remarks}], "page":1, "pagecount":99}
    /// </summary>
    Task<string> CategoryContentAsync(VodSiteInfo site, string tid, string pg, CancellationToken ct = default);

    /// <summary>
    /// 影片详情（协议 detailContent）。
    /// 返回 {"list":[{vod_id,vod_name,vod_play_from,vod_play_url,...}]}，
    /// vod_play_url 格式与 MacCMS 一致：线路1$集1#集2$$$线路2$...
    /// </summary>
    Task<string> DetailContentAsync(VodSiteInfo site, string id, CancellationToken ct = default);

    /// <summary>搜索（协议 searchContent）。返回结构同分类列表。</summary>
    Task<string> SearchContentAsync(VodSiteInfo site, string keyword, string pg, CancellationToken ct = default);

    /// <summary>
    /// 播放地址（协议 playerContent）。
    /// 返回 {"parse":0/1, "playUrl":"", "url":"...", "header":{...}}；
    /// parse=0 直链，parse=1 需嗅探网页。
    /// </summary>
    Task<string> PlayerContentAsync(VodSiteInfo site, string flag, string id, CancellationToken ct = default);
}

/// <summary>
/// 支持宿主本地 <c>/proxy</c> 回调的运行时（TVBox <c>ApiConfig.proxyLocal</c> 语义）。
/// js2Proxy 拼出的 <c>http://127.0.0.1:port/proxy?...</c> 由 <see cref="Services.SpiderProxyServer"/>
/// 接收后回调本方法，运行时把它转给 JS/Java 爬虫的 <c>proxy()</c> 拿内容。
/// </summary>
public interface ISpiderProxyRuntime
{
    /// <summary>
    /// 处理一条本地代理请求。
    /// 返回 null = 该运行时无法处理（调用方回 502）。
    /// </summary>
    Task<(int Status, string Mime, byte[]? Body)?> ProxyAsync(
        IReadOnlyDictionary<string, string> query, CancellationToken ct = default);
}
