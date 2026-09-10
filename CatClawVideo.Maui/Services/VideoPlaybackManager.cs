using CatClawVideo.Data;

namespace CatClawVideo.Maui.Services;

/// <summary>
/// 播放会话管理：持有当前播放状态并落库播放历史（断点续看数据源）。
/// </summary>
public class VideoPlaybackManager
{
    private readonly VideoDatabase _db;

    /// <summary>当前播放标题（空 = 无活动播放会话）</summary>
    public string CurrentTitle { get; private set; } = string.Empty;

    /// <summary>当前播放地址</summary>
    public string CurrentUrl { get; private set; } = string.Empty;

    /// <summary>当前播放影片封面（海报墙用，可空）</summary>
    public string? CurrentCover { get; private set; }

    /// <summary>来源定位（非空时历史卡可跳回观看页详情续看；网页直链/本地播放为空）</summary>
    public string CurrentSourceKey { get; private set; } = string.Empty;
    public int CurrentItemType { get; private set; }
    public string CurrentItemApi { get; private set; } = string.Empty;
    public string CurrentItemId { get; private set; } = string.Empty;
    public string CurrentEpisodeName { get; private set; } = string.Empty;
    public string CurrentCategory { get; private set; } = string.Empty;
    public string CurrentYear { get; private set; } = string.Empty;
    public string CurrentRemarks { get; private set; } = string.Empty;
    public string CurrentDescription { get; private set; } = string.Empty;

    public VideoPlaybackManager(VideoDatabase db) => _db = db;

    /// <summary>开始一次播放会话（带来源定位与影片详情：历史卡才能跳回观看页续看并还原信息区）</summary>
    public void BeginSession(string title, string url, string? cover = null,
        string? sourceKey = null, int itemType = 0, string? itemApi = null,
        string? itemId = null, string? episodeName = null,
        string? category = null, string? year = null, string? remarks = null, string? description = null)
    {
        CurrentTitle = title;
        CurrentUrl = url;
        CurrentCover = string.IsNullOrEmpty(cover) ? null : cover;
        CurrentSourceKey = sourceKey ?? string.Empty;
        CurrentItemType = itemType;
        CurrentItemApi = itemApi ?? string.Empty;
        CurrentItemId = itemId ?? string.Empty;
        CurrentEpisodeName = episodeName ?? string.Empty;
        CurrentCategory = category ?? string.Empty;
        CurrentYear = year ?? string.Empty;
        CurrentRemarks = remarks ?? string.Empty;
        CurrentDescription = description ?? string.Empty;
    }

    /// <summary>结束播放会话并记录历史（fire-and-forget，不阻塞页面退出）</summary>
    public void EndSession(double positionSeconds, double durationSeconds)
    {
        if (string.IsNullOrEmpty(CurrentUrl)) return;

        var entry = new PlayHistoryEntry
        {
            Title = CurrentTitle,
            Url = CurrentUrl,
            Cover = CurrentCover,
            PositionSeconds = positionSeconds,
            DurationSeconds = durationSeconds,
            WatchedAt = DateTime.Now,
            SourceKey = CurrentSourceKey,
            ItemType = CurrentItemType,
            ItemApi = CurrentItemApi,
            ItemId = CurrentItemId,
            EpisodeName = CurrentEpisodeName,
            Category = CurrentCategory,
            Year = CurrentYear,
            Remarks = CurrentRemarks,
            Description = CurrentDescription,
        };
        var title = CurrentTitle;
        CurrentTitle = string.Empty;
        CurrentUrl = string.Empty;
        CurrentCover = null;
        CurrentSourceKey = string.Empty;
        CurrentItemType = 0;
        CurrentItemApi = string.Empty;
        CurrentItemId = string.Empty;
        CurrentEpisodeName = string.Empty;
        CurrentCategory = string.Empty;
        CurrentYear = string.Empty;
        CurrentRemarks = string.Empty;
        CurrentDescription = string.Empty;
        _ = Task.Run(async () =>
        {
            try { await _db.UpsertHistoryAsync(entry); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Playback] 历史记录失败: {ex.Message}"); }
            finally { System.Diagnostics.Debug.WriteLine($"[Playback] 会话结束: {title} @{positionSeconds:F0}s"); }
        });
    }
}
