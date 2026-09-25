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
            SectionHeader.IsVisible = Items.Count > 0;
            SectionHeader.Text = Items.Count == 0 ? "最近添加" : $"最近添加 · {Items.Count}";
            // 简单同步：整列表重建（条目量小）
            LocalList.Children.Clear();
            foreach (var item in Items)
                LocalList.Children.Add(BuildLocalCard(item));
        };
    }

    private View BuildLocalCard(LocalItem item)
    {
        var resources = Application.Current!.Resources;
        Color Strong() => (Color)resources["CardBackgroundStrongColor"];
        Color Card() => (Color)resources["CardBackgroundColor"];

        var title = new Label
        {
            Text = item.Title,
            FontSize = 14,
            FontFamily = "OpenSansSemibold",
            TextColor = (Color)resources["TextPrimaryColor"],
            LineBreakMode = LineBreakMode.TailTruncation,
        };
        var path = new Label
        {
            Text = item.FullPath,
            FontSize = 11,
            TextColor = (Color)resources["TextHintColor"],
            LineBreakMode = LineBreakMode.MiddleTruncation,
        };
        var info = new VerticalStackLayout { Spacing = 3, VerticalOptions = LayoutOptions.Center };
        info.Add(title);
        info.Add(path);
        if (item.Meta.Length > 0)
        {
            var meta = new Label
            {
                Text = item.Meta,
                FontSize = 10.5,
                TextColor = (Color)resources["ChipInactiveTextColor"],
                VerticalOptions = LayoutOptions.Center,
            };
            var badge = new Border
            {
                Content = meta,
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(6) },
                BackgroundColor = (Color)resources["InputBackgroundColor"],
                Padding = new Thickness(8, 2),
                HorizontalOptions = LayoutOptions.Start,
                Margin = new Thickness(0, 2, 0, 0),
            };
            info.Add(badge);
        }

        // 播放键做成圆形徽章：比一颗裸图标更像一个"可点的目标"
        var playIcon = new Image
        {
            Source = "ic_play.png",
            WidthRequest = 18,
            HeightRequest = 18,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Opacity = 0.92,
        };
        var playBadge = new Border
        {
            Content = playIcon,
            WidthRequest = 38,
            HeightRequest = 38,
            StrokeThickness = 1,
            Stroke = (Color)resources["InputBorderColor"],
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(19) },
            BackgroundColor = (Color)resources["PrimaryButtonBackgroundColor"],
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Center,
        };

        var grid = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
            ColumnSpacing = 14,
        };
        grid.Add(info, 0);
        grid.Add(playBadge, 1);

        var border = new Border
        {
            Content = grid,
            Padding = new Thickness(16, 12),
            StrokeThickness = 1,
            Stroke = (Color)resources["DividerColor"],
            BackgroundColor = Card(),
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

        // 桌面端指针反馈：卡片在鼠标悬停时抬起一档，点击时压暗
        var pointer = new PointerGestureRecognizer();
        pointer.PointerEntered += (_, _) => border.BackgroundColor = Strong();
        pointer.PointerExited += (_, _) => border.BackgroundColor = Card();
        border.GestureRecognizers.Add(pointer);

        return border;
    }

    public Task OnTabShownAsync() => Task.CompletedTask;

    /// <summary>打开本地视频文件（跨平台 FilePicker）</summary>
    private async void OnPickFileClicked(object? sender, TappedEventArgs e)
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

    /// <summary>网络媒体入口 → WebDAV 连接管理与远程目录浏览页</summary>
    private async void OnNetworkMediaClicked(object? sender, TappedEventArgs e)
    {
        try { await Shell.Current.GoToAsync("networkmedia"); } catch { }
    }
}

/// <summary>本地视频条目（本次会话内存记录）</summary>
public class LocalItem
{
    public string Title { get; }
    public string FullPath { get; }

    /// <summary>大小 · 扩展名（取不到文件时留空，卡片就不显示这一档）。</summary>
    public string Meta { get; }

    public LocalItem(string title, string fullPath)
    {
        Title = title;
        FullPath = fullPath;
        Meta = Describe(fullPath);
    }

    private static string Describe(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return "";
            var mb = fi.Length / (1024.0 * 1024.0);
            var size = mb >= 1024 ? $"{mb / 1024.0:0.##} GB" : mb >= 1 ? $"{mb:0.#} MB" : $"{fi.Length / 1024.0:0} KB";
            return $"{size} · {fi.Extension.TrimStart('.').ToUpperInvariant()}";
        }
        catch { return ""; }
    }
}
