using CatClawVideo.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CatClawVideo.Maui.ViewModels;

/// <summary>收藏页 ViewModel：我的收藏海报墙（最近播放归历史页，见 2026-09-11 反馈）。</summary>
public partial class FavoritesViewModel : ObservableObject
{
    private readonly VideoDatabase _db;

    public FavoritesViewModel(VideoDatabase db) => _db = db;

    [ObservableProperty]
    private IReadOnlyList<FavoriteEntry> _favorites = [];

    /// <summary>刷新收藏</summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
            Favorites = await _db.GetFavoritesAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Favorites] 加载收藏失败: {ex.Message}");
            Favorites = [];
        }
    }
}
