using CatClawVideo.Maui.ViewModels;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 诊断日志页：查看/筛选/导出 <c>logs/debug.log</c>。
/// 入口在「设置 → 诊断日志」（开关开启后才会有内容）。
/// </summary>
public partial class DiagnosticLogPage : ContentPage
{
    private readonly DiagnosticLogViewModel _vm = new();

    /// <summary>级别 chip 与级别码的对应（"all" = 不筛）</summary>
    private readonly Dictionary<Border, string> _chips;

    public DiagnosticLogPage()
    {
        InitializeComponent();
#if ANDROID
        // Edge-to-Edge：推入式页面必须自己补顶部安全区（同 AboutPage / SearchPage）
        SafeAreaHelper.ApplyPageTopInset(this);
#endif
        BindingContext = _vm;

        _chips = new Dictionary<Border, string>
        {
            [ChipAll] = "all",
            [ChipError] = "e",
            [ChipWarn] = "w",
            [ChipInfo] = "i",
            [ChipDebug] = "d"
        };

        // 默认停在「全部」
        UpdateChipVisuals("all");
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        await _vm.LoadLogsAsync();

        // 模块下拉：把动态提取到的标签灌进去（保留当前选择）
        try
        {
            var keep = TagPicker.SelectedIndex;
            TagPicker.ItemsSource = _vm.AvailableTags.ToList();
            TagPicker.SelectedIndex = keep >= 0 && keep < _vm.AvailableTags.Count ? keep : 0;
        }
        catch { }

        // 直接定位到最新一条（日志通常关心最近发生了什么）
        try
        {
            if (_vm.FilteredEntries.Count > 0)
                LogList.ScrollTo(_vm.FilteredEntries.Count - 1, position: ScrollToPosition.End, animate: false);
        }
        catch { }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // 离开页面时把缓冲区刷盘，保证最后几行（往往是关键现场）不丢
        Services.DiagnosticLog.Instance?.Flush();
    }

    /// <summary>自定义返回（页面隐藏了 Shell 系统返回键）</summary>
    private async void OnBackTapped(object? sender, TappedEventArgs e)
    {
        try { await Shell.Current.GoToAsync(".."); } catch { }
    }

    private async void OnRefreshClicked(object? sender, EventArgs e) => await _vm.LoadLogsAsync();

    private async void OnCopyClicked(object? sender, EventArgs e) => await _vm.CopyLogsAsync();

    private async void OnExportClicked(object? sender, EventArgs e) => await _vm.ExportAsync();

    private async void OnClearClicked(object? sender, EventArgs e)
    {
        var ok = await Shell.Current.DisplayAlertAsync("清空日志",
            "将删除当前诊断日志（含备份文件），用于重新开始抓取。确定清空吗？", "清空", "取消");
        if (!ok) return;
        await _vm.ClearLogsAsync();
    }

    /// <summary>级别 chip：既显示统计数，也作为筛选按钮</summary>
    private void OnLevelChipTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Border chip || !_chips.TryGetValue(chip, out var level)) return;
        _vm.SetLevelFilter(level);
        UpdateChipVisuals(level);
    }

    /// <summary>刷新 chip 的选中态（选中：主色底；未选中：默认 chip 底色）</summary>
    private void UpdateChipVisuals(string level)
    {
        var selectedColor = Res("ChipActiveColor", "#9B7ED8");
        var normalBg = Res("ChipInactiveColor", "#15FFFFFF");
        var normalStroke = Res("DividerColor", "#14FFFFFF");

        foreach (var (chip, code) in _chips)
        {
            var on = code == level;
            chip.BackgroundColor = on ? selectedColor : normalBg;
            chip.Stroke = new SolidColorBrush(on ? selectedColor : normalStroke);
        }
    }

    /// <summary>取主题色（合并字典里是普通 Color；取不到时回退到内置色，绝不因换肤失败）</summary>
    private static Color Res(string key, string fallback)
    {
        try
        {
            if (Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c) return c;
        }
        catch { }
        return Color.FromArgb(fallback);
    }

    private void OnTagChanged(object? sender, EventArgs e)
    {
        if (TagPicker.SelectedItem is string tag) _vm.SetTagFilter(tag);
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) => _vm.SearchQuery = e.NewTextValue ?? "";
}
