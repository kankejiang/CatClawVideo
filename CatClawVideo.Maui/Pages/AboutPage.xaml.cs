using System.Text.RegularExpressions;
using CatClawVideo.Core.Services;
using CatClawVideo.Maui.ViewModels;

namespace CatClawVideo.Maui.Pages;

/// <summary>关于页：品牌信息、简介、免责声明 / 开源协议 / 猫爪音乐 / QQ 交流群 / GitHub / 检查更新。</summary>
public partial class AboutPage : ContentPage
{
    private AboutViewModel Vm => (AboutViewModel)BindingContext;

    private UpdateCheckResult? _updateResult;

    /// <summary>更新日志区域的最大高度（弹窗显示时按页面高度动态计算）</summary>
    private double _notesMaxHeight = 280;

    public AboutPage(AboutViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
        vm.UpdateCheckCompleted += OnUpdateCheckCompleted;
        vm.UpdateCheckFailed += OnUpdateCheckFailed;
        UpdateNotesLayout.SizeChanged += OnNotesLayoutSizeChanged;
    }

    /// <summary>
    /// 日志内容比上限高时给 ScrollView 定高（出现滚动），不足时保持自适应（避免卡片下方留白）。
    /// </summary>
    private void OnNotesLayoutSizeChanged(object? sender, EventArgs e)
        => UpdateNotesScroll.HeightRequest = UpdateNotesLayout.Height > _notesMaxHeight
            ? _notesMaxHeight
            : -1;

    /// <summary>自定义返回（页面隐藏了 Shell 系统返回键）</summary>
    private async void OnBackTapped(object? sender, TappedEventArgs e)
    {
        try { await Shell.Current.GoToAsync(".."); } catch { }
    }

    // ═══════════════════════════════════════════════════
    // 检查更新弹窗（自定义卡片，替代系统 DisplayAlert）
    // ═══════════════════════════════════════════════════

    private void OnUpdateCheckCompleted(UpdateCheckResult result) => ShowUpdateDialog(result);

    private async void OnUpdateCheckFailed(string message)
        => await DisplayAlert("检查更新", message, "好的");

    private void ShowUpdateDialog(UpdateCheckResult result)
    {
        _updateResult = result;
        var hasUpdate = result.HasUpdate;

        // 日志区上限：桌面给足 280，窗口/手机屏幕矮时按页面高度收缩（卡片总高 ≈ 上限 + 170）
        _notesMaxHeight = Math.Clamp(Height * 0.42, 120, 280);

        UpdateTitleLabel.Text = hasUpdate ? "发现新版本" : "已是最新版本";
        UpdateVersionLabel.Text = $"v{result.LatestVersion}";
        UpdateSubtitleLabel.Text = hasUpdate
            ? $"当前版本 {Vm.Version}，建议更新以获得更好的体验"
            : $"当前版本 {Vm.Version} 已是最新，以下为该版本的更新内容";
        UpdatePrimaryButtonText.Text = hasUpdate ? "立即下载" : "好的";
        UpdateSecondaryButton.IsVisible = hasUpdate;
        Grid.SetColumnSpan(UpdatePrimaryButton, hasUpdate ? 1 : 2);

        BuildUpdateNotes(result.ReleaseNotes);

        UpdateOverlay.Opacity = 0;
        UpdateOverlay.IsVisible = true;
        _ = UpdateOverlay.FadeTo(1, 140, Easing.CubicOut);
    }

    private async void OnUpdatePrimaryTapped(object? sender, TappedEventArgs e)
    {
        var result = _updateResult;
        HideUpdateOverlay();
        if (result is { HasUpdate: true })
        {
            try { await Launcher.OpenAsync(new Uri(result.DownloadUrl ?? result.ReleasePageUrl)); }
            catch { }
        }
    }

    private void OnUpdateSecondaryTapped(object? sender, TappedEventArgs e) => HideUpdateOverlay();

    private void HideUpdateOverlay() => UpdateOverlay.IsVisible = false;

    /// <summary>
    /// 把 Markdown 更新说明格式化成卡片内的富文本行：
    /// 标题（#）→ 强调色加粗；列表（-）→ 圆点；表格行（|）→ 单元格合并成一行；
    /// 行内标记（**加粗**、`代码`、[链接](url)）全部剥成纯文本。最多显示 26 行。
    /// </summary>
    private void BuildUpdateNotes(string? body)
    {
        UpdateNotesLayout.Children.Clear();

        if (string.IsNullOrWhiteSpace(body))
        {
            UpdateNotesScroll.IsVisible = false;
            return;
        }

        var primaryColor = GetResourceColor("TextPrimaryColor");
        var hintColor = GetResourceColor("TextHintColor");
        var accentColor = Color.FromArgb("#9B7ED8");

        var isTableHeader = true;
        var shown = 0;
        foreach (var raw in body.Replace("\r\n", "\n").Split('\n'))
        {
            if (shown >= 26)
            {
                UpdateNotesLayout.Children.Add(
                    MakeNoteLabel("…… 更多内容请查看 GitHub Releases", 11.5, hintColor));
                break;
            }

            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (IsTableDivider(line)) continue;

            Label label;
            if (line.StartsWith('|'))
            {
                var cells = line.Split('|')
                                .Select(c => StripInline(c.Trim()))
                                .Where(c => c.Length > 0)
                                .ToList();
                if (cells.Count == 0) continue;
                // 表头行用弱化色，数据行正常色
                label = MakeNoteLabel("•  " + string.Join(" · ", cells), 12.5,
                    isTableHeader ? hintColor : primaryColor);
                isTableHeader = false;
            }
            else if (line.StartsWith('#'))
            {
                label = MakeNoteLabel(StripInline(line.TrimStart('#').Trim()), 13.5, accentColor, bold: true);
            }
            else if (line.StartsWith('-') || line.StartsWith('*') || line.StartsWith('•'))
            {
                label = MakeNoteLabel("•  " + StripInline(line[1..].Trim()), 12.5, primaryColor);
            }
            else
            {
                label = MakeNoteLabel(StripInline(line), 12.5, primaryColor);
            }

            if (string.IsNullOrWhiteSpace(label.Text)) continue;
            UpdateNotesLayout.Children.Add(label);
            shown++;
        }

        UpdateNotesScroll.IsVisible = UpdateNotesLayout.Children.Count > 0;
    }

    private static Label MakeNoteLabel(string text, double size, Color color, bool bold = false)
        => new()
        {
            Text = text,
            FontSize = size,
            FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None,
            TextColor = color,
            LineBreakMode = LineBreakMode.WordWrap
        };

    private static Color GetResourceColor(string key)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Color c)
            return c;
        return Colors.White;
    }

    /// <summary>表格分隔行（|---|:---:| 之类）</summary>
    private static bool IsTableDivider(string line)
        => line.StartsWith('|') && line.All(c => c is '|' or '-' or ':' or ' ');

    /// <summary>剥掉行内 Markdown 标记：[文本](链接) → 文本、**加粗**、`代码`</summary>
    private static string StripInline(string text)
        => Regex.Replace(text ?? "", @"\[([^\]]+)\]\([^)]*\)", "$1")
                .Replace("**", "")
                .Replace("`", "")
                .Trim();
}
