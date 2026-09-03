using CatClawVideo.Data;
using CatClawVideo.Maui.ViewModels;
using Microsoft.Maui.Controls.Shapes;

namespace CatClawVideo.Maui.Pages;

/// <summary>收藏页：最近播放（断点续看）+ 我的收藏占位。</summary>
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
        RebuildHistoryList();
    }

    /// <summary>重建最近播放列表（代码构建卡片，避免 DataTemplate 选择器的兼容性问题）</summary>
    private void RebuildHistoryList()
    {
        HistoryList.Clear();

        var items = _vm.RecentPlays;
        ClearHistoryButton.IsVisible = items.Count > 0;
        HistoryEmpty.IsVisible = items.Count == 0;

        foreach (var entry in items)
        {
            var card = BuildHistoryCard(entry);
            HistoryList.Add(card);
        }
    }

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

        // 进度副标题：看到 mm:ss / 总 mm:ss（总时长未知时仅显示位置）
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
        tap.Tapped += (_, _) => _ = _vm.PlayAgainCommand.ExecuteAsync(entry);
        border.GestureRecognizers.Add(tap);
        return border;
    }
}
