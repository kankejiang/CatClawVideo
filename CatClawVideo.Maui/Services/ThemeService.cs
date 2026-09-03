using CatClawVideo.Core.Interfaces;
using CoreAppTheme = CatClawVideo.Core.Interfaces.AppTheme;
using MauiAppTheme = Microsoft.Maui.ApplicationModel.AppTheme;

namespace CatClawVideo.Maui.Services;

/// <summary>
/// MAUI 主题管理服务：5 种主题色 × 明/暗/跟随系统，通过资源字典动态切换（同猫爪音乐的设计）。
/// </summary>
public class ThemeService : IThemeService
{
    private const string KeyTheme = "theme_index";
    private const string KeyDarkMode = "dark_mode";

    private CoreAppTheme _currentTheme;

    public event Action? Applied;

    /// <summary>主题色定义（5 种主题：紫、粉、蓝、橙、青）</summary>
    private static readonly Dictionary<CoreAppTheme, ThemeColors> ThemeMap = new()
    {
        [CoreAppTheme.Purple] = new("#9B7ED8", "#E8E0FF", "#7C5DCE"),
        [CoreAppTheme.Pink] = new("#EC407A", "#FFE0EB", "#D81B60"),
        [CoreAppTheme.Blue] = new("#42A5F5", "#D6E8FF", "#1E88E5"),
        [CoreAppTheme.Orange] = new("#FF7043", "#FFE0D6", "#F4511E"),
        [CoreAppTheme.Teal] = new("#26A69A", "#D6F5F0", "#00897B"),
    };

    public CoreAppTheme CurrentTheme => _currentTheme;
    public DarkModeSetting DarkModeSetting { get; private set; }
    public List<CoreAppTheme> AvailableThemes => Enum.GetValues<CoreAppTheme>().ToList();

    public ThemeService()
    {
        LoadSettings();
        if (Application.Current != null)
        {
            Application.Current.UserAppTheme = DarkModeSetting switch
            {
                DarkModeSetting.Light => MauiAppTheme.Light,
                DarkModeSetting.Dark => MauiAppTheme.Dark,
                _ => MauiAppTheme.Unspecified,
            };
        }
    }

    public void SetTheme(CoreAppTheme theme)
    {
        _currentTheme = theme;
        try { Preferences.Default.Set(KeyTheme, (int)theme); } catch { }
        ApplyTheme();
    }

    public void SetDarkModeSetting(DarkModeSetting setting)
    {
        DarkModeSetting = setting;
        try { Preferences.Default.Set(KeyDarkMode, (int)setting); } catch { }

        Application.Current!.UserAppTheme = setting switch
        {
            DarkModeSetting.Light => MauiAppTheme.Light,
            DarkModeSetting.Dark => MauiAppTheme.Dark,
            _ => MauiAppTheme.Unspecified,
        };
        ApplyTheme();
    }

    /// <summary>应用当前主题色与深浅模式到应用资源字典，刷新所有绑定</summary>
    public void ApplyTheme()
    {
        try
        {
            var app = Application.Current;
            if (app?.Resources == null) return;

            var colors = ThemeMap[_currentTheme];
            var isDark = IsEffectivelyDark();

            app.Resources["PrimaryColor"] = Color.FromArgb(colors.Primary);
            app.Resources["PrimaryLightColor"] = Color.FromArgb(colors.Light);
            app.Resources["PrimaryDarkColor"] = Color.FromArgb(colors.Dark);
            app.Resources["AccentColor"] = Color.FromArgb(GetAccentColor(_currentTheme));

            if (isDark)
                ApplyDarkPalette(app.Resources, colors);
            else
                ApplyLightPalette(app.Resources, colors);

            UpdatePlatformStatusBar(isDark);
            Applied?.Invoke();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ThemeService] ApplyTheme failed: {ex.Message}");
        }
    }

    private static void UpdatePlatformStatusBar(bool isDark)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
#if ANDROID
            try
            {
                var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
                if (activity?.Window != null)
                {
                    // 深色背景用亮色系统栏图标，浅色背景用暗色图标
                    var controller = AndroidX.Core.View.WindowCompat.GetInsetsController(activity.Window, activity.Window.DecorView);
                    controller.AppearanceLightStatusBars = !isDark;
                    controller.AppearanceLightNavigationBars = !isDark;
                }
            }
            catch { }
#endif
#if WINDOWS
            try { CatClawVideo.Maui.App.UpdateWindowsTheme(isDark); } catch { }
#endif
        });
    }

    public bool IsSystemDarkMode() =>
        Application.Current?.RequestedTheme == MauiAppTheme.Dark;

    public bool IsEffectivelyDark() => DarkModeSetting switch
    {
        DarkModeSetting.Dark => true,
        DarkModeSetting.Light => false,
        _ => IsSystemDarkMode(),
    };

    private void LoadSettings()
    {
        try
        {
            _currentTheme = (CoreAppTheme)Preferences.Default.Get(KeyTheme, 0);
            DarkModeSetting = (DarkModeSetting)Preferences.Default.Get(KeyDarkMode, 2);
            if (!ThemeMap.ContainsKey(_currentTheme))
                _currentTheme = CoreAppTheme.Purple;
        }
        catch
        {
            _currentTheme = CoreAppTheme.Purple;
            DarkModeSetting = DarkModeSetting.FollowSystem;
        }
    }

    private static void ApplyDarkPalette(ResourceDictionary res, ThemeColors colors)
    {
        var primary = Color.FromArgb(colors.Primary);

        // Windows 无独立背景层，用不透明深色底；Android 透出窗口层背景
        res["WindowBackgroundColor"] = Color.FromArgb("#12102B");
#if ANDROID
        res["WindowBackgroundColor"] = Color.FromArgb("#12102B");
#endif
        res["WindowBackgroundAltColor"] = Color.FromArgb("#1E1C42");
        res["SurfaceColor"] = Color.FromArgb("#2A2755");
        res["CardBackgroundColor"] = Color.FromArgb("#332F5A");
        res["CardBackgroundStrongColor"] = Color.FromArgb("#3A3668");
        res["GlassButtonColor"] = Color.FromArgb("#12FFFFFF");
        res["InputBackgroundColor"] = Color.FromArgb("#0DFFFFFF");
        res["InputBorderColor"] = Color.FromArgb("#18FFFFFF");
        res["DividerColor"] = Color.FromArgb("#14FFFFFF");
        res["ChipInactiveColor"] = Color.FromArgb("#15FFFFFF");
        res["ChipActiveColor"] = primary;
        res["ChipInactiveTextColor"] = Color.FromArgb("#C8CDE8");
        res["ChipActiveTextColor"] = Colors.White;
        res["PrimaryButtonBackgroundColor"] = primary.WithAlpha(0.55f);
        res["TextPrimaryColor"] = Color.FromArgb("#F5F6FF");
        res["TextSecondaryColor"] = Color.FromArgb("#BCC0DD");
        res["TextHintColor"] = Color.FromArgb("#868CAE");
        res["TabActiveColor"] = primary;
        res["TabInactiveColor"] = Color.FromArgb("#FFFFFF");
        res["TabBarBackgroundColor"] = Color.FromArgb("#CC1A1838");
        res["ProgressTrackColor"] = Color.FromArgb("#20FFFFFF");
    }

    private static void ApplyLightPalette(ResourceDictionary res, ThemeColors colors)
    {
        var primary = Color.FromArgb(colors.Primary);

        res["WindowBackgroundColor"] = Color.FromArgb("#F8F7FF");
        res["WindowBackgroundAltColor"] = Color.FromArgb("#EEEBFF");
        res["SurfaceColor"] = Color.FromArgb("#FFFFFFFF");
        res["CardBackgroundColor"] = Color.FromArgb("#FFFFFF");
        res["CardBackgroundStrongColor"] = Color.FromArgb("#FFFFFF");
        res["GlassButtonColor"] = Color.FromArgb("#99FFFFFF");
        res["InputBackgroundColor"] = Color.FromArgb("#F0F2FF");
        res["InputBorderColor"] = Color.FromArgb("#30000000");
        res["DividerColor"] = Color.FromArgb("#1A000000");
        res["ChipInactiveColor"] = Color.FromArgb("#E8ECFF");
        res["ChipActiveColor"] = primary;
        res["ChipInactiveTextColor"] = Color.FromArgb("#4A5278");
        res["ChipActiveTextColor"] = Colors.White;
        res["PrimaryButtonBackgroundColor"] = primary.WithAlpha(0.55f);
        res["TextPrimaryColor"] = Color.FromArgb("#1A1F3A");
        res["TextSecondaryColor"] = Color.FromArgb("#4A5278");
        res["TextHintColor"] = Color.FromArgb("#6B7399");
        res["TabActiveColor"] = primary;
        res["TabInactiveColor"] = Color.FromArgb("#9AA0B4");
        res["TabBarBackgroundColor"] = Color.FromArgb("#E6F8F7FF");
        res["ProgressTrackColor"] = Color.FromArgb("#18000000");
    }

    private static string GetAccentColor(CoreAppTheme theme) => theme switch
    {
        CoreAppTheme.Purple => "#55D6FF",
        CoreAppTheme.Pink => "#FFB86E",
        CoreAppTheme.Blue => "#5AE4FF",
        CoreAppTheme.Orange => "#FFD36E",
        CoreAppTheme.Teal => "#80CBC4",
        _ => "#55D6FF",
    };

    private record ThemeColors(string Primary, string Light, string Dark);
}
