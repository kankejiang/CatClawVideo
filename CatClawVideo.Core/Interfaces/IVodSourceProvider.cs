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

    /// <summary>
    /// 按分类分页获取影片列表（page 从 1 开始）。
    /// <paramref name="filter"/> = 分类筛选条件（键取 <c>VodFilterGroup.Key</c>），null/空 = 不筛选。
    /// </summary>
    Task<List<VodItem>> GetItemsAsync(VodSiteInfo site, VodCategory category, int page = 1,
        IReadOnlyDictionary<string, string>? filter = null, CancellationToken ct = default);

    /// <summary>获取影片详情的播放线路与剧集</summary>
    Task<List<VodPlaySource>> GetPlaySourcesAsync(VodSiteInfo site, VodItem item, CancellationToken ct = default);

    /// <summary>把剧集地址解析为可播放直链（含嗅探/换源逻辑的入口）</summary>
    Task<PlayRequest> ResolvePlayUrlAsync(VodSiteInfo site, VodEpisode episode, CancellationToken ct = default);

    /// <summary>站点内搜索</summary>
    Task<List<VodItem>> SearchAsync(VodSiteInfo site, string keyword, CancellationToken ct = default);
}

/// <summary>
/// 「渐进式」播放线路加载（可选能力，仅聚合 Provider 实现）。
///
/// <para><b>为什么需要</b>：磁力站的详情页要把「一条打包磁力」展开成「种子内每个文件 = 一集」，
/// 而引擎侧是**单会话串行**探测 —— 实测一条磁力 0.2~3.1s，一次详情页最多 8 条打包磁力，
/// 全展开要 8~15s。这段时间用户盯着空白选集栏，体感就是「磁力片特别慢」。</para>
///
/// <para><b>做法</b>：先 yield 一次**未展开**的原始线路（站点本来给的集名，立即可上屏），
/// 之后每完成一条磁力的展开就再 yield 一次完整列表，界面按序刷新。用户在几百毫秒内
/// 就能看到选集内容，后续只是集名从打包名细化为真实文件名。</para>
/// </summary>
public interface IProgressiveVodSourceProvider
{
    /// <summary>流式产出线路列表：首个元素为未展开版本，随后每次展开有进展再产出。</summary>
    IAsyncEnumerable<List<VodPlaySource>> StreamPlaySourcesAsync(
        VodSiteInfo site, VodItem item, CancellationToken ct = default);
}

/// <summary>
/// 支持「操作入口」卡片的 Provider（可选能力，仅 spider 系 Provider 实现）。
/// <para>卡片 <see cref="VodItem.Action"/> 非空时点击不是打开详情，而是把那段 JSON 交给爬虫的
/// <c>action(String)</c>（TVBox <c>SourceViewModel.doAction</c> 语义）——Guard 系网盘源的
/// 「登入自己网盘」原生对话框与扫码二维码只有这条路能弹出来。</para>
/// </summary>
public interface IActionVodSourceProvider
{
    /// <summary>执行卡片 action，返回爬虫给的协议 JSON；站点/爬虫不支持时返回 null。</summary>
    Task<string?> DoActionAsync(VodSiteInfo site, VodItem item, CancellationToken ct = default);
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

    /// <summary>
    /// 探测这个地址是不是「影视仓多仓」（顶层只有 urls）。是就返回线路清单，不是返回空表。
    /// <para>UI 用它决定要不要让用户先选一条线路，再把选中的线路号编码回地址（见 <c>#line=N</c>）。</para>
    /// </summary>
    Task<IReadOnlyList<SubscriptionLine>> ProbeLinesAsync(string subscriptionUrl, CancellationToken ct = default);

    /// <summary>
    /// 依次加载<b>全部</b>订阅并合并站点表（多订阅并存）。
    /// <para>此前启动恢复是「第一个成功即整体 Replace」，第二个及以后的订阅永远不生效
    /// （2026-09-26 用户实测：加了英格里希嗷呜后仍只见饭太硬的站）。合并规则：按站点
    /// <c>Key</c> 去重，<b>先到优先</b>（订阅列表顺序 = 优先级）；单个订阅失败不拖垮其余。</para>
    /// </summary>
    Task<List<VodSiteInfo>> LoadAllSubscriptionsAsync(
        IEnumerable<SubscriptionRef> subscriptions, CancellationToken ct = default)
        => Task.FromResult(new List<VodSiteInfo>());
}

/// <summary>订阅引用（名称 + 地址）：Core 侧不依赖 Data 层的订阅实体，调用方转换。</summary>
public sealed record SubscriptionRef(string Name, string SourceUrl);

/// <summary>多仓订阅里的一条线路（影视仓 <c>urls[]</c> 的 name/url）。</summary>
public sealed record SubscriptionLine(string Name, string Url);
