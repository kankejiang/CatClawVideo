using CatClawVideo.Maui.ViewModels;
using AppTheme = CatClawVideo.Core.Interfaces.AppTheme;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 主页面：顶部导航栏宿主（首页/历史/收藏/下载/本地/设置），tab 内容为 ContentView 从 DI 注入常驻复用。
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
        FavoritesPage favorites, DownloadsPage downloads, LocalMediaPage local, SettingsPage settings)
    {
        InitializeComponent();
        _vm = vm;
        _theme = theme;
        // 顺序须与 MainViewModel.Tabs 一致：首页 / 历史 / 收藏 / 下载 / 本地 / 设置
        _tabs = [home, history, favorites, downloads, local, settings];

        foreach (var tab in _tabs)
        {
            tab.IsVisible = false;
            ContentHost.Add(tab);
        }

        BindingContext = _vm;
        _vm.TabChanged += OnTabChanged;
        _theme.Applied += UpdateNavTabs;

        // 安全区 padding（Android 透明状态栏/手势条下内容避开系统栏）
        ApplySafeAreaPadding();
#if ANDROID
        LogLayoutChain();
#endif
#if ANDROID
        // 关键：Edge-to-Edge 下 MAUI 会把窗口 insets 自动套在页面内容上（SafeAreaEdges 默认值），
        // 与上面的手动 padding 叠加 → 底部多出一条 ~55dp 的双重空白（2026-09-11 真机 logcat 实锤：
        // ContentHost 244dp，应为 300dp）。insets 已由 SafeAreaHelper 自管，这里关闭 MAUI 自动行为。
        SafeAreaEdges = SafeAreaEdges.None;
#endif
        SafeAreaHelper.SafeAreaChanged += OnSafeAreaChanged;

        // 首个 tab 直接显示（SelectedTabIndex 默认 0 不触发 TabChanged）
        ShowTab(0);

        HandlerChanged += OnPageHandlerChanged;
    }

    protected override void OnAppearing()
    {
        UpdateNavTabs();
        OnPageHandlerChanged(this, EventArgs.Empty);
    }

    /// <summary>
    /// 挂键盘监听。顶部 tab 是 Border + TapGestureRecognizer，默认不在 Tab 焦点链里，
    /// 这里改挂到原生窗口 Content 根元素（键盘事件会冒泡到根），保证任何焦点下都能收到。
    /// </summary>
    private void OnPageHandlerChanged(object? sender, EventArgs e)
    {
#if WINDOWS
        try
        {
            var native = (Window?.Handler?.PlatformView as Microsoft.UI.Xaml.Window)
                ?? (Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Microsoft.UI.Xaml.Window);

            if (native?.Content is Microsoft.UI.Xaml.UIElement root)
            {
                root.KeyDown -= OnPlatformKeyDown;
                root.KeyDown += OnPlatformKeyDown;
            }
        }
        catch { }
#endif
    }

#if WINDOWS
    /// <summary>
    /// 键盘 / 电视遥控导航：数字键 1-5（或 F1-F5）直接切换顶部 tab，F6 直达源配置页。
    /// 焦点位于文本框内时不响应，避免抢走输入。
    /// </summary>
    private void OnPlatformKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var srcName = e.OriginalSource?.GetType().Name ?? "";
        if (srcName.Contains("TextBox") || srcName.Contains("AutoSuggestBox") || srcName.Contains("RichEdit"))
            return;

        int index = e.Key switch
        {
            Windows.System.VirtualKey.Number1 or Windows.System.VirtualKey.NumberPad1 or Windows.System.VirtualKey.F1 => 0,
            Windows.System.VirtualKey.Number2 or Windows.System.VirtualKey.NumberPad2 or Windows.System.VirtualKey.F2 => 1,
            Windows.System.VirtualKey.Number3 or Windows.System.VirtualKey.NumberPad3 or Windows.System.VirtualKey.F3 => 2,
            Windows.System.VirtualKey.Number4 or Windows.System.VirtualKey.NumberPad4 or Windows.System.VirtualKey.F4 => 3,
            Windows.System.VirtualKey.Number5 or Windows.System.VirtualKey.NumberPad5 or Windows.System.VirtualKey.F5 => 4,
            // 第 6 个 tab 只用数字键：F6 另有用途（下方分支直达源配置页）
            Windows.System.VirtualKey.Number6 or Windows.System.VirtualKey.NumberPad6 => 5,
            _ => -1,
        };

        if (index >= 0)
        {
            e.Handled = true;
            MainThread.BeginInvokeOnMainThread(() => _vm.SelectTab(index));
            return;
        }

        if (e.Key == Windows.System.VirtualKey.F6)
        {
            e.Handled = true;
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                try { await Shell.Current.GoToAsync("sourceconfig"); } catch { }
            });
        }
    }
#endif

#if WINDOWS
    /// <summary>
    /// 顶部交互元素（导航 tabs + 搜索框）相对窗口客户区的物理像素矩形。
    /// 无边框窗口下顶栏处于系统标题栏语义区，App 宿主把这些区域标记为
    /// InputNonClientPointerSource.Passthrough，否则点击被拖拽吞掉。
    /// </summary>
    public Windows.Graphics.RectInt32[] GetTitleBarPassthroughRects()
    {
        var rects = new List<Windows.Graphics.RectInt32>();
        void Add(Microsoft.Maui.Controls.VisualElement el)
        {
            if (el?.Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement fe && fe.XamlRoot != null)
            {
                try
                {
                    var p = fe.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
                    var scale = fe.XamlRoot.RasterizationScale;
                    if (fe.ActualWidth <= 0 || fe.ActualHeight <= 0) return;
                    rects.Add(new Windows.Graphics.RectInt32
                    {
                        X = (int)Math.Round(p.X * scale),
                        Y = (int)Math.Round(p.Y * scale),
                        Width = (int)Math.Ceiling(fe.ActualWidth * scale),
                        Height = (int)Math.Ceiling(fe.ActualHeight * scale),
                    });
                }
                catch { }
            }
        }
        Add(NavTabs);
        Add(TopSearchBox);
        return rects.ToArray();
    }
#endif

    private int TabIndexOf(object? sender) =>
        (sender == NavBg1) ? 1
        : (sender == NavBg2) ? 2
        : (sender == NavBg3) ? 3
        : (sender == NavBg4) ? 4
        : (sender == NavBg5) ? 5
        : 0;

    /// <summary>hover 空壳胶囊：未选中 tab 悬停时显示主题色描边 + 文字提亮；选中态样式不覆盖。</summary>
    private void OnTabPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Border b) return;
        var index = TabIndexOf(b);
        if (_vm.SelectedTabIndex == index) return;
        var activeHex = ThemeHex.GetValueOrDefault(_theme.CurrentTheme, "9b7ed8");
        var primary = Microsoft.Maui.Graphics.Color.FromArgb($"#{activeHex}");
        b.Stroke = primary;
        b.StrokeThickness = 1;
        if (index >= 0 && index < 6)
            ((new[] { NavLabel0, NavLabel1, NavLabel2, NavLabel3, NavLabel4, NavLabel5 })[index]).TextColor =
                (Microsoft.Maui.Graphics.Color)Application.Current!.Resources["TextPrimaryColor"];
    }

    private void OnTabPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is not Border b) return;
        var index = TabIndexOf(b);
        if (_vm.SelectedTabIndex == index) return;
        b.StrokeThickness = 0;
        if (index >= 0 && index < 6)
            ((new[] { NavLabel0, NavLabel1, NavLabel2, NavLabel3, NavLabel4, NavLabel5 })[index]).TextColor =
                (Microsoft.Maui.Graphics.Color)Application.Current!.Resources["TextSecondaryColor"];
    }

    private void OnTabTapped(object? sender, TappedEventArgs e)
    {
        _vm.SelectTab(TabIndexOf(sender));
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
        var labels = new[] { NavLabel0, NavLabel1, NavLabel2, NavLabel3, NavLabel4, NavLabel5 };
        var bgs = new[] { NavBg0, NavBg1, NavBg2, NavBg3, NavBg4, NavBg5 };

        for (int i = 0; i < labels.Length; i++)
        {
            bool isActive = _vm.SelectedTabIndex == i;
            labels[i].TextColor = isActive
                ? Colors.White
                : (Microsoft.Maui.Graphics.Color)Application.Current!.Resources["TextSecondaryColor"];
            bgs[i].BackgroundColor = isActive
                ? primary
                : Microsoft.Maui.Graphics.Colors.Transparent;
            bgs[i].StrokeThickness = 0; // 清 hover 空壳胶囊残留
        }
    }

    private void OnSafeAreaChanged(object? sender, EventArgs e) =>
        MainThread.BeginInvokeOnMainThread(ApplySafeAreaPadding);

    /// <summary>
    /// 安全区 padding：顶部避开状态栏，底部避开手势导航条。
    /// 这台设备全面屏手势隐藏导航条时系统上报的 BottomInset=0，内容会贴死物理底边
    /// （2026-09-11 真机实测）——因此保底 16dp 呼吸边距；页面背景仍铺满全屏。
    /// </summary>
    private void ApplySafeAreaPadding() =>
        Padding = new Thickness(0, GetTopSafeArea(), 0, GetBottomSafeArea());

#if ANDROID
    /// <summary>布局追踪 + 底部空白补偿：定位高度分配链（海报墙底部空白的排查入口）。
    /// 2.5s 后（布局稳定）测量：若 ContentHost 之外仍有「窗口高度 − 页面高度 − 页面垂直
    /// padding」的差值（= MAUI 在 Window 层套的手势条安全区内嵌，实测 ~56dp），用负
    /// bottom margin 抵消，让内容延伸到底部系统栏上沿——去除启动时的底部大片空白。</summary>
    private void LogLayoutChain()
    {
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(2500), () =>
        {
            var nav = (ContentHost.Parent is Grid g && g.Children.Count > 0 && g.Children[0] is VisualElement v)
                ? v.Height : -1;
            Android.Util.Log.Info("PosterLayout",
                $"main={Height:F0} padT={Padding.Top:F0} padB={Padding.Bottom:F0} " +
                $"nav={nav:F0} host={ContentHost.Height:F0} win={Window?.Height:F0}");

            // 底部空白补偿：差值 = 窗口高 − 页面高 − 底部 padding（MAUI 在 Window 层套的
            // 手势条安全区内嵌，页面内部 padding 够不着）→ 负 margin 把页面延伸到底部系统栏上沿
            var gap = (Window?.Height ?? Height) - Height - Padding.Bottom;
            if (gap > 12)
            {
                ContentHost.Margin = new Thickness(0, 0, 0, -(gap - 8));
                Android.Util.Log.Info("PosterLayout", $"补偿底部空白 {gap:F0}dp");
            }
            return false;
        });
    }
#endif

    private static double GetTopSafeArea() =>
#if ANDROID
        SafeAreaHelper.TopInset;
#else
        0;
#endif

    private static double GetBottomSafeArea() =>
#if ANDROID
        Math.Max(SafeAreaHelper.BottomInset, 8);
#else
        0;
#endif
}
