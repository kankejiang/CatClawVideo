using CatClawVideo.Core.Models;
using CatClawVideo.Data;
using CatClawVideo.Maui.ViewModels;

namespace CatClawVideo.Maui.Pages;

/// <summary>收藏页：最近播放 + 我的收藏，分组海报墙（真数据 VideoDatabase）。</summary>
public partial class FavoritesPage : ContentView, ITabView
{
    private readonly FavoritesViewModel _vm;

    public FavoritesPage(FavoritesViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = _vm;
    }

    public async Task OnTabShownAsync()
    {
        await _vm.LoadCommand.ExecuteAsync(null);

        ClearHistoryButton.IsVisible = _vm.RecentPlays.Count > 0;
        EmptyLabel.IsVisible = _vm.RecentPlays.Count == 0 && _vm.Favorites.Count == 0;

        var sections = new List<WallSection>();

        if (_vm.RecentPlays.Count > 0)
            sections.Add(new WallSection
            {
                Name = "最近播放",
                Items = _vm.RecentPlays.Select(e => new WallCard
                {
                    Title = e.Title,
                    Cover = e.Cover,
                    Meta = e.DurationSeconds > 0
                        ? $"看到 {VideoPlayerViewModel.FormatTime(e.PositionSeconds)} / {VideoPlayerViewModel.FormatTime(e.DurationSeconds)} · {e.WatchedAt:MM-dd HH:mm}"
                        : $"{VideoPlayerViewModel.FormatTime(e.PositionSeconds)} · {e.WatchedAt:MM-dd HH:mm}",
                    OnOpen = () => _ = _vm.PlayAgainCommand.ExecuteAsync(e),
                }).ToList(),
            });

        if (_vm.Favorites.Count > 0)
            sections.Add(new WallSection
            {
                Name = "我的收藏",
                Items = _vm.Favorites.Select(f => new WallCard
                {
                    Title = f.Title,
                    Cover = f.Cover,
                    Meta = string.Join(" · ", new[] { f.Year, f.Category }.Where(s => !string.IsNullOrEmpty(s))),
                    Remark = f.Remarks,
                    OnOpen = () => _ = OpenFavoriteAsync(f),
                }).ToList(),
            });

        Wall.ItemsSource = sections;
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
                    $"&desc={Uri.EscapeDataString(fav.Description ?? "")}";
        await Shell.Current.GoToAsync(query);
    }

    private void OnCardSelected(object? sender, SelectionChangedEventArgs e)
    {
        Wall.SelectedItem = null;
        if (e.CurrentSelection.FirstOrDefault() is WallCard card)
            card.OnOpen();
    }
}
