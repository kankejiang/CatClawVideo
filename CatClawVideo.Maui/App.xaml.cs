using CatClawVideo.Maui.Services;

namespace CatClawVideo.Maui;

public partial class App : Application
{
#if WINDOWS
    private static IntPtr _appHwnd;
    private static Microsoft.UI.Windowing.AppWindow? _appWindow;
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
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var shell = MauiProgram.Services.GetRequiredService<AppShell>();
        var window = new Window(shell)
        {
            // 清空原生窗口标题文字（避免任务栏/Alt+Tab 显示 "CatClawVideo"）
            Title = "",
        };

#if WINDOWS
        window.MinimumWidth = 900;
        window.MinimumHeight = 600;
        window.Width = 1280;
        window.Height = 800;

        // MAUI 内容根默认按"有标题栏"预留 ~32px 占位 → 设不可见 TitleBar 让内容延伸进标题栏区域
        window.TitleBar = new Microsoft.Maui.Controls.TitleBar { IsVisible = false };

        window.HandlerChanged += (_, _) =>
        {
            if (window.Handler?.PlatformView is Microsoft.UI.Xaml.Window nativeWindow)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
                _appHwnd = hwnd;
                _appWindow = appWindow;

                // 启动窗口居中（物理像素 = 逻辑 × DPI）
                try
                {
                    var work = Microsoft.UI.Windowing.DisplayArea.Primary.WorkArea;
                    int dpi = (int)GetDpiForWindow(hwnd);
                    double scale = dpi > 0 ? dpi / 96.0 : 1.0;
                    var winW = (int)Math.Min(1280 * scale, work.Width);
                    var winH = (int)Math.Min(800 * scale, work.Height);
                    appWindow.MoveAndResize(new global::Windows.Graphics.RectInt32
                    {
                        X = work.X + (work.Width - winW) / 2,
                        Y = work.Y + (work.Height - winH) / 2,
                        Width = winW,
                        Height = winH,
                    });
                }
                catch { }

                try
                {
                    // ① 内容延伸到标题栏区域（隐藏系统标题栏绘制，顶导航即标题栏）
                    appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
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

                // ③ 标题栏拖拽区穿透：顶栏处于非客户区语义，导航 tabs/搜索框必须标为 Passthrough
                nativeWindow.SizeChanged += (_, _) => SchedulePassthroughRefresh();

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
                                SchedulePassthroughRefresh();
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
    /// <summary>是否由用户锁定横屏（播放页旋转按钮状态）</summary>
    public bool ManualLandscape { get; private set; }

    /// <summary>强制横屏（播放页全屏观看）</summary>
    public void ForceLandscape()
    {
        ManualLandscape = true;
        try
        {
            if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity is { } activity)
                activity.RequestedOrientation = Android.Content.PM.ScreenOrientation.SensorLandscape;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[App] 强制横屏失败: {ex.Message}"); }
    }

    /// <summary>恢复竖屏（退出全屏观看）</summary>
    public void ReleaseLandscape()
    {
        ManualLandscape = false;
        try
        {
            if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity is { } activity)
                activity.RequestedOrientation = Android.Content.PM.ScreenOrientation.SensorPortrait;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[App] 恢复竖屏失败: {ex.Message}"); }
    }

    /// <summary>切换横竖屏（旋转按钮）</summary>
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint p);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out NativeRect r);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    private Microsoft.UI.Input.InputNonClientPointerSource? _nonClientInput;

    /// <summary>
    /// 布局稳定后多次刷新 Passthrough 矩形：标题栏宿主折叠（SetTitleBarVisibility）
    /// 会让顶栏布局在启动后数秒内持续变化，单次计算会拿到旧位置。
    /// </summary>
    private void SchedulePassthroughRefresh()
    {
        _ = Task.Run(async () =>
        {
            foreach (var delay in new[] { 200, 400, 800, 1500, 3000 })
            {
                try { await Task.Delay(delay); } catch { return; }
                MainThread.BeginInvokeOnMainThread(UpdateTitleBarPassthrough);
            }
        });
    }

    /// <summary>
    /// 标题栏拖拽区（用户方案·反向白名单）：**显式声明拖拽区 = 搜索框右侧空白段**
    /// （搜索框右缘 → 窗口控制按钮区），其余顶栏（品牌/tabs/搜索框）自动归客户区、点击直达控件。
    /// 注：InputNonClientPointerSource Passthrough 对物理鼠标输入不可靠（SendInput 有效、真实点击仍被吞），
    /// SetDragRectangles 是根治方案。
    /// </summary>
    private void UpdateTitleBarPassthrough()
    {
        try
        {
            if (_appWindow == null || _appHwnd == IntPtr.Zero) return;
            var mainPage = MauiProgram.Services.GetService<Pages.MainPage>();
            var rects = mainPage?.GetTitleBarPassthroughRects();
            if (rects is not { Length: > 0 }) return;

            // 客户区尺寸 + 坐标系校正（TransformToVisual 是客户区坐标，SetDragRectangles 是窗口坐标）
            if (!GetClientRect(_appHwnd, out var rc)) return;
            var origin = new NativePoint { X = 0, Y = 0 };
            ClientToScreen(_appHwnd, ref origin);
            var offX = origin.X - _appWindow.Position.X;
            var offY = origin.Y - _appWindow.Position.Y;
            var clientW = rc.Right - rc.Left;

            var scale = GetDpiForWindow(_appHwnd) / 96.0;
            var searchRight = rects.Max(r => r.X + r.Width);
            var buttonZone = (int)(150 * scale); // 右端 min/max/close 按钮区（~138 逻辑px，留余量）
            var dragX = offX + searchRight;
            var dragW = offX + clientW - buttonZone - dragX;
            if (dragW <= 0) return;

            var dragRect = new Windows.Graphics.RectInt32
            {
                X = dragX,
                Y = offY,
                Width = dragW,
                Height = (int)Math.Ceiling(56 * scale),
            };
            _appWindow.TitleBar.SetDragRectangles(new[] { dragRect });

            // 调试日志（自测点击用）
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CatClawVideo");
                Directory.CreateDirectory(dir);
                var winPos = _appWindow.Position;
                var winSize = _appWindow.Size;
                File.AppendAllText(Path.Combine(dir, "passthrough.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} win={winPos.X},{winPos.Y},{winSize.Width},{winSize.Height} drag=[{dragRect.X},{dragRect.Y},{dragRect.Width},{dragRect.Height}]{Environment.NewLine}");
            }
            catch { }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] UpdateTitleBarPassthrough failed: {ex.Message}");
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
#endif
}
