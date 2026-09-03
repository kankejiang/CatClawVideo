using CatClawVideo.Core.Models;

namespace CatClawVideo.Core.Interfaces;

/// <summary>
/// 影视源提供者抽象：把具体源格式（TVBox json/xml/spider、影视仓多仓）适配为统一的领域模型。
/// 基础架构阶段仅定义契约；订阅源功能开发时提供 MacCMS(json/xml) 等实现并注册到 DI。
/// </summary>
public interface IVodSourceProvider
{
    /// <summary>提供者唯一 ID（如 "maccms-json"）</summary>
    string Id { get; }

    /// <summary>显示名称（如「苹果CMS JSON 源」）</summary>
    public string Name { get; }

    /// <summary>判断是否能处理该站点（按站点 Type / Api 前缀识别）</summary>
    bool CanHandle(VodSiteInfo site);

    /// <summary>获取站点分类列表</summary>
    Task<List<VodCategory>> GetCategoriesAsync(VodSiteInfo site, CancellationToken ct = default);

    /// <summary>按分类分页获取影片列表（page 从 1 开始）</summary>
    Task<List<VodItem>> GetItemsAsync(VodSiteInfo site, VodCategory category, int page = 1, CancellationToken ct = default);

    /// <summary>获取影片详情的播放线路与剧集</summary>
    Task<List<VodPlaySource>> GetPlaySourcesAsync(VodSiteInfo site, VodItem item, CancellationToken ct = default);

    /// <summary>把剧集地址解析为可播放直链（含嗅探/换源逻辑的入口）</summary>
    Task<PlayRequest> ResolvePlayUrlAsync(VodSiteInfo site, VodEpisode episode, CancellationToken ct = default);

    /// <summary>站点内搜索</summary>
    Task<List<VodItem>> SearchAsync(VodSiteInfo site, string keyword, CancellationToken ct = default);
}

/// <summary>
/// 订阅源管理器抽象：负责拉取/解析 TVBox 与影视仓多仓订阅地址，产出统一站点列表。
/// 基础架构阶段仅定义契约，实现随订阅源功能开发落地。
/// </summary>
public interface ISubscriptionManager
{
    /// <summary>从订阅地址加载站点列表（自动识别单仓 TVBox 配置或影视仓多仓格式）</summary>
    Task<List<VodSiteInfo>> LoadSubscriptionAsync(string subscriptionUrl, CancellationToken ct = default);

    /// <summary>解析原始 JSON 文本为站点列表（用于导入本地配置文件）</summary>
    Task<List<VodSiteInfo>> ParseConfigTextAsync(string jsonText, string subscriptionName, CancellationToken ct = default);
}
