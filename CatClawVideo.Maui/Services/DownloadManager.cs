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
        CatClawVideo.Core.AppPaths.Of("download_tasks.json");

    private readonly HttpClient _http;
    /// <summary>迅雷磁力引擎（仅 Windows 注入；磁力下载=引擎独占下载+经媒体口导出本机）</summary>
    private readonly CatClawVideo.Core.Services.QemuThunder.QemuThunderEngine? _thunder;
    /// <summary>排队任务等待"并发槽位空出"的通知信号</summary>
    private readonly SemaphoreSlim _slotWake = new(0);

    /// <summary>
    /// 磁力任务的**单并发闸**（与 HTTP 下载的 <see cref="_active"/><see cref="ConcurrentLimit"/> 分离）。
    ///
    /// <para>迅雷引擎是**单 VM 单会话**：新任务会把 <c>QemuThunderEngine._session</c> 顶掉，
    /// 正在跑的旧任务随即判定「会话被替换」失败。而 <c>ConcurrentLimit</c>（默认 2）是为 HTTP
    /// 下载设计的，两个磁力同时进来必然互顶 —— 表现为「暂停后恢复提示引擎被占用」
    /// （2026-09-20 用户实测）。故磁力永远串行，与用户设置的并发数无关。</para>
    /// </summary>
    private readonly SemaphoreSlim _magnetGate = new(1, 1);
    private readonly Dictionary<string, CancellationTokenSource> _ctsMap = new();
    private readonly object _lock = new();
    /// <summary>落盘串行化（主线程与后台线程都会触发保存，避免写出半截文件）</summary>
    private readonly object _saveLock = new();
    private int _active;
    private bool _disposed;

    /// <summary>对账时可认领为「已完成成果」的视频扩展名。</summary>
    private static readonly HashSet<string> ReconcileVideoExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".ts", ".mov", ".wmv", ".flv", ".m2ts", ".webm", ".mpg", ".mpeg", ".m4v", ".rmvb", ".rm"
    };

    /// <summary>当前最大并发下载任务数</summary>
    public int ConcurrentLimit { get; private set; } = DefaultConcurrent;

    /// <summary>是否支持磁力下载（需迅雷引擎就绪；Android 端恒为 false）。
    /// 宿主据此在点击「下载」时给出明确反馈，而不是让任务建了再失败。</summary>
    public bool SupportsMagnetDownload => _thunder is { IsReady: true };

    /// <summary>下载任务集合（按创建时间排序）</summary>
    public ObservableCollection<DownloadTaskItem> Tasks { get; } = new();

    /// <summary>任务集合变化（增删）时触发</summary>
    public event Action? TasksChanged;

    /// <summary>单个任务进度/状态变化时触发</summary>
    public event Action<DownloadTaskItem>? TaskUpdated;

    public DownloadManager(CatClawVideo.Core.Services.QemuThunder.QemuThunderEngine? thunderEngine = null)
    {
        _thunder = thunderEngine;
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

    /// <summary>新建磁力下载任务（Windows：迅雷引擎独占下载选中文件后经媒体口导出本机）。
    /// 与播放共用同一个引擎 VM：下载期间起播会打断下载（以播放优先），任务可重试续传。</summary>
    public DownloadTaskItem EnqueueMagnet(string magnet, string? fileName = null)
    {
        var name = SanitizeFileName(string.IsNullOrWhiteSpace(fileName)
            ? DeriveMagnetName(magnet)
            : fileName);
        var item = CreateTask(magnet, name, "magnet");
        _ = RunAsync(item);
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

    /// <summary>继续任务（重新发起下载；磁力走迅雷引擎重建任务续传，HTTP 用 .part 断点续传）</summary>
    public void Resume(string id)
    {
        var task = Find(id);
        if (task == null) return;
        if (task.Kind == "magnet")
        {
            // 磁力任务的 Queued 是"死状态"：它不走并发槽位队列，一旦进入且无人推回
            // Downloading 就永远卡在"排队中"且三个操作按钮都不显示。
            // 所以磁力任务允许从 Paused / Queued 两种状态继续（Queued 覆盖 App 重启后
            // 从持久化恢复的任务——它们的管理器已不存在，必须走完整重跑）。
            if (task.Status is not (DownloadStatus.Paused or DownloadStatus.Queued or DownloadStatus.Failed)) return;
            UpdateTask(task, t => { t.Status = DownloadStatus.Queued; t.IsPaused = false; t.Error = ""; });
            _ = RunAsync(task);
            return;
        }
        if (task.Status != DownloadStatus.Paused) return;

        UpdateTask(task, t =>
        {
            t.Status = DownloadStatus.Queued;
            t.IsPaused = false;
            t.Error = "";
        });
        _ = RunAsync(task);
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
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Tasks.Remove(task);
                SaveTasks();
                TasksChanged?.Invoke();
            });
        }
        return fileError;
    }

    /// <summary>失败任务重试 / 已完成任务重新下载（磁力已完成的重新导出引擎侧文件，若 tmpfs 仍在）</summary>
    public void Retry(string id)
    {
        var task = Find(id);
        if (task == null || task.Status is not (DownloadStatus.Failed or DownloadStatus.Completed)) return;
        if (task.Status == DownloadStatus.Completed)
        {
            DeletePartFile(task);
            try { if (File.Exists(task.LocalPath)) File.Delete(task.LocalPath); } catch { }
        }
        UpdateTask(task, t =>
        {
            t.Status = DownloadStatus.Queued;
            t.Error = "";
            t.DownloadedBytes = 0;
        });
        _ = RunAsync(task);
    }

    // ═══════════════════════════════════════════════════════
    // 内部执行
    // ═══════════════════════════════════════════════════════

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
        var magnetSlot = false;
        try
        {
            // ── 磁力：引擎是**单会话**，必须独占跑，不能与任何其他磁力并发 ──
            //
            // 2026-09-20 用户实测「暂停后恢复提示引擎被占用」的真因之一：并发槽位
            // （默认 2）是按 HTTP 下载设计的，对磁力**不适用** —— 两个磁力任务同时进来时，
            // 后到的会把 QemuThunderEngine._session 顶掉（单 VM 单会话），先到的随即
            // 判定「会话被替换」返回 false，界面报「引擎被播放占用」。
            // 这里给磁力单独一道**单并发闸**，与 HTTP 的 _active/ConcurrentLimit 完全分开：
            // 无论用户把并发设成几，磁力永远串行。
            if (task.Kind == "magnet")
            {
                await _magnetGate.WaitAsync(cts.Token).ConfigureAwait(false);
                magnetSlot = true;
            }
            else
            {
                await AcquireSlotAsync(cts.Token).ConfigureAwait(false);
            }
            reserved = true;
            if (_disposed || IsTerminal(task)) return;

            UpdateTask(task, t => { t.Status = DownloadStatus.Downloading; t.IsPaused = false; });
            try
            {
                if (task.Kind == "magnet")
                    await DownloadMagnetAsync(task, cts.Token).ConfigureAwait(false);
                else
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
            if (reserved && !magnetSlot) ReleaseSlot();
            if (magnetSlot) { try { _magnetGate.Release(); } catch (SemaphoreFullException) { } }
            lock (_lock) { _ctsMap.Remove(task.Id); cts.Dispose(); }
        }
    }

    /// <summary>获取一个 HTTP 下载并发槽位（并发已满时等待通知，无固定轮询）</summary>
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

    /// <summary>磁力下载（迅雷引擎）：引擎解析文件列表 → 选中文件（任务名匹配，否则最大视频）独占
    /// 下载 → 完成后经媒体口导出到本机下载目录。进度/速度沿用任务卡片展示；
    /// 与播放共用同一个 VM（单会话），播放优先——被打断的任务置失败，重试可从 .part 续传。</summary>
    private async Task DownloadMagnetAsync(DownloadTaskItem task, CancellationToken ct)
    {
        var engine = _thunder;
        if (engine is null || !engine.IsReady)
        {
            MarkFailed(task, "迅雷引擎不可用：磁力下载目前仅 Windows 版支持");
            return;
        }

        long lastTick = 0, lastBytes = 0;
        var ok = false;
        var reason = "";
        try
        {
            // 用带原因的重载：失败时不再只拿到一个 bool（那样只能写「占用/超时/无源」的模糊文案，
            // 实测把引擎的 9128 任务已存在显示成「被占用」，把排查方向带偏）。
            (ok, reason) = await engine.DownloadToFileExAsync(
                task.Url, task.Name,
                destPathFor: pickName =>
                {
                    // 引擎解析出的真实文件名（含扩展名）回填任务，并去重
                    var newPath = GetUniquePath(Path.Combine(DownloadFolderPath, SanitizeFileName(pickName)));
                    UpdateTask(task, t => { t.Name = Path.GetFileName(newPath); t.LocalPath = newPath; });
                    return newPath;
                },
                progress: (done, total) =>
                {
                    // ★ 节流必须在**投递之前**判定（2026-09-19 实测）：UpdateTask 内部是
                    //   MainThread.BeginInvokeOnMainThread，若把它放在频率判断之外，即使不需要
                    //   刷新速度也照样每次投递一个 UI 任务，足以灌满消息队列导致界面未响应。
                    var now = Environment.TickCount64;
                    if (lastTick != 0 && now - lastTick < 500) return;

                    string? speedText = null;
                    if (lastTick != 0)
                    {
                        var bps = (long)((done - lastBytes) / Math.Max((now - lastTick) / 1000.0, 0.001));
                        speedText = DownloadTaskItem.FormatBytes(bps) + "/s";
                    }
                    lastTick = now; lastBytes = done;
                    UpdateTask(task, t => { t.TotalBytes = total; t.DownloadedBytes = done; if (speedText != null) t.SpeedText = speedText; });
                },
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }

        if (ok)
        {
            FinalizeDownload(task, task.LocalPath + ".part");
        }
        else if (!ct.IsCancellationRequested && task.Status == DownloadStatus.Downloading)
        {
            MarkFailed(task, string.IsNullOrEmpty(reason)
                ? "磁力下载未完成（重试可从已下部分续传）"
                : $"磁力下载未完成：{reason}（重试可从已下部分续传）");
        }
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

        // ★ 状态变更 + 落盘必须在**同一次主线程派发**里完成（2026-09-19 实测事故）：
        //   此前是 UpdateTask（派发主线程）+ SaveTasksOnMainThread（再派发一次），
        //   两次派发之间若主线程被别的工作堵住、用户此时强关进程，磁盘上留下的就是
        //   「任务刚创建时」的旧快照 —— 进度归零、点不动播放，而文件其实是好的。
        //   合并成一次派发后，状态与持久化要么都完成、要么都不发生。
        MainThread.BeginInvokeOnMainThread(() =>
        {
            task.Status = DownloadStatus.Completed;
            task.SpeedText = "";
            try { task.TotalBytes = new FileInfo(task.LocalPath).Length; } catch { }
            task.DownloadedBytes = task.TotalBytes;
            task.RaiseDerivedChanged();
            TaskUpdated?.Invoke(task);
            SaveTasks();            // 直接写盘（已加锁），不排队
            TasksChanged?.Invoke();
        });
    }

    private void MarkFailed(DownloadTaskItem task, string message)
    {
        BtFileLog.Write($"[dm] 任务失败 {task.Id}：{message}");
        DeletePartFile(task);
        // 状态与落盘合并为一次派发（理由见 FinalizeDownload）
        MainThread.BeginInvokeOnMainThread(() =>
        {
            task.Status = DownloadStatus.Failed;
            task.Error = message;
            task.SpeedText = "";
            task.RaiseDerivedChanged();
            TaskUpdated?.Invoke(task);
            SaveTasks();
            TasksChanged?.Invoke();
        });
    }

    // ═══════════════════════════════════════════════════════
    // 工具
    // ═══════════════════════════════════════════════════════

    private void AddTask(DownloadTaskItem item)
    {
        // 建任务与落盘同一次派发：新建后即使立刻强关进程，任务记录也在磁盘上
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Tasks.Insert(0, item);
            SaveTasks();
            TasksChanged?.Invoke();
        });
    }

    /// <summary>把任务列表序列化落盘（投递主线程执行，保证内存状态已应用）。
    /// 关键状态变更路径请直接调用 <see cref="SaveTasks"/>（已在主线程派发内），避免二次排队。</summary>
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

    /// <summary>序列化任务列表落盘。主线程与后台线程都会调用，串行化避免写出半截文件。
    /// ⚠ 不依赖 MainThread：状态是必须持久化的关键数据，不能因为 UI 忙就丢掉
    /// （2026-09-19 实测：进度风暴堵住主线程 → 已完成状态没写盘 → 重启后进度归零）。</summary>
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
            var json = JsonSerializer.Serialize(list);
            lock (_saveLock)
            {
                // 先写临时文件再替换：避免进程在写一半时被杀导致 JSON 损坏（整份任务列表丢失）
                var tmp = TasksFilePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, TasksFilePath, overwrite: true);
            }
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
            }

            ReconcileWithDisk();
        }
        catch { }
    }

    /// <summary>
    /// 启动对账：把「未完成但磁盘上其实已有成品」的任务修正为已完成。
    ///
    /// <para><b>为什么必需</b>（2026-09-19 用户实测）：导出完成后立即写盘的时机若被打断
    /// （UI 线程被进度回调灌满、用户强关进程），落盘里留下的是**任务刚创建时**的快照
    /// （Status=Queued、0 字节、LocalPath 还带着种子名而非真实文件名），而磁盘上文件是好的。
    /// 结果：进度显示 0、点不动播放 —— 已下载成果明明在，用户体验却是「白下了」。</para>
    ///
    /// <para>匹配用「任务名做子串」：引擎导出后的真实文件名通常包含任务名
    /// （如任务名「流人.Slow.Horses.S06E01.6v电影 地址发布页 www.6v123.net 收藏不迷路」
    /// → 文件名「流人.Slow.Horses.S06E01.Circle.of.Life.1080p.HD中英双字[...].mp4」）。
    /// 命中且大小与原记录明显不符（未完成态）时，取该目录下最大的匹配视频。</para>
    /// </summary>
    private void ReconcileWithDisk()
    {
        var dir = DownloadFolderPath;
        if (!Directory.Exists(dir)) return;

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
                .Where(f => ReconcileVideoExts.Contains(Path.GetExtension(f)))
                .ToList();
        }
        catch { return; }
        if (files.Count == 0) return;

        var owned = new HashSet<string>(Tasks.Select(t => t.LocalPath), StringComparer.OrdinalIgnoreCase);

        foreach (var task in Tasks)
        {
            // 只救「未完成」且文件不在记录里的任务
            if (task.Status is DownloadStatus.Completed or DownloadStatus.Canceled) continue;
            if (File.Exists(task.LocalPath)) continue;          // 记录路径本身就在 → 走正常加载

            var hit = FindArtifact(files, owned, task);
            if (hit is null) continue;

            long size = 0;
            try { size = new FileInfo(hit).Length; } catch { }
            if (size <= 0) continue;

            task.LocalPath = hit;
            task.Name = Path.GetFileName(hit);
            task.TotalBytes = size;
            task.DownloadedBytes = size;
            task.Status = DownloadStatus.Completed;
            task.Error = string.Empty;
            task.IsPaused = false;
            owned.Add(hit);
            BtFileLog.Write($"[dm] 启动对账：任务 {task.Id} 认领磁盘成品 {Path.GetFileName(hit)}（{size / 1048576.0:F1}MB）");
        }

        SaveTasks();   // 修正结果立即落盘，避免下次重启再对一次
    }

    /// <summary>
    /// 按任务名在下载目录里查找该任务的成品文件（播放入口在记录路径失效时的兜底）。
    /// 找到会**顺带修正任务记录并落盘**，避免下次点击又找不到。
    /// </summary>
    public string? TryFindArtifact(DownloadTaskItem task)
    {
        var dir = DownloadFolderPath;
        if (!Directory.Exists(dir)) return null;

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Where(f => ReconcileVideoExts.Contains(Path.GetExtension(f)))
                .ToList();
        }
        catch { return null; }
        if (files.Count == 0) return null;

        var owned = new HashSet<string>(
            Tasks.Where(t => !ReferenceEquals(t, task)).Select(t => t.LocalPath),
            StringComparer.OrdinalIgnoreCase);

        var hit = FindArtifact(files, owned, task);
        if (hit is null) return null;

        long size = 0;
        try { size = new FileInfo(hit).Length; } catch { }
        task.LocalPath = hit;
        task.Name = Path.GetFileName(hit);
        if (size > 0) { task.TotalBytes = size; task.DownloadedBytes = size; }
        task.Status = DownloadStatus.Completed;
        task.RaiseDerivedChanged();
        SaveTasks();
        BtFileLog.Write($"[dm] 点击播放兜底命中：{Path.GetFileName(hit)}");
        return hit;
    }

    /// <summary>在候选文件里找该任务的成品：优先任务名子串匹配（排除已被其他任务认领的）。</summary>
    private static string? FindArtifact(List<string> files, HashSet<string> owned, DownloadTaskItem task)
    {
        var key = NormalizeForMatch(task.Name);
        IEnumerable<string> candidates = files;

        if (key.Length >= 4)
        {
            // 任务名常带站点水印（「地址发布页 www.6v123.net 收藏不迷路」），而真实文件名不含，
            // 故用「任务名去掉水印后的主体」做匹配 —— 取前 12 个字符就足够唯一。
            var stem = NormalizeForMatch(key[..Math.Min(key.Length, 12)]);
            var matched = files
                .Where(f => NormalizeForMatch(Path.GetFileName(f)).Contains(stem, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matched.Count > 0) candidates = matched;
        }

        return candidates
            .Where(f => !owned.Contains(f))
            .OrderByDescending(f => { try { return new FileInfo(f).Length; } catch { return 0L; } })
            .FirstOrDefault();
    }

    /// <summary>归一化用于子串匹配：去掉空白与常见分隔符。</summary>
    private static string NormalizeForMatch(string s) =>
        new string(s.Where(c => !char.IsWhiteSpace(c) && c != '.' && c != '_' && c != '-').ToArray());

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
