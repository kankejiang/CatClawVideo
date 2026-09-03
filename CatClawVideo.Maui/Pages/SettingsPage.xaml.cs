using CatClawVideo.Maui.ViewModels;
using Microsoft.Maui.Controls.Shapes;

namespace CatClawVideo.Maui.Pages;

/// <summary>设置页：主题色 / 深浅模式 / 关于。</summary>
public partial class SettingsPage : ContentView, ITabView
{
    private readonly SettingsViewModel _vm;
    private readonly IThemeService _theme;

    public SettingsPage(SettingsViewModel vm, IThemeService theme)
    {
        InitializeComponent();
        _vm = vm;
        _theme = theme;
        BindingContext = _vm;

        BuildThemeSwatches();
        BuildDarkModeChips();
    }

    public Task OnTabShownAsync()
    {
        RefreshSelections();
        return Task.CompletedTask;
    }

    /// <summary>主题色圆点（选中项带主题色描边环）</summary>
    private void BuildThemeSwatches()
    {
        foreach (var (theme, name, hex) in _vm.ThemeOptions)
        {
            var swatch = new Border
            {
                WidthRequest = 40,
                HeightRequest = 40,
                StrokeThickness = 0,
                BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb(hex),
                StrokeShape = new RoundRectangle { CornerRadius = 20 },
                AutomationId = $"swatch_{theme}",
            };

            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) =>
            {
                _vm.SelectedTheme = theme;
                RefreshSelections();
            };
            swatch.GestureRecognizers.Add(tap);
            ThemeSwatches.Add(swatch);
        }
    }

    /// <summary>深浅模式胶囊（选中项主题色底白字）</summary>
    private void BuildDarkModeChips()
    {
        foreach (var (setting, name) in _vm.DarkModeOptions)
        {
            var chip = new Border
            {
                Padding = new Thickness(16, 8),
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 15 },
                AutomationId = $"chip_{setting}",
            };
            chip.Content = new Label
            {
                Text = name,
                FontSize = 13,
                FontFamily = "OpenSansSemibold",
            };

            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) =>
            {
                _vm.SelectedDarkMode = setting;
                RefreshSelections();
            };
            chip.GestureRecognizers.Add(tap);
            DarkModeChips.Add(chip);
        }
    }

    /// <summary>按当前选择态刷新控件配色</summary>
    private void RefreshSelections()
    {
        var res = Application.Current!.Resources;
        var selectedThemeHex = _vm.ThemeOptions.First(t => t.Theme == _vm.SelectedTheme).Hex;

        foreach (var child in ThemeSwatches)
        {
            if (child is not Border swatch) continue;
            var hex = _vm.ThemeOptions.First(t => swatch.AutomationId == $"swatch_{t.Theme}").Hex;
            bool selected = hex == selectedThemeHex;
            swatch.StrokeThickness = selected ? 3 : 0;
            swatch.Stroke = selected
                ? Microsoft.Maui.Graphics.Color.FromArgb(selectedThemeHex).WithAlpha(0.5f)
                : Microsoft.Maui.Graphics.Colors.Transparent;
        }

        foreach (var child in DarkModeChips)
        {
            if (child is not Border chip || chip.Content is not Label label) continue;
            bool selected = chip.AutomationId == $"chip_{_vm.SelectedDarkMode}";
            chip.BackgroundColor = selected
                ? (Color)res["ChipActiveColor"]
                : (Color)res["ChipInactiveColor"];
            label.TextColor = selected
                ? (Color)res["ChipActiveTextColor"]
                : (Color)res["ChipInactiveTextColor"];
        }
    }
}
