namespace CatClawVideo.Maui.Services;

/// <summary>
/// 首页「主页」tab 显示哪种推荐内容（对位 TVBox <c>HawkConfig.HOME_REC</c> 三选一）：
/// 站点推荐（spider homeContent 的 list）/ 豆瓣热播 / 观看历史。
/// </summary>
public static class HomeRecPrefs
{
    /// <summary>偏好存储 key（对位 TVBox "home_rec"）。</summary>
    public const string StoreKey = "home_rec";

    /// <summary>站点推荐 = spider homeContent 的 list 字段（TVBox 默认值）。</summary>
    public const int SiteRecommend = 0;
    /// <summary>豆瓣热播（TVBox 首页同款豆瓣接口）。</summary>
    public const int DoubanHot = 1;
    /// <summary>观看历史（本地最近 20 条）。</summary>
    public const int History = 2;

    /// <summary>当前值变更时递增（HomeViewModel 据此判断行流缓存要不要重建）。</summary>
    public static int Version { get; private set; }

    public static int Load()
    {
        try { return Preferences.Default.Get(StoreKey, SiteRecommend); }
        catch { return SiteRecommend; }
    }

    public static void Save(int value)
    {
        try { Preferences.Default.Set(StoreKey, value); } catch { }
        Version++;
    }

    public static string Label(int v) => v switch
    {
        SiteRecommend => "站点推荐",
        DoubanHot => "豆瓣热播",
        History => "观看历史",
        _ => "站点推荐",
    };
}
