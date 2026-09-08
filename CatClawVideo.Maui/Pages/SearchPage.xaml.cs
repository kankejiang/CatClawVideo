using Microsoft.Maui.Controls.Shapes;
using System.Collections.ObjectModel;
using CatClawVideo.Maui.ViewModels;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 搜索页：关键词 + 热门搜索 + 结果网格。
/// 订阅源接入前用假结果占位（SearchAsync 契约已就绪，接入后跨源并发搜索替换 DoSearch 的假数据）。
/// </summary>
public partial class SearchPage : ContentPage
{
    private static readonly string[] HotWordList =
        ["漫长的季节", "庆余年", "流浪地球", "三体", "狂飙", "繁花", "宫崎骏", "诺兰", "悬疑", "科幻"];

    public Command SearchCommand { get; }

    public SearchPage()
    {
        InitializeComponent();
        BindingContext = this;
        SearchCommand = new Command(() => DoSearch(SearchEntry.Text));

        foreach (var w in HotWordList)
        {
            var chip = new Border
            {
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 14 },
                BackgroundColor = Application.Current?.Resources["ChipInactiveColor"] as Color,
                Padding = new Thickness(14, 6),
                Margin = new Thickness(0, 0, 8, 8),
                Content = new Label { Text = w, FontSize = 12, TextColor = Application.Current?.Resources["TextSecondaryColor"] as Color },
            };
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) =>
            {
                SearchEntry.Text = w;
                DoSearch(w);
            };
            chip.GestureRecognizers.Add(tap);
            HotWords.Add(chip);
        }
    }

    private void OnBackTapped(object? sender, TappedEventArgs e) => Shell.Current.GoToAsync("..");

    /// <summary>执行搜索（订阅源接入前：假结果 = 固定占位卡，演示结果网格交互）</summary>
    private void DoSearch(string? keyword)
    {
        var kw = keyword?.Trim();
        if (string.IsNullOrEmpty(kw)) return;

        HotSection.IsVisible = false;
        ResultSection.IsVisible = true;

        var results = new ObservableCollection<VodCard>
        {
            new("漫长的季节", "9.1", "2023 · 剧集"),
            new("狂飙", "8.4", "2023 · 剧集"),
            new("三体", "7.9", "2023 · 剧集"),
            new("流浪地球 2", "8.7", "2023 · 电影"),
            new("满江红", "7.0", "2023 · 电影"),
            new("封神第一部", "7.8", "2023 · 电影"),
        };
        ResultGrid.ItemsSource = results;
    }
}
