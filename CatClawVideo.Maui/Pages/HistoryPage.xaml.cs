using CatClawVideo.Data;
using CatClawVideo.Maui.ViewModels;
using Microsoft.Maui.Controls.Shapes;

namespace CatClawVideo.Maui.Pages;

/// <summary>历史页：完整播放历史（断点续看入口，真数据 VideoDatabase）。</summary>
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

            HistoryList.Clear();
            HistoryEmpty.IsVisible = items.Count == 0;

            foreach (var entry in items)
                HistoryList.Add(BuildHistoryCard(entry));
        }
        catch
        {
            HistoryEmpty.IsVisible = true;
        }
    }

    /// <summary>历史卡片（点击续看 → 全屏播放页；与收藏页最近播放同款）</summary>
    private Border BuildHistoryCard(PlayHistoryEntry entry)
    {
        var title = new Label
        {
            Text = entry.Title,
            FontSize = 15,
            FontFamily = "OpenSansSemibold",
            TextColor = (Color)Application.Current!.Resources["TextPrimaryColor"],
            LineBreakMode = LineBreakMode.TailTruncation,
        };

        var progress = entry.DurationSeconds > 0
            ? $"{VideoPlayerViewModel.FormatTime(entry.PositionSeconds)} / {VideoPlayerViewModel.FormatTime(entry.DurationSeconds)}"
            : $"{VideoPlayerViewModel.FormatTime(entry.PositionSeconds)}";
        var subtitle = new Label
        {
            Text = $"{progress} · {entry.WatchedAt:MM-dd HH:mm}",
            FontSize = 12,
            TextColor = (Color)Application.Current!.Resources["TextSecondaryColor"],
        };

        var info = new VerticalStackLayout { Spacing = 4, VerticalOptions = LayoutOptions.Center };
        info.Add(title);
        info.Add(subtitle);

        var playIcon = new Image
        {
            Source = "ic_play.svg",
            WidthRequest = 22,
            HeightRequest = 22,
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Center,
            Opacity = 0.9,
        };

        var grid = new Grid { ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)] };
        grid.Add(info, 0);
        grid.Add(playIcon, 1);

        var border = new Border
        {
            Content = grid,
            Padding = new Thickness(16, 12),
            StrokeThickness = 1,
            Stroke = (Color)Application.Current!.Resources["DividerColor"],
            BackgroundColor = (Color)Application.Current!.Resources["CardBackgroundColor"],
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(16) },
        };

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            try { await Shell.Current.GoToAsync($"player?title={Uri.EscapeDataString(entry.Title)}&url={Uri.EscapeDataString(entry.Url)}"); }
            catch { }
        };
        border.GestureRecognizers.Add(tap);
        return border;
    }
}
