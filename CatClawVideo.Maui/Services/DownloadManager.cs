using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CatClawVideo.Maui.Services;

/// <summary>下载任务状态</summary>
public enum DownloadStatus
{
    /// <summary>排队等待</summary>
    Queued,
    /// <summary>正在下载</summary>
    Downloading,
    /// <summary>已暂停（可恢复）</summary>
    Paused,
    /// <summary>已完成</summary>
    Completed,
    /// <summary>下载失败</summary>
    Failed,
    /// <summary>已取消</summary>
    Canceled
}

/// <summary>
/// 下载任务项：一个待下载/正在下载/已完成的文件任务。
/// 持久化字段由 DownloadManager 序列化；运行时的进度/状态以 INPC 驱动 UI 刷新。
/// </summary>
public partial class DownloadTaskItem : ObservableObject
{
    /// <summary>任务唯一 ID</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>展示名称（文件显示名）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>源地址（HTTP URL 或 magnet: 链接）</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>本地保存路径（HTTP 完成后为最终文件路径；BT 为保存目录）</summary>
    public string LocalPath { get; set; } = string.Empty;

    /// <summary>任务类型：url=HTTP 直链下载；magnet=BT 磁力下载</summary>
    public string Kind { get; set; } = "url";

    /// <summary>创建时间（Unix 秒）</summary>
    public long CreatedAt { get; set; }

    [ObservableProperty]
    private long _totalBytes;

    [ObservableProperty]
    private long _downloadedBytes;

    [ObservableProperty]
    private DownloadStatus _status = DownloadStatus.Queued;

    [ObservableProperty]
    private string _error = string.Empty;

    /// <summary>瞬时下载速度展示文本（如 1.2 MB/s），运行时字段</summary>
    [ObservableProperty]
    private string _speedText = string.Empty;

    /// <summary>是否暂停中（运行时字段）</summary>
    [ObservableProperty]
    private bool _isPaused;

    /// <summary>当前连接节点数（BT 任务运行时字段，HTTP 任务恒 0）</summary>
    [ObservableProperty]
    private int _connections;

    /// <summary>上传速度展示文本（BT 任务）</summary>
    [ObservableProperty]
    private string _uploadSpeedText = "";

    /// <summary>已上传字节数（BT 任务）</summary>
    [ObservableProperty]
    private long _uploadedBytes;

    /// <summary>种子数（BT 任务）</summary>
    [ObservableProperty]
    private int _seeds;

    /// <summary>瞬时下载速率（B/s，剩余时间计算用）</summary>
    [ObservableProperty]
    private long _downloadRateBytes;

    /// <summary>速度采样历史（B/s 等间隔，运行时字段，节点图数据源）</summary>
    private readonly Queue<long> _speedHistory = new();

    /// <summary>展示名称回退：无文件名时显示 URL</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Url : Name;

    /// <summary>是否为磁力（BT）任务：节点图仅 BT 任务有意义</summary>
    public bool IsMagnet => Kind == "magnet";

    /// <summary>节点图可见性：仅"下载中"的 BT 任务显示</summary>
    public bool ShowNodeGraph => Status == DownloadStatus.Downloading && IsMagnet;

    /// <summary>连接数展示文本（👥 N）</summary>
    public string ConnectionsText => $"👥 {Connections}";

    /// <summary>种子数展示文本（⚡ N）</summary>
    public string SeedsText => $"⚡ {Seeds}";

    /// <summary>上传速度展示文本（↑ x/s）</summary>
    public string UploadSpeedShow => $"↑ {(string.IsNullOrWhiteSpace(UploadSpeedText) ? "0 KB/s" : UploadSpeedText)}";

    /// <summary>下载速度展示文本（↓ x/s）</summary>
    public string DownloadSpeedShow => $"↓ {(string.IsNullOrWhiteSpace(SpeedText) ? "0 KB/s" : SpeedText)}";

    /// <summary>剩余时间展示文本（Motrix 风格：剩余 17时 22分 29秒）</summary>
    public string RemainText
    {
        get
        {
            if (DownloadRateBytes <= 0 || TotalBytes <= 0 || DownloadedBytes >= TotalBytes) return "";
            var ts = TimeSpan.FromSeconds((TotalBytes - DownloadedBytes) / (double)DownloadRateBytes);
            if (ts.TotalHours >= 1) return $"剩余 {(int)ts.TotalHours}时 {ts.Minutes}分 {ts.Seconds}秒";
            if (ts.TotalMinutes >= 1) return $"剩余 {ts.Minutes}分 {ts.Seconds}秒";
            return $"剩余 {ts.Seconds}秒";
        }
    }

    /// <summary>分享率（已上传 / 已下载）</summary>
    public string ShareRatioText => DownloadedBytes > 0
        ? ((double)UploadedBytes / DownloadedBytes).ToString("F3")
        : "0";

    /// <summary>追加速度采样（保留最近 60 个 = 约 30 秒窗口）</summary>
    public void PushSpeedSample(long bps)
    {
        _speedHistory.Enqueue(bps);
        while (_speedHistory.Count > 60) _speedHistory.Dequeue();
        OnPropertyChanged(nameof(SpeedHistory));
    }

    /// <summary>速度采样快照（节点图绑定源）</summary>
    public long[] SpeedHistory => _speedHistory.ToArray();

    /// <summary>进度（0-1，MAUI ProgressBar 范围）</summary>
    public double Progress => TotalBytes > 0 ? Math.Clamp((double)DownloadedBytes / TotalBytes, 0, 1) : 0;

    /// <summary>状态展示文本</summary>
    public string StatusText => Status switch
    {
        DownloadStatus.Queued => "排队中",
        DownloadStatus.Downloading => "下载中",
        DownloadStatus.Paused => "已暂停",
        DownloadStatus.Completed => "已完成",
        DownloadStatus.Failed => $"失败：{Error}",
        DownloadStatus.Canceled => "已取消",
        _ => ""
    };

    /// <summary>状态颜色</summary>
    public string StatusColor => Status switch
    {
        DownloadStatus.Completed => "#4CAF50",
        DownloadStatus.Failed => "#F44336",
        DownloadStatus.Downloading or DownloadStatus.Queued => "#42A5F5",
        DownloadStatus.Paused or DownloadStatus.Canceled => "#9E9E9E",
        _ => "#9E9E9E"
    };

    /// <summary>已下载/总大小展示文本</summary>
    public string SizeText => TotalBytes > 0
        ? $"{FormatBytes(DownloadedBytes)} / {FormatBytes(TotalBytes)}"
        : FormatBytes(DownloadedBytes);

    /// <summary>已下载大小 + 速度展示文本</summary>
    public string DownloadingText => string.IsNullOrWhiteSpace(SpeedText)
        ? SizeText
        : $"{SizeText} · {SpeedText}";

    /// <summary>完成文本（附带保存路径，任务卡片全程可见）</summary>
    public string CompletedText => Status == DownloadStatus.Completed ? $"{FormatBytes(TotalBytes)} · 保存至 {LocalPath}" : "";

    /// <summary>批量刷新计算属性（DownloadManager 在进度/状态变化后调用）</summary>
    public void RaiseDerivedChanged()
    {
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(DownloadingText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(CompletedText));
        OnPropertyChanged(nameof(ShowNodeGraph));
        OnPropertyChanged(nameof(ConnectionsText));
        OnPropertyChanged(nameof(SeedsText));
        OnPropertyChanged(nameof(UploadSpeedShow));
        OnPropertyChanged(nameof(DownloadSpeedShow));
        OnPropertyChanged(nameof(RemainText));
        OnPropertyChanged(nameof(ShareRatioText));
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1073741824) return $"{bytes / 1073741824.0:F1} GB";
        if (bytes >= 1048576) return $"{bytes / 1048576.0:F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }
}

/// <summary>
/// 下载管理器（复刻猫爪音乐同款架构）：维护下载任务队列（HTTP 直链 + BT 磁力），
/// 并发槽位控制、~500ms 节流进度上报、暂停/继续/取消/重试/删除、JSON 任务持久化。
/// </summary>
public class DownloadManager : IDisposable
{
    /// <summary>下载路径默认值（Android 外部存储根目录）</summary>
    public const string DefaultFolderPath = "/storage/emulated/0/CatClawVideo";

    /// <summary>下载路径偏好键</summary>
    public const string PrefKey = "download_folder_path";

    /// <summary>最大并发下载任务数偏好键</summary>
    public const string ConcurrentPrefKey = "download_max_concurrent";

    /// <summary>默认最大并发下载任务数</summary>
    public const int DefaultConcurrent = 2;
    public const int ConcurrentMin = 1;
    public const int ConcurrentMax = 5;

    private static readonly string TasksFilePath =
        Path.Combine(FileSystem.AppDataDirectory, "download_tasks.json");

    private readonly HttpClient _http;
    /// <summary>排队任务等待"并发槽位空出"的通知信号</summary>
    private readonly SemaphoreSlim _slotWake = new(0);
    private readonly Dictionary<string, CancellationTokenSource> _ctsMap = new();
    private readonly object _lock = new();
    private int _active;
    private bool _disposed;

    /// <summary>磁力（BT）下载引擎懒工厂（首次真正使用磁力功能时才构造 ClientEngine）</summary>
    private readonly Func<BitTorrentDownloadService?>? _btFactory;

    private BitTorrentDownloadService? Bt => _btFactory?.Invoke();

    /// <summary>当前最大并发下载任务数</summary>
    public int ConcurrentLimit { get; private set; } = DefaultConcurrent;

    /// <summary>下载任务集合（按创建时间排序）</summary>
    public ObservableCollection<DownloadTaskItem> Tasks { get; } = new();

    /// <summary>任务集合变化（增删）时触发</summary>
    public event Action? TasksChanged;

    /// <summary>单个任务进度/状态变化时触发</summary>
    public event Action<DownloadTaskItem>? TaskUpdated;

    /// <summary>BT 任务运行快照（详情页/文件与 Tracker 列表；HTTP 任务返回 null）</summary>
    public CatClawVideo.Core.Services.BtTorrentStats? GetBtStats(string id)
    {
        var task = Find(id);
        if (task == null || task.Kind != "magnet") return null;
        return Bt?.GetStats(id);
    }

    /// <summary>勾选/取消勾选 BT 任务内文件</summary>
    public Task<bool> SetBtFileSelectionAsync(string id, int fileIndex, bool selected)
    {
        var bt = Bt;
        return bt == null ? Task.FromResult(false) : bt.SetFileSelectionAsync(id, fileIndex, selected);
    }

    public DownloadManager(Func<BitTorrentDownloadService?>? btFactory = null)
    {
        _btFactory = btFactory;
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Add("User-Agent", "CatClawVideo/1.0");
        ConcurrentLimit = LoadConcurrentLimit();
        LoadTasks();
    }

    private static int LoadConcurrentLimit()
    {
        var v = Preferences.Default.Get(ConcurrentPrefKey, DefaultConcurrent);
        return Math.Clamp(v, ConcurrentMin, ConcurrentMax);
    }

    /// <summary>设置并发数上限（立即生效，并持久化）。提高并发时唤醒排队任务。</summary>
    public void SetConcurrentLimit(int count)
    {
        var v = Math.Clamp(count, ConcurrentMin, ConcurrentMax);
        int delta;
        lock (_lock)
        {
            delta = v - ConcurrentLimit;
            ConcurrentLimit = v;
        }
        if (delta > 0)
        {
            try { _slotWake.Release(delta); } catch (SemaphoreFullException) { }
        }
        Preferences.Default.Set(ConcurrentPrefKey, v);
    }

    /// <summary>获取下载目录：优先用户设置，否则平台默认值</summary>
    public static string GetDownloadFolderPath()
    {
        var saved = Preferences.Default.Get(PrefKey, string.Empty);
        if (!string.IsNullOrWhiteSpace(saved)) return saved;
#if ANDROID
        return DefaultFolderPath;
#else
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        return string.IsNullOrWhiteSpace(videos)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CatClawVideo")
            : Path.Combine(videos, "CatClawVideo");
#endif
    }

    /// <summary>当前下载目录</summary>
    public string DownloadFolderPath => GetDownloadFolderPath();

    /// <summary>保存下载目录设置</summary>
    public void SetDownloadFolderPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        Preferences.Default.Set(PrefKey, path.TrimEnd('/', '\\'));
    }

    // ═══════════════════════════════════════════════════════
    // 任务创建
    // ═══════════════════════════════════════════════════════

    /// <summary>新建普通 URL 下载任务</summary>
    public DownloadTaskItem EnqueueUrl(string url, string? fileName = null)
    {
        var name = string.IsNullOrWhiteSpace(fileName)
            ? DeriveFileName(url, "download")
            : SanitizeFileName(fileName);
        var item = CreateTask(url, name, "url");
        _ = RunAsync(item);
        return item;
    }

    /// <summary>新建磁力（BT）下载任务：magnet: 链接由内置 BT 引擎下载（DHT/tracker 发现做种者）</summary>
    public DownloadTaskItem EnqueueMagnet(string magnet, string? displayName = null)
    {
        var name = !string.IsNullOrWhiteSpace(displayName)
            ? SanitizeFileName(displayName)
            : DeriveMagnetName(magnet);
        // BT 任务保存到 下载目录/BT/名称/（多文件种子保持目录结构）
        var dir = Path.Combine(DownloadFolderPath, "BT");
        try { Directory.CreateDirectory(dir); } catch { }
        var saveDir = GetUniqueDirPath(Path.Combine(dir, name));

        var item = new DownloadTaskItem
        {
            Name = name,
            Url = magnet,
            Kind = "magnet",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            LocalPath = saveDir
        };
        AddTask(item);
        _ = RunMagnetAsync(item, magnet, saveDir);
        return item;
    }

    private DownloadTaskItem CreateTask(string url, string fileName, string kind)
    {
        var dir = DownloadFolderPath;
        try { Directory.CreateDirectory(dir); } catch { }

        var item = new DownloadTaskItem
        {
            Name = fileName,
            Url = url,
            Kind = kind,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            LocalPath = Path.Combine(dir, fileName)
        };
        // 文件名冲突时自动追加序号
        item.LocalPath = GetUniquePath(item.LocalPath);
        item.Name = Path.GetFileName(item.LocalPath);

        AddTask(item);
        return item;
    }

    // ═══════════════════════════════════════════════════════
    // 任务控制
    // ═══════════════════════════════════════════════════════

    /// <summary>暂停任务（中断本次下载，保留任务与已下字节数）</summary>
    public void Pause(string id)
    {
        lock (_lock)
        {
            if (_ctsMap.TryGetValue(id, out var cts))
                cts.Cancel();
        }
        var task = Find(id);
        if (task == null) return;
        if (task.Kind == "magnet")
        {
            _ = Bt?.PauseAsync(id);
            if (task.Status == DownloadStatus.Downloading)
            {
                UpdateTask(task, t => { t.Status = DownloadStatus.Paused; t.IsPaused = true; t.SpeedText = ""; });
            }
            return;
        }
        if (task.Status == DownloadStatus.Downloading)
        {
            UpdateTask(task, t => { t.Status = DownloadStatus.Paused; t.IsPaused = true; t.SpeedText = ""; });
        }
    }

    /// <summary>继续任务（重新发起下载；BT 由引擎校验已下数据后续传）</summary>
    public void Resume(string id)
    {
        var task = Find(id);
        // 磁力任务的 Queued 是"死状态"：它不走并发槽位队列，一旦进入且无人推回
        // Downloading 就永远卡在"排队中"且三个操作按钮都不显示。
        // 所以磁力任务允许从 Paused / Queued 两种状态继续（Queued 覆盖 App 重启后
        // 从持久化恢复的任务——它们的管理器已不存在，必须走完整重跑）。
        if (task == null) return;
        if (task.Kind == "magnet")
        {
            if (task.Status is not (DownloadStatus.Paused or DownloadStatus.Queued)) return;
        }
        else if (task.Status != DownloadStatus.Paused) return;

        if (task.Kind == "magnet")
        {
            UpdateTask(task, t => { t.Status = DownloadStatus.Queued; t.IsPaused = false; t.Error = ""; });
            _ = ResumeMagnetAsync(task);
            return;
        }
        UpdateTask(task, t =>
        {
            t.Status = DownloadStatus.Queued;
            t.IsPaused = false;
            t.Error = "";
        });
        _ = RunAsync(task);
    }

    /// <summary>
    /// 磁力任务继续：优先原地恢复引擎会话（保留已连 peer 与校验进度，秒级生效）；
    /// 引擎里已无该会话（如 App 重启后从持久化恢复）则退回完整重跑（重新 AddAsync，
    /// 会先校验已下数据再续传）。
    /// 此前的实现只把状态置为 Queued，而回调只刷字节/速度、无人推回 Downloading，
    /// 导致任务永远卡在"排队中"且暂停/继续/重试三个按钮全都不显示。
    /// </summary>
    private async Task ResumeMagnetAsync(DownloadTaskItem task)
    {
        var bt = Bt;
        if (bt == null)
        {
            MarkFailed(task, "磁力下载引擎未初始化");
            return;
        }

        var resumed = await bt.ResumeAsync(task.Id);
        if (resumed)
        {
            // 引擎会话还在：manager 已 Start，原有周期回调会继续刷进度，这里只补状态
            UpdateTask(task, t => { t.Status = DownloadStatus.Downloading; t.Error = ""; });
            return;
        }

        await RunMagnetAsync(task, task.Url, task.LocalPath);
    }

    /// <summary>取消任务（删除临时文件，任务标记已取消）</summary>
    public void Cancel(string id)
    {
        lock (_lock)
        {
            if (_ctsMap.TryGetValue(id, out var cts))
                cts.Cancel();
        }
        var task = Find(id);
        if (task == null) return;
        DeletePartFile(task);
        UpdateTask(task, t =>
        {
            t.Status = DownloadStatus.Canceled;
            t.IsPaused = false;
            t.SpeedText = "";
        });
    }

    /// <summary>删除任务记录；deleteFile=true 时同时删除已下载文件。
    /// 返回 null=删除成功；否则返回失败原因（如文件被占用），任务记录仍会被移除。</summary>
    public string? Delete(string id, bool deleteFile = false)
    {
        var task = Find(id);
        lock (_lock)
        {
            if (_ctsMap.TryGetValue(id, out var cts)) { cts.Cancel(); cts.Dispose(); _ctsMap.Remove(id); }
        }
        if (task?.Kind == "magnet")
            _ = Bt?.RemoveAsync(id);

        string? fileError = null;
        if (task != null)
        {
            DeletePartFile(task);
            var target = task.LocalPath;
            if (deleteFile && !string.IsNullOrEmpty(target))
            {
                // BT 任务是目录（多文件种子），HTTP 任务是文件
                var isDir = task.Kind == "magnet" && Directory.Exists(target);
                if (isDir)
                {
                    try { Directory.Delete(target, recursive: true); }
                    catch (Exception ex)
                    {
                        fileError = ex is IOException
                            ? "文件夹正被占用（可能正在播放），请先停止播放后再试"
                            : $"删除失败：{ex.Message}";
                    }
                }
                else if (File.Exists(target))
                {
                    try { File.Delete(target); }
                    catch (Exception ex)
                    {
                        fileError = ex is IOException
                            ? "文件正被占用（可能正在播放），请先停止播放后再试"
                            : $"文件删除失败：{ex.Message}";
                    }
                }
            }
            MainThread.BeginInvokeOnMainThread(() => Tasks.Remove(task));
            SaveTasksOnMainThread();
            TasksChanged?.Invoke();
        }
        return fileError;
    }

    /// <summary>失败任务重试 / HTTP 已完成任务重新下载（BT 已完成不重下，避免覆盖）</summary>
    public void Retry(string id)
    {
        var task = Find(id);
        if (task == null || task.Status is not (DownloadStatus.Failed or DownloadStatus.Completed)) return;
        if (task.Status == DownloadStatus.Completed)
        {
            if (task.Kind == "magnet") return;
            DeletePartFile(task);
            try { if (File.Exists(task.LocalPath)) File.Delete(task.LocalPath); } catch { }
        }
        UpdateTask(task, t =>
        {
            t.Status = DownloadStatus.Queued;
            t.Error = "";
            t.DownloadedBytes = 0;
        });
        if (task.Kind == "magnet")
            _ = RunMagnetAsync(task, task.Url, task.LocalPath);
        else
            _ = RunAsync(task);
    }

    // ═══════════════════════════════════════════════════════
    // 内部执行
    // ═══════════════════════════════════════════════════════

    /// <summary>磁力任务执行：委托 BT 引擎下载，回调更新任务进度/状态</summary>
    private async Task RunMagnetAsync(DownloadTaskItem task, string magnet, string saveDir)
    {
        var bt = Bt;
        if (bt == null)
        {
            MarkFailed(task, "磁力下载引擎未初始化");
            return;
        }
        try
        {
            BtFileLog.Write($"[dm] 磁力任务执行开始 {task.Id} saveDir={saveDir}");
            UpdateTask(task, t => { t.Status = DownloadStatus.Downloading; t.SpeedText = ""; });
            var error = await bt.StartAsync(task.Id, magnet, saveDir,
                onState: text => UpdateTask(task, t => { if (t.Status != DownloadStatus.Completed) t.Error = text == "下载中" ? "" : text; }),
                onProgress: p => UpdateTask(task, t =>
                {
                    if (t.TotalBytes > 0) t.DownloadedBytes = (long)(t.TotalBytes * p / 100.0);
                }),
                onStats: stats => UpdateTask(task, t =>
                {
                    t.DownloadedBytes = stats.Downloaded;
                    t.TotalBytes = stats.Total;
                    t.SpeedText = stats.DownloadRate > 0 ? $"{DownloadTaskItem.FormatBytes(stats.DownloadRate)}/s" : "";
                    t.DownloadRateBytes = stats.DownloadRate;
                    t.UploadSpeedText = stats.UploadRate > 0 ? $"{DownloadTaskItem.FormatBytes(stats.UploadRate)}/s" : "";
                    t.UploadedBytes = stats.Uploaded;
                    t.Connections = stats.Connections;
                    t.Seeds = stats.Seeds;
                    if (stats.DownloadRate > 0) t.PushSpeedSample(stats.DownloadRate);
                }),
                onComplete: () =>
                {
                    if (IsTerminal(task)) return;
                    UpdateTask(task, t =>
                    {
                        t.Status = DownloadStatus.Completed;
                        t.DownloadedBytes = t.TotalBytes;
                        t.SpeedText = "";
                        t.UploadSpeedText = "";
                        t.DownloadRateBytes = 0;
                        t.Seeds = 0;
                        t.Connections = 0;
                    });
                    SaveTasksOnMainThread();
                });
            if (error != null)
            {
                MarkFailed(task, error);
            }
        }
        catch (Exception ex)
        {
            MarkFailed(task, ex.Message);
        }
    }

    /// <summary>从 magnet 链接提取展示名（dn= 参数优先，否则取 infohash）</summary>
    private static string DeriveMagnetName(string magnet)
    {
        try
        {
            var q = magnet.Contains('?') ? magnet[(magnet.IndexOf('?') + 1)..] : "";
            foreach (var part in q.Split('&'))
            {
                if (part.StartsWith("dn=", StringComparison.OrdinalIgnoreCase))
                {
                    var dn = Uri.UnescapeDataString(part[3..]).Trim();
                    if (!string.IsNullOrWhiteSpace(dn)) return SanitizeFileName(dn);
                }
            }
            var btih = System.Text.RegularExpressions.Regex.Match(magnet, @"btih:([0-9a-fA-F]{40})").Groups[1].Value;
            if (!string.IsNullOrEmpty(btih)) return btih[..12];
        }
        catch { }
        return "bt-download";
    }

    /// <summary>目标目录已存在时追加序号（BT 任务同名目录不覆盖）</summary>
    private static string GetUniqueDirPath(string path)
    {
        if (!Directory.Exists(path)) return path;
        for (int i = 1; ; i++)
        {
            var candidate = $"{path} ({i})";
            if (!Directory.Exists(candidate)) return candidate;
        }
    }

    private async Task RunAsync(DownloadTaskItem task)
    {
        var cts = new CancellationTokenSource();
        lock (_lock) _ctsMap[task.Id] = cts;
        var reserved = false;
        try
        {
            // 排队阶段即注册 cts：排队/等待 slot 的任务也能被取消，暂停同样生效
            await AcquireSlotAsync(cts.Token).ConfigureAwait(false);
            reserved = true;
            if (_disposed || IsTerminal(task)) return;

            UpdateTask(task, t => { t.Status = DownloadStatus.Downloading; t.IsPaused = false; });
            try
            {
                await DownloadFromUrlAsync(task, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 取消/暂停由控制方法处理状态
            }
            catch (Exception ex)
            {
                if (task.Status == DownloadStatus.Downloading)
                    MarkFailed(task, ex.Message);
            }
        }
        finally
        {
            if (reserved) ReleaseSlot();
            lock (_lock) { _ctsMap.Remove(task.Id); cts.Dispose(); }
        }
    }

    /// <summary>获取一个下载并发槽位（并发已满时等待通知，无固定轮询）</summary>
    private async Task AcquireSlotAsync(CancellationToken ct)
    {
        while (true)
        {
            lock (_lock)
            {
                if (_active < ConcurrentLimit)
                {
                    _active++;
                    return;
                }
            }
            await _slotWake.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private void ReleaseSlot()
    {
        lock (_lock) _active--;
        try { _slotWake.Release(1); } catch (SemaphoreFullException) { }
    }

    /// <summary>HTTP 下载：先探测总大小，再流式写入临时文件。存在有效 .part 时优先用 Range 断点续传。</summary>
    private async Task DownloadFromUrlAsync(DownloadTaskItem task, CancellationToken ct)
    {
        var partPath = task.LocalPath + ".part";
        long resumeFrom = 0;
        if (File.Exists(partPath))
        {
            try { resumeFrom = new FileInfo(partPath).Length; } catch { resumeFrom = 0; }
            if (resumeFrom <= 0) DeletePartFile(task);
        }

        using (var req = new HttpRequestMessage(HttpMethod.Get, task.Url))
        {
            if (resumeFrom > 0)
                req.Headers.TryAddWithoutValidation("Range", $"bytes={resumeFrom}-");

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (resp.StatusCode == System.Net.HttpStatusCode.PartialContent)
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentRange?.Length ??
                            resp.Content.Headers.ContentLength ?? resumeFrom;
                UpdateTask(task, t => { t.TotalBytes = total; t.DownloadedBytes = resumeFrom; });
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = new FileStream(partPath, FileMode.Append, FileAccess.Write);
                await CopyStreamAsync(task, src, dst, total, ct, start: resumeFrom).ConfigureAwait(false);
            }
            else
            {
                resp.EnsureSuccessStatusCode();
                DeletePartFile(task);
                var total = resp.Content.Headers.ContentLength ?? -1;
                UpdateTask(task, t => t.TotalBytes = total);
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = new FileStream(partPath, FileMode.Create, FileAccess.Write);
                await CopyStreamAsync(task, src, dst, total, ct).ConfigureAwait(false);
            }
        }

        // 取消/暂停时保留 .part 供后续续传；正常完成才落盘为最终文件
        if (ct.IsCancellationRequested) return;
        FinalizeDownload(task, partPath);
    }

    /// <summary>流式拷贝：进度/速度按 ~500ms 节流上报主线程</summary>
    private async Task CopyStreamAsync(DownloadTaskItem task, Stream src, FileStream dst, long total, CancellationToken ct, long start = 0)
    {
        var buffer = new byte[81920];
        long read = start;
        long lastTick = Environment.TickCount64;
        long lastBytes = start;
        UpdateTask(task, t => { if (total > 0) t.TotalBytes = total; t.DownloadedBytes = read; });

        int n;
        while ((n = await src.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer, 0, n, ct).ConfigureAwait(false);
            read += n;

            if (Environment.TickCount64 - lastTick >= 500)
            {
                var elapsedSec = (Environment.TickCount64 - lastTick) / 1000.0;
                var speed = (read - lastBytes) / Math.Max(elapsedSec, 0.001);
                var speedText = $"{DownloadTaskItem.FormatBytes((long)speed)}/s";
                UpdateTask(task, t =>
                {
                    if (total > 0) t.TotalBytes = total;
                    t.DownloadedBytes = read;
                    t.SpeedText = speedText;
                });
                lastTick = Environment.TickCount64;
                lastBytes = read;
            }
        }
        UpdateTask(task, t => { t.DownloadedBytes = read; t.SpeedText = ""; });
    }

    private void FinalizeDownload(DownloadTaskItem task, string partPath)
    {
        try
        {
            if (File.Exists(task.LocalPath)) File.Delete(task.LocalPath);
            File.Move(partPath, task.LocalPath);
        }
        catch (Exception ex)
        {
            DeletePartFile(task);
            MarkFailed(task, $"写入文件失败：{ex.Message}");
            return;
        }
        UpdateTask(task, t =>
        {
            t.Status = DownloadStatus.Completed;
            t.SpeedText = "";
            try { t.TotalBytes = new FileInfo(task.LocalPath).Length; } catch { }
            t.DownloadedBytes = t.TotalBytes;
        });
        SaveTasksOnMainThread();
        TasksChanged?.Invoke();
    }

    private void MarkFailed(DownloadTaskItem task, string message)
    {
        BtFileLog.Write($"[dm] 任务失败 {task.Id}：{message}");
        DeletePartFile(task);
        UpdateTask(task, t => { t.Status = DownloadStatus.Failed; t.Error = message; t.SpeedText = ""; });
        SaveTasksOnMainThread();
        TasksChanged?.Invoke();
    }

    // ═══════════════════════════════════════════════════════
    // 工具
    // ═══════════════════════════════════════════════════════

    private void AddTask(DownloadTaskItem item)
    {
        MainThread.BeginInvokeOnMainThread(() => Tasks.Insert(0, item));
        SaveTasksOnMainThread();
        TasksChanged?.Invoke();
    }

    /// <summary>把任务列表序列化落盘。⚠ 必须投递主线程执行：状态修改与 Tasks 增删均异步投递
    /// 主线程，主线程 FIFO 保证先应用内存状态再序列化（音乐版历史 bug：线程池先跑会写旧状态）。</summary>
    private void SaveTasksOnMainThread() => MainThread.BeginInvokeOnMainThread(SaveTasks);

    private void UpdateTask(DownloadTaskItem task, Action<DownloadTaskItem> apply)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            apply(task);
            task.RaiseDerivedChanged();
            TaskUpdated?.Invoke(task);
        });
    }

    private DownloadTaskItem? Find(string id) => Tasks.FirstOrDefault(t => t.Id == id);

    private static bool IsTerminal(DownloadTaskItem task) =>
        task.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Canceled;

    private static void DeletePartFile(DownloadTaskItem task)
    {
        try { if (File.Exists(task.LocalPath + ".part")) File.Delete(task.LocalPath + ".part"); } catch { }
    }

    /// <summary>从 URL 推导文件名；不合法时用 fallback</summary>
    private static string DeriveFileName(string url, string fallback)
    {
        try
        {
            var name = Path.GetFileName(new Uri(url).AbsolutePath);
            if (!string.IsNullOrWhiteSpace(name)) return SanitizeFileName(name);
        }
        catch { }
        return fallback;
    }

    /// <summary>清理文件名中的非法字符</summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Where(c => !invalid.Contains(c) && c != '/' && c != '\\').ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "download" : cleaned;
    }

    /// <summary>目标文件已存在时追加序号，避免覆盖</summary>
    private static string GetUniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var ext = Path.GetExtension(path);
        var baseName = Path.GetFileNameWithoutExtension(path);
        for (int i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{baseName} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    // ═══════════════════════════════════════════════════════
    // 持久化
    // ═══════════════════════════════════════════════════════

    private class TaskDto
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public string LocalPath { get; set; } = "";
        public string Kind { get; set; } = "url";
        public long CreatedAt { get; set; }
        public long TotalBytes { get; set; }
        public long DownloadedBytes { get; set; }
        public DownloadStatus Status { get; set; }
        public string Error { get; set; } = "";
    }

    private void SaveTasks()
    {
        try
        {
            List<TaskDto> list;
            lock (_lock)
            {
                list = Tasks.Select(t => new TaskDto
                {
                    Id = t.Id,
                    Name = t.Name,
                    Url = t.Url,
                    LocalPath = t.LocalPath,
                    Kind = t.Kind,
                    CreatedAt = t.CreatedAt,
                    TotalBytes = t.TotalBytes,
                    DownloadedBytes = t.DownloadedBytes,
                    Status = t.Status,
                    Error = t.Error
                }).ToList();
            }
            File.WriteAllText(TasksFilePath, JsonSerializer.Serialize(list));
        }
        catch { }
    }

    private void LoadTasks()
    {
        try
        {
            if (!File.Exists(TasksFilePath)) return;
            var list = JsonSerializer.Deserialize<List<TaskDto>>(File.ReadAllText(TasksFilePath));
            if (list == null) return;
            foreach (var dto in list.OrderByDescending(d => d.CreatedAt))
            {
                // 恢复时：下载中的任务视为暂停，避免应用重启后自动重下
                var status = dto.Status == DownloadStatus.Downloading ? DownloadStatus.Paused : dto.Status;
                var item = new DownloadTaskItem
                {
                    Id = dto.Id,
                    Name = dto.Name,
                    Url = dto.Url,
                    LocalPath = dto.LocalPath,
                    Kind = dto.Kind,
                    CreatedAt = dto.CreatedAt,
                    TotalBytes = dto.TotalBytes,
                    DownloadedBytes = dto.DownloadedBytes,
                    Status = status,
                    Error = dto.Error,
                    IsPaused = status == DownloadStatus.Paused
                };
                Tasks.Add(item);

                // 磁力任务：重启后自动续传（BT 引擎校验已下数据后继续，无需重新开始）
                if (dto.Kind == "magnet" && Bt != null
                    && status is DownloadStatus.Queued or DownloadStatus.Paused or DownloadStatus.Downloading)
                {
                    item.Status = DownloadStatus.Queued;
                    item.IsPaused = false;
                    _ = RunMagnetAsync(item, dto.Url, dto.LocalPath);
                }
            }
        }
        catch { }
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_lock)
        {
            foreach (var cts in _ctsMap.Values) cts.Cancel();
            _ctsMap.Clear();
        }
        _http.Dispose();
        _slotWake.Dispose();
    }
}
