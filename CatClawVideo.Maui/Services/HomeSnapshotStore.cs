using System.Text.Json;
using System.Text.Json.Serialization;

namespace CatClawVideo.Maui.Services;

/// <summary>缓存里的分类（只留界面要用的：ID + 名称）。</summary>
public sealed record SnapCategory(string Id, string Name);

/// <summary>缓存里的影片条目（只留列表渲染要用的：ID/来源/片名/封面）。</summary>
public sealed record SnapItem(string Id, string SourceKey, string Title, string? Cover);

/// <summary>首页快照：上次成功的站点 + 分类 + 某个分类的第一页。</summary>
public sealed class HomeSnapshot
{
    public string SiteKey { get; set; } = string.Empty;
    public string SiteName { get; set; } = string.Empty;
    public string? CategoryId { get; set; }
    public DateTime SavedUtc { get; set; }
    public List<SnapCategory> Categories { get; set; } = [];
    public List<SnapItem> Items { get; set; } = [];
}

/// <summary>
/// 首页首屏缓存的**落盘**（2026-10-03）。位置：<c>%LOCALAPPDATA%\CatClawVideo.debug\home-snapshot.json</c>
/// —— 数据盘，不是内存：进程一退内存就没了，而「每次启动都重新加载首页」正是这个原因。
/// （订阅站点列表本来就有盘上缓存：SiteCache → 日志「站点缓存命中 110 个」。）
///
/// <para>为什么必须落盘：实测站点存活率很差（110 站只有 ~8 个能出分类），现拉首屏要 35~38s；
/// 有快照则启动即上屏、后台再刷新。写坏了大不了下次当没有，绝不影响启动与正确性。</para>
/// </summary>
public static class HomeSnapshotStore
{
    private static string FilePath => CatClawVideo.Core.AppPaths.LocalSub("home-snapshot.json");

    private static readonly JsonSerializerOptions Opt = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>读快照；不存在/过期/坏了都返回 null（调用方按「没有缓存」处理）。</summary>
    public static HomeSnapshot? Load(int maxAgeDays = 14)
    {
        try
        {
            var p = FilePath;
            if (!File.Exists(p)) return null;
            var snap = JsonSerializer.Deserialize<HomeSnapshot>(File.ReadAllText(p), Opt);
            if (snap is null || snap.Categories.Count == 0) return null;
            if ((DateTime.UtcNow - snap.SavedUtc).TotalDays > maxAgeDays)
            {
                DiagLog.Write($"[home-cache] 快照过期（{snap.SavedUtc:MM-dd HH:mm}），忽略");
                return null;
            }
            return snap;
        }
        catch (Exception ex)
        {
            DiagLog.Write($"[home-cache] 读取失败（当没有缓存）：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>原子写（临时文件 + Move），失败只记日志。</summary>
    public static void Save(HomeSnapshot snap)
    {
        try
        {
            snap.SavedUtc = DateTime.UtcNow;
            var p = FilePath;
            if (snap.Items.Count == 0)
            {
                // 分类刚就绪就先存了一次（条目还没有）：站点与分类没变时保留盘上原有条目，
                // 免得把上次的首屏海报清空。
                var prev = Load();
                if (prev is not null && prev.SiteKey == snap.SiteKey && prev.CategoryId == snap.CategoryId)
                    snap.Items = prev.Items;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            var tmp = p + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snap, Opt));
            File.Move(tmp, p, overwrite: true);
            DiagLog.Write($"[home-cache] 已落盘：{snap.Categories.Count} 分类 / {snap.Items.Count} 部（{snap.SiteName}）");
        }
        catch (Exception ex)
        {
            DiagLog.Write($"[home-cache] 写入失败（忽略）：{ex.GetType().Name}: {ex.Message}");
        }
    }
}
