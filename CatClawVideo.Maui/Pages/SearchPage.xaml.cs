using Microsoft.Maui.Controls.Shapes;
using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;
using CatClawVideo.Core.Services;
using CatClawVideo.Maui.Services;
using CatClawVideo.Maui.ViewModels;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 搜索页：关键词 + 热搜词（豆瓣热门片单，TVBox 同源）+ 跨源真实搜索（并发搜全部可播站点，聚合结果）。
/// 结果卡点击 → 观看页（携带 type/api/itemId 路由）。
/// </summary>
/// <summary>支持带关键词进入：<c>search?q=片名</c>（收藏所属源失效时直接跨源找回该片）。</summary>
[QueryProperty(nameof(Keyword), "q")]
public partial class SearchPage : ContentPage
{
    /// <summary>路由入参关键词（Shell 已 URL 解码）；进入页面时消费一次并自动搜索。</summary>
    private string? _autoKeyword;

    /// <summary><c>search?q=...</c> 入参。仅用于自动搜索，不在页面上长期保存。</summary>
    public string? Keyword
    {
        get => _autoKeyword;
        set => _autoKeyword = value;
    }

    private readonly IVodSourceProvider _provider;

    /// <summary>封面解析（源封面失效 → 豆瓣 → 占位海报）</summary>
    private readonly CoverImageService _covers;

    public Command SearchCommand { get; }

    private bool _searching;
    private bool _hotLoaded;

    public SearchPage(IVodSourceProvider provider, CoverImageService covers)
    {
        InitializeComponent();
#if WINDOWS
        ResultGrid.SizeChanged += (_, _) => PosterLayoutHelper.Apply(ResultGrid, ResultGrid.Width, ResultGrid.Height, cap: 252);   // 桌面固定 168×252
#else
        ResultGrid.SizeChanged += (_, _) => PosterLayoutHelper.Apply(ResultGrid, ResultGrid.Width, ResultGrid.Height);   // 移动端 182 高，与首页/历史/收藏一致
#endif
        _provider = provider;
        _covers = covers;

        // 先建命令再设 BindingContext：页面未实现 INPC，绑定时 SearchCommand 必须已就位，
        // 否则搜索按钮 / Entry.ReturnCommand 绑定到 null 后永不刷新（点击无反应）
        SearchCommand = new Command(() => _ = DoSearchAsync(SearchEntry.Text), () => !_searching);

        BindingContext = this;

#if WINDOWS
        // 无边框窗口内容延伸进标题栏区：顶部留出 caption 按钮（最小化/最大化/关闭）条高度，
        // 避免顶行右上角的搜索按钮与窗口控件重叠（顶行其他推入页只有左侧返回，无需让位）
        Padding = new Thickness(0, 48, 0, 0);
#endif
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
#if WINDOWS
        PosterLayoutHelper.Apply(ResultGrid, ResultGrid.Width, ResultGrid.Height, cap: 252);
#endif
        // 带关键词进入（收藏所属源失效 → 直接跨源搜索该片）：填框并自动搜
        var auto = _autoKeyword;
        _autoKeyword = null;
        if (!string.IsNullOrWhiteSpace(auto))
        {
            SearchEntry.Text = auto;
            _ = DoSearchAsync(auto);
        }

        if (_hotLoaded) return;
        _hotLoaded = true;

        // 热搜词：豆瓣热门电影+剧集标题（TVBox 同源）；失败隐藏热搜区，不放假数据
        var words = await DoubanHotService.GetHotWordsAsync();
        HotStatus.IsVisible = false;
        if (words.Count == 0)
        {
            HotSection.IsVisible = false;
            return;
        }
        foreach (var w in words)
            HotWords.Add(BuildHotChip(w));
        HotSection.IsVisible = !ResultSection.IsVisible;
    }

    /// <summary>热搜词 chip（点击填入并搜索）</summary>
    private Border BuildHotChip(string word)
    {
        var chip = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            BackgroundColor = Application.Current?.Resources["ChipInactiveColor"] as Color,
            Padding = new Thickness(14, 6),
            Margin = new Thickness(0, 0, 8, 8),
            Content = new Label { Text = word, FontSize = 12, TextColor = Application.Current?.Resources["TextSecondaryColor"] as Color },
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            SearchEntry.Text = word;
            _ = DoSearchAsync(word);
        };
        chip.GestureRecognizers.Add(tap);
        return chip;
    }

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

            // 边搜边出：每个站返回即增量上屏，不等全场（死站/慢站用 12s 超时熔断，不拖整体）
            var results = new System.Collections.ObjectModel.ObservableCollection<VodItem>();
            ResultGrid.ItemsSource = results;
            var gate = new object();
            var doneCount = 0;

            var tasks = sites.Select(async site =>
            {
                try
                {
                    var searchTask = _provider.SearchAsync(site, kw);
                    var done = await Task.WhenAny(searchTask, Task.Delay(12000));
                    var items = done == searchTask && searchTask.IsCompletedSuccessfully
                        ? searchTask.Result
                        : new List<VodItem>();
                    if (items.Count == 0) return;

                    // 跨站聚合：每条结果标出**来源站**（卡片左下角站点角标，方便同片多站时挑源）
                    foreach (var it in items) { it.SiteName = site.Name; it.Category ??= site.Name; }
                    List<VodItem> added;
                    lock (gate)
                    {
                        foreach (var it in items) results.Add(it);
                        added = items;
                    }
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        StatusLabel.Text = $"「{kw}」已找到 {results.Count} 个结果…（{site.Name} +{items.Count}）";
                        CoverResolver.Attach(_covers, added);   // 增量补封面
                    });
                }
                catch { }
                finally
                {
                    Interlocked.Increment(ref doneCount);
                }
            });
            await Task.WhenAll(tasks);

            MainThread.BeginInvokeOnMainThread(() =>
            {
                StatusLabel.Text = results.Count > 0
                    ? $"「{kw}」找到 {results.Count} 个结果（{sites.Count} 个站点）"
                    : $"未找到与「{kw}」相关的内容";
            });
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
                    $"&desc={Uri.EscapeDataString(item.Description ?? "")}" +
                    $"&cover={Uri.EscapeDataString(item.Cover ?? "")}";
        Shell.Current.GoToAsync(query);
    }
}
