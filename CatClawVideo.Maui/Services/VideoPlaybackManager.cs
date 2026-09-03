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

    public VideoPlaybackManager(VideoDatabase db) => _db = db;

    /// <summary>开始一次播放会话</summary>
    public void BeginSession(string title, string url)
    {
        CurrentTitle = title;
        CurrentUrl = url;
    }

    /// <summary>结束播放会话并记录历史（fire-and-forget，不阻塞页面退出）</summary>
    public void EndSession(double positionSeconds, double durationSeconds)
    {
        if (string.IsNullOrEmpty(CurrentUrl)) return;

        var entry = new PlayHistoryEntry
        {
            Title = CurrentTitle,
            Url = CurrentUrl,
            PositionSeconds = positionSeconds,
            DurationSeconds = durationSeconds,
            WatchedAt = DateTime.Now,
        };
        var title = CurrentTitle;
        CurrentTitle = string.Empty;
        CurrentUrl = string.Empty;
        _ = Task.Run(async () =>
        {
            try { await _db.UpsertHistoryAsync(entry); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Playback] 历史记录失败: {ex.Message}"); }
            finally { System.Diagnostics.Debug.WriteLine($"[Playback] 会话结束: {title} @{positionSeconds:F0}s"); }
        });
    }
}
