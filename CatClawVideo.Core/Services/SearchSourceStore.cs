using System.Text.Json;

namespace CatClawVideo.Core.Services;

/// <summary>
/// 「搜哪些站点」的持久化（对位 TVBox <c>HawkConfig.SOURCES_FOR_SEARCH</c>）：**按订阅分别记**，
/// 换一个订阅就是另一套勾选，不会把上个订阅的选择带过来。
///
/// <para><b>存黑名单而不是白名单</b>（界面上仍然是「勾了才搜」）：TVBox 存勾选列表，
/// 于是订阅里新出现的站点默认搜不到，用户会遇到「昨天还能搜到的源今天没了」；
/// 存排除集合则新站点自然纳入。两者在任意时刻的用户可见效果相同。</para>
///
/// <para>一次 <see cref="SetSearchable"/> 就落一次盘：弹窗「确定」走 <see cref="SetAll"/>，
/// 只写一次文件。</para>
/// </summary>
public static class SearchSourceStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
    private static readonly object Gate = new();

    /// <summary>订阅名 → 被排除的站点 key。</summary>
    private static readonly Dictionary<string, List<string>> Excluded = Load();

    /// <summary>缓存文件路径（诊断与台架用）。</summary>
    public static string FilePath => AppPaths.Of("search-sources.json");

    /// <summary>没有排除记录 = 全部可搜（默认不限制任何站点）。</summary>
    public static bool IsSearchable(string? subscription, string siteKey)
    {
        if (string.IsNullOrEmpty(subscription)) return true;
        lock (Gate)
            return !Excluded.TryGetValue(subscription, out var keys) || !keys.Contains(siteKey);
    }

    /// <summary>当前订阅被排除的站点数（chip 上标「已排除 N」）。</summary>
    public static int ExcludedCount(string? subscription)
    {
        if (string.IsNullOrEmpty(subscription)) return 0;
        lock (Gate)
            return Excluded.TryGetValue(subscription, out var keys) ? keys.Count : 0;
    }

    public static void SetSearchable(string? subscription, string siteKey, bool on)
    {
        if (string.IsNullOrEmpty(subscription)) return;
        lock (Gate)
        {
            var keys = GetOrAdd(subscription);
            if (on) keys.Remove(siteKey);
            else if (!keys.Contains(siteKey)) keys.Add(siteKey);
            if (keys.Count == 0) Excluded.Remove(subscription);
            SaveNoLock();
        }
    }

    /// <summary>
    /// 整批写入（弹窗「确定」）。<paramref name="sites"/> 是**当时呈现给用户的全部候选站点**，
    /// 所以本次未勾的都进黑名单、勾了的都放行 —— 上一轮遗留但已不在这个列表里的 key 顺带清掉。
    /// </summary>
    public static void SetAll(string? subscription, IEnumerable<(string Key, bool On)> sites)
    {
        if (string.IsNullOrEmpty(subscription)) return;
        var pairs = sites.ToList();
        lock (Gate)
        {
            var off = pairs.Where(p => !p.On).Select(p => p.Key).ToList();
            if (off.Count == 0) Excluded.Remove(subscription);
            else Excluded[subscription] = off;
            SaveNoLock();
        }
    }

    /// <summary>订阅被删除时清掉它的勾选记录，免得文件里堆死数据。</summary>
    public static void Forget(string? subscription)
    {
        if (string.IsNullOrEmpty(subscription)) return;
        lock (Gate)
        {
            if (Excluded.Remove(subscription)) SaveNoLock();
        }
    }

    private static List<string> GetOrAdd(string subscription) =>
        Excluded.TryGetValue(subscription, out var keys) ? keys : Excluded[subscription] = [];

    private static Dictionary<string, List<string>> Load()
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        try
        {
            var path = AppPaths.Of("search-sources.json");
            if (!File.Exists(path)) return result;
            var raw = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(
                File.ReadAllText(path));
            if (raw is null) return result;
            foreach (var kv in raw)
                if (kv.Value is { Count: > 0 }) result[kv.Key] = kv.Value;
        }
        catch { }
        return result;
    }

    /// <summary>落盘失败静默：这只是个偏好，不该让搜索报错。</summary>
    private static void SaveNoLock()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(Excluded, Options)); }
        catch { }
    }
}
