using Microsoft.Maui.Controls.Shapes;
using System.Collections.ObjectModel;
using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;
using CatClawVideo.Maui.ViewModels;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 搜索页：关键词 + 热门搜索 + 跨源真实搜索（并发搜全部可播站点，聚合结果）。
/// 结果卡点击 → 观看页（携带 type/api/itemId 路由）。
/// </summary>
public partial class SearchPage : ContentPage
{
    private static readonly string[] HotWordList =
        ["漫长的季节", "庆余年", "流浪地球", "三体", "狂飙", "繁花", "宫崎骏", "诺兰", "悬疑", "科幻"];

    private readonly IVodSourceProvider _provider;

    public Command SearchCommand { get; }

    public SearchPage(IVodSourceProvider provider)
    {
        InitializeComponent();
        _provider = provider;
        BindingContext = this;
        SearchCommand = new Command(() => _ = DoSearchAsync(SearchEntry.Text), () => !_searching);

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
                _ = DoSearchAsync(w);
            };
            chip.GestureRecognizers.Add(tap);
            HotWords.Add(chip);
        }
    }

    private bool _searching;

    private void OnBackTapped(object? sender, TappedEventArgs e) => Shell.Current.GoToAsync("..");

    /// <summary>跨源真实搜索：并发搜全部可播站点，聚合结果；点击卡片进观看页</summary>
    private async Task DoSearchAsync(string? keyword)
    {
        var kw = keyword?.Trim();
        if (string.IsNullOrEmpty(kw) || _searching) return;
        _searching = true;
        ((Command)SearchCommand).ChangeCanExecute();

        HotSection.IsVisible = false;
        ResultSection.IsVisible = true;
        ResultGrid.ItemsSource = null;
        StatusLabel.Text = $"正在搜索「{kw}」…";
        StatusLabel.IsVisible = true;

        try
        {
            var sites = SiteRegistry.Playable.ToList();
            if (sites.Count == 0)
            {
                StatusLabel.Text = "暂无可用影片源，请先在设置中添加订阅";
                return;
            }

            var tasks = sites.Select(async site =>
            {
                try
                {
                    var items = await _provider.SearchAsync(site, kw);
                    foreach (var it in items) it.Category ??= site.Name;
                    return items;
                }
                catch { return new List<VodItem>(); }
            });
            var batches = await Task.WhenAll(tasks);
            var results = batches.SelectMany(b => b).ToList();

            ResultGrid.ItemsSource = results;
            StatusLabel.Text = results.Count > 0
                ? $"「{kw}」找到 {results.Count} 个结果（{sites.Count} 个站点）"
                : $"未找到与「{kw}」相关的内容";
        }
        catch
        {
            StatusLabel.Text = "搜索失败，请稍后重试";
        }
        finally
        {
            _searching = false;
            ((Command)SearchCommand).ChangeCanExecute();
        }
    }

    /// <summary>结果卡点击 → 观看页（还原站点 type/api 路由）</summary>
    private void OnResultSelected(object? sender, SelectionChangedEventArgs e)
    {
        ResultGrid.SelectedItem = null;
        if (e.CurrentSelection.FirstOrDefault() is not VodItem item) return;

        var site = SiteRegistry.Find(item.SourceKey);
        if (site == null)
        {
            _ = DisplayAlertAsync("提示", "该结果所属源已失效，请重新搜索", "确定");
            return;
        }

        var query = $"watch?title={Uri.EscapeDataString(item.Title)}" +
                    $"&sourceKey={Uri.EscapeDataString(item.SourceKey)}" +
                    $"&type={site.Type}" +
                    $"&api={Uri.EscapeDataString(site.Api)}" +
                    $"&itemId={Uri.EscapeDataString(item.Id)}" +
                    $"&year={Uri.EscapeDataString(item.Year ?? "")}" +
                    $"&remarks={Uri.EscapeDataString(item.Remarks ?? "")}" +
                    $"&desc={Uri.EscapeDataString(item.Description ?? "")}";
        Shell.Current.GoToAsync(query);
    }
}
