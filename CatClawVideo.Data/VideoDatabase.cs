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

    // ── 来源定位（非空时点历史卡可跳回观看页详情续看；网页直链/本地播放为空 → 直接播放） ──

    /// <summary>来源站点 Key（VodSiteInfo.Key）</summary>
    public string SourceKey { get; set; } = string.Empty;

    /// <summary>来源站点类型（VodSiteInfo.Type）</summary>
    public int ItemType { get; set; }

    /// <summary>来源站点 Api 地址（VodSiteInfo.Api）</summary>
    public string ItemApi { get; set; } = string.Empty;

    /// <summary>影片 Id（VodItem.Id）</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>集名（用于回跳后自动选中该集，如「第02集」）</summary>
    public string EpisodeName { get; set; } = string.Empty;
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

    /// <summary>建表（幂等）。PlayHistoryEntry 后加的列需要手工迁移——
    /// CreateTableAsync 只建缺表、不会给老表加列，老库查询缺列时直接报错。</summary>
    public async Task EnsureInitializedAsync()
    {
        await Task.WhenAll(
            _db.CreateTableAsync<VodSubscription>(),
            _db.CreateTableAsync<FavoriteEntry>(),
            _db.CreateTableAsync<PlayHistoryEntry>());

        // 播放历史 v2：来源定位列（2026-09-10，历史卡跳回观看页续看）
        await EnsureColumnAsync(_db, "play_history", "SourceKey", "text");
        await EnsureColumnAsync(_db, "play_history", "ItemType", "integer");
        await EnsureColumnAsync(_db, "play_history", "ItemApi", "text");
        await EnsureColumnAsync(_db, "play_history", "ItemId", "text");
        await EnsureColumnAsync(_db, "play_history", "EpisodeName", "text");

        // 同影片多集只保留一条历史（清理按「影片 · 集名」分条时期的存量重复）
        await DedupeHistoryAsync();
    }

    /// <summary>缺列则补（sqlite-net 的 MigrateTable 是 internal，只能自己 ALTER）</summary>
    private static async Task EnsureColumnAsync(SQLiteAsyncConnection db, string table, string column, string decl)
    {
        var cols = await db.GetTableInfoAsync(table);
        if (cols.Any(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase))) return;
        await db.ExecuteAsync($"ALTER TABLE {table} ADD COLUMN {column} {decl}");
    }

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
    /// <summary>
    /// 记录/续看一次播放。合并键是**影片**（SourceKey+ItemId），不是「影片 · 集名」——
    /// 同一部片的不同版本/集数共用一条历史，只更新集名与进度（否则每集一条，历史页没法看）。
    /// 无来源定位的记录（网页直链/本地）退回按标题合并。
    /// </summary>
    public async Task UpsertHistoryAsync(PlayHistoryEntry entry)
    {
        PlayHistoryEntry? existing;
        if (!string.IsNullOrEmpty(entry.SourceKey) && !string.IsNullOrEmpty(entry.ItemId))
        {
            existing = await _db.Table<PlayHistoryEntry>()
                .Where(h => h.SourceKey == entry.SourceKey && h.ItemId == entry.ItemId)
                .OrderByDescending(h => h.WatchedAt)
                .FirstOrDefaultAsync();
        }
        else
        {
            existing = await _db.Table<PlayHistoryEntry>()
                .Where(h => h.Title == entry.Title)
                .OrderByDescending(h => h.WatchedAt)
                .FirstOrDefaultAsync();
        }

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

    /// <summary>
    /// 历史按影片去重 + 标题规范化（一次性清理）：同 SourceKey+ItemId 只留最新一条；
    /// 顺带把「影片 · 集名」时期的老标题清洗回纯影片名（含「 · 集名」开头/结尾两种残缺形态）。
    /// </summary>
    public async Task DedupeHistoryAsync()
    {
        try
        {
            await _db.ExecuteAsync(
                @"DELETE FROM play_history
                  WHERE SourceKey <> '' AND Id NOT IN (
                      SELECT Id FROM (
                          SELECT Id, MAX(Id) AS MaxId FROM play_history
                          WHERE SourceKey <> ''
                          GROUP BY SourceKey || '|' || ItemId
                      ))");

            // 老标题规范化（仅限带来源与集名的记录）
            await _db.ExecuteAsync(
                "UPDATE play_history SET Title = LTRIM(Title, ' ·') WHERE SourceKey <> '' AND EpisodeName <> ''");
            await _db.ExecuteAsync(
                @"UPDATE play_history SET Title = RTRIM(SUBSTR(Title, 1, INSTR(Title, ' · ') - 1))
                  WHERE SourceKey <> '' AND EpisodeName <> '' AND Title LIKE '% · %'");
            await _db.ExecuteAsync(
                "UPDATE play_history SET Title = EpisodeName WHERE SourceKey <> '' AND EpisodeName <> '' AND TRIM(Title) = ''");
        }
        catch { }
    }

    public Task<int> ClearHistoryAsync() => _db.DeleteAllAsync<PlayHistoryEntry>();

    /// <summary>批量删除播放历史（勾选删除）</summary>
    public async Task DeleteHistoryAsync(IEnumerable<int> ids)
    {
        foreach (var id in ids)
            await _db.DeleteAsync<PlayHistoryEntry>(id);
    }

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
