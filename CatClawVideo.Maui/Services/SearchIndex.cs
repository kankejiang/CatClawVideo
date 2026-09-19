using CatClawVideo.Core.Models;
using CatClawVideo.Core.Services;
using CatClawVideo.Data;

namespace CatClawVideo.Maui.Services;

/// <summary>
/// 片名索引的**被动积累**入口（片名 → 首字母 / 系列基名）。
///
/// <para><b>它现在只是「增强」，不是搜索的主路径</b>：拼音联想由
/// <see cref="TvBoxSuggestService"/>（照抄 TVBox）在线提供。本索引的价值在于
/// <b>补上接口给不了的东西</b> —— 「这个片名在我哪个源上、能不能直接播」，
/// 让候选里本地已有的片可以直接跳播放，省掉一次跨源搜索。</para>
///
/// <para><b>为什么不再是主路径</b>（2026-09-19 用户指出）：为了补覆盖而做「启动时批量抓站」
/// 有风控风险 —— 主动并发拉取第三方站点与 TVBox 的行为模式差别很大，
/// 真实用户流量不会这么走。联想交给在线接口，索引只做被动积累（用户在浏览，天然不异常）。</para>
///
/// <para><b>接入点只有一处</b>：<see cref="CoverResolver.Attach"/> —— 首页/分类/搜索/
/// 搜索增量上屏全都经过它，一处挂钩即全覆盖，不必逐页埋点（漏一处就少一片索引）。</para>
///
/// <para><b>性能</b>：调用方不等待（fire-and-forget），内部做**合批**（把短时间内的多次
/// 上屏合并成一次写库），避免滚动加载时每页一次 DB 写入。</para>
/// </summary>
public static class SearchIndex
{
    private static VideoDatabase? _db;

    /// <summary>合批缓冲：等一小段时间把同一批上屏的影片合并写库。</summary>
    private static readonly List<SearchIndexEntry> _pending = [];
    private static readonly Lock _lock = new();
    private static bool _flushScheduled;

    /// <summary>合批窗口：滚动加载/增量搜索会在短时间连续上屏，合并后只写一次库。</summary>
    private static readonly TimeSpan FlushDelay = TimeSpan.FromMilliseconds(1200);

    /// <summary>单批上限，防止一次上屏上千条把缓冲撑大。</summary>
    private const int MaxBatch = 300;

    /// <summary>注入数据库（MauiProgram 启动时调用一次）。</summary>
    public static void Initialize(VideoDatabase db) => _db = db;

    /// <summary>
    /// 记录一批影片（不阻塞调用方）。索引不可用时静默跳过 —— 它是增强项，
    /// 任何失败都不该影响浏览主流程。
    /// </summary>
    public static void Remember(IEnumerable<VodItem>? items)
    {
        if (items is null || _db is null) return;
        if (!PinyinInitial.IsAvailable) return;   // 平台不支持拼音序：不做索引

        var added = 0;
        lock (_lock)
        {
            foreach (var it in items)
            {
                if (added >= MaxBatch) break;
                var entry = ToEntry(it);
                if (entry is null) continue;
                _pending.Add(entry);
                added++;
            }
            if (added == 0) return;

            if (_flushScheduled) return;
            _flushScheduled = true;
        }

        // 合批：等一小会儿，把期间连续上屏的都收进同一次写库
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(FlushDelay).ConfigureAwait(false); }
            catch { }
            await FlushAsync().ConfigureAwait(false);
        });
    }

    /// <summary>把缓冲写入数据库。可被外部主动调用（如退出页面前强制落盘）。</summary>
    public static async Task FlushAsync()
    {
        List<SearchIndexEntry> batch;
        lock (_lock)
        {
            if (_pending.Count == 0) { _flushScheduled = false; return; }
            batch = [.. _pending];
            _pending.Clear();
            _flushScheduled = false;
        }

        var db = _db;
        if (db is null) return;
        try { await db.UpsertSearchIndexAsync(batch).ConfigureAwait(false); }
        catch { /* 索引写入失败不影响浏览 */ }
    }

    /// <summary>影片 → 索引条目；无站点定位 / 非影视内容 / 首字母为空时返回 null。</summary>
    private static SearchIndexEntry? ToEntry(VodItem it)
    {
        if (string.IsNullOrWhiteSpace(it.Id) || string.IsNullOrWhiteSpace(it.SourceKey)) return null;
        if (string.IsNullOrWhiteSpace(it.Title)) return null;

        // 索引里存清洗后的片名（去 [全集] 等标注），避免「带标注的重复条目」
        var title = TitleNormalizer.Clean(it.Title);
        if (title.Length == 0) title = it.Title;

        // 非影视内容（UP主视频/新闻/MV/游戏爆料）不进片名索引 —— 它们会污染首字母搜索。
        // 判据见 TitleNormalizer.LooksLikeVideoTitle（经真实库回归，误伤 0）。
        if (!TitleNormalizer.LooksLikeVideoTitle(title)) return null;

        // 基名 = 去季标识（「凡人修仙传第六季」→「凡人修仙传」），按系列聚合用
        var baseTitle = TitleNormalizer.StripSeason(title);
        if (baseTitle.Length == 0) baseTitle = title;

        var initials = PinyinInitial.OfTitle(baseTitle);
        if (initials.Length == 0) return null;   // 纯符号/纯假名片名：无首字母可用

        return new SearchIndexEntry
        {
            SourceKey = it.SourceKey,
            ItemId = it.Id,
            Title = title,
            BaseTitle = baseTitle,
            Initials = initials,
            Cover = it.Cover,
            Year = it.Year,
            Remarks = it.Remarks,
            Category = it.Category,
            Description = it.Description,
            UpdatedAt = DateTime.Now,
        };
    }
}
