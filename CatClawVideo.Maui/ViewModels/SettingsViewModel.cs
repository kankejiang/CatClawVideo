using CatClawVideo.Core.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using AppTheme = CatClawVideo.Core.Interfaces.AppTheme;

namespace CatClawVideo.Maui.ViewModels;

/// <summary>设置页 ViewModel（主题已锁定为蓝色 + 深色，外观设置项已移除）。</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly IThemeService _theme;

    public SettingsViewModel(IThemeService theme)
    {
        _theme = theme;
        // 打开设置页即锁定主题：海洋蓝 + 深色（历史存储值覆盖）
        _selectedTheme = AppTheme.Blue;
        _selectedDarkMode = DarkModeSetting.Dark;
    }

    [ObservableProperty]
    private AppTheme _selectedTheme;

    partial void OnSelectedThemeChanged(AppTheme value) => _theme.SetTheme(value);

    [ObservableProperty]
    private DarkModeSetting _selectedDarkMode;

    partial void OnSelectedDarkModeChanged(DarkModeSetting value) => _theme.SetDarkModeSetting(value);
}
