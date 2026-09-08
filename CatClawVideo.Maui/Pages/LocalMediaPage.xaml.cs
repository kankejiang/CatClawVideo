using Microsoft.Maui.Controls.Shapes;
using System.Collections.ObjectModel;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 本地播放页：FilePicker 选本地视频 → 列表记录 → 点击跳全屏播放器（file:// 直链）。
/// 本地媒体库扫描（文件夹递归）接入后再扩展列表来源。
/// </summary>
public partial class LocalMediaPage : ContentView, ITabView
{
    public ObservableCollection<LocalItem> Items { get; } = new();

    public LocalMediaPage()
    {
        InitializeComponent();
        Items.CollectionChanged += (_, _) =>
        {
            LocalEmpty.IsVisible = Items.Count == 0;
            // 简单同步：整列表重建（条目量小）
            LocalList.Children.Clear();
            foreach (var item in Items)
                LocalList.Children.Add(BuildLocalCard(item));
        };
    }

    private View BuildLocalCard(LocalItem item)
    {
        var title = new Label
        {
            Text = item.Title,
            FontSize = 14,
            FontFamily = "OpenSansSemibold",
            TextColor = (Color)Application.Current!.Resources["TextPrimaryColor"],
            LineBreakMode = LineBreakMode.TailTruncation,
        };
        var path = new Label
        {
            Text = item.FullPath,
            FontSize = 11,
            TextColor = (Color)Application.Current!.Resources["TextHintColor"],
            LineBreakMode = LineBreakMode.MiddleTruncation,
        };
        var info = new VerticalStackLayout { Spacing = 3, VerticalOptions = LayoutOptions.Center };
        info.Add(title);
        info.Add(path);

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
            try
            {
                await Shell.Current.GoToAsync($"player?title={Uri.EscapeDataString(item.Title)}&url={Uri.EscapeDataString(item.FullPath)}");
            }
            catch { }
        };
        border.GestureRecognizers.Add(tap);
        return border;
    }

    public Task OnTabShownAsync() => Task.CompletedTask;

    /// <summary>打开本地视频文件（跨平台 FilePicker）</summary>
    private async void OnPickFileClicked(object? sender, EventArgs e)
    {
        try
        {
            var result = await FilePicker.PickAsync(new PickOptions
            {
                PickerTitle = "选择本地视频文件",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    [DevicePlatform.WinUI] = new[] { ".mp4", ".mkv", ".avi", ".mov", ".flv", ".webm", ".ts" },
                    [DevicePlatform.Android] = new[] { "video/*" },
                }),
            });
            if (result == null) return;

            Items.Insert(0, new LocalItem(result.FileName, result.FullPath));
            // 直接进入全屏播放器（本地路径播放内核已支持 file 直链）
            await Shell.Current.GoToAsync($"player?title={Uri.EscapeDataString(result.FileName)}&url={Uri.EscapeDataString(result.FullPath)}");
        }
        catch { }
    }
}

/// <summary>本地视频条目（本次会话内存记录）</summary>
public class LocalItem
{
    public string Title { get; }
    public string FullPath { get; }

    public LocalItem(string title, string fullPath)
    {
        Title = title;
        FullPath = fullPath;
    }
}
