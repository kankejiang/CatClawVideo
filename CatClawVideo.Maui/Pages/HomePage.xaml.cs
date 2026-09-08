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

        CategoryChips.SelectionChanged += async (_, _) =>
        {
            if (CategoryChips.SelectedItem is VodCategory cat)
                await _vm.SelectCategoryAsync(cat);
        };
    }

    public Task OnTabShownAsync() => _vm.LoadHomeCommand.ExecuteAsync(null);

    /// <summary>海报卡点击 → 观看页（带源定位参数）</summary>
    private void OnPosterTapped(object? sender, TappedEventArgs e)
    {
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
