using CatClawVideo.Maui.ViewModels;
using AppTheme = CatClawVideo.Core.Interfaces.AppTheme;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 主页面：顶部导航栏宿主（首页/历史/收藏/本地/设置），tab 内容为 ContentView 从 DI 注入常驻复用。
/// 顶部导航 = 电视遥控横向导航友好；安卓横屏与 Windows 共用同款布局。
/// </summary>
public partial class MainPage : ContentPage
{
    private readonly MainViewModel _vm;
    private readonly IThemeService _theme;
    private readonly ContentView[] _tabs = null!;

    /// <summary>主题色枚举 → 主题色十六进制（与 ThemeService 的 ThemeMap 一致）</summary>
    private static readonly Dictionary<AppTheme, string> ThemeHex = new()
    {
        [AppTheme.Purple] = "9b7ed8",
        [AppTheme.Pink] = "ec407a",
        [AppTheme.Blue] = "42a5f5",
        [AppTheme.Orange] = "ff7043",
        [AppTheme.Teal] = "26a69a",
    };

    public MainPage(MainViewModel vm, IThemeService theme, HomePage home, HistoryPage history,
        FavoritesPage favorites, LocalMediaPage local, SettingsPage settings)
    {
        InitializeComponent();
        _vm = vm;
        _theme = theme;
        _tabs = [home, history, favorites, local, settings];

        foreach (var tab in _tabs)
        {
            tab.IsVisible = false;
            ContentHost.Add(tab);
        }

        BindingContext = _vm;
        _vm.TabChanged += OnTabChanged;
        _theme.Applied += UpdateNavTabs;

        // 安全区 padding（Android 透明状态栏下内容避开系统栏）
        Padding = new Thickness(0, GetTopSafeArea(), 0, 0);
        SafeAreaHelper.SafeAreaChanged += OnSafeAreaChanged;

        // 首个 tab 直接显示（SelectedTabIndex 默认 0 不触发 TabChanged）
        ShowTab(0);
    }

    protected override void OnAppearing() => UpdateNavTabs();

    private void OnTabTapped(object? sender, TappedEventArgs e)
    {
        var index = (sender == NavBg1) ? 1
            : (sender == NavBg2) ? 2
            : (sender == NavBg3) ? 3
            : (sender == NavBg4) ? 4
            : 0;
        _vm.SelectTab(index);
    }

    /// <summary>顶栏搜索入口 → 搜索页</summary>
    private async void OnSearchTapped(object? sender, TappedEventArgs e)
    {
        try { await Shell.Current.GoToAsync("search"); } catch { }
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

        UpdateNavTabs();
    }

    /// <summary>刷新导航 tabs（选中：主题色实底胶囊 + 白字；未选中：透明底 + 次级文字）</summary>
    private void UpdateNavTabs()
    {
        var activeHex = ThemeHex.GetValueOrDefault(_theme.CurrentTheme, "9b7ed8");
        var primary = Microsoft.Maui.Graphics.Color.FromArgb($"#{activeHex}");
        var labels = new[] { NavLabel0, NavLabel1, NavLabel2, NavLabel3, NavLabel4 };
        var bgs = new[] { NavBg0, NavBg1, NavBg2, NavBg3, NavBg4 };

        for (int i = 0; i < labels.Length; i++)
        {
            bool isActive = _vm.SelectedTabIndex == i;
            labels[i].TextColor = isActive
                ? Colors.White
                : (Microsoft.Maui.Graphics.Color)Application.Current!.Resources["TextSecondaryColor"];
            bgs[i].BackgroundColor = isActive
                ? primary
                : Microsoft.Maui.Graphics.Colors.Transparent;
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
