using System.Collections.ObjectModel;
using CatClawVideo.Maui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CatClawVideo.Maui.ViewModels;

/// <summary>
/// 下载管理页 ViewModel：展示下载任务列表（进度/状态/速度），
/// 提供暂停、继续、取消、重试、删除与新建下载等操作。
/// </summary>
public partial class DownloadsViewModel : ObservableObject, IDisposable
{
    private readonly DownloadManager _manager;

    /// <summary>下载任务列表（直接引用 DownloadManager 的任务集合）</summary>
    public ObservableCollection<DownloadTaskItem> Tasks => _manager.Tasks;

    /// <summary>任务总数</summary>
    [ObservableProperty]
    private int _totalCount;

    /// <summary>进行中任务数（排队 + 下载中）</summary>
    [ObservableProperty]
    private int _activeCount;

    /// <summary>已完成任务数</summary>
    [ObservableProperty]
    private int _completedCount;

    /// <summary>当前下载目录</summary>
    [ObservableProperty]
    private string _downloadPath = "";

    /// <summary>是否为空列表</summary>
    public bool IsEmpty => TotalCount == 0;

    public DownloadsViewModel(DownloadManager manager)
    {
        _manager = manager;
        _manager.TasksChanged += OnTasksChanged;
        _manager.TaskUpdated += OnTaskUpdated;
        RefreshStats();
    }

    private void OnTasksChanged() => RefreshStats();
    private void OnTaskUpdated(DownloadTaskItem _) => RefreshStats();

    /// <summary>刷新下载统计（由 DownloadsPage 在更改下载目录后调用）。
    /// <para>订阅了 TaskUpdated（进度上报）→ 调用频繁，故先算再比，值没变就不触发属性通知
    /// （避免每次进度回调都惊动绑定系统）。</para></summary>
    public void RefreshStats()
    {
        int total = Tasks.Count, active = 0, completed = 0;
        foreach (var t in Tasks)
        {
            if (t.Status is DownloadStatus.Queued or DownloadStatus.Downloading) active++;
            else if (t.Status == DownloadStatus.Completed) completed++;
        }

        if (TotalCount != total)
        {
            TotalCount = total;
            OnPropertyChanged(nameof(IsEmpty));
        }
        if (ActiveCount != active) ActiveCount = active;
        if (CompletedCount != completed) CompletedCount = completed;

        var path = _manager.DownloadFolderPath;
        if (DownloadPath != path) DownloadPath = path;
    }

    /// <summary>新建下载任务（http/https 直链与 magnet: 磁力——磁力走迅雷引擎下载）</summary>
    public void AddUrlDownload(string url, string? fileName = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            _manager.EnqueueMagnet(url.Trim());
            return;
        }
        _manager.EnqueueUrl(url.Trim(), fileName);
    }

    /// <summary>暂停任务</summary>
    [RelayCommand]
    public void PauseTask(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id)) _manager.Pause(id);
    }

    /// <summary>继续任务</summary>
    [RelayCommand]
    public void ResumeTask(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id)) _manager.Resume(id);
    }

    /// <summary>取消任务</summary>
    [RelayCommand]
    public void CancelTask(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id)) _manager.Cancel(id);
    }

    /// <summary>失败任务重试</summary>
    [RelayCommand]
    public void RetryTask(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id)) _manager.Retry(id);
    }

    /// <summary>删除任务记录（deleteFile=true 时同时删除已下载文件）</summary>
    [RelayCommand]
    public void DeleteTask(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id)) _manager.Delete(id);
    }

    /// <summary>清空已结束的任务（完成/失败/取消）</summary>
    [RelayCommand]
    public void ClearFinished()
    {
        var finished = Tasks.Where(t => t.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Canceled).ToList();
        foreach (var t in finished) _manager.Delete(t.Id);
    }

    public void Dispose()
    {
        _manager.TasksChanged -= OnTasksChanged;
        _manager.TaskUpdated -= OnTaskUpdated;
    }
}
