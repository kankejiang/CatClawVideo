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

    /// <summary>上次播放的线路名（线路分组名，如「线路3」/「磁力播放」）。续看时优先选中该线路。</summary>
    public string RouteName { get; set; } = string.Empty;

    // ── 影片详情字段（跳回观看页时还原完整信息区，与首页进入一致） ──

    public string Category { get; set; } = string.Empty;
    public string Year { get; set; } = string.Empty;
    public string Remarks { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
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
/// 片名首字母索引（首字母搜索用）。
///
/// <para><b>为什么需要落库</b>：遥控器没法打字，首字母搜索是电视端最实用的入口。
/// 要做「输入 <c>lr</c> → 列出流人」，就得有一份「片名 → 首字母」的本地索引——
/// 而这个索引只能在用户**浏览过**某些影片之后才存在（我们不做全站预抓取）。
/// 落库后跨启动复用，用户看过一次首页/搜索过一次，之后就一直能首字母直达。</para>
/// </summary>
[Table("search_index")]
public class SearchIndexEntry
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>来源站点 Key（回跳观看页必需）</summary>
    public string SourceKey { get; set; } = string.Empty;

    /// <summary>站点内影片 ID</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>片名（完整，含季标识；用于定位与展示）</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// 系列基名（去季标识；用于**按系列聚合**）。
    ///
    /// <para>「凡人修仙传第六季」的基名是「凡人修仙传」。用户输字母找的是**系列**，
    /// 而不是某一季 —— 若按完整片名聚合，`frx` 只会捞到「凡人修仙传第六季」这一条，
    /// 前几季全被挡在门外（2026-09-19 用户实测反馈）。</para>
    ///
    /// <para>旧数据此列为空：读取时用 <c>TitleNormalizer.StripSeason(Title)</c> 回退补齐，
    /// 不强制迁移存量。</para>
    /// </summary>
    public string BaseTitle { get; set; } = string.Empty;

    /// <summary>
    /// 片名**基名**的拼音首字母串（「凡人修仙传第六季」→ <c>FRXXC</c>，不含季的字母）。
    /// 用户输 <c>FRX</c> 即可命中该系列。
    /// </summary>
    public string Initials { get; set; } = string.Empty;

    public string? Cover { get; set; }
    public string? Year { get; set; }
    public string? Remarks { get; set; }
    public string? Category { get; set; }
    public string? Description { get; set; }

    /// <summary>最近一次写入时间（超量时按它淘汰最旧）</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>去重键：同站同片只留一条</summary>
    public string DedupeKey => SourceKey + "|" + ItemId;
}

/// <summary>
/// 一个**片名系列**（同一部剧的不同季聚合在一起）。
///
/// <para>用户输字母找的是系列，例如输 <c>frx</c> 想看「凡人修仙传」——
/// 而不是「凡人修仙传第六季」。聚合成系列后，点进去能看到各季全在。</para>
/// </summary>
public class SearchSeries
{
    /// <summary>系列基名（如「凡人修仙传」）</summary>
    public string BaseTitle { get; set; } = string.Empty;

    /// <summary>该系列下各季条目（按季号升序；单集片只有一个成员）</summary>
    public List<SearchIndexEntry> Seasons { get; set; } = [];

    /// <summary>最近更新时间（排序用）</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>季数（1 = 单片，不显示「N 季」标签）</summary>
    public int SeasonCount => Seasons.Count;
}

/// <summary>
/// 影视数据库（SQLite）：订阅源、播放历史、收藏、片名首字母索引。
/// 单连接单例，与宿主音乐库的 MusicDatabase 同模式。
/// </summary>
public class VideoDatabase
{
    /// <summary>播放历史保留条数上限（对位 TVBox HISTORY_NUM，本仓默认放宽到 500）。
    /// 由宿主启动时从设置里读一次写入 —— Data 层不碰 Preferences。</summary>
    public static int MaxHistoryEntries { get; set; } = 500;
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
            _db.CreateTableAsync<PlayHistoryEntry>(),
            _db.CreateTableAsync<SearchIndexEntry>());

        // 播放历史 v2：来源定位列（2026-09-10，历史卡跳回观看页续看）
        await EnsureColumnAsync(_db, "play_history", "SourceKey", "text");
        await EnsureColumnAsync(_db, "play_history", "ItemType", "integer");
        await EnsureColumnAsync(_db, "play_history", "ItemApi", "text");
        await EnsureColumnAsync(_db, "play_history", "ItemId", "text");
        await EnsureColumnAsync(_db, "play_history", "RouteName", "text");
        await EnsureColumnAsync(_db, "play_history", "EpisodeName", "text");

        // 播放历史 v3：影片详情列（历史卡跳回观看页时还原完整信息区）
        await EnsureColumnAsync(_db, "play_history", "Category", "text");
        await EnsureColumnAsync(_db, "play_history", "Year", "text");
        await EnsureColumnAsync(_db, "play_history", "Remarks", "text");
        await EnsureColumnAsync(_db, "play_history", "Description", "text");

        // 同影片多集只保留一条历史（清理按「影片 · 集名」分条时期的存量重复）
        await DedupeHistoryAsync();

        // 片名索引 v2：系列基名（按系列聚合用）。老库缺列 → 补空列，
        // 读取时用 TitleNormalizer.StripSeason(Title) 兜底，不强制回填存量。
        await EnsureColumnAsync(_db, "search_index", "BaseTitle", "text");
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

    /// <summary>按影片定位取历史（续播用）：SourceKey+ItemId 精确匹配，取最近一条。</summary>
    public Task<PlayHistoryEntry?> FindHistoryAsync(string? sourceKey, string? itemId)
    {
        if (string.IsNullOrEmpty(sourceKey) || string.IsNullOrEmpty(itemId))
            return Task.FromResult<PlayHistoryEntry?>(null);
        return _db.Table<PlayHistoryEntry>()
            .Where(h => h.SourceKey == sourceKey && h.ItemId == itemId)
            .OrderByDescending(h => h.WatchedAt)
            .FirstOrDefaultAsync()!;
    }

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
            // 控制历史总量：超出上限删最旧（上限由宿主在启动时写入，见 HistoryCap）
            var cap = MaxHistoryEntries;
            var total = await _db.Table<PlayHistoryEntry>().CountAsync();
            if (total > cap)
            {
                var oldest = await _db.Table<PlayHistoryEntry>()
                    .OrderBy(h => h.WatchedAt).Take(total - cap).ToListAsync();
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

    // ══════════════════════ 片名首字母索引 ══════════════════════
    //
    // 被动积累：用户浏览首页/分类/搜索结果时把片名写进来（见 SearchIndexService）。
    // 不做全站预抓取 —— 遥控器场景下，用户要找的多是「刚看过的片」，
    // 浏览过的片名已足够覆盖。

    /// <summary>索引总量上限。超出按更新时间淘汰最旧一批（避免无限增长）。</summary>
    private const int MaxSearchIndex = 5000;

    /// <summary>
    /// 批量写入索引（幂等）。同 SourceKey+ItemId 已存在则只刷新元数据与时间，不重复插入。
    /// 失败静默（索引是加速项，不该影响浏览主流程）。
    /// </summary>
    public async Task UpsertSearchIndexAsync(IReadOnlyCollection<SearchIndexEntry> entries)
    {
        if (entries.Count == 0) return;
        try
        {
            // 一次性把已有键读进内存比对：一次浏览可能来几十条，逐条查库太慢
            var existing = await _db.Table<SearchIndexEntry>().ToListAsync();
            var byKey = new Dictionary<string, SearchIndexEntry>(StringComparer.Ordinal);
            foreach (var e in existing) byKey[e.DedupeKey] = e;

            var toInsert = new List<SearchIndexEntry>();
            var toUpdate = new List<SearchIndexEntry>();
            foreach (var e in entries)
            {
                if (string.IsNullOrWhiteSpace(e.ItemId) || string.IsNullOrWhiteSpace(e.Initials)) continue;
                if (byKey.TryGetValue(e.DedupeKey, out var old))
                {
                    // 已有：只更新可变字段（片名/角标可能变，如「更新至12集」）
                    if (old.Title == e.Title && old.Initials == e.Initials
                        && old.Remarks == e.Remarks && old.Cover == e.Cover
                        && old.BaseTitle == e.BaseTitle) continue;
                    old.Title = e.Title;
                    old.BaseTitle = e.BaseTitle;
                    old.Initials = e.Initials;
                    old.Remarks = e.Remarks;
                    old.Cover = e.Cover ?? old.Cover;
                    old.Year = e.Year ?? old.Year;
                    old.UpdatedAt = DateTime.Now;
                    toUpdate.Add(old);
                }
                else
                {
                    toInsert.Add(e);
                    byKey[e.DedupeKey] = e;   // 同批次内去重
                }
            }

            if (toInsert.Count > 0) await _db.InsertAllAsync(toInsert);
            if (toUpdate.Count > 0) await _db.UpdateAllAsync(toUpdate);

            await TrimSearchIndexAsync(existing.Count + toInsert.Count);
        }
        catch { }
    }

    /// <summary>超量时按更新时间淘汰最旧的一批（保留最近一半）。</summary>
    private async Task TrimSearchIndexAsync(int projectedTotal)
    {
        if (projectedTotal <= MaxSearchIndex) return;
        var total = await _db.Table<SearchIndexEntry>().CountAsync();
        if (total <= MaxSearchIndex) return;
        var drop = await _db.Table<SearchIndexEntry>()
            .OrderBy(e => e.UpdatedAt)
            .Take(total - MaxSearchIndex / 2)
            .ToListAsync();
        foreach (var d in drop) await _db.DeleteAsync(d);
    }

    /// <summary>
    /// 按首字母前缀查候选。
    /// <para>在内存里过滤而非 SQL LIKE：索引总量数千条、单次匹配微秒级，
    /// 而 SQL 无法表达「多音字变体」这层逻辑（见 <c>PinyinInitial.Matches</c>）。</para>
    /// </summary>
    public async Task<List<SearchIndexEntry>> SearchByInitialsAsync(string input, int limit = 30)
    {
        var q = input?.Trim();
        if (string.IsNullOrEmpty(q)) return [];
        try
        {
            var all = await _db.Table<SearchIndexEntry>().ToListAsync();
            var hit = new List<SearchIndexEntry>();
            foreach (var e in all)
            {
                if (Core.Services.PinyinInitial.Matches(e.Initials, q))
                {
                    hit.Add(e);
                }
            }
            // 短前缀（1~2 个字母）命中可能很多：优先给较新的
            return hit.OrderByDescending(e => e.UpdatedAt).Take(limit).ToList();
        }
        catch { return []; }
    }

    /// <summary>
    /// 按**系列**聚合查候选。
    ///
    /// <para>返回「系列基名 → 该系列的全部条目（按季号升序）」。这样用户输 <c>frx</c>
    /// 得到的是**一个系列**，点进去能看到第一季到最新季全都在 ——
    /// 而不是像按完整片名聚合那样只冒出一季（2026-09-19 用户实测反馈）。</para>
    ///
    /// <para>单集片（无季）自然形成只有一个成员的组，行为与逐条列出等价。</para>
    /// </summary>
    public async Task<List<SearchSeries>> SearchSeriesByInitialsAsync(string input, int limit = 40)
    {
        var q = input?.Trim();
        if (string.IsNullOrEmpty(q)) return [];
        try
        {
            var all = await _db.Table<SearchIndexEntry>().ToListAsync();

            return all
                // 基名为空的老数据：用 Title 现算（不依赖迁移是否跑过）
                .Where(e => Core.Services.PinyinInitial.Matches(BaseOf(e), q))
                .GroupBy(BaseOf, StringComparer.OrdinalIgnoreCase)
                .Select(g => new SearchSeries
                {
                    BaseTitle = g.Key,
                    // 季号升序：第一季在前（用户最可能从头看）；不带季的排最后
                    Seasons = [.. g.OrderBy(e => Core.Services.TitleNormalizer.SeasonNumber(e.Title))
                                   .ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase)],
                    UpdatedAt = g.Max(e => e.UpdatedAt),
                })
                .OrderByDescending(s => s.Seasons.Any(e => !string.IsNullOrEmpty(e.Cover)))  // 有封面的在前
                .ThenByDescending(s => s.UpdatedAt)
                .Take(limit)
                .ToList();
        }
        catch { return []; }
    }

    /// <summary>取条目的系列基名（老数据 BaseTitle 为空时按 Title 现算）。</summary>
    private static string BaseOf(SearchIndexEntry e) =>
        !string.IsNullOrEmpty(e.BaseTitle)
            ? e.BaseTitle
            : Core.Services.TitleNormalizer.StripSeason(e.Title);

    /// <summary>索引条数（设置页展示/诊断用）。</summary>
    public async Task<int> GetSearchIndexCountAsync()
    {
        try { return await _db.Table<SearchIndexEntry>().CountAsync(); }
        catch { return 0; }
    }

    /// <summary>清空索引（供设置页「重建索引」用）。</summary>
    public Task ClearSearchIndexAsync() => _db.DeleteAllAsync<SearchIndexEntry>();
}
