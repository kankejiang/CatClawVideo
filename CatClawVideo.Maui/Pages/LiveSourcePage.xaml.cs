using CatClawVideo.Core.Live;
using Microsoft.Maui.Controls.Shapes;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 直播源配置页（TVBox「直播源设置」的轻量移植）：地址 / 本地文件 / 历史 + EPG·UA·超时。
///
/// <para>与影视的 <see cref="SourceConfigPage"/> 不同，直播源**一次只用一个**：保存即设为当前源，
/// 成功后直接返回直播间（直播间按 <c>Prefs.ApiUrl</c> 变化自动重载）。</para>
/// </summary>
public partial class LiveSourcePage : ContentPage
{
    private readonly LiveSourceService _source;
    private bool _busy;

    public LiveSourcePage(LiveSourceService source)
    {
        InitializeComponent();
#if ANDROID
        // Edge-to-Edge：推入式页面必须自己补顶部安全区，否则顶栏压状态栏（同 SourceConfigPage）
        SafeAreaHelper.ApplyPageTopInset(this);
#endif
        _source = source;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Refresh();
    }

    /// <summary>回填当前源 / EPG / UA / 历史 / 超时</summary>
    private void Refresh()
    {
        var prefs = _source.Prefs;

        LblCurrent.Text = prefs.ApiUrl.Length == 0
            ? "当前：未配置"
            : prefs.ApiUrl == LiveSourceService.CapturePath
                ? "当前：点播订阅自动导入（订阅自带直播源）"
                : "当前：" + Shorten(prefs.ApiUrl);
        if (SourceEditor.Text.Length == 0) SourceEditor.Text = prefs.ApiUrl;
        EpgEntry.Text = prefs.EpgUrl;
        UaEntry.Text = prefs.Ua;

        BuildTimeouts();
        BuildHistory();
    }

    private void BuildTimeouts()
    {
        TimeoutChips.Children.Clear();
        foreach (var value in new[] { 5, 10, 15, 20, 30 })
        {
            var captured = value;
            var active = _source.Prefs.TimeoutSeconds == value;
            var chip = new Border
            {
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 8 },
                Padding = new Thickness(10, 4),
                BackgroundColor = active
                    ? (Color)Application.Current!.Resources["PrimaryColor"]
                    : (Color)Application.Current!.Resources["ChipInactiveColor"],
                Content = new Label
                {
                    Text = value + "s",
                    FontSize = 11.5,
                    TextColor = active
                        ? Colors.White
                        : (Color)Application.Current!.Resources["TextSecondaryColor"],
                },
            };
            chip.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(() =>
                {
                    _source.Prefs.TimeoutSeconds = captured;
                    _source.Save();
                    BuildTimeouts();
                }),
            });
            TimeoutChips.Children.Add(chip);
        }
    }

    private void BuildHistory()
    {
        HistoryList.Children.Clear();
        var items = _source.Prefs.History;
        HistoryEmpty.IsVisible = items.Count == 0;

        foreach (var api in items.ToList())
        {
            var row = new Grid
            {
                ColumnDefinitions =
                [
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto),
                ],
                Padding = new Thickness(0, 6),
            };

            var name = new Label
            {
                Text = Shorten(api),
                FontSize = 12.5,
                TextColor = (Color)Application.Current!.Resources["TextPrimaryColor"],
                VerticalOptions = LayoutOptions.Center,
                LineBreakMode = LineBreakMode.TailTruncation,
            };
            row.Add(name, 0);

            var use = new Label
            {
                Text = "使用",
                FontSize = 11.5,
                FontFamily = "OpenSansSemibold",
                TextColor = (Color)Application.Current!.Resources["PrimaryColor"],
                VerticalOptions = LayoutOptions.Center,
                Margin = new Thickness(0, 0, 14, 0),
            };
            use.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(() => _ = LoadAsync(api)),
            });
            row.Add(use, 1);

            var del = new Label
            {
                Text = "删除",
                FontSize = 11.5,
                TextColor = Color.FromArgb("#c0392b"),
                VerticalOptions = LayoutOptions.Center,
            };
            del.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(() =>
                {
                    _source.Prefs.History.Remove(api);
                    _source.Save();
                    BuildHistory();
                }),
            });
            row.Add(del, 2);

            HistoryList.Children.Add(row);
        }
    }

    // ═══════════════════ 交互 ═══════════════════

    private void OnBackTapped(object? sender, TappedEventArgs e) => _ = Shell.Current.GoToAsync("..");

    private async void OnPickFileClicked(object? sender, EventArgs e)
    {
        try
        {
            var result = await FilePicker.PickAsync(new PickOptions
            {
                PickerTitle = "选择直播源文件",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    [DevicePlatform.WinUI] = new[] { ".txt", ".m3u", ".m3u8", ".json" },
                    [DevicePlatform.Android] = new[] { "text/plain", "text/*" },
                }),
            });
            if (result == null) return;

            SourceEditor.Text = result.FullPath;
            LblStatus.Text = "已选择文件：" + result.FileName;
            await LoadAsync(result.FullPath);
        }
        catch (Exception ex)
        {
            LblStatus.Text = "文件选择失败：" + ex.Message;
        }
    }

    private async void OnLoadClicked(object? sender, EventArgs e)
    {
        var text = (SourceEditor.Text ?? "").Trim();
        if (text.Length == 0)
        {
            await DisplayAlertAsync("直播源为空", "请填写地址、选择本地文件，或直接粘贴 TXT / M3U 内容。", "确定");
            return;
        }
        await LoadAsync(text);
    }

    /// <summary>拉取并解析直播源：成功即设为当前源并返回直播间。</summary>
    private async Task LoadAsync(string api)
    {
        if (_busy) return;
        _busy = true;
        BtnLoad.IsEnabled = false;
        BtnPickFile.IsEnabled = false;
        LblStatus.Text = "正在加载直播源…";
        LblStatus.TextColor = (Color)Application.Current!.Resources["TextSecondaryColor"];

        try
        {
            // 选项先落盘（SetCurrentApi 会一起保存）
            _source.Prefs.EpgUrl = (EpgEntry.Text ?? "").Trim();
            _source.Prefs.Ua = (UaEntry.Text ?? "").Trim();

            var result = await _source.LoadAsync(api);

            if (result.Groups.Count == 0)
            {
                LblStatus.TextColor = Color.FromArgb("#c0392b");
                LblStatus.Text = string.IsNullOrEmpty(result.Error) ? "未解析出任何频道" : result.Error;
                return;
            }

            _source.SetCurrentApi(api);
            Refresh();

            var lives = result.Lives.Count > 1 ? $"（多源第 {result.LivesIndex + 1}/{result.Lives.Count} 个）" : "";
            await DisplayAlertAsync("直播源已加载",
                $"{result.Groups.Count} 个分组 / {result.TotalChannels} 个频道{lives}", "确定");
            await Shell.Current.GoToAsync("..");
        }
        catch (Exception ex)
        {
            LblStatus.TextColor = Color.FromArgb("#c0392b");
            LblStatus.Text = "加载失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            BtnLoad.IsEnabled = true;
            BtnPickFile.IsEnabled = true;
        }
    }

    private void OnSaveOptionsClicked(object? sender, EventArgs e)
    {
        _source.Prefs.EpgUrl = (EpgEntry.Text ?? "").Trim();
        _source.Prefs.Ua = (UaEntry.Text ?? "").Trim();
        _source.Save();
        LblStatus.TextColor = (Color)Application.Current!.Resources["TextSecondaryColor"];
        LblStatus.Text = "已保存";
    }

    /// <summary>长地址折成「头 … 尾」两端显示（历史列表与当前源共用）。</summary>
    private static string Shorten(string s)
    {
        if (s.Contains('\n') || s.Contains('\r')) return "（内联内容，" + s.Length + " 字符）";
        if (s.Length <= 64) return s;
        return s[..44] + " … " + s[^16..];
    }
}
