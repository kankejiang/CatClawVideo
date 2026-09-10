using CatClawVideo.Data;
using CatClawVideo.Maui.ViewModels;

namespace CatClawVideo.Maui.Pages;

/// <summary>历史页：播放历史海报墙（断点续看入口，真数据 VideoDatabase，点击续看）。</summary>
public partial class HistoryPage : ContentView, ITabView
{
    private readonly VideoDatabase _db;

    public HistoryPage(VideoDatabase db)
    {
        InitializeComponent();
        _db = db;
    }

    public async Task OnTabShownAsync()
    {
        try
        {
            var items = await _db.GetRecentHistoryAsync(50);

            HistoryEmpty.IsVisible = items.Count == 0;
            Wall.ItemsSource = items.Select(e =>
            {
                // 有来源定位 → 跳回观看页详情并自动选中该集续看；
                // 网页直链/本地播放等无来源记录 → 沿用直接播放（老数据仍可用）
                var hasSource = !string.IsNullOrEmpty(e.SourceKey) && !string.IsNullOrEmpty(e.ItemId);
                var posParam = $"&pos={Math.Max(0, (int)e.PositionSeconds)}";
                var watchQuery = $"watch?sourceKey={Uri.EscapeDataString(e.SourceKey)}&type={e.ItemType}" +
                    $"&api={Uri.EscapeDataString(e.ItemApi)}&itemId={Uri.EscapeDataString(e.ItemId)}" +
                    (string.IsNullOrEmpty(e.EpisodeName) ? "" : $"&resumeEp={Uri.EscapeDataString(e.EpisodeName)}") +
                    posParam;
                var playerQuery = $"player?title={Uri.EscapeDataString(e.Title)}&url={Uri.EscapeDataString(e.Url)}" +
                    posParam +
                    (string.IsNullOrEmpty(e.Cover) ? "" : $"&cover={Uri.EscapeDataString(e.Cover)}");
                Action open = hasSource
                    ? () => _ = Shell.Current.GoToAsync(watchQuery)
                    : () => _ = Shell.Current.GoToAsync(playerQuery);

                return new WallCard
                {
                    Title = e.Title,
                    Cover = e.Cover,
                    Meta = e.DurationSeconds > 0
                        ? $"看到 {VideoPlayerViewModel.FormatTime(e.PositionSeconds)} / {VideoPlayerViewModel.FormatTime(e.DurationSeconds)} · {e.WatchedAt:MM-dd HH:mm}"
                        : $"{VideoPlayerViewModel.FormatTime(e.PositionSeconds)} · {e.WatchedAt:MM-dd HH:mm}",
                    OnOpen = open,
                };
            }).ToList();
        }
        catch
        {
            HistoryEmpty.IsVisible = true;
            Wall.ItemsSource = null;
        }
    }

    private void OnCardSelected(object? sender, SelectionChangedEventArgs e)
    {
        Wall.SelectedItem = null;
        if (e.CurrentSelection.FirstOrDefault() is WallCard card)
            card.OnOpen();
    }
}
