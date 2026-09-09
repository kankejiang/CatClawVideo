using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;
using CatClawVideo.Maui.ViewModels;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 首页：MacCMS 真实源（分类 + 影片列表）+ 快速播放入口。
/// 海报卡点击 → 观看页（携带源/影片参数拉真实详情与选集）。
/// </summary>
public partial class HomePage : ContentView, ITabView
{
    private readonly HomeViewModel _vm;

    public HomePage(HomeViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = _vm;

        // 分类集合变化 / 选中项变化时刷新 chip 高亮
        _vm.Categories.CollectionChanged += (_, _) => MainThread.BeginInvokeOnMainThread(UpdateChipStyles);
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HomeViewModel.SelectedCategoryId))
                MainThread.BeginInvokeOnMainThread(UpdateChipStyles);
        };
    }

    public Task OnTabShownAsync() => _vm.LoadHomeCommand.ExecuteAsync(null);

    /// <summary>分类 chip 点击 → 拉取该分类影片</summary>
    private async void OnCategoryTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as VisualElement)?.BindingContext is not VodCategory cat) return;
        await _vm.SelectCategoryAsync(cat);
    }

    /// <summary>选中分类 chip 用主题色实底 + 白字，其余用未激活 chip 色</summary>
    private void UpdateChipStyles()
    {
        var res = Application.Current?.Resources;
        var active = res?["PrimaryColor"] as Color ?? Colors.Purple;
        var inactive = res?["ChipInactiveColor"] as Color ?? Colors.Gray;
        var activeText = Colors.White;
        var inactiveText = res?["TextSecondaryColor"] as Color ?? Colors.Gray;

        foreach (var child in CategoryChipHost.Children)
        {
            if (child is not Border chip) continue;
            bool on = (chip.BindingContext as VodCategory)?.Id == _vm.SelectedCategoryId;
            chip.BackgroundColor = on ? active : inactive;
            if (chip.Content is Label label)
                label.TextColor = on ? activeText : inactiveText;
        }
    }

    /// <summary>海报卡点击 → 观看页（带源定位参数）</summary>
    private void OnPosterTapped(object? sender, TappedEventArgs e)
    {
        if (_vm.Site is null) return;
        if ((sender as VisualElement)?.BindingContext is not VodItem item) return;

        var query = $"watch?title={Uri.EscapeDataString(item.Title)}" +
                    $"&sourceKey={Uri.EscapeDataString(item.SourceKey)}" +
                    $"&api={Uri.EscapeDataString(_vm.Site.Api)}" +
                    $"&itemId={Uri.EscapeDataString(item.Id)}" +
                    $"&year={Uri.EscapeDataString(item.Year ?? "")}" +
                    $"&remarks={Uri.EscapeDataString(item.Remarks ?? "")}" +
                    $"&desc={Uri.EscapeDataString(item.Description ?? "")}";
        Shell.Current.GoToAsync(query);
    }
}
