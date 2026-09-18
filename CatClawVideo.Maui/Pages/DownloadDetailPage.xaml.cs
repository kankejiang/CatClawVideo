using CatClawVideo.Core.Services;
using CatClawVideo.Maui.Services;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 任务详情页（照搬 Motrix 任务详情）：基本信息 / 进度 / Tracker / 文件。
/// <para>内置 BT 引擎已移除（2026-09-16）：磁力任务改由迅雷引擎承载，
/// 宿主拿不到分片地图与逐条 peer 明细，故这些区块展示任务级进度并说明原因。</para>
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
#if ANDROID
        // Edge-to-Edge：推入式页面必须自己补顶部安全区，否则顶栏压状态栏（原因见 SafeAreaHelper.ApplyPageTopInset）
        SafeAreaHelper.ApplyPageTopInset(this);
#endif
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

            CatClawVideo.Core.Services.BtTorrentStats? stats = null;   // 内置 BT 已移除：不再有分片/peer 明细
            _lastStats = stats;

            // 所有任务（HTTP 直链 / 磁力）都走这条基础展示路径。
            // 磁力现在由迅雷引擎（QEMU 内的独立下载器）承载，宿主拿不到分片图与 peer 明细，
            // 因此这里展示任务级进度/速度；引擎侧的下载与导出进度由任务卡片自身上报。
            InfoHash.Text = task.IsMagnet ? "（磁力任务：迅雷引擎）" : "（HTTP 直链任务）";
            InfoPieceSize.Text = InfoPieceCount.Text = "-";
            InfoTotal.Text = DownloadTaskItem.FormatBytes(task.TotalBytes);
            PieceMap.Pieces = null;
            ProgressBarView.Progress = task.Progress;
            ProgressText.Text = $"{task.Progress * 100:F2}%";
            ProgressBytes.Text = $"{DownloadTaskItem.FormatBytes(task.DownloadedBytes)} / {DownloadTaskItem.FormatBytes(task.TotalBytes)}";
            StatSeeds.Text = StatConnections.Text = StatUploaded.Text = StatRatio.Text = "-";
            StatDown.Text = string.IsNullOrWhiteSpace(task.SpeedText) ? "-" : task.SpeedText;
            StatUp.Text = "-";
            TrackerHeader.Text = task.IsMagnet
                ? "磁力任务由迅雷引擎解析与下载，宿主不持有 tracker 列表"
                : "HTTP 直链任务无 tracker";
            ConnSummary.Text = task.IsMagnet
                ? "磁力下载由迅雷引擎（QEMU 内）承载：连接/种子明细在引擎侧，宿主仅上报任务级进度"
                : "HTTP 直链任务无连接明细";
            FileSelectionSummary.Text = "";
            TrackerListHost.Children.Clear();
            FileListHost.Children.Clear();
            _fileChecks.Clear();
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
                var ok = await Task.FromResult(false) /* BT 已移除 */;
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
            await Task.FromResult(false) /* BT 已移除 */;
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
            await Task.FromResult(false) /* BT 已移除 */;
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
