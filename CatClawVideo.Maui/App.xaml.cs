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
            Title = "猫爪影视",
        };

#if WINDOWS
        window.MinimumWidth = 900;
        window.MinimumHeight = 600;
        window.Width = 1280;
        window.Height = 800;

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

                // 按当前主题刷新标题栏配色
                UpdateWindowsTheme(
                    MauiProgram.Services.GetService<IThemeService>()?.IsEffectivelyDark()
                    ?? RequestedTheme == Microsoft.Maui.ApplicationModel.AppTheme.Dark);
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
#endif
}
