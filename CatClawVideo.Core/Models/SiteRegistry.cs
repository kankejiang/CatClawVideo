namespace CatClawVideo.Core.Models;

/// <summary>
/// 已启用影片源的内存仓库（进程级单例）。
/// 项目不内置任何源：SourceConfigPage 解析订阅成功后 Replace 写入，
/// HomeViewModel / SearchPage 等从这里取可播站点。
/// </summary>
public static class SiteRegistry
{
    /// <summary>spider 运行时可用性（由宿主启动时按平台注入；决定 type=3 站点是否可播）</summary>
    public static bool JsSpiderAvailable { get; set; }
    public static bool JarSpiderAvailable { get; set; }

    /// <summary>当前全部站点（含不可播的，UI 开关控制是否参与聚合）</summary>
    public static IReadOnlyList<VodSiteInfo> Sites => _sites;
    private static readonly List<VodSiteInfo> _sites = [];

    /// <summary>站点集合变化（订阅增删后通知首页等刷新）</summary>
    public static event Action? Changed;

    /// <summary>可播站点：type=1 直连源 + 运行时已就绪的 spider 源</summary>
    public static IEnumerable<VodSiteInfo> Playable =>
        _sites.Where(s => s.Playable
                          || (s.SpiderKind == VodSpiderKind.Script && JsSpiderAvailable)
                          || (s.SpiderKind == VodSpiderKind.Jar && JarSpiderAvailable));

    /// <summary>用订阅解析结果整体替换站点集合</summary>
    public static void Replace(IEnumerable<VodSiteInfo> sites)
    {
        _sites.Clear();
        _sites.AddRange(sites);
        Changed?.Invoke();
    }

    /// <summary>按 Key 找站点</summary>
    public static VodSiteInfo? Find(string key) => _sites.FirstOrDefault(s => s.Key == key);
}
