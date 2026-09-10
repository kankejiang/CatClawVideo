namespace CatClawVideo.Core.Services;

/// <summary>
/// BT 任务运行快照（任务详情页 / 卡片统计用）。
/// <para>能力边界：MonoTorrent 3.0.1 不暴露 peer 连接明细（PeerManager 仅有计数），
/// 故无"连接列表"（地址/客户端/单 peer 速率）。</para>
/// </summary>
public sealed record BtTorrentStats(
    string InfoHash,
    string StateText,
    bool HasMetadata,
    string SavePath,
    string Name,
    double ProgressPercent,
    long TotalBytes,
    long DownloadedBytes,
    long UploadedBytes,
    long DownloadRate,
    long UploadRate,
    int Seeds,
    int Leeches,
    int Connections,
    long PieceLength,
    bool[] Pieces,
    List<BtTrackerInfo> Trackers,
    List<BtFileInfo> Files)
{
    /// <summary>分享率（已上传 / 已下载）</summary>
    public double ShareRatio => DownloadedBytes > 0 ? (double)UploadedBytes / DownloadedBytes : 0;

    /// <summary>预计剩余时间（秒；速度为零返回 -1）</summary>
    public double RemainingSeconds => DownloadRate > 0
        ? (TotalBytes - DownloadedBytes) / (double)DownloadRate
        : -1;
}

/// <summary>Tracker 状态行</summary>
public sealed record BtTrackerInfo(string Url, string Status, string Message);

/// <summary>种子内文件行</summary>
public sealed record BtFileInfo(
    int Index,
    string Name,
    string Extension,
    string FullPath,
    long Length,
    long DownloadedBytes,
    double ProgressPercent,
    bool Selected);

/// <summary>下载任务周期统计（下载服务 → 任务卡片）</summary>
public sealed record BtTaskStats(
    long Downloaded,
    long Total,
    long DownloadRate,
    long UploadRate,
    long Uploaded,
    int Connections,
    int Seeds,
    int Leeches);
