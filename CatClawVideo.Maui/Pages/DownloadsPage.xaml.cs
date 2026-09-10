using System.IO;
using CatClawVideo.Maui.Services;
using CatClawVideo.Maui.ViewModels;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 下载管理页：展示与管理下载任务（新建 URL/磁力下载、暂停/继续/取消/重试/删除、并发设置）。
/// 复刻猫爪音乐下载管理器（任务模型/事件驱动进度/操作分发），视频化：已完成视频文件点卡片直接进播放页。
/// </summary>
public partial class DownloadsPage : ContentView, ITabView
{
    private readonly DownloadsViewModel _vm;
    private readonly DownloadManager _manager;

    /// <summary>可直接播放的视频扩展名（已完成任务点卡片找这类文件）</summary>
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".ts", ".mov", ".wmv", ".flv", ".m2ts", ".webm", ".mpg", ".mpeg", ".m4v", ".rmvb", ".rm"
    };

    public DownloadsPage(DownloadsViewModel vm, DownloadManager manager)
    {
        InitializeComponent();
        _vm = vm;
        _manager = manager;
        BindingContext = vm;
    }

    /// <summary>切到本 tab 时刷新统计（任务数 / 下载目录）；列表本身由 DownloadManager 事件驱动</summary>
    public Task OnTabShownAsync()
    {
        _vm.RefreshStats();
        return Task.CompletedTask;
    }

    /// <summary>右上角 ⚙：打开下载 / BT 设置页</summary>
    private async void OnBtSettingsTapped(object? sender, EventArgs e)
    {
        try { await Shell.Current.GoToAsync("btsettings"); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
    }

    // ═══════════════ Motrix 风格卡片图标操作 ═══════════════

    /// <summary>删除确认弹层待处理的任务 ID</summary>
    private string? _pendingDeleteId;

    /// <summary>卡片图标按钮（Border.ClassId 标动作，BindingContext 为任务项）</summary>
    private void OnTaskIconTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Border { ClassId: string action } border) return;
        if (border.BindingContext is not DownloadTaskItem task) return;
        switch (action)
        {
            case "pause": _vm.PauseTask(task.Id); break;
            case "resume": _vm.ResumeTask(task.Id); break;
            case "retry": _vm.RetryTask(task.Id); break;
            case "delete": ShowDeleteConfirm(task); break;
            case "folder": OpenTaskFolder(task); break;
            case "copy": _ = CopyTaskLinkAsync(task); break;
            case "info": _ = OpenTaskDetailAsync(task); break;
        }
    }

    /// <summary>删除确认（Motrix：是/否 + 同时删除文件）</summary>
    private void ShowDeleteConfirm(DownloadTaskItem task)
    {
        _pendingDeleteId = task.Id;
        DeleteConfirmText.Text = $"你确定要移除「{task.DisplayName}」下载任务吗？";
        DeleteFileCheck.IsChecked = false;
        DeleteOverlay.IsVisible = true;
    }

    private void OnDeleteCancelled(object? sender, TappedEventArgs e)
    {
        DeleteOverlay.IsVisible = false;
        _pendingDeleteId = null;
    }

    private async void OnDeleteConfirmed(object? sender, TappedEventArgs e)
    {
        var id = _pendingDeleteId;
        var deleteFile = DeleteFileCheck.IsChecked == true;
        DeleteOverlay.IsVisible = false;
        _pendingDeleteId = null;
        if (string.IsNullOrEmpty(id)) return;
        var error = await Task.Run(() => _manager.Delete(id, deleteFile));
        if (error != null)
            await AlertAsync("文件删除失败", error, "确定");
    }

    /// <summary>打开任务所在目录（BT 任务是目录，HTTP 任务是文件所在目录）</summary>
    private async void OpenTaskFolder(DownloadTaskItem task)
    {
        try
        {
            var path = task.Kind == "magnet"
                ? task.LocalPath
                : (Path.GetDirectoryName(task.LocalPath) ?? task.LocalPath);
            if (!Directory.Exists(path))
                path = Path.GetDirectoryName(path) ?? path;
#if WINDOWS
            if (Directory.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                });
            else
                await AlertAsync("目录不存在", $"路径：{path}", "确定");
#else
            await AlertAsync("下载目录", $"路径：{path}", "确定");
#endif
        }
        catch (Exception ex)
        {
            await AlertAsync("打开目录失败", ex.Message, "确定");
        }
    }

    /// <summary>复制任务链接（磁力或直链）到剪贴板</summary>
    private async Task CopyTaskLinkAsync(DownloadTaskItem task)
    {
        try
        {
            await Clipboard.Default.SetTextAsync(task.Url);
            await AlertAsync("已复制", "任务链接已复制到剪贴板", "确定");
        }
        catch (Exception ex)
        {
            await AlertAsync("复制失败", ex.Message, "确定");
        }
    }

    /// <summary>打开任务详情页（分片图/文件/Tracker/统计）</summary>
    private async Task OpenTaskDetailAsync(DownloadTaskItem task)
    {
        try
        {
            await Shell.Current.GoToAsync($"downloaddetail?id={Uri.EscapeDataString(task.Id)}");
        }
        catch (Exception ex)
        {
            await AlertAsync("打开详情失败", ex.Message, "确定");
        }
    }

    /// <summary>右上角 ＋ ：新建下载任务（支持 http/https 直链与 magnet: 磁力链接）</summary>
    private async void OnAddDownloadTapped(object? sender, EventArgs e)
    {
        var url = await PromptAsync("新建下载", "输入下载地址\n支持：http/https 直链、magnet: 磁力链接", "开始下载", "取消",
            placeholder: "https://... 或 magnet:?xt=urn:btih:...", keyboard: Keyboard.Url);
        if (string.IsNullOrWhiteSpace(url)) return;

        string? name = null;
        if (!url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            name = await PromptAsync("文件名称", "输入保存文件名（留空自动识别）", "开始下载", "取消",
                placeholder: "video.mp4", keyboard: Keyboard.Text);
        }
        _vm.AddUrlDownload(url, string.IsNullOrWhiteSpace(name) ? null : name);
    }

    /// <summary>⚙ 下载设置：并发任务数（复刻音乐版并发槽位；用 ActionSheet 简化面板）</summary>
    private async void OnSettingsTapped(object? sender, EventArgs e)
    {
        var choice = await AlertActionAsync("同时下载任务数",
            new[] { "1 个", "2 个", "3 个", "4 个", "5 个" });
        if (choice != null && int.TryParse(choice.AsSpan(0, 1), out var n))
            _manager.SetConcurrentLimit(n);
    }

    /// <summary>任务操作按钮统一入口（ClassId 标记动作，不依赖绑定树）</summary>
    private async void OnTaskActionClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string id } btn) return;
        switch (btn.ClassId)
        {
            case "pause": _vm.PauseTask(id); break;
            case "resume": _vm.ResumeTask(id); break;
            case "cancel": _vm.CancelTask(id); break;
            case "retry":
                var retryItem = _manager.Tasks.FirstOrDefault(t => t.Id == id);
                if (retryItem?.Status == DownloadStatus.Completed && retryItem.Kind != "magnet")
                {
                    var reOk = await ConfirmAsync("重新下载", "将重新下载该文件并覆盖本地，是否继续？", "重新下载", "取消");
                    if (!reOk) break;
                }
                _vm.RetryTask(id); break;
            case "delete":
                var delOk = await ConfirmAsync("删除任务", "仅删除任务记录（保留已下载文件），确定？", "删除", "取消");
                if (delOk) _vm.DeleteTask(id);
                break;
            case "deletefile":
                var delFileOk = await ConfirmAsync("删除任务及文件", "将删除任务并移除其已下载文件，确定？", "删除", "取消");
                if (!delFileOk) break;
                var error = await Task.Run(() => _manager.Delete(id, deleteFile: true));
                if (error != null)
                    await AlertAsync("文件删除失败", error, "确定");
                break;
        }
    }

    /// <summary>点击任务卡片：已完成任务找视频文件直接进播放页；磁力目录取最大的视频文件</summary>
    private async void OnTaskTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Border { BindingContext: DownloadTaskItem item }) return;
        if (item.Status != DownloadStatus.Completed) return;

        var path = item.LocalPath;
        var isDir = Directory.Exists(path);
        if (!isDir && !File.Exists(path))
        {
            await AlertAsync("提示", "文件不存在或已被移动", "确定");
            return;
        }

        // 定位可播放的视频文件：文件直接用；目录（BT 多文件）取最大的视频文件
        string? video = null;
        if (isDir)
        {
            video = SafeEnumerateFiles(path)
                .Where(f => VideoExtensions.Contains(System.IO.Path.GetExtension(f)))
                .OrderByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();
            if (video == null)
            {
                await AlertAsync("下载完成", $"文件已保存到：\n{path}", "确定");
                return;
            }
        }
        else
        {
            if (!VideoExtensions.Contains(System.IO.Path.GetExtension(path)))
            {
                await AlertAsync("下载完成", $"文件已保存到：\n{path}", "确定");
                return;
            }
            video = path;
        }

        var title = System.IO.Path.GetFileNameWithoutExtension(video);
        await Shell.Current.GoToAsync(
            $"player?title={Uri.EscapeDataString(title)}&url={Uri.EscapeDataString(video)}");
    }

    private static IEnumerable<string> SafeEnumerateFiles(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories); }
        catch { return Enumerable.Empty<string>(); }
    }

    // ═══ 弹窗辅助 ═══
    // 本页现在是顶部 tab 的 ContentView（不是 Page），自身没有 DisplayAlert/Prompt/ActionSheet，
    // 一律经窗口根 Page 调用。

    private static Page? RootPage => Application.Current?.Windows.FirstOrDefault()?.Page;

    private static Task<string?> PromptAsync(string title, string message, string accept, string cancel,
        string placeholder, Keyboard keyboard)
    {
        var root = RootPage;
        return root is null
            ? Task.FromResult<string?>(null)
            : root.DisplayPromptAsync(title, message, accept, cancel, placeholder: placeholder, keyboard: keyboard);
    }

    private static Task AlertAsync(string title, string message, string cancel = "确定")
    {
        var root = RootPage;
        return root is null ? Task.CompletedTask : root.DisplayAlertAsync(title, message, cancel);
    }

    /// <summary>确认/取消双按钮弹窗</summary>
    private static Task<bool> ConfirmAsync(string title, string message, string accept, string cancel)
    {
        var root = RootPage;
        return root is null ? Task.FromResult(false) : root.DisplayAlertAsync(title, message, accept, cancel);
    }

    private static Task<string?> AlertActionAsync(string title, params string[] buttons)
    {
        var root = RootPage;
        return root is null
            ? Task.FromResult<string?>(null)
            : root.DisplayActionSheetAsync(title, "取消", null, buttons);
    }
}
