using CatClawVideo.Maui.ViewModels;
using AppTheme = CatClawVideo.Core.Interfaces.AppTheme;

namespace CatClawVideo.Maui.Pages;

/// <summary>主页面：底部导航宿主（首页/收藏/设置），tab 内容为 ContentView 从 DI 注入常驻复用。</summary>
public partial class MainPage : ContentPage
{
    private readonly MainViewModel _vm;
    private readonly IThemeService _theme;
    private readonly ContentView[] _tabs = null!;

    /// <summary>主题色枚举 → 主题图标十六进制（与 ThemeService 的 ThemeMap 一致）</summary>
    private static readonly Dictionary<AppTheme, string> ThemeHex = new()
    {
        [AppTheme.Purple] = "9b7ed8",
        [AppTheme.Pink] = "ec407a",
        [AppTheme.Blue] = "42a5f5",
        [AppTheme.Orange] = "ff7043",
        [AppTheme.Teal] = "26a69a",
    };

    private static readonly string[] TabIconBases = ["ic_home", "ic_favorite", "ic_settings"];

    public MainPage(MainViewModel vm, IThemeService theme, HomePage home, FavoritesPage favorites, SettingsPage settings)
    {
        InitializeComponent();
        _vm = vm;
        _theme = theme;
        _tabs = [home, favorites, settings];

        foreach (var tab in _tabs)
        {
            tab.IsVisible = false;
            ContentHost.Add(tab);
        }

        BindingContext = _vm;
        _vm.TabChanged += OnTabChanged;

        // 主题变化时刷新 tab 图标配色
        _theme.Applied += UpdateTabIcons;

        // 安全区 padding（Android 透明状态栏下内容避开系统栏）
        Padding = new Thickness(0, GetTopSafeArea(), 0, 0);
        SafeAreaHelper.SafeAreaChanged += OnSafeAreaChanged;

        // 首个 tab 直接显示（SelectedTabIndex 默认 0 不触发 TabChanged）
        ShowTab(0);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        UpdateTabIcons();
    }

    private void OnTabTapped(object? sender, TappedEventArgs e)
    {
        var index = (sender == TabItem1) ? 1 : (sender == TabItem2) ? 2 : 0;
        _vm.SelectTab(index);
    }

    private void OnTabIconClicked(object? sender, EventArgs e)
    {
        var index = (sender == TabIcon1) ? 1 : (sender == TabIcon2) ? 2 : 0;
        _vm.SelectTab(index);
    }

    /// <summary>tab 切换入口（MainViewModel 事件）</summary>
    private void OnTabChanged(int index) => ShowTab(index);

    /// <summary>显示指定 tab：可见性切换 + 淡入 + 数据刷新</summary>
    private void ShowTab(int index)
    {
        if (index < 0 || index >= _tabs.Length) return;

        for (int i = 0; i < _tabs.Length; i++)
            _tabs[i].IsVisible = i == index;

        var tab = _tabs[index];
        tab.Opacity = 0;
        _ = tab.FadeToAsync(1, 180, Easing.CubicOut);

        if (tab is ITabView tabView)
            _ = tabView.OnTabShownAsync();

        UpdateTabIcons();
    }

    /// <summary>刷新 tab 图标/标签/高亮底色（active=主题色变体+光晕底，inactive=深色白/浅色灰）</summary>
    private void UpdateTabIcons()
    {
        var isDark = _theme.IsEffectivelyDark();
        var activeHex = ThemeHex.GetValueOrDefault(_theme.CurrentTheme, "9b7ed8");
        var primary = Microsoft.Maui.Graphics.Color.FromArgb($"#{activeHex}");
        var icons = new[] { TabIcon0, TabIcon1, TabIcon2 };
        var labels = new[] { TabLabel0, TabLabel1, TabLabel2 };
        var bgs = new[] { TabBg0, TabBg1, TabBg2 };

        for (int i = 0; i < icons.Length; i++)
        {
            bool isActive = _vm.SelectedTabIndex == i;
            icons[i].Source = isActive
                ? $"{TabIconBases[i]}_{activeHex}_active"
                : (isDark ? TabIconBases[i] : $"{TabIconBases[i]}_gray");
            icons[i].Scale = isActive ? 1.12 : 1.0;

            labels[i].TextColor = isActive
                ? (Microsoft.Maui.Graphics.Color)Application.Current!.Resources["TabActiveColor"]
                : (Microsoft.Maui.Graphics.Color)Application.Current!.Resources["TabInactiveColor"];

            // 选中高亮光晕底：主题色 22% 透明度
            bgs[i].BackgroundColor = isActive ? primary.WithAlpha(0.22f) : Microsoft.Maui.Graphics.Colors.Transparent;
        }
    }

    private void OnSafeAreaChanged(object? sender, EventArgs e) =>
        MainThread.BeginInvokeOnMainThread(() =>
            Padding = new Thickness(0, GetTopSafeArea(), 0, 0));

    private static double GetTopSafeArea() =>
#if ANDROID
        SafeAreaHelper.TopInset;
#else
        0;
#endif
}
