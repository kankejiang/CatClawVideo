using CatClawVideo.Core.Models;
using CatClawVideo.Data;
using CatClawVideo.Maui.ViewModels;

namespace CatClawVideo.Maui.Pages;

/// <summary>收藏页：我的收藏海报墙（真数据 VideoDatabase；最近播放归「历史」页）。</summary>
public partial class FavoritesPage : ContentView, ITabView
{
    private readonly FavoritesViewModel _vm;

    public FavoritesPage(FavoritesViewModel vm)
    {
        InitializeComponent();
        Wall.SizeChanged += (_, _) => PosterLayoutHelper.Apply(Wall, Wall.Width, Wall.Height);
        _vm = vm;
        BindingContext = _vm;
    }

    public async Task OnTabShownAsync()
    {
#if WINDOWS
        PosterLayoutHelper.Apply(Wall, Wall.Width, Wall.Height, cap: 420);   // 修长 2:3
#else
        PosterLayoutHelper.Apply(Wall, Wall.Width, Wall.Height);
#endif
        await _vm.LoadCommand.ExecuteAsync(null);

        EmptyLabel.IsVisible = _vm.Favorites.Count == 0;
        Wall.ItemsSource = _vm.Favorites.Select(f => new WallCard
        {
            Title = f.Title,
            Cover = f.Cover,
            Meta = string.Join(" · ", new[] { f.Year, f.Category }.Where(s => !string.IsNullOrEmpty(s))),
            Remark = f.Remarks,
            OnOpen = () => _ = OpenFavoriteAsync(f),
        }).ToList();
    }

    /// <summary>收藏卡点击 → 观看页（还原站点 type/api 路由，同搜索结果）</summary>
    private async Task OpenFavoriteAsync(FavoriteEntry fav)
    {
        var site = SiteRegistry.Find(fav.SourceKey);
        if (site == null)
        {
            try { await Shell.Current.DisplayAlertAsync("提示", "该收藏所属源已失效，请重新收藏", "确定"); } catch { }
            return;
        }

        var query = $"watch?title={Uri.EscapeDataString(fav.Title)}" +
                    $"&sourceKey={Uri.EscapeDataString(fav.SourceKey)}" +
                    $"&type={site.Type}" +
                    $"&api={Uri.EscapeDataString(site.Api)}" +
                    $"&itemId={Uri.EscapeDataString(fav.ItemId)}" +
                    $"&year={Uri.EscapeDataString(fav.Year ?? "")}" +
                    $"&remarks={Uri.EscapeDataString(fav.Remarks ?? "")}" +
                    $"&desc={Uri.EscapeDataString(fav.Description ?? "")}" +
                    $"&cover={Uri.EscapeDataString(fav.Cover ?? "")}";
        await Shell.Current.GoToAsync(query);
    }

    private void OnCardSelected(object? sender, SelectionChangedEventArgs e)
    {
        Wall.SelectedItem = null;
        if (e.CurrentSelection.FirstOrDefault() is WallCard card)
            card.OnOpen();
    }
}
