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
    // 保留全部 ConfigurationChanges：旋转播放页（横屏模式）时 Activity 不重建
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
    }

    protected override void OnResume()
    {
        base.OnResume();
        SetupEdgeToEdge();
    }

    /// <summary>Edge-to-Edge：内容延伸到系统栏下方，insets 上报 SafeAreaHelper 供页面加 padding。</summary>
    private void SetupEdgeToEdge()
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

        var rootView = Window.DecorView.FindViewById(Android.Resource.Id.Content);
        if (rootView == null) return;

        if (_insetsListener == null)
        {
            _insetsListener = new EdgeToEdgeInsets();
            ViewCompat.SetOnApplyWindowInsetsListener(rootView, _insetsListener);
        }
        ViewCompat.RequestApplyInsets(rootView);
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
