using CatClawVideo.Core.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using AppTheme = CatClawVideo.Core.Interfaces.AppTheme;

namespace CatClawVideo.Maui.ViewModels;

/// <summary>设置页 ViewModel：主题色与深浅模式。</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly IThemeService _theme;

    public SettingsViewModel(IThemeService theme)
    {
        _theme = theme;
        _selectedTheme = theme.CurrentTheme;
        _selectedDarkMode = theme.DarkModeSetting;
    }

    /// <summary>主题色选项（显示名 + 十六进制色）</summary>
    public IReadOnlyList<(AppTheme Theme, string Name, string Hex)> ThemeOptions =
    [
        (AppTheme.Purple, "梦幻紫", "#9B7ED8"),
        (AppTheme.Pink, "樱花粉", "#EC407A"),
        (AppTheme.Blue, "海洋蓝", "#42A5F5"),
        (AppTheme.Orange, "落日橙", "#FF7043"),
        (AppTheme.Teal, "薄荷青", "#26A69A"),
    ];

    /// <summary>深浅模式选项</summary>
    public IReadOnlyList<(DarkModeSetting Setting, string Name)> DarkModeOptions =
    [
        (DarkModeSetting.Light, "浅色"),
        (DarkModeSetting.Dark, "深色"),
        (DarkModeSetting.FollowSystem, "跟随系统"),
    ];

    [ObservableProperty]
    private AppTheme _selectedTheme;

    partial void OnSelectedThemeChanged(AppTheme value) => _theme.SetTheme(value);

    [ObservableProperty]
    private DarkModeSetting _selectedDarkMode;

    partial void OnSelectedDarkModeChanged(DarkModeSetting value) => _theme.SetDarkModeSetting(value);
}
