using CatClawVideo.Core.Models;
using CatClawVideo.Core.Services;
using CatClawVideo.Data;
using CatClawVideo.Maui.Services;
using CatClawVideo.Maui.ViewModels;

namespace CatClawVideo.Maui.Pages;

/// <summary>收藏页：我的收藏海报墙（真数据 VideoDatabase；最近播放归「历史」页）。</summary>
public partial class FavoritesPage : ContentView, ITabView, IRemoteKeyHandler
{
    private readonly FavoritesViewModel _vm;
    private readonly VideoDatabase _db;

    /// <summary>封面解析（源封面失效 → 豆瓣 → 占位海报）</summary>
    private readonly CoverImageService _covers;

    /// <summary>海报墙遥控器焦点（与历史页共用同一套网格移动逻辑）。</summary>
    private readonly PosterWallFocus _focus;

    public FavoritesPage(FavoritesViewModel vm, CoverImageService covers, VideoDatabase db)
    {
        InitializeComponent();
        Wall.SizeChanged += (_, _) => PosterLayoutHelper.Apply(Wall, Wall.Width, Wall.Height);
        _vm = vm;
        _covers = covers;
        _db = db;
        _focus = new PosterWallFocus(Wall);
        BindingContext = _vm;
    }

    public async Task OnTabShownAsync()
    {
        // 本页接管方向键（Push 幂等；若本页之上还压着二级页，那些页会先拿到按键）
        RemoteKeyRouter.Push(this);

#if WINDOWS
        PosterLayoutHelper.Apply(Wall, Wall.Width, Wall.Height, cap: 260);   // 固定尺寸 173×260
#else
        PosterLayoutHelper.Apply(Wall, Wall.Width, Wall.Height);
#endif
        await ReloadWallAsync();
    }

    /// <summary>重建海报墙（进入页面 / 取消收藏后刷新共用）</summary>
    private async Task ReloadWallAsync()
    {
        await _vm.LoadCommand.ExecuteAsync(null);

        EmptyLabel.IsVisible = _vm.Favorites.Count == 0;
        var cards = _vm.Favorites.Select(f => new WallCard
        {
            Title = f.Title,
            Cover = f.Cover,
            Meta = string.Join(" · ", new[] { f.Year, f.Category }.Where(s => !string.IsNullOrEmpty(s))),
            Remark = f.Remarks,
            OnOpen = () => _ = OpenFavoriteAsync(f),
            MenuCommand = new Command(() => _ = ShowCardMenuAsync(f)),
        }).ToList();

        Wall.ItemsSource = cards;
        CoverResolver.Attach(_covers, cards);   // 卡片先出，封面异步补齐（失败 → 占位海报）
        _focus.Refresh();                       // 数据换了：焦点索引夹回有效范围
    }

    /// <summary>收藏卡点击 → 观看页（还原站点 type/api 路由，同搜索结果）。
    /// 所属源已失效（订阅换源/删源）时**直接跨源搜回该片**，而不是只弹一句「请重新收藏」了事。</summary>
    private async Task OpenFavoriteAsync(FavoriteEntry fav)
    {
        var site = SiteRegistry.Find(fav.SourceKey);
        if (site == null)
        {
            await SearchTitleAsync(fav.Title);
            return;
        }

        var query = $"watch?title={Uri.EscapeDataString(fav.Title)}" +
                    $"&sourceKey={Uri.EscapeDataString(fav.SourceKey)}" +
                    $"&type={site.Type}" +
                    $"&api={Uri.EscapeDataString(site.Api)}" +
                    $"&itemId={Uri.EscapeDataString(fav.ItemId)}" +
                    $"&year={Uri.EscapeDataString(fav.Year ?? "")}" +
                    $"&remarks={Uri.EscapeDataString(fav.Remarks ?? "")}" +
                    $"&desc={Uri.EscapeDataString(fav.Description ?? "")}" +
                    $"&cover={Uri.EscapeDataString(fav.Cover ?? "")}";
        await Shell.Current.GoToAsync(query);
    }

    /// <summary>跨源搜索片名（推入搜索页并自动开搜）</summary>
    private static async Task SearchTitleAsync(string title)
    {
        try { await Shell.Current.GoToAsync($"search?q={Uri.EscapeDataString(title)}"); }
        catch { }
    }

    /// <summary>长按（Android）/ 右键（Windows）卡片 → 操作菜单</summary>
    private async Task ShowCardMenuAsync(FavoriteEntry fav)
    {
        var choice = await AlertActionAsync(fav.Title, "搜索该影片", "取消收藏");
        switch (choice)
        {
            case "搜索该影片":
                await SearchTitleAsync(fav.Title);
                break;
            case "取消收藏":
                await RemoveFavoriteAsync(fav);
                break;
        }
    }

    /// <summary>直接取消收藏（不必进详情页）</summary>
    private async Task RemoveFavoriteAsync(FavoriteEntry fav)
    {
        try
        {
            await _db.RemoveFavoriteAsync(fav);
            await ReloadWallAsync();
        }
        catch (Exception ex)
        {
            await AlertAsync("提示", $"取消收藏失败：{ex.Message}");
        }
    }

    // ════════════════ IRemoteKeyHandler（遥控器焦点）════════════════

    public void FocusContent() => _focus.Focus();

    public void BlurContent() => _focus.Blur();

    public bool Handle(RemoteKey key)
    {
        switch (key)
        {
            case RemoteKey.Left:
            case RemoteKey.Right:
            case RemoteKey.Up:
            case RemoteKey.Down:
                return _focus.Move(key);   // 越界 → false，按键继续冒泡到顶栏

            case RemoteKey.Enter:
                if (!_focus.Engaged || _focus.Current is not WallCard card) return false;
                card.OnOpen();
                return true;
        }
        return false;   // Back 等交还顶栏
    }

    // ════════════════ 弹层：ContentView 自己不能弹，借宿主页面 ════════════════

    private static Page? RootPage => Application.Current?.Windows.FirstOrDefault()?.Page;

    private static Task AlertAsync(string title, string message) =>
        RootPage is { } p ? p.DisplayAlertAsync(title, message, "确定") : Task.CompletedTask;

    private static Task<string?> AlertActionAsync(string title, params string[] buttons) =>
        RootPage is { } p
            ? p.DisplayActionSheetAsync(title, "取消", null, buttons)
            : Task.FromResult<string?>(null);

    private void OnCardSelected(object? sender, SelectionChangedEventArgs e)
    {
        Wall.SelectedItem = null;
        if (e.CurrentSelection.FirstOrDefault() is WallCard card)
            card.OnOpen();
    }
}
