using CatClawVideo.Core.Models;
using CatClawVideo.Data;
using CatClawVideo.Maui.ViewModels;

namespace CatClawVideo.Maui.Pages;

/// <summary>历史页：播放历史海报墙（断点续看入口，真数据 VideoDatabase，点击续看，支持勾选删除）。</summary>
public partial class HistoryPage : ContentView, ITabView
{
    private readonly VideoDatabase _db;
    private bool _selectMode;

    public HistoryPage(VideoDatabase db)
    {
        InitializeComponent();
        Wall.SizeChanged += (_, _) => PosterLayoutHelper.Apply(Wall, Wall.Width, Wall.Height);
        _db = db;
    }

    public async Task OnTabShownAsync()
    {
#if WINDOWS
        PosterLayoutHelper.Apply(Wall, Wall.Width, Wall.Height, cap: 260);   // 固定尺寸 173×260
#else
        PosterLayoutHelper.Apply(Wall, Wall.Width, Wall.Height);
#endif
        try
        {
            var items = await _db.GetRecentHistoryAsync(50);

            HistoryEmpty.IsVisible = items.Count == 0;
            Wall.ItemsSource = items.Select(e =>
            {
                // 有来源定位 → 跳回观看页详情并自动选中该集续看；
                // 网页直链/本地播放等无来源记录 → 沿用直接播放（老数据仍可用）
                var hasSource = !string.IsNullOrEmpty(e.SourceKey) && !string.IsNullOrEmpty(e.ItemId);

                // ⚠️ type/api 必须取**当前注册表**的活源定义，不能用记录里存的旧值——
                // 旧值指向录制时的源（如 v1 静态快照文件），回跳会读到过期数据
                // （实测：快照里大主宰年番只有 24 集 1 线路，站点实为 4 线路 89 集）
                var site = SiteRegistry.Find(e.SourceKey);
                var type = site?.Type ?? e.ItemType;
                var api = site?.Api ?? e.ItemApi;

                var posParam = $"&pos={Math.Max(0, (int)e.PositionSeconds)}";
                // 影片标题 = 「影片 · 集名」去掉集名部分；极端情况退化用集名（标题栏不能为空）
                var itemTitle = e.Title.Contains(" · ")
                    ? e.Title.Split(" · ")[0]
                    : e.Title;
                if (string.IsNullOrWhiteSpace(itemTitle)) itemTitle = e.EpisodeName;
                var watchQuery = $"watch?title={Uri.EscapeDataString(itemTitle)}" +
                    $"&sourceKey={Uri.EscapeDataString(e.SourceKey)}&type={type}" +
                    $"&api={Uri.EscapeDataString(api)}&itemId={Uri.EscapeDataString(e.ItemId)}" +
                    (string.IsNullOrEmpty(e.EpisodeName) ? "" : $"&resumeEp={Uri.EscapeDataString(e.EpisodeName)}") +
                    (string.IsNullOrEmpty(e.RouteName) ? "" : $"&route={Uri.EscapeDataString(e.RouteName)}") +
                    $"&year={Uri.EscapeDataString(e.Year)}" +
                    $"&remarks={Uri.EscapeDataString(e.Remarks)}" +
                    $"&desc={Uri.EscapeDataString(e.Description)}" +
                    $"&category={Uri.EscapeDataString(e.Category)}" +
                    posParam +
                    (string.IsNullOrEmpty(e.Cover) ? "" : $"&cover={Uri.EscapeDataString(e.Cover)}");
                var playerQuery = $"player?title={Uri.EscapeDataString(e.Title)}&url={Uri.EscapeDataString(e.Url)}" +
                    posParam +
                    (string.IsNullOrEmpty(e.Cover) ? "" : $"&cover={Uri.EscapeDataString(e.Cover)}");
                Action open = hasSource
                    ? () => _ = Shell.Current.GoToAsync(watchQuery)
                    : () => _ = Shell.Current.GoToAsync(playerQuery);

                // 卡片标题=影片名（历史按影片合并）；集名放进副标题，方便识别看的是哪版
                var ep = string.IsNullOrEmpty(e.EpisodeName) ? "" : e.EpisodeName + " · ";
                return new WallCard
                {
                    Title = e.Title,
                    Cover = e.Cover,
                    Meta = e.DurationSeconds > 0
                        ? $"看到 {ep}{VideoPlayerViewModel.FormatTime(e.PositionSeconds)} / {VideoPlayerViewModel.FormatTime(e.DurationSeconds)} · {e.WatchedAt:MM-dd HH:mm}"
                        : $"{ep}{VideoPlayerViewModel.FormatTime(e.PositionSeconds)} · {e.WatchedAt:MM-dd HH:mm}",
                    OnOpen = open,
                    SelectMode = _selectMode,
                    Tag = e.Id,
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
        if (e.CurrentSelection.FirstOrDefault() is not WallCard card) return;

        // 勾选删除模式：点卡片 = 反选，不触发打开
        if (_selectMode)
        {
            card.IsSelected = !card.IsSelected;
            UpdateSelectCount();
            return;
        }

        card.OnOpen();
    }

    // ════════════════ 勾选删除 ════════════════

    private void OnDeleteModeClicked(object? sender, EventArgs e) => SetSelectMode(true);

    private void OnCancelDeleteClicked(object? sender, EventArgs e) => SetSelectMode(false);

    private async void OnConfirmDeleteClicked(object? sender, EventArgs e)
    {
        var cards = (Wall.ItemsSource as IEnumerable<WallCard>)?.Where(c => c.IsSelected).ToList() ?? [];
        if (cards.Count == 0)
        {
            await AlertAsync("提示", "请先勾选要删除的记录");
            return;
        }
        if (!await AlertAsync("删除播放记录", $"确定删除选中的 {cards.Count} 条记录？"))
            return;

        try
        {
            await _db.DeleteHistoryAsync(cards.Select(c => c.Tag).OfType<int>());
            SetSelectMode(false);
            await OnTabShownAsync(); // 重新拉取刷新海报墙
        }
        catch (Exception ex)
        {
            await AlertAsync("提示", $"删除失败：{ex.Message}");
        }
    }

    private void SetSelectMode(bool on)
    {
        _selectMode = on;
        SelectBar.IsVisible = on;
        DeleteEntryButton.IsVisible = !on;
        HeaderHint.Text = on ? "点击卡片勾选，完成后点「确定删除」" : "最近 50 条 · 点击续看";
        UpdateSelectCount();

        if (Wall.ItemsSource is IEnumerable<WallCard> cards)
            foreach (var c in cards)
            {
                c.SelectMode = on;
                if (!on) c.IsSelected = false;
            }
    }

    private void UpdateSelectCount()
    {
        var n = (Wall.ItemsSource as IEnumerable<WallCard>)?.Count(c => c.IsSelected) ?? 0;
        SelectCountLabel.Text = $"已选 {n} 项";
        ConfirmDeleteButton.Text = n > 0 ? $"确定删除({n})" : "确定删除";
    }

    /// <summary>ContentView 没有 DisplayAlert，借 Shell 页面弹确认框</summary>
    private async Task<bool> AlertAsync(string title, string message)
    {
        try
        {
            if (Shell.Current is Page page)
                return await page.DisplayAlertAsync(title, message, "删除", "取消");
        }
        catch { }
        return true;
    }
}
