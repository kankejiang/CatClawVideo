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
        // 注：本页**不再**补顶部安全区 —— 它自己隐藏状态栏（见 OnAppearing），
        // 补 inset 反而会在页首留出一条与渐变内容不连续的不透明深色带
        // （2026-09-18 用户实测报告：要求「删除顶部深蓝色空白区域 + 关于页不显示系统状态栏」）。
        BindingContext = vm;
        vm.UpdateCheckCompleted += OnUpdateCheckCompleted;
        vm.UpdateCheckFailed += OnUpdateCheckFailed;
        UpdateNotesLayout.SizeChanged += OnNotesLayoutSizeChanged;
    }

#if ANDROID
    /// <summary>
    /// 本页不显示系统状态栏（返回按钮已改为浮在内容区内，无需预留顶部安全区）。
    /// 只隐藏状态栏、保留导航栏 —— 不要用 SetImmersive（那会连底部手势条一起吃掉）。
    /// </summary>
    protected override void OnAppearing()
    {
        base.OnAppearing();
        MainActivity.SetStatusBarVisible(false);
    }

    /// <summary>离开本页必须恢复状态栏，否则会在其它页面一直缺席。</summary>
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        MainActivity.SetStatusBarVisible(true);
    }
#endif

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
        UpdatePrimaryButtonText.Text = hasUpdate
            ? (Services.AppInstaller.Supported ? "下载并安装" : "立即下载") : "好的";
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
        if (result is not { HasUpdate: true }) { HideUpdateOverlay(); return; }

        // 安卓：本机下载 + 拉起系统安装器（对位 TVBox 的应用内升级）。其它端只能交给浏览器。
        if (!Services.AppInstaller.Supported || string.IsNullOrEmpty(result.DownloadUrl))
        {
            HideUpdateOverlay();
            try { await Launcher.OpenAsync(new Uri(result.DownloadUrl ?? result.ReleasePageUrl)); }
            catch { }
            return;
        }

        if (!Services.AppInstaller.HasInstallPermission())
        {
            var go = await DisplayAlertAsync("需要安装权限",
                "Android 不允许应用直接装包，需要到系统页里给「猫爪影视」打开\"安装未知应用\"。现在去吗？",
                "去设置", "算了");
            if (go) Services.AppInstaller.OpenInstallPermissionSettings();
            return;
        }

        var url = result.DownloadUrl!;
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            HideUpdateOverlay();
            await DisplayAlertAsync("下载更新", "下载地址不是 http(s) 链接。", "好");
            return;
        }

        try
        {
            var progress = new Progress<double>(p =>
            {
                MainThread.BeginInvokeOnMainThread(() => UpdatePrimaryButtonText.Text = $"下载中 {p:P0}");
            });
            UpdatePrimaryButton.IsEnabled = false;
            var (bytes, hasTotal) = await Services.AppInstaller.DownloadAsync(
                url, progress, CancellationToken.None);
            UpdatePrimaryButtonText.Text = hasTotal ? "正在安装…" : $"已下载 {bytes / 1048576.0:F1} MB";

            var (ok, message) = Services.AppInstaller.Install();
            HideUpdateOverlay();
            if (!ok) await DisplayAlertAsync("安装", message, "好");
            // 成功时不弹提示：系统安装界面已经盖在上面的
        }
        catch (Exception ex)
        {
            HideUpdateOverlay();
            await DisplayAlertAsync("下载更新", "失败：" + ex.Message, "好");
        }
        finally
        {
            UpdatePrimaryButton.IsEnabled = true;
            UpdatePrimaryButtonText.Text = "下载并安装";
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
