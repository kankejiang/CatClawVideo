using CatClawVideo.Core.Models;
using SQLite;

namespace CatClawVideo.Data;

/// <summary>订阅源记录（TVBox 单仓 / 影视仓多仓地址）</summary>
[Table("vod_subscriptions")]
public class VodSubscription
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>订阅显示名称</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>订阅地址（URL 或本地路径）</summary>
    public string SourceUrl { get; set; } = string.Empty;

    /// <summary>订阅类型：tvbox=单仓 TVBox 配置、multirepo=影视仓多仓</summary>
    public string Kind { get; set; } = "tvbox";

    /// <summary>是否启用</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>排序权重（越小越靠前）</summary>
    public int SortOrder { get; set; }

    /// <summary>最近一次成功刷新时间</summary>
    public DateTime? LastRefreshedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>播放历史记录</summary>
[Table("play_history")]
public class PlayHistoryEntry
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>影片标题（含集名，如「庆余年 第02集」）</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>来源站点名（订阅源功能上线后回填）</summary>
    public string SourceName { get; set; } = string.Empty;

    /// <summary>播放地址</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>封面（可空）</summary>
    public string? Cover { get; set; }

    /// <summary>上次观看位置（秒）</summary>
    public double PositionSeconds { get; set; }

    /// <summary>总时长（秒）</summary>
    public double DurationSeconds { get; set; }

    /// <summary>观看时间</summary>
    public DateTime WatchedAt { get; set; } = DateTime.Now;
}

/// <summary>收藏的影片</summary>
[Table("favorites")]
public class FavoriteEntry
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>来源站点 Key</summary>
    public string SourceKey { get; set; } = string.Empty;

    /// <summary>站点内影片 ID</summary>
    public string ItemId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Cover { get; set; }

    public string? Category { get; set; }

    public string? Year { get; set; }

    public string? Description { get; set; }

    /// <summary>更新说明（如「更新至12集」）</summary>
    public string? Remarks { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// 影视数据库（SQLite）：订阅源、播放历史、收藏。
/// 单连接单例，与宿主音乐库的 MusicDatabase 同模式。
/// </summary>
public class VideoDatabase
{
    private readonly SQLiteAsyncConnection _db;

    public VideoDatabase(string dbPath)
    {
        _db = new SQLiteAsyncConnection(dbPath,
            SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.SharedCache);
    }

    /// <summary>建表（幂等）</summary>
    public Task EnsureInitializedAsync() => Task.WhenAll(
        _db.CreateTableAsync<VodSubscription>(),
        _db.CreateTableAsync<PlayHistoryEntry>(),
        _db.CreateTableAsync<FavoriteEntry>());

    // ══════════════════════ 订阅源 ══════════════════════

    public Task<List<VodSubscription>> GetSubscriptionsAsync() =>
        _db.Table<VodSubscription>().Where(s => s.Enabled).OrderBy(s => s.SortOrder).ToListAsync();

    public Task<List<VodSubscription>> GetAllSubscriptionsAsync() =>
        _db.Table<VodSubscription>().OrderBy(s => s.SortOrder).ToListAsync();

    public Task<int> AddSubscriptionAsync(VodSubscription sub) => _db.InsertAsync(sub);

    public Task<int> UpdateSubscriptionAsync(VodSubscription sub) => _db.UpdateAsync(sub);

    public Task<int> DeleteSubscriptionAsync(VodSubscription sub) => _db.DeleteAsync(sub);

    /// <summary>按订阅地址查重</summary>
    public Task<VodSubscription?> FindSubscriptionAsync(string url) =>
        _db.Table<VodSubscription>().Where(s => s.SourceUrl == url).FirstOrDefaultAsync()!;

    // ══════════════════════ 播放历史 ══════════════════════

    public Task<List<PlayHistoryEntry>> GetRecentHistoryAsync(int limit = 50) =>
        _db.Table<PlayHistoryEntry>().OrderByDescending(h => h.WatchedAt).Take(limit).ToListAsync();

    /// <summary>记录/续看一次播放：同标题的旧记录合并（更新位置与时间），保留最近 N 条</summary>
    public async Task UpsertHistoryAsync(PlayHistoryEntry entry)
    {
        var existing = await _db.Table<PlayHistoryEntry>()
            .Where(h => h.Title == entry.Title)
            .OrderByDescending(h => h.WatchedAt)
            .FirstOrDefaultAsync();
        if (existing != null)
        {
            entry.Id = existing.Id;
            await _db.UpdateAsync(entry);
        }
        else
        {
            await _db.InsertAsync(entry);
            // 控制历史总量：超出 500 条删最旧
            var total = await _db.Table<PlayHistoryEntry>().CountAsync();
            if (total > 500)
            {
                var oldest = await _db.Table<PlayHistoryEntry>()
                    .OrderBy(h => h.WatchedAt).Take(total - 500).ToListAsync();
                foreach (var o in oldest) await _db.DeleteAsync(o);
            }
        }
    }

    public Task<int> ClearHistoryAsync() => _db.DeleteAllAsync<PlayHistoryEntry>();

    // ══════════════════════ 收藏 ══════════════════════

    public Task<List<FavoriteEntry>> GetFavoritesAsync() =>
        _db.Table<FavoriteEntry>().OrderByDescending(f => f.AddedAt).ToListAsync();

    public Task<FavoriteEntry?> FindFavoriteAsync(string sourceKey, string itemId) =>
        _db.Table<FavoriteEntry>()
            .Where(f => f.SourceKey == sourceKey && f.ItemId == itemId)
            .FirstOrDefaultAsync()!;

    public Task<int> AddFavoriteAsync(VodItem item)
    {
        var fav = new FavoriteEntry
        {
            SourceKey = item.SourceKey,
            ItemId = item.Id,
            Title = item.Title,
            Cover = item.Cover,
            Category = item.Category,
            Year = item.Year,
            Description = item.Description,
            Remarks = item.Remarks,
        };
        return _db.InsertAsync(fav);
    }

    public Task<int> RemoveFavoriteAsync(FavoriteEntry fav) => _db.DeleteAsync(fav);

    /// <summary>收藏影片转领域模型（点击后进入详情/播放）</summary>
    public static VodItem ToVodItem(FavoriteEntry fav) => new()
    {
        SourceKey = fav.SourceKey,
        Id = fav.ItemId,
        Title = fav.Title,
        Cover = fav.Cover,
        Category = fav.Category,
        Year = fav.Year,
        Description = fav.Description,
        Remarks = fav.Remarks,
    };
}
