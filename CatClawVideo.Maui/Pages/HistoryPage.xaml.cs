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
            Wall.ItemsSource = items.Select(e => new WallCard
            {
                Title = e.Title,
                Cover = e.Cover,
                Meta = e.DurationSeconds > 0
                    ? $"看到 {VideoPlayerViewModel.FormatTime(e.PositionSeconds)} / {VideoPlayerViewModel.FormatTime(e.DurationSeconds)} · {e.WatchedAt:MM-dd HH:mm}"
                    : $"{VideoPlayerViewModel.FormatTime(e.PositionSeconds)} · {e.WatchedAt:MM-dd HH:mm}",
                OnOpen = () => _ = Shell.Current.GoToAsync(
                    $"player?title={Uri.EscapeDataString(e.Title)}&url={Uri.EscapeDataString(e.Url)}" +
                    $"&pos={Math.Max(0, (int)e.PositionSeconds)}" +
                    (string.IsNullOrEmpty(e.Cover) ? "" : $"&cover={Uri.EscapeDataString(e.Cover)}")),
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
