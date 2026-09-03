using CatClawVideo.Maui.ViewModels;
using AppTheme = CatClawVideo.Core.Interfaces.AppTheme;

namespace CatClawVideo.Maui.Pages;

/// <summary>首页：快速播放入口 + 订阅源功能预告。</summary>
public partial class HomePage : ContentView, ITabView
{
    private readonly HomeViewModel _vm;
    private readonly IThemeService _theme;

    public HomePage(HomeViewModel vm, IThemeService theme)
    {
        InitializeComponent();
        _vm = vm;
        _theme = theme;
        BindingContext = _vm;

        _theme.Applied += UpdateThemeIcon;
        UpdateThemeIcon();
    }

    public Task OnTabShownAsync() => Task.CompletedTask;

    /// <summary>测试流按钮图标跟随主题色</summary>
    private void UpdateThemeIcon()
    {
        var hex = _theme.CurrentTheme switch
        {
            AppTheme.Pink => "ec407a",
            AppTheme.Blue => "42a5f5",
            AppTheme.Orange => "ff7043",
            AppTheme.Teal => "26a69a",
            _ => "9b7ed8",
        };
        TestPlayButton.Source = $"ic_play_{hex}_active";
    }
}
