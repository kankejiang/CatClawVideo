using System.Text;
using System.Text.Json;
using CatClawVideo.Core.Logging;

namespace CatClawVideo.Core.Network;

/// <summary>WebDAV 连接配置的持久化偏好（存 <c>AppPaths/Sub("webdav")/profiles.json</c>）。</summary>
public class WebDavPrefs
{
    /// <summary>连接配置列表（按 LastUsedAt 新在前）</summary>
    public List<ConnectionProfile> Profiles { get; set; } = new();

    /// <summary>上次使用的连接 Id（进入网络媒体页时自动恢复，无则 -1）</summary>
    public int LastProfileId { get; set; } = -1;
}

/// <summary>
/// WebDAV 连接配置仓库：加载 / 保存 / 增删改。
/// 连接条目量小（个人 NAS 一般就一两个），用 JSON 而非库表 —— 与直播源偏好同款做法。
/// </summary>
public class WebDavProfileStore
{
    private static string PrefsPath => AppPaths.Sub("webdav", "profiles.json");

    /// <summary>持久化偏好（页面直接改后调用 <see cref="Save"/>）。</summary>
    public WebDavPrefs Prefs { get; private set; }

    public WebDavProfileStore()
    {
        Prefs = Load();
    }

    /// <summary>分配下一个可用 Id（从 1 起，取现有最大 +1）。</summary>
    public int NextId() => Prefs.Profiles.Count == 0 ? 1 : Prefs.Profiles.Max(p => p.Id) + 1;

    /// <summary>按 Id 取连接（无则 null）。</summary>
    public ConnectionProfile? Find(int id) => Prefs.Profiles.FirstOrDefault(p => p.Id == id);

    /// <summary>新增或更新连接（按 Id 匹配），并按 LastUsedAt 新在前排序。</summary>
    public void Upsert(ConnectionProfile profile)
    {
        var idx = Prefs.Profiles.FindIndex(p => p.Id == profile.Id);
        if (idx >= 0) Prefs.Profiles[idx] = profile;
        else Prefs.Profiles.Add(profile);
        Sort();
        Save();
    }

    /// <summary>删除连接。</summary>
    public void Remove(int id)
    {
        Prefs.Profiles.RemoveAll(p => p.Id == id);
        if (Prefs.LastProfileId == id) Prefs.LastProfileId = -1;
        Save();
    }

    /// <summary>标记某连接为「正在使用」（提到列表最前 + 记为 LastProfileId）。</summary>
    public void MarkUsed(ConnectionProfile profile)
    {
        profile.LastUsedAt = DateTime.Now;
        Prefs.LastProfileId = profile.Id;
        Sort();
        Save();
    }

    private void Sort() =>
        Prefs.Profiles = Prefs.Profiles.OrderByDescending(p => p.LastUsedAt).ToList();

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PrefsPath)!);
            File.WriteAllText(PrefsPath, JsonSerializer.Serialize(Prefs, JsonOptions), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log.Warn("WebDavProfileStore", $"[WebDAV] 偏好保存失败: {ex.Message}");
        }
    }

    private static WebDavPrefs Load()
    {
        try
        {
            if (File.Exists(PrefsPath))
            {
                var json = File.ReadAllText(PrefsPath, Encoding.UTF8);
                return JsonSerializer.Deserialize<WebDavPrefs>(json, JsonOptions) ?? new WebDavPrefs();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("WebDavProfileStore", $"[WebDAV] 偏好加载失败: {ex.Message}");
        }
        return new WebDavPrefs();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };
}
