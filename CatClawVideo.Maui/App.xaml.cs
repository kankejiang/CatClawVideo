using CatClawVideo.Maui.Services;

namespace CatClawVideo.Maui;

public partial class App : Application
{
#if WINDOWS
    private static IntPtr _appHwnd;
    private static Microsoft.UI.Windowing.AppWindow? _appWindow;

    /// <summary>原生窗口 / AppWindow 静态访问器（照抄猫爪音乐：页面用 SetTitleBar 指定拖拽区）</summary>
    public static Microsoft.UI.Xaml.Window? CurrentNativeWindow { get; private set; }
    public static Microsoft.UI.Windowing.AppWindow? CurrentAppWindow { get; private set; }

    /// <summary>主窗口句柄（原生对话框 owner 用；窗口创建前为 Zero）</summary>
    public static IntPtr MainWindowHwnd => _appHwnd;

    /// <summary>
    /// 播放页原地全屏：窗口切换 FullScreen/Overlapped presenter（不重建页面/播放器）。
    /// 非 Windows 平台由各页自行处理（Android 走横屏 + 沉浸式）。
    ///
    /// <para><b>幂等</b>（2026-09-19 用户实测「全屏时 NVIDIA 浮窗反复闪烁」）：重复调用
    /// <c>SetPresenter</c> 会让窗口经历一次「退出全屏 → 再进全屏」的状态抖动，
    /// 这类抖动会被 GPU 厂商的覆盖层（NVIDIA GeForce Experience / AMD 等）识别为
    /// 全屏状态变化并弹出/刷新它们的提示浮窗 —— 表现为浮窗闪烁。故状态未变时直接返回。</para>
    /// </summary>
    public static void SetWindowFullscreen(bool fullscreen)
    {
        try
        {
            if (_appWindow == null) return;

            var want = fullscreen
                ? Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen
                : Microsoft.UI.Windowing.AppWindowPresenterKind.Overlapped;
            if (_appWindow.Presenter.Kind == want) return;   // 已是目标状态：不重复设置

            _appWindow.SetPresenter(want);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] SetWindowFullscreen({fullscreen}) failed: {ex.Message}");
        }
    }
#endif

    public App()
    {
        InitializeComponent();

        // 应用主题 + 跟随系统深浅色变化
        try
        {
            var themeService = MauiProgram.Services.GetRequiredService<IThemeService>();
            themeService.ApplyTheme();
            RequestedThemeChanged += (_, _) =>
            {
                MainThread.BeginInvokeOnMainThread(themeService.ApplyTheme);
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] Theme init failed: {ex.Message}");
        }

        // WebDAV 本地流代理预热：播放历史里存的是 http://127.0.0.1:{port}/wd/... 代理 URL，
        // 重启后直接从历史回放时代理必须已在监听。默认端口优先（跨会话尽量复用同一端口），
        // 未配置过连接则不占端口（此时历史里也不可能有网络播放记录）。
        try
        {
            var webDavProxy = MauiProgram.Services.GetRequiredService<CatClawVideo.Core.Network.WebDavStreamProxy>();
            if (!webDavProxy.IsRunning && webDavProxy.HasProfiles)
                webDavProxy.EnsureStarted();
        }
        catch { /* 代理启动失败只影响 WebDAV 回放，不阻塞启动 */ }
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var shell = MauiProgram.Services.GetRequiredService<AppShell>();

#if ANDROID
        // Android 启动页：窗口先落轻量启动页，布局稳定后切主界面。
        // 直接以主界面冷启动时，MAUI 的窗口 inset 重置竞态会让底部留一块空白；
        // 「进二级页再返回」式的页面切换会触发完整重排——把这次切换搬到启动时。
        var window = new Window(new Pages.SplashPage()) { Title = "" };
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(1500); } catch { }
            MainThread.BeginInvokeOnMainThread(() => window.Page = shell);
        });
        return window;
#else
        var window = new Window(shell)
        {
            // 清空原生窗口标题文字（避免任务栏/Alt+Tab 显示 "CatClawVideo"）
            Title = "",
        };
#endif // ANDROID

#if WINDOWS
        // 窗口尺寸：默认 1600×800；用户调整过则回放上次尺寸（见 LoadSavedWindowSize）
        var (initW, initH) = LoadSavedWindowSize();
        window.MinimumWidth = 900;
        window.MinimumHeight = 600;
        window.Width = initW;
        window.Height = initH;

        // MAUI 内容根默认按"有标题栏"预留 ~32px 占位 → 设不可见 TitleBar 让内容延伸进标题栏区域
        window.TitleBar = new Microsoft.Maui.Controls.TitleBar { IsVisible = false };

        window.HandlerChanged += (_, _) =>
        {
            if (window.Handler?.PlatformView is Microsoft.UI.Xaml.Window nativeWindow)
            {
                CurrentNativeWindow = nativeWindow;
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
                _appHwnd = hwnd;
                _appWindow = appWindow;
                CurrentAppWindow = appWindow;

                // 启动窗口尺寸 + 居中（物理像素 = 逻辑 × DPI）：
                // 尺寸优先回放用户上次调整值，无记录时用默认 1600×800
                ApplyStartupWindowSize(appWindow, hwnd);

                // 退出时记住窗口尺寸，下次启动回放
                nativeWindow.Closed += (_, _) => SaveWindowSize(appWindow, hwnd);

                try
                {
                    // ① 内容延伸到标题栏区域（隐藏系统标题栏绘制，顶导航即标题栏）
                    appWindow.TitleBar.ExtendsContentIntoTitleBar = true;

                    // ①b 拖拽矩形随窗口尺寸重算：顶栏那段空白是 * 列，宽度跟着窗口变，
                    //    不重算就会错位（旧版「窗口拖不动」的另一半原因）。
                    nativeWindow.SizeChanged += (_, _) => SyncTitleBarDrag();
                    // ② 保留标题栏实体（原生拖拽与窗口能力的前提），能力全保留
                    if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter overlappedPresenter)
                    {
                        overlappedPresenter.IsMaximizable = true;
                        overlappedPresenter.IsResizable = true;
                        overlappedPresenter.IsMinimizable = true;
                    }
                    // ③ 显式恢复 Windows 11 原生圆角
                    int cornerRound = 2; // DWMWCP_ROUND
                    DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerRound,
                        (uint)System.Runtime.InteropServices.Marshal.SizeOf<int>());
                }
                catch { }

                UpdateWindowsTheme(MauiProgram.Services.GetService<IThemeService>()?.IsEffectivelyDark()
                    ?? RequestedTheme == Microsoft.Maui.ApplicationModel.AppTheme.Dark);

                // ③ 窗口拖拽区：照抄猫爪音乐用 Window.SetTitleBar(元素)，由框架托管元素位置 ——
                //   **不需要**挂 SizeChanged 重算（这是替代 SetDragRectangles 的关键好处）。

                // ③b 每次导航后按当前页面重设拖拽元素：
                //   主页/观看页各自有拖拽元素，其它页面（搜索/设置/源配置…）没有 → 传 null，
                //   避免元素不可用时留下一个无效拖拽区。
                if (Microsoft.Maui.Controls.Shell.Current is { } sh)
                    sh.Navigated += (_, _) => SyncTitleBarDrag();

                // ④ 窗口激活后（布局完成）：反射折叠 MAUI 内部 32px 标题栏宿主（官方 workaround dotnet/maui#36040）
                global::Windows.Foundation.TypedEventHandler<object, Microsoft.UI.Xaml.WindowActivatedEventArgs>? firstActivated = null;
                firstActivated = (_, _) =>
                {
                    nativeWindow.Activated -= firstActivated;
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(150);
                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            try
                            {
                                InvokeMauiSetTitleBarVisibility(window);
                                SyncTitleBarDrag();
                                UpdateWindowsTheme(MauiProgram.Services.GetService<IThemeService>()?.IsEffectivelyDark()
                                    ?? RequestedTheme == Microsoft.Maui.ApplicationModel.AppTheme.Dark);
                            }
                            catch { }
                        });
                    });
                };
                nativeWindow.Activated += firstActivated;
            }
        };
#endif
        return window;
    }

#if ANDROID
    /// <summary>
    /// 当前是否处于横屏。App 基准方向即横屏（MainActivity 声明 SensorLandscape），
    /// 故初始为 true —— 播放页旋转按钮首次点击应切到竖屏，而不是"先切一次无效的横屏"。
    /// </summary>
    public bool ManualLandscape { get; private set; } = true;

    /// <summary>切回横屏（基准方向）。播放页旋转按钮 / 退出播放页时用。</summary>
    public void ForceLandscape()
    {
        ManualLandscape = true;
        try
        {
            if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity is { } activity)
                activity.RequestedOrientation = Android.Content.PM.ScreenOrientation.SensorLandscape;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[App] 切横屏失败: {ex.Message}"); }
    }

    /// <summary>临时切竖屏（播放页旋转按钮覆盖基准横屏）。</summary>
    public void ReleaseLandscape()
    {
        ManualLandscape = false;
        try
        {
            if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity is { } activity)
                activity.RequestedOrientation = Android.Content.PM.ScreenOrientation.SensorPortrait;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[App] 切竖屏失败: {ex.Message}"); }
    }

    /// <summary>切换横竖屏（旋转按钮）：当前横屏则切竖屏，当前竖屏则切回横屏。</summary>
    public void ToggleLandscape()
    {
        if (ManualLandscape) ReleaseLandscape();
        else ForceLandscape();
    }
#endif

#if WINDOWS
    /// <summary>Windows：更新标题栏深浅色配色（DWM 沉浸式深色模式 + caption 按钮颜色）</summary>
    public static void UpdateWindowsTheme(bool isDark)
    {
        try
        {
            if (_appHwnd == IntPtr.Zero) return;

            int darkMode = isDark ? 1 : 0;
            DwmSetWindowAttribute(_appHwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode,
                (uint)System.Runtime.InteropServices.Marshal.SizeOf<int>());

            if (_appWindow?.TitleBar is { } titleBar)
            {
                var transparent = global::Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00);
                titleBar.ButtonBackgroundColor = transparent;
                titleBar.ButtonInactiveBackgroundColor = transparent;
                if (isDark)
                {
                    titleBar.ButtonForegroundColor = global::Windows.UI.Color.FromArgb(0xFF, 0x8D, 0x93, 0xB7);
                    titleBar.ButtonHoverForegroundColor = global::Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
                    titleBar.ButtonHoverBackgroundColor = global::Windows.UI.Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF);
                    titleBar.ForegroundColor = global::Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
                }
                else
                {
                    titleBar.ButtonForegroundColor = global::Windows.UI.Color.FromArgb(0xFF, 0x4A, 0x52, 0x78);
                    titleBar.ButtonHoverForegroundColor = global::Windows.UI.Color.FromArgb(0xFF, 0x1A, 0x1F, 0x3A);
                    titleBar.ButtonHoverBackgroundColor = global::Windows.UI.Color.FromArgb(0x1A, 0x00, 0x00, 0x00);
                    titleBar.ForegroundColor = global::Windows.UI.Color.FromArgb(0xFF, 0x1A, 0x1F, 0x3A);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] UpdateWindowsTheme failed: {ex.Message}");
        }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint dwAttribute, ref int pvAttribute, uint cbAttribute);

    private const uint DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33; // 值：0=默认 1=不圆角 2=圆角(ROUND)

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>
    /// 按**当前页面**设置窗口拖拽元素（照抄猫爪音乐：<c>Window.SetTitleBar(element)</c>）。
    ///
    /// <para>取代原先的 <c>AppWindow.TitleBar.SetDragRectangles</c> 方案 —— 那套要自己算矩形
    /// （坐标系换算 + DPI + 窗口按钮留白），且**算一次就定死**：布局未就绪、页面切换、
    /// 窗口缩放时不重算就会错位或归零，表现为「窗口突然拖不动、缩放一下才恢复」。
    /// <c>SetTitleBar</c> 由框架托管元素位置，**任何尺寸变化都自动跟随，无需重算**。</para>
    ///
    /// <para>页面自带拖拽元素的用页面的（观看页），其余页面用主页顶栏的空白段（MainPage），
    /// 都没有则不设，避免元素不可用时留下无效拖拽区。</para>
    /// </summary>
    public void SyncTitleBarDrag()
    {
        try
        {
            var current = Microsoft.Maui.Controls.Shell.Current?.CurrentPage;

            // ① 页面自管优先：播放页这类「空白分散在控件四周」的顶栏，由页面自己按
            //    「整条顶栏的横向补集」声明（AttachStrip）。必须在这里处理 —— 页面在
            //    OnAppearing 里自己设会被下面 ② 的清零覆盖掉。
            if (current is Services.IWindowDragArea selfManaged && selfManaged.ApplyWindowDragArea())
                return;

            var el = current switch
            {
                Pages.MainPage mp => mp.TitleBarDragElement,
                _ => null,
            };

            // 有拖拽元素的页面 → 声明矩形；没有的页面 → 清空。
            // 清空是必须的：否则上一页留下的拖拽区会在这个页面上把顶栏当标题栏、误吞点击。
            if (el is null) Services.WindowDragHelper.Detach();
            else Services.WindowDragHelper.Attach(el);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] SyncTitleBarDrag failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 反射调用 MAUI 内部 NavigationRootManager.SetTitleBarVisibility(false)：
    /// 折叠内容根里 32px 标题栏宿主、清零 NavigationViewContentMargin（官方 workaround dotnet/maui#36040）。
    /// </summary>
    private static void InvokeMauiSetTitleBarVisibility(Microsoft.Maui.Controls.Window mauiWindow)
    {
        try
        {
            var mauiContext = mauiWindow.Handler?.MauiContext;
            var navManager = mauiContext?.Services.GetService(typeof(Microsoft.Maui.Platform.NavigationRootManager));
            if (navManager == null)
                navManager = FindNavigationRootManagerFromWindow(mauiWindow);
            if (navManager == null) return;

            var method = navManager.GetType().GetMethod("SetTitleBarVisibility",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public);
            method?.Invoke(navManager, new object[] { false });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] InvokeMauiSetTitleBarVisibility failed: {ex.Message}");
        }
    }

    /// <summary>遍历原生视觉树找 WindowRootView，反射读其 _navigationRootManager 私有字段。</summary>
    private static object? FindNavigationRootManagerFromWindow(Microsoft.Maui.Controls.Window mauiWindow)
    {
        try
        {
            var nativeWindow = mauiWindow.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
            if (nativeWindow == null) return null;

            var windowRootView = FindVisualNode(nativeWindow.Content, "WindowRootView");
            if (windowRootView == null) return null;

            var field = windowRootView.GetType().GetField("_navigationRootManager",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return field?.GetValue(windowRootView);
        }
        catch { return null; }
    }

    private static Microsoft.UI.Xaml.DependencyObject? FindVisualNode(Microsoft.UI.Xaml.DependencyObject? el, string typeName, int depth = 0)
    {
        if (el == null || depth > 8) return null;
        if (el.GetType().Name == typeName) return el;
        try
        {
            var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(el);
            for (int i = 0; i < count; i++)
            {
                var hit = FindVisualNode(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(el, i), typeName, depth + 1);
                if (hit != null) return hit;
            }
        }
        catch { }
        return null;
    }

    // ═══════════ 窗口尺寸记忆（默认 1600×800；用户调整后下次启动保持）═══════════

    private const string PrefWinW = "window_width";
    private const string PrefWinH = "window_height";
    private const double DefaultWinW = 1600;
    private const double DefaultWinH = 800;

    /// <summary>回放窗口逻辑尺寸：优先上次用户调整值，无记录 / 值非法时用默认 1600×800。</summary>
    private static (double Width, double Height) LoadSavedWindowSize()
    {
        try
        {
            var w = Microsoft.Maui.Storage.Preferences.Default.Get(PrefWinW, 0d);
            var h = Microsoft.Maui.Storage.Preferences.Default.Get(PrefWinH, 0d);
            if (w >= 400 && h >= 300) return (w, h);
        }
        catch { }
        return (DefaultWinW, DefaultWinH);
    }

    /// <summary>按逻辑尺寸 × DPI 还原窗口大小并居中（只记忆尺寸，不记忆位置）。</summary>
    private static void ApplyStartupWindowSize(Microsoft.UI.Windowing.AppWindow appWindow, IntPtr hwnd)
    {
        try
        {
            var (logicalW, logicalH) = LoadSavedWindowSize();
            var work = Microsoft.UI.Windowing.DisplayArea.Primary.WorkArea;
            var dpi = (int)GetDpiForWindow(hwnd);
            var scale = dpi > 0 ? dpi / 96.0 : 1.0;
            var winW = (int)Math.Min(logicalW * scale, work.Width);
            var winH = (int)Math.Min(logicalH * scale, work.Height);
            appWindow.MoveAndResize(new global::Windows.Graphics.RectInt32
            {
                X = work.X + (work.Width - winW) / 2,
                Y = work.Y + (work.Height - winH) / 2,
                Width = winW,
                Height = winH,
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] ApplyStartupWindowSize failed: {ex.Message}");
        }
    }

    /// <summary>持久化当前窗口尺寸（换算成逻辑像素，跨不同 DPI 依然有效）。
    /// 最大化 / 全屏 / 最小化时不覆盖 —— 用户还原窗口后仍回到上次设定尺寸。</summary>
    private static void SaveWindowSize(Microsoft.UI.Windowing.AppWindow appWindow, IntPtr hwnd)
    {
        try
        {
            if (appWindow.Presenter.Kind != Microsoft.UI.Windowing.AppWindowPresenterKind.Overlapped) return;
            if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter
                { State: not Microsoft.UI.Windowing.OverlappedPresenterState.Restored }) return;
            if (appWindow.Size.Width <= 0 || appWindow.Size.Height <= 0) return;

            var dpi = (int)GetDpiForWindow(hwnd);
            var scale = dpi > 0 ? dpi / 96.0 : 1.0;
            Microsoft.Maui.Storage.Preferences.Default.Set(PrefWinW, appWindow.Size.Width / scale);
            Microsoft.Maui.Storage.Preferences.Default.Set(PrefWinH, appWindow.Size.Height / scale);
        }
        catch { }
    }
#endif
}
