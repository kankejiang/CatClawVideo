using CatClawVideo.Core.Interfaces;
using CatClawVideo.Data;
using CatClawVideo.Maui.ViewModels;
using Microsoft.Maui.Controls.Shapes;

namespace CatClawVideo.Maui.Pages;

/// <summary>设置页：订阅源添加 / 主题色 / 深浅模式 / 关于。</summary>
public partial class SettingsPage : ContentView, ITabView
{
    private readonly SettingsViewModel _vm;
    private readonly IThemeService _theme;
    private readonly ISubscriptionManager _subscriptionManager;
    private readonly VideoDatabase _db;

    public SettingsPage(SettingsViewModel vm, IThemeService theme,
        ISubscriptionManager subscriptionManager, VideoDatabase db)
    {
        InitializeComponent();
        _vm = vm;
        _theme = theme;
        _subscriptionManager = subscriptionManager;
        _db = db;
        BindingContext = _vm;

        BuildThemeSwatches();
        BuildDarkModeChips();
    }

    public Task OnTabShownAsync()
    {
        RefreshSelections();
        return Task.CompletedTask;
    }

    /// <summary>主题色圆点（选中项带主题色描边环）</summary>
    private void BuildThemeSwatches()
    {
        foreach (var (theme, name, hex) in _vm.ThemeOptions)
        {
            var swatch = new Border
            {
                WidthRequest = 40,
                HeightRequest = 40,
                StrokeThickness = 0,
                BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb(hex),
                StrokeShape = new RoundRectangle { CornerRadius = 20 },
                AutomationId = $"swatch_{theme}",
            };

            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) =>
            {
                _vm.SelectedTheme = theme;
                RefreshSelections();
            };
            swatch.GestureRecognizers.Add(tap);
            ThemeSwatches.Add(swatch);
        }
    }

    /// <summary>深浅模式胶囊（选中项主题色底白字）</summary>
    private void BuildDarkModeChips()
    {
        foreach (var (setting, name) in _vm.DarkModeOptions)
        {
            var chip = new Border
            {
                Padding = new Thickness(16, 8),
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 15 },
                AutomationId = $"chip_{setting}",
            };
            chip.Content = new Label
            {
                Text = name,
                FontSize = 13,
                FontFamily = "OpenSansSemibold",
            };

            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) =>
            {
                _vm.SelectedDarkMode = setting;
                RefreshSelections();
            };
            chip.GestureRecognizers.Add(tap);
            DarkModeChips.Add(chip);
        }
    }

    /// <summary>按当前选择态刷新控件配色</summary>
    private void RefreshSelections()
    {
        var res = Application.Current!.Resources;
        var selectedThemeHex = _vm.ThemeOptions.First(t => t.Theme == _vm.SelectedTheme).Hex;

        foreach (var child in ThemeSwatches)
        {
            if (child is not Border swatch) continue;
            var hex = _vm.ThemeOptions.First(t => swatch.AutomationId == $"swatch_{t.Theme}").Hex;
            bool selected = hex == selectedThemeHex;
            swatch.StrokeThickness = selected ? 3 : 0;
            swatch.Stroke = selected
                ? Microsoft.Maui.Graphics.Color.FromArgb(selectedThemeHex).WithAlpha(0.5f)
                : Microsoft.Maui.Graphics.Colors.Transparent;
        }

        foreach (var child in DarkModeChips)
        {
            if (child is not Border chip || chip.Content is not Label label) continue;
            bool selected = chip.AutomationId == $"chip_{_vm.SelectedDarkMode}";
            chip.BackgroundColor = selected
                ? (Color)res["ChipActiveColor"]
                : (Color)res["ChipInactiveColor"];
            label.TextColor = selected
                ? (Color)res["ChipActiveTextColor"]
                : (Color)res["ChipInactiveTextColor"];
        }
    }

    /// <summary>跳转源配置页（订阅源/站点完整管理）</summary>
    private async void OnOpenSourceConfig(object? sender, TappedEventArgs e)
    {
        try { await Shell.Current.GoToAsync("sourceconfig"); } catch { }
    }

    /// <summary>添加订阅：拉取解析 TVBox 配置 → 写库 → 站点仓库立即生效</summary>
    private async void OnAddSubClicked(object? sender, EventArgs e)
    {
        var url = SubEntry.Text?.Trim();
        if (string.IsNullOrEmpty(url)) return;

        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            await Shell.Current.DisplayAlertAsync("无效地址", "请输入 http(s) 开头的订阅地址。", "确定");
            return;
        }

        try
        {
            AddSubButton.IsEnabled = false;
            var sites = await _subscriptionManager.LoadSubscriptionAsync(url);

            // 站点仓库立即生效（首页/搜索事件刷新）
            SiteRegistry.Replace(sites);

            // 订阅入库（按地址去重）
            var name = new Uri(url).Host;
            if (await _db.FindSubscriptionAsync(url) is null)
                await _db.AddSubscriptionAsync(new VodSubscription { Name = name, SourceUrl = url, Kind = "tvbox" });

            SubEntry.Text = "";

            // 需要账号认证的站点（alist 类）：逐个弹窗录入凭据，无凭据无法观看
            foreach (var server in Core.Providers.SpiderCredentials.MissingServers(sites))
            {
                var dlg = new CredentialsDialogPage(name, server);
                await Shell.Current.Navigation.PushModalAsync(dlg);
            }

            var playableCount = sites.Count(s => s.Playable);
            await Shell.Current.DisplayAlertAsync("订阅已添加",
                $"解析到 {sites.Count} 个站点，其中可播 {playableCount} 个。", "确定");
        }
        catch (NotSupportedException ex)
        {
            await Shell.Current.DisplayAlertAsync("暂不支持该订阅", ex.Message, "确定");
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlertAsync("订阅添加失败", $"无法拉取或解析该地址：{ex.Message}", "确定");
        }
        finally
        {
            AddSubButton.IsEnabled = true;
        }
    }
}
