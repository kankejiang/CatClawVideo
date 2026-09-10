using CatClawVideo.Core.Services;
using CatClawVideo.Maui.Services;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 任务详情页（照搬 Motrix 任务详情）：基本信息 / 进度（分片地图 + 速率统计）/ Tracker / 文件（可勾选）。
/// <para>「连接」tab 为引擎能力说明：MonoTorrent 3.0.1 不暴露逐条 peer 明细。</para>
/// </summary>
[QueryProperty(nameof(TaskId), "id")]
public partial class DownloadDetailPage : ContentPage
{
    private readonly DownloadManager _manager;
    private IDispatcherTimer? _timer;
    private readonly List<CheckBox> _fileChecks = new();
    private BtTorrentStats? _lastStats;

    /// <summary>任务 ID（Shell 路由参数）</summary>
    public string? TaskId { get; set; }

    public DownloadDetailPage(DownloadManager manager)
    {
        InitializeComponent();
        _manager = manager;
#if WINDOWS
        Padding = new Thickness(0, 48, 0, 0);
#endif
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Refresh();
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _timer?.Stop();
        _timer = null;
    }

    private void OnBackTapped(object? sender, EventArgs e) => Shell.Current.GoToAsync("..");

    // ─────────── Tab 切换 ───────────
    private void OnTabTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not VerticalStackLayout tab) return;
        int index = tab == Tab0 ? 0 : tab == Tab1 ? 1 : tab == Tab2 ? 2 : tab == Tab3 ? 3 : 4;
        var panels = new[] { Panel0, Panel1, Panel2, Panel3, Panel4 };
        var labels = new[] { Tab0, Tab1, Tab2, Tab3, Tab4 }
            .Select(t => (Label)t.Children[0]).ToArray();
        var lines = new[] { TabLine0, TabLine1, TabLine2, TabLine3, TabLine4 };
        var primary = (Color)Application.Current!.Resources["PrimaryColor"];
        for (int i = 0; i < panels.Length; i++)
        {
            panels[i].IsVisible = i == index;
            labels[i].TextColor = i == index ? primary : (Color)Application.Current!.Resources["TextHintColor"];
            lines[i].Color = i == index ? primary : Colors.Transparent;
        }
    }

    // ─────────── 数据刷新 ───────────
    private void Refresh()
    {
        try
        {
            var task = _manager.Tasks.FirstOrDefault(t => t.Id == TaskId);
            if (task == null)
            {
                HintLabel.Text = "任务不存在或已被删除";
                _timer?.Stop();
                return;
            }

            InfoName.Text = task.DisplayName;
            InfoPath.Text = task.LocalPath;
            InfoState.Text = task.Status switch
            {
                DownloadStatus.Downloading => "ACTIVE",
                DownloadStatus.Queued => "QUEUED",
                DownloadStatus.Paused => "PAUSED",
                DownloadStatus.Completed => "COMPLETE",
                DownloadStatus.Failed => "ERROR",
                _ => "STOPPED",
            };

            var stats = _manager.GetBtStats(task.Id);
            _lastStats = stats;
            if (stats == null)
            {
                // HTTP 任务：只有基础统计
                InfoHash.Text = "（非 BT 任务）";
                InfoPieceSize.Text = InfoPieceCount.Text = "-";
                InfoTotal.Text = DownloadTaskItem.FormatBytes(task.TotalBytes);
                PieceMap.Pieces = null;
                ProgressBarView.Progress = task.Progress;
                ProgressText.Text = $"{task.Progress * 100:F2}%";
                ProgressBytes.Text = $"{DownloadTaskItem.FormatBytes(task.DownloadedBytes)} / {DownloadTaskItem.FormatBytes(task.TotalBytes)}";
                StatSeeds.Text = StatConnections.Text = StatDown.Text = StatUp.Text = StatUploaded.Text = StatRatio.Text = "-";
                TrackerHeader.Text = "非 BT 任务无 tracker";
                FileSelectionSummary.Text = "";
                return;
            }

            InfoHash.Text = stats.InfoHash;
            InfoPieceSize.Text = stats.PieceLength > 0 ? DownloadTaskItem.FormatBytes(stats.PieceLength) : "获取中…";
            InfoPieceCount.Text = stats.Pieces.Length > 0 ? stats.Pieces.Length.ToString() : "获取中…";
            InfoTotal.Text = stats.TotalBytes > 0 ? DownloadTaskItem.FormatBytes(stats.TotalBytes) : "获取中…";

            PieceMap.Pieces = stats.Pieces;
            ProgressBarView.Progress = Math.Clamp(stats.ProgressPercent / 100.0, 0, 1);
            ProgressText.Text = $"{stats.ProgressPercent:F2}%";
            var remain = stats.RemainingSeconds > 0
                ? $"　剩余 {FormatDuration(stats.RemainingSeconds)}"
                : "";
            ProgressBytes.Text = $"{DownloadTaskItem.FormatBytes(stats.DownloadedBytes)} / {DownloadTaskItem.FormatBytes(stats.TotalBytes)}{remain}";
            StatSeeds.Text = stats.Seeds.ToString();
            StatConnections.Text = stats.Connections.ToString();
            StatDown.Text = $"{DownloadTaskItem.FormatBytes(stats.DownloadRate)}/s";
            StatUp.Text = $"{DownloadTaskItem.FormatBytes(stats.UploadRate)}/s";
            StatUploaded.Text = DownloadTaskItem.FormatBytes(stats.UploadedBytes);
            StatRatio.Text = stats.ShareRatio.ToString("F4");

            ConnSummary.Text = $"当前连接：{stats.Connections} 个　种子：{stats.Seeds}　下载者：{stats.Leeches}";

            // Tracker 列表（状态着色）
            TrackerHeader.Text = $"共 {stats.Trackers.Count} 个 tracker";
            if (TrackerListHost.Children.Count != stats.Trackers.Count)
            {
                TrackerListHost.Children.Clear();
                foreach (var t in stats.Trackers)
                {
                    var row = new Label
                    {
                        FontSize = 11.5,
                        LineBreakMode = LineBreakMode.TailTruncation,
                        TextColor = (Color)Application.Current!.Resources["TextSecondaryColor"],
                    };
                    TrackerListHost.Children.Add(row);
                }
            }
            for (int i = 0; i < stats.Trackers.Count && i < TrackerListHost.Children.Count; i++)
            {
                var t = stats.Trackers[i];
                if (TrackerListHost.Children[i] is Label l)
                    l.Text = string.IsNullOrEmpty(t.Message) ? $"{t.Url}　[{t.Status}]" : $"{t.Url}　[{t.Status}] {t.Message}";
            }

            // 文件列表（结构变化时重建）
            if (_fileChecks.Count != stats.Files.Count)
                BuildFileList(stats.Files);
            UpdateFileSummary(stats.Files);
        }
        catch { }
    }

    private void BuildFileList(List<BtFileInfo> files)
    {
        FileListHost.Children.Clear();
        _fileChecks.Clear();
        var primary = (Color)Application.Current!.Resources["PrimaryColor"];
        var hint = (Color)Application.Current!.Resources["TextHintColor"];
        var text = (Color)Application.Current!.Resources["TextPrimaryColor"];

        foreach (var f in files)
        {
            var index = f.Index;
            var check = new CheckBox { Color = primary, IsChecked = f.Selected, VerticalOptions = LayoutOptions.Center };
            check.CheckedChanged += async (_, e) =>
            {
                if (TaskId == null) return;
                var ok = await _manager.SetBtFileSelectionAsync(TaskId, index, e.Value);
                if (!ok) HintLabel.Text = "切换下载勾选失败（任务可能未就绪）";
                else HintLabel.Text = e.Value ? "已加入下载" : "已跳过该文件";
            };
            _fileChecks.Add(check);

            var name = new Label { Text = f.Name, FontSize = 12, TextColor = text, LineBreakMode = LineBreakMode.TailTruncation, VerticalOptions = LayoutOptions.Center };
            var pct = new Label { Text = $"{f.ProgressPercent:F1}%", FontSize = 11.5, TextColor = hint, WidthRequest = 64, HorizontalTextAlignment = TextAlignment.End, VerticalOptions = LayoutOptions.Center };
            var size = new Label { Text = DownloadTaskItem.FormatBytes(f.Length), FontSize = 11.5, TextColor = hint, WidthRequest = 96, HorizontalTextAlignment = TextAlignment.End, VerticalOptions = LayoutOptions.Center };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitionCollection(new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto)), ColumnSpacing = 8 };
            Grid.SetColumn(check, 0); grid.Children.Add(check);
            Grid.SetColumn(name, 1); grid.Children.Add(name);
            Grid.SetColumn(pct, 2); grid.Children.Add(pct);
            Grid.SetColumn(size, 3); grid.Children.Add(size);
            FileListHost.Children.Add(grid);
        }
    }

    private void UpdateFileSummary(List<BtFileInfo> files)
    {
        if (files.Count == 0) { FileSelectionSummary.Text = ""; return; }
        var selected = files.Where(f => f.Selected).ToList();
        FileSelectionSummary.Text = $"已选：{selected.Count} 个文件，共 {DownloadTaskItem.FormatBytes(selected.Sum(f => f.Length))}";
    }

    private async void OnSelectAllClicked(object? sender, EventArgs e) => await SetAllAsync(true);
    private async void OnSelectNoneClicked(object? sender, EventArgs e) => await SetAllAsync(false);

    private async void OnInvertSelectionClicked(object? sender, EventArgs e)
    {
        var stats = _lastStats;
        if (stats == null || TaskId == null) return;
        for (int i = 0; i < stats.Files.Count && i < _fileChecks.Count; i++)
        {
            _fileChecks[i].IsChecked = !_fileChecks[i].IsChecked;
            await _manager.SetBtFileSelectionAsync(TaskId, i, _fileChecks[i].IsChecked == true);
        }
        Refresh();
    }

    private async Task SetAllAsync(bool selected)
    {
        var stats = _lastStats;
        if (stats == null || TaskId == null) return;
        for (int i = 0; i < stats.Files.Count && i < _fileChecks.Count; i++)
        {
            _fileChecks[i].IsChecked = selected;
            await _manager.SetBtFileSelectionAsync(TaskId, i, selected);
        }
        Refresh();
    }

    private static string FormatDuration(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}时 {ts.Minutes}分 {ts.Seconds}秒";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes}分 {ts.Seconds}秒";
        return $"{ts.Seconds}秒";
    }
}
