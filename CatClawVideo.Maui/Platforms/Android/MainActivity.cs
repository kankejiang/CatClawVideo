using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Views;
using AndroidX.Core.View;

namespace CatClawVideo.Maui;

/// <summary>Android 主 Activity：Edge-to-Edge 显示与安全区 insets 上报。</summary>
[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    // App 基准方向 = 横屏（影视类应用，启动即锁定横屏）。
    // 用 SensorLandscape 而非 Landscape：横屏内允许随重力 180° 翻转，体验更自然。
    // 播放页的旋转按钮仍可在运行时用 activity.RequestedOrientation 临时覆盖为竖屏。
    ScreenOrientation = ScreenOrientation.SensorLandscape,
    // 保留全部 ConfigurationChanges：旋转播放页（横竖屏切换）时 Activity 不重建
    ConfigurationChanges = ConfigChanges.ScreenSize
        | ConfigChanges.Orientation
        | ConfigChanges.UiMode
        | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize
        | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    private EdgeToEdgeInsets? _insetsListener;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetupEdgeToEdge();
        // 首帧布局（含 Splash 关闭后的真实布局）后再次强制 Edge-to-Edge：
        // MAUI 的 AndroidWindow 可能在 OnCreate 之后才真正建立并把 DecorFitsSystemWindows 重置为 true，
        // 导致「启动有空白、导航返回后变全屏」。GlobalLayout 监听在每次真实布局后都再强制一次，覆盖该时机。
        AttachEdgeToEdgeReassert();
    }

    /// <summary>窗口获得焦点时再强制一次 Edge-to-Edge：MAUI 可能在任意生命周期点
    /// （含启动后首帧、二级页返回）把 DecorFitsSystemWindows 重置回 true——
    /// 旧方案只重申有限次，输给启动时序就会出现「启动底部大片空白、导航返回后消失」。</summary>
    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus) SetupEdgeToEdge();
    }

    /// <summary>Activity 创建完成（窗口已附加、MAUI 已完成首轮窗口搭建）后回调。
    /// MAUI 的 AndroidWindow 可能在 OnCreate 之后才真正建立并把 DecorFitsSystemWindows 重置为 true，
    /// 因此这里再次强制 Edge-to-Edge，确保首帧即全屏、启动无底部空白。（同猫爪音乐）</summary>
    protected override void OnPostCreate(Bundle? savedInstanceState)
    {
        base.OnPostCreate(savedInstanceState);
        SetupEdgeToEdge();
    }

    protected override void OnResume()
    {
        base.OnResume();
        SetupEdgeToEdge();
        UpdateWindowChromeColor();
    }

    /// <summary>
    /// 挂载一次性（重复数次）全局布局监听：每次真实布局后都重新强制 Edge-to-Edge，
    /// 覆盖 Splash 关闭、主题切换等可能把窗口重置为「内容止于导航栏」的时机，确保启动即全屏。
    /// （照搬猫爪音乐——MAUI 的 AndroidWindow 会在 OnCreate 之后把 DecorFitsSystemWindows
    /// 重置回 true，导致「启动有空白、导航返回后变全屏」，必须每次真实布局后再强制。）
    /// </summary>
    private void AttachEdgeToEdgeReassert()
    {
        try
        {
            var rootView = Window?.DecorView?.FindViewById(Android.Resource.Id.Content);
            if (rootView?.ViewTreeObserver != null)
            {
                var listener = new EdgeToEdgeGlobalLayoutListener(this, rootView);
                rootView.ViewTreeObserver.AddOnGlobalLayoutListener(listener);
            }
        }
        catch { }
    }

    /// <summary>Edge-to-Edge：内容延伸到系统栏下方，insets 上报 SafeAreaHelper 供页面加 padding。
    /// 可在 OnCreate/OnPostCreate/OnResume 及每次布局后重复调用（幂等）。</summary>
    internal void SetupEdgeToEdge()
    {
        if (Window == null) return;

        WindowCompat.SetDecorFitsSystemWindows(Window, false);
        Window.AddFlags(WindowManagerFlags.DrawsSystemBarBackgrounds);
        Window.SetStatusBarColor(Android.Graphics.Color.Transparent);
        Window.SetNavigationBarColor(Android.Graphics.Color.Transparent);
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            Window.Attributes.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.Always;
            Window.NavigationBarContrastEnforced = false;
            Window.StatusBarContrastEnforced = false;
        }

        // 系统栏（状态栏/导航栏）区域在内容未覆盖时由「窗口背景」填充。
        // 本 App 的 Activity 主题是 Maui.SplashTheme，其 windowBackground 是启动图
        // （颜色 #0B0D20，近黑），与 App 背景不一致 → 底部会露出一条导航栏高度的"黑条"。
        // 这里把窗口根视图背景同步为 App 的实际背景色，让系统栏区域与界面融为一体。
        UpdateWindowChromeColor();

        var rootView = Window.DecorView.FindViewById(Android.Resource.Id.Content);
        if (rootView == null) return;

        if (_insetsListener == null)
        {
            _insetsListener = new EdgeToEdgeInsets();
            ViewCompat.SetOnApplyWindowInsetsListener(rootView, _insetsListener);
        }
        ViewCompat.RequestApplyInsets(rootView);
    }

    /// <summary>
    /// 把窗口根视图（DecorView）背景同步为 App 当前的 WindowBackgroundColor。
    /// 主题（深/浅色）切换后需重新调用，否则系统栏区域会残留旧色。
    /// </summary>
    public static void UpdateWindowChromeColor()
    {
        try
        {
            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            if (activity?.Window?.DecorView is not { } decor) return;

            if (Microsoft.Maui.Controls.Application.Current?.Resources["WindowBackgroundColor"]
                is not Microsoft.Maui.Graphics.Color c) return;

            decor.SetBackgroundColor(Android.Graphics.Color.Argb(
                (int)Math.Round(c.Alpha * 255),
                (int)Math.Round(c.Red * 255),
                (int)Math.Round(c.Green * 255),
                (int)Math.Round(c.Blue * 255)));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Chrome] 窗口背景色同步失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 沉浸式开关：隐藏/恢复状态栏与导航栏（播放页全屏用）。
    /// 隐藏后系统栏可从上/下滑边缘临时唤出（BehaviorShowTransientBarsBySwipe）。
    /// 注：Android 15+ 已废弃 setStatusBarColor/setNavigationBarColor，控制显隐必须走
    /// WindowInsetsControllerCompat，不能用旧的 SystemUiVisibility 标志。
    /// </summary>
    public static void SetImmersive(bool on)
    {
        try
        {
            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            if (activity?.Window is not { } window) return;

            var controller = WindowCompat.GetInsetsController(window, window.DecorView);
            if (controller == null) return;

            if (on)
            {
                controller.Hide(WindowInsetsCompat.Type.SystemBars());
                controller.SystemBarsBehavior =
                    WindowInsetsControllerCompat.BehaviorShowTransientBarsBySwipe;
            }
            else
            {
                controller.Show(WindowInsetsCompat.Type.SystemBars());
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Chrome] 沉浸式切换失败: {ex.Message}");
        }
    }
}

/// <summary>
/// 窗口 insets 监听：记录系统栏高度到 SafeAreaHelper，供页面手动应用 padding
/// （播放页保持全屏不加 padding，让视频延伸到状态栏/导航栏区域）。
/// </summary>
internal class EdgeToEdgeInsets : Java.Lang.Object, IOnApplyWindowInsetsListener
{
    public WindowInsetsCompat OnApplyWindowInsets(Android.Views.View? v, WindowInsetsCompat? insets)
    {
        if (v == null || insets == null) return insets!;

        int top = 0, bottom = 0;
        try { var sb = insets.GetInsets(WindowInsetsCompat.Type.SystemBars()); top = Math.Max(top, sb.Top); bottom = Math.Max(bottom, sb.Bottom); } catch { }
        try { var nb = insets.GetInsets(WindowInsetsCompat.Type.NavigationBars()); top = Math.Max(top, nb.Top); bottom = Math.Max(bottom, nb.Bottom); } catch { }

        v.SetPadding(0, 0, 0, 0);

        try
        {
            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            var density = activity?.Resources?.DisplayMetrics?.Density ?? 1f;
            Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
                SafeAreaHelper.UpdateInsets(top / density, bottom / density));
        }
        catch { }

        return WindowInsetsCompat.Consumed;
    }
}


/// <summary>
/// 全局布局监听：每次真实布局后重新强制 Edge-to-Edge（**常驻不注销**——
/// MAUI 可能在任意布局时机把 DecorFitsSystemWindows 重置回 true，限次重申
/// 会输给启动时序，出现「启动底部大片空白、导航返回后消失」；SetupEdgeToEdge
/// 幂等且廉价，常驻开销可忽略）。（照搬猫爪音乐同名实现；用 WeakReference 持有 Activity 避免泄漏。）
/// </summary>
internal class EdgeToEdgeGlobalLayoutListener : Java.Lang.Object, Android.Views.ViewTreeObserver.IOnGlobalLayoutListener
{
    private readonly System.WeakReference<MainActivity> _activity;

    public EdgeToEdgeGlobalLayoutListener(MainActivity activity, Android.Views.View view)
    {
        _activity = new System.WeakReference<MainActivity>(activity);
    }

    public void OnGlobalLayout()
    {
        if (_activity.TryGetTarget(out var activity))
            activity.SetupEdgeToEdge();
    }
}
