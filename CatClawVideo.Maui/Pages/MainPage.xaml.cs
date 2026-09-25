using CatClawVideo.Core.Interfaces;
using CatClawVideo.Maui.Services;
using CatClawVideo.Maui.ViewModels;
using AppTheme = CatClawVideo.Core.Interfaces.AppTheme;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 主页面：顶部导航栏宿主（首页/历史/收藏/下载/本地/设置），tab 内容为 ContentView 从 DI 注入常驻复用。
/// 顶部导航 = 电视遥控横向导航友好；安卓横屏与 Windows 共用同款布局。
///
/// <para><b>焦点导航（三端统一）</b>：主壳层自身是 <see cref="RemoteKeyRouter"/> 处理栈的**最底层**，
/// 管理顶部 tab 的焦点；↑↓←→ 移动、OK 切换、↓ 进入当前页内容区（由页面实现
/// <see cref="IRemoteKeyHandler"/> 接管）。页面内无处可去时按键会落回本层，
/// 于是形成「内容 → 侧栏 → 顶部 tab」的完整走出路径。</para>
/// </summary>
public partial class MainPage : ContentPage, IRemoteKeyHandler
{
    private readonly MainViewModel _vm;
    private readonly IThemeService _theme;
    private readonly ContentView[] _tabs = null!;

    /// <summary>顶部 tab 的焦点壳（与 tab 一一对应；承载焦点态描边）。</summary>
    private readonly Border[] _navShells = new Border[6];
    private bool _topNavFocused;
    private int _focusedTab;

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
        // 顺序须与 MainViewModel.Tabs 一致：首页 / 历史 / 收藏 / 本地 / 设置
        _tabs = [home, history, favorites, local, settings];

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

#if WINDOWS
        // 顶栏拖拽区：布局/尺寸变化后重算（矩形由拖拽元素的实时位置推出，见 WindowDragHelper）。
        // 拖拽区是「反向白名单」—— 只有这一段归系统管，顶栏其余部分自动是客户区、点击直达控件，
        // 所以顶栏压在窗口标题栏那条非客户区上也照样能点（沉浸式与可点兼得）。
        SizeChanged += (_, _) => (Application.Current as App)?.SyncTitleBarDrag();
        // 构造期元素尚无尺寸，等首帧布局完成再补一次
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(300),
            () => { (Application.Current as App)?.SyncTitleBarDrag(); return false; });
#endif
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

        // 顶栏焦点壳须先于首次渲染建立（UpdateNavTabs / RenderTopNav 会用到）
        BuildTopNavFocusShells();

        // 首个 tab 直接显示（SelectedTabIndex 默认 0 不触发 TabChanged）
        ShowTab(0);

        HandlerChanged += OnPageHandlerChanged;
    }

    protected override void OnAppearing()
    {
        UpdateNavTabs();
        OnPageHandlerChanged(this, EventArgs.Empty);
        SyncRemoteKeyStack();
    }

    /// <summary>
    /// 重建按键处理栈顺序：主壳层在下、当前 tab 页在上。
    /// 每次切换 tab 与从二级页返回时都要重排，否则会出现
    /// 「焦点在不可见的页面上移动」或「当前页收不到方向键」。
    /// </summary>
    private void SyncRemoteKeyStack()
    {
        RemoteKeyRouter.Push(this);

        // 关键：把**上一个** tab 的处理者移出栈，并让它交出焦点。
        //
        // 只 Push 不 Pop 的话，切过的 tab 会一直留在路由栈里，而且它们都**不可见** ——
        // 一旦当前页与主壳层都拒绝某个按键，就会落到这些看不见的页面上被执行。
        // 实测（2026-09-19）：焦点在顶栏「设置」上按回车，弹出了首页的「请选择首页数据源」
        // 对话框 —— 因为首页 HomePage 还在栈底，它的默认落点正是「切换源」按钮。
        var current = CurrentTab as IRemoteKeyHandler;
        SwapShownTab(current);
        if (current is not null) RemoteKeyRouter.Push(current);
    }

    /// <summary>当前显示中的 tab 处理者（切走时要先让它交出焦点再出栈）。</summary>
    private IRemoteKeyHandler? _shownTab;

    /// <summary>
    /// 换掉「当前生效的 tab 处理者」：旧的**先交出焦点、再出栈**。
    ///
    /// <para>只出栈是不够的 —— 页面只是被隐藏，对象还活着。若它还停在内容层焦点上
    /// （首页停在分类 chip、历史停在某张海报），按键会先被它吃掉：用户看到的是
    /// 「按了没反应」，而动作其实发生在看不见的页面上。</para>
    /// </summary>
    private void SwapShownTab(IRemoteKeyHandler? next)
    {
        if (_shownTab is not null && !ReferenceEquals(_shownTab, next))
        {
            _shownTab.BlurContent();
            RemoteKeyRouter.Pop(_shownTab);
        }

        _shownTab = next;
    }

    // ═══════════════════════ 顶部 tab 焦点 ═══════════════════════

    /// <summary>
    /// 给 6 个 tab 包一层透明焦点壳（描边不占布局：内容外边距缩进，拉大 Border 画在 padding 区域）。
    /// 不直接改 tab 的选中态样式，避免与 <see cref="UpdateNavTabs"/> 打架。
    /// </summary>
    private void BuildTopNavFocusShells()
    {
        var tabs = new[] { NavBg0, NavBg1, NavBg2, NavBg3, NavBg4 };
        for (int i = 0; i < tabs.Length; i++)
        {
            var shell = new Border
            {
                StrokeThickness = 2,
                Stroke = Colors.Transparent,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
                Padding = new Thickness(2),
                Margin = new Thickness(-2),
                Content = tabs[i],
            };
            _navShells[i] = shell;
            NavTabs.Children[i] = shell;
        }
    }

    /// <summary>把顶部 tab 的焦点态与选中态合并渲染。</summary>
    private void RenderTopNav()
    {
        // 搜索入口：焦点下标 = _navShells.Length（排在 6 个 tab 之后）
        bool searchFocused = _topNavFocused && _focusedTab == _navShells.Length;
        TopSearchBox.Stroke = searchFocused ? ThemeColor("PrimaryColor") : Colors.Transparent;
        TopSearchBox.StrokeThickness = searchFocused ? 2 : 0;
        TopSearchBox.Scale = searchFocused ? 1.05 : 1.0;

        for (int i = 0; i < _navShells.Length; i++)
        {
            if (_navShells[i] is not { } shell) continue;
            bool focused = _topNavFocused && i == _focusedTab;
            shell.Stroke = focused ? ThemeColor("PrimaryColor") : Colors.Transparent;
            shell.Scale = focused ? 1.06 : 1.0;
        }
    }

    private Color ThemeColor(string key)
    {
        try
        {
            if (Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c)
                return c;
        }
        catch { }
        return Colors.Transparent;
    }

    /// <summary>
    /// 本页是否在最前（没有推送页盖在上面）。
    ///
    /// <para>推送页（搜索页、观看页）是整窗覆盖，连顶栏一起盖住 —— 此时绝不能把焦点交给顶栏，
    /// 否则焦点落到**看不见的地方**，表现就是「按方向键没反应，得按 Back 才出来」。
    /// 所以下面「走到边界 → 交给顶栏」都必须先过这道判断。</para>
    /// </summary>
    private static bool Frontmost =>
        Microsoft.Maui.Controls.Shell.Current?.CurrentPage is Pages.MainPage;

    /// <summary>
    /// 顶栏横向移动。范围是「6 个 tab + 搜索入口」（搜索入口排在最右，所以 +1 的位置）。
    /// </summary>
    private void MoveTopFocus(int dir)
    {
        int count = _navShells.Length + 1;                     // +1 = 搜索入口
        _focusedTab = (_focusedTab + dir + count) % count;     // 循环
        RenderTopNav();
    }

    // ═══════════════════════ IRemoteKeyHandler（最底层） ═══════════════════════

    /// <summary>被上层（如设置页）要求接管焦点：落到顶部 tab。</summary>
    public void FocusContent() => FocusTopNav();

    /// <summary>顶栏的持有者就是本页，无需清理自己的高亮。</summary>
    public void BlurContent() { }

    /// <summary>把焦点交给顶栏，落在**当前选中**的 tab 上。</summary>
    public void FocusTopNav() => FocusTopNav(_vm.SelectedTabIndex);

    /// <summary>
    /// 把焦点交给顶栏，并落在指定 tab 上。
    ///
    /// <para><paramref name="tabIndex"/> 由调用方显式传入而不是读 <c>SelectedTabIndex</c>：
    /// 启动与切页都经由 <see cref="ShowTab"/> 调用，此刻不能依赖 MVVM 属性赋值与事件回调的先后顺序。</para>
    /// </summary>
    public void FocusTopNav(int tabIndex)
    {
        // 先让内容区交还焦点：不清它的高亮，顶栏与内容区会「两处同时亮」两个焦点
        // （2026-09-19 用户截图：焦点上移到顶栏后，设置页侧栏还亮着）。
        (CurrentTab as IRemoteKeyHandler)?.BlurContent();

        _topNavFocused = true;
        _focusedTab = Math.Clamp(tabIndex, 0, _navShells.Length - 1);
        RenderTopNav();
    }

    public bool Handle(RemoteKey key)
    {
        switch (key)
        {
            case RemoteKey.Up:
                if (_topNavFocused) return true;                 // 已在最顶，吃掉
                if (!Frontmost) return false;                    // 有推送页盖着顶栏 → 不能把焦点给它
                FocusTopNav();
                return true;

            case RemoteKey.Down:
                if (!_topNavFocused) return false;               // 顶栏未持焦点 → 无事可做
                if (CurrentTab is IRemoteKeyHandler downTarget)
                {
                    _topNavFocused = false;
                    RenderTopNav();
                    downTarget.FocusContent();
                    return true;
                }
                return true;


            case RemoteKey.Left:
                if (_topNavFocused) { MoveTopFocus(-1); return true; }
                if (!Frontmost) return false;
                // 内容区在本方向已到头（设置页侧栏那句「← 交回顶栏」走的就是这条）：
                // 与 ↑ 一致，把焦点交给顶栏 —— 否则按键被丢弃，用户只能按 Back 才出得来。
                FocusTopNav();
                return true;

            case RemoteKey.Right:
                if (_topNavFocused) { MoveTopFocus(+1); return true; }
                if (!Frontmost) return false;
                FocusTopNav();
                return true;

            case RemoteKey.Enter:
                if (!_topNavFocused) return false;

                // 搜索入口（排在 tab 之后）：OK = 进搜索页
                if (_focusedTab == _navShells.Length)
                {
                    OnSearchTapped(this, new TappedEventArgs(null));
                    return true;
                }

                if (_focusedTab != _vm.SelectedTabIndex)
                    _vm.SelectTab(_focusedTab);
                else if (CurrentTab is IRemoteKeyHandler enterTarget)
                {
                    _topNavFocused = false;
                    RenderTopNav();
                    enterTarget.FocusContent();
                }
                return true;

            case RemoteKey.Back:
                // 焦点已不在顶栏：先收回顶栏；已在顶栏则交操作系统处理（Android 由 MAUI 默认退出）
                if (_topNavFocused) return false;
                FocusTopNav();
                return true;
        }
        return false;
    }

    private ContentView CurrentTab =>
        _tabs[Math.Clamp(_vm.SelectedTabIndex, 0, _tabs.Length - 1)];

    // ═══════════════════════ 平台按键接入 ═══════════════════════

    /// <summary>
    /// 挂键盘监听。
    ///
    /// <para><b>必须用 <c>AddHandler(handledEventsToo: true)</c></b>：方向键在 WinUI 里会被
    /// 焦点引擎 / 内部可聚焦控件在冒泡途中标记为 <c>Handled</c>，普通 <c>+=</c> 订阅在根元素上
    /// 根本收不到（数字键因无人认领才能到达，所以「数字键好使、方向键失灵」极具迷惑性）。
    /// <c>handledEventsToo: true</c> 让订阅在事件已被处理的情况下仍被调用，从而可靠接管。</para>
    ///
    /// <para>文本框获得焦点时跳过方向键处理，否则会破坏光标移动。</para>
    /// </summary>
    private void OnPageHandlerChanged(object? sender, EventArgs e)
    {
#if WINDOWS
        try
        {
            var native = (Window?.Handler?.PlatformView as Microsoft.UI.Xaml.Window)
                ?? (Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Microsoft.UI.Xaml.Window);

            if (native?.Content is Microsoft.UI.Xaml.UIElement root && !_keyHandlerAttached)
            {
                // handledEventsToo: true —— 关键。方向键已被上游标记 Handled，必须显式接收。
                //
                // ⚠ 再看这里：冒泡（KeyDownEvent）阶段设置 e.Handled 已经**太晚** ——
                // WinUI 的元素级处理（含「焦点移到带手势的 Border → 回车被当成点击」）
                // 在事件到达根元素之前就跑完了。实测（2026-09-19 用户日志）：
                // 焦点在内容区时按 → 回车，回车被顶栏 tab 的 tap 吃掉 → 又切回首页。
                // 因此必须挂 **隧道路由（PreviewKeyDownEvent）**：从根向下传递，
                // 我们在最上层先拿到按键，处理完置 Handled，原生焦点与元素处理都收不到。
                root.AddHandler(
                    Microsoft.UI.Xaml.UIElement.PreviewKeyDownEvent,
                    new Microsoft.UI.Xaml.Input.KeyEventHandler(OnPlatformKeyDown),
                    handledEventsToo: true);

                _keyHandlerAttached = true;
            }
        }
        catch { }
#endif
    }

#if WINDOWS
    private bool _keyHandlerAttached;
#endif

#if WINDOWS

    /// <summary>
    /// 键盘 / 电视遥控按键总入口（经 <c>AddHandler(handledEventsToo: true)</c> 挂载）。
    /// <list type="bullet">
    ///   <item>方向键 / Enter / Esc → 焦点导航（<see cref="RemoteKeyRouter"/>）；</item>
    ///   <item>数字键 1-6、F1-F5 → 直接切换顶部 tab；</item>
    ///   <item>F6 → 源配置页。</item>
    /// </list>
    /// 数字键与 F 键优先于焦点导航处理，避免被当作导航键。
    /// </summary>
    private void OnPlatformKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var srcName = e.OriginalSource?.GetType().Name ?? "";
        bool inText = srcName.Contains("TextBox") || srcName.Contains("AutoSuggestBox") || srcName.Contains("RichEdit");

        // ⓪ 模态弹窗（如「请选择首页数据源」）盖在最上层时，数字键 / F6 不该在背后切页 ——
        //    弹窗期间只允许弹窗自己（经 RemoteKeyRouter）消费按键。
        //    （2026-09-19：加弹窗遥控支持时一并堵掉这个洞）
        bool modalUp = Shell.Current is { } shell && shell.Navigation.ModalStack.Count > 0;

        // ① 数字键 / F1-F5 直达 tab（文本框内不拦截）
        int index = inText || modalUp ? -1 : e.Key switch
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

        if (!inText && !modalUp && e.Key == Windows.System.VirtualKey.F6)
        {
            e.Handled = true;
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                try { await Shell.Current.GoToAsync("sourceconfig"); } catch { }
            });
            return;
        }

        // ② 焦点导航（方向键 / Enter / Esc）
        if (inText) return;   // 文本框内放行，否则会破坏光标移动
        if (!TryMapRemoteKey(e.Key, out var remote)) return;

        // ⚠ 无论栈里有没有人消费，都要吃掉这个键。
        // 焦点由应用自己管（RemoteKeyRouter）：一旦放行给 WinUI，它的焦点引擎会自己
        // 找下一个可聚焦元素（**带手势的 Border 也算**），并在它身上画系统默认焦点框
        // —— 白线 + 蓝色外圈，会与我们的焦点环叠在一起（2026-09-19 用户截图：
        // 播放页「返回」上出现白框）。
        RemoteKeyRouter.Handle(remote);
        e.Handled = true;
    }

    /// <summary>Windows 虚拟键 → 统一远程按键。</summary>
    private static bool TryMapRemoteKey(Windows.System.VirtualKey key, out RemoteKey remote)
    {
        switch (key)
        {
            case Windows.System.VirtualKey.Up: remote = RemoteKey.Up; return true;
            case Windows.System.VirtualKey.Down: remote = RemoteKey.Down; return true;
            case Windows.System.VirtualKey.Left: remote = RemoteKey.Left; return true;
            case Windows.System.VirtualKey.Right: remote = RemoteKey.Right; return true;
            case Windows.System.VirtualKey.Enter: remote = RemoteKey.Enter; return true;
            case Windows.System.VirtualKey.Escape: remote = RemoteKey.Back; return true;
            default: remote = default; return false;
        }
    }

    /// <summary>
    /// Windows 窗口拖拽元素 = 顶栏「搜索框 → 窗口按钮」之间的空白段（XAML 里的 TitleBarDragArea）。
    ///
    /// <para>由 <see cref="App.SyncTitleBarDrag"/> 取用，交给 <see cref="Services.WindowDragHelper"/>
    /// 换算成 <c>AppWindow.TitleBar.SetDragRectangles</c>：只有这一段归系统当标题栏，
    /// 品牌 / tabs / 搜索框都在别的列 —— 自动是客户区、点击直达控件。</para>
    ///
    /// <para>2026-09-19 定案：中途试过 <c>Window.SetTitleBar(元素)</c>（沉浸式更省事），
    /// 但它会让顶栏 tab「点好几次才有反应」—— 原因与对比见 WindowDragHelper 类注释。</para>
    /// </summary>
    public Microsoft.UI.Xaml.FrameworkElement? TitleBarDragElement =>
        TitleBarDragArea?.Handler?.PlatformView as Microsoft.UI.Xaml.FrameworkElement;
#endif

    private int TabIndexOf(object? sender) =>
        (sender == NavBg1) ? 1
        : (sender == NavBg2) ? 2
        : (sender == NavBg3) ? 3
        : (sender == NavBg4) ? 4
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
        if (index >= 0 && index < 5)
            ((new[] { NavLabel0, NavLabel1, NavLabel2, NavLabel3, NavLabel4 })[index]).TextColor =
                (Microsoft.Maui.Graphics.Color)Application.Current!.Resources["TextPrimaryColor"];
    }

    private void OnTabPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is not Border b) return;
        var index = TabIndexOf(b);
        if (_vm.SelectedTabIndex == index) return;
        b.StrokeThickness = 0;
        if (index >= 0 && index < 5)
            ((new[] { NavLabel0, NavLabel1, NavLabel2, NavLabel3, NavLabel4 })[index]).TextColor =
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
    private void OnTabChanged(int index)
    {
        ShowTab(index);
        HandFocusToContent();
    }

    /// <summary>
    /// 用户**明确选了**某个 tab 之后，把焦点直接送进内容区。
    ///
    /// <para>不这么做的话，焦点还留在顶栏上：用户接着按 ←/→ 是在顶栏换 tab，
    /// 再按 OK 又切到别的 tab —— 观感就是「我明明选了历史、按方向键进了海报、
    /// 一回车却回到了首页」（2026-09-19 用户反馈）。</para>
    ///
    /// <para>启动时不走这里（首个 tab 是直接显示的，不触发 TabChanged），
    /// 所以「启动停在顶栏首页」这条仍然成立。</para>
    /// </summary>
    private void HandFocusToContent()
    {
        _topNavFocused = false;
        RenderTopNav();
        (CurrentTab as IRemoteKeyHandler)?.FocusContent();
    }

    /// <summary>显示指定 tab：可见性切换 + 淡入 + 数据刷新</summary>
    private void ShowTab(int index)
    {
        if (index < 0 || index >= _tabs.Length) return;

        for (int i = 0; i < _tabs.Length; i++)
            _tabs[i].IsVisible = i == index;

        var tab = _tabs[index];
        tab.Opacity = 0;
        _ = tab.FadeToAsync(1, 180, Easing.CubicOut);

        // 上一个 tab 交出焦点并出栈 —— 它只是被隐藏，仍留在按键栈里，
        // 若还停在内容层焦点上就会继续吃按键（用户看不到任何反应）。
        SwapShownTab(tab as IRemoteKeyHandler);

        // 先让主壳层入栈（靠下），再由页面把自己推到栈顶
        RemoteKeyRouter.Push(this);
        if (tab is ITabView tabView)
            _ = tabView.OnTabShownAsync();

        UpdateNavTabs();

        // 焦点留在顶栏，落在刚显示的 tab 上（2026-09-19 用户反馈：启动时焦点被首页的
        // 「切换源」抢走，应该在顶栏「首页」上）。要进内容区按 ↓ / OK 即可。
        //
        // ⚠ 必须排在 OnTabShownAsync 之后：内容页会在那里给自己设一个初始焦点层
        // （首页 → 切换源、设置页 → 侧栏），而 FocusTopNav 会调它的 BlurContent
        // 把这一层熄掉 —— 顺序反过来就会留下双高亮。
        FocusTopNav(index);
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
            bgs[i].StrokeThickness = 0; // 清 hover 空壳胶囊残留
        }
        RenderTopNav();
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
