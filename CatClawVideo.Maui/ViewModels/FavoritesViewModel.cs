using CatClawVideo.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CatClawVideo.Maui.ViewModels;

/// <summary>收藏页 ViewModel：最近播放（历史）+ 我的收藏（订阅源上线后启用）。</summary>
public partial class FavoritesViewModel : ObservableObject
{
    private readonly VideoDatabase _db;

    public FavoritesViewModel(VideoDatabase db) => _db = db;

    [ObservableProperty]
    private IReadOnlyList<PlayHistoryEntry> _recentPlays = [];

    [ObservableProperty]
    private IReadOnlyList<FavoriteEntry> _favorites = [];

    [ObservableProperty]
    private bool _isLoaded;

    /// <summary>刷新最近播放 + 收藏</summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
            RecentPlays = await _db.GetRecentHistoryAsync(50);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Favorites] 加载历史失败: {ex.Message}");
            RecentPlays = [];
        }
        try
        {
            Favorites = await _db.GetFavoritesAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Favorites] 加载收藏失败: {ex.Message}");
            Favorites = [];
        }
        finally
        {
            IsLoaded = true;
        }
    }

    /// <summary>继续观看：跳转播放页（携带断点位置续播）</summary>
    [RelayCommand]
    private async Task PlayAgainAsync(PlayHistoryEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Url)) return;
        await Shell.Current.GoToAsync(
            $"player?title={Uri.EscapeDataString(entry.Title)}&url={Uri.EscapeDataString(entry.Url)}" +
            $"&pos={Math.Max(0, (int)entry.PositionSeconds)}" +
            (string.IsNullOrEmpty(entry.Cover) ? "" : $"&cover={Uri.EscapeDataString(entry.Cover)}"));
    }

    /// <summary>清空播放历史</summary>
    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        try { await _db.ClearHistoryAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Favorites] 清空历史失败: {ex.Message}"); }
        await LoadAsync();
    }
}
