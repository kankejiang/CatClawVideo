using System.Collections.ObjectModel;
using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CatClawVideo.Maui.ViewModels;

/// <summary>
/// 首页 ViewModel：影片数据全部来自订阅源（设置 → 订阅源管理添加），不内置任何源。
/// 站点仓库见 <see cref="SiteRegistry"/>；仅 type=1 MacCMS 等可播站点参与首页聚合。
/// </summary>
public partial class HomeViewModel : ObservableObject
{
    private readonly IVodSourceProvider _provider;

    /// <summary>当前生效站点（UI 显示/详情页拉取用；无可用源时为 null）</summary>
    public VodSiteInfo? Site => CurrentSite;

    /// <summary>顶部站点条显示文本（无源时给引导文案）</summary>
    public string SiteDisplayName => CurrentSite?.Name ?? "未选择数据源";

    /// <summary>可播站点列表（数据源选择弹窗用）</summary>
    public List<VodSiteInfo> PlayableSites => SiteRegistry.Playable.ToList();

    /// <summary>用户首选站点 Key（Preferences 持久记忆，跨启动生效）</summary>
    private const string PreferredSiteKey = "home_preferred_site";

    /// <summary>本会话内切换失败过的站点（弹窗置灰标注；重试成功会移除）</summary>
    public HashSet<string> FailedSites { get; } = new();

    [ObservableProperty]
    private VodSiteInfo? _currentSite;

    [ObservableProperty]
    private bool _isHomeLoading;

    [ObservableProperty]
    private string _homeStatus = string.Empty;

    /// <summary>当前选中分类 ID（分类 chip 高亮用；改用 BindableLayout 后由本属性驱动）</summary>
    [ObservableProperty]
    private string _selectedCategoryId = string.Empty;

    /// <summary>分页状态：当前页 / 是否还有下一页（滚动到底自动加载）</summary>
    [ObservableProperty]
    private bool _hasMoreItems = true;
    private int _currentPage = 1;
    private bool _loadingMore;
    private VodCategory? _currentCategory;

    public ObservableCollection<VodCategory> Categories { get; } = new();
    public ObservableCollection<VodItem> Items { get; } = new();

    public HomeViewModel(IVodSourceProvider provider)
    {
        _provider = provider;
        // 订阅变化后允许首页重新拉一次（常驻页，之前以 Categories.Count>0 跳过）
        SiteRegistry.Changed += () =>
            MainThread.BeginInvokeOnMainThread(() => { if (!IsHomeLoading) _ = LoadHomeCommand.ExecuteAsync(null); });
    }

    /// <summary>首页首载：用户首选站点优先（失败回退自动探测第一个成功者）→ 选第一个分类拉列表</summary>
    [RelayCommand]
    public async Task LoadHomeAsync()
    {
        if (Categories.Count > 0) return; // 常驻页只拉一次
        IsHomeLoading = true;
        HomeStatus = "正在加载影片…";

        var sites = SiteRegistry.Playable.ToList();
        if (sites.Count == 0)
        {
            Categories.Clear();
            Items.Clear();
            HomeStatus = "暂无可用影片源，请在 设置 → 源配置 添加订阅";
            IsHomeLoading = false;
            return;
        }

        // 用户首选站点优先（数据源弹窗选择后记忆）；拉取失败回退自动探测
        var preferredKey = Preferences.Default.Get(PreferredSiteKey, string.Empty);
        var preferred = sites.FirstOrDefault(s => s.Key == preferredKey);

        var cats = new List<VodCategory>();
        VodSiteInfo? usedSite = null;
        if (preferred != null)
        {
            try
            {
                cats = await _provider.GetCategoriesAsync(preferred);
                if (cats.Count > 0) usedSite = preferred;
            }
            catch { }
        }

        foreach (var site in sites.Where(s => s.Key != usedSite?.Key && s.Key != preferred?.Key))
        {
            if (usedSite != null) break;
            try
            {
                cats = await _provider.GetCategoriesAsync(site);
                if (cats.Count > 0) { usedSite = site; break; }
            }
            catch { }
        }

        if (usedSite == null)
        {
            HomeStatus = "订阅站点均拉取失败，请检查网络或在源配置中更换订阅";
            IsHomeLoading = false;
            return;
        }

        CurrentSite = usedSite;
        OnPropertyChanged(nameof(Site));
        OnPropertyChanged(nameof(SiteDisplayName));
        Categories.Clear();
        foreach (var c in cats) Categories.Add(c);

        await SelectCategoryAsync(cats[0]);
    }

    /// <summary>
    /// 切换首页数据源（数据源弹窗选择后）：记忆首选 → 清空当前 → 直接加载所选站点。
    /// 失败不静默回退（回退会让用户以为切换无效），停在所选站点并给出具体原因。
    /// </summary>
    [RelayCommand]
    public async Task SelectSiteAsync(VodSiteInfo? site)
    {
        if (site == null) return;
        Preferences.Default.Set(PreferredSiteKey, site.Key);

        Categories.Clear();
        Items.Clear();
        SelectedCategoryId = string.Empty;
        CurrentSite = site;
        OnPropertyChanged(nameof(Site));
        OnPropertyChanged(nameof(SiteDisplayName));

        IsHomeLoading = true;
        HomeStatus = $"正在切换到「{site.Name}」…";

        List<VodCategory> cats;
        try
        {
            cats = await _provider.GetCategoriesAsync(site);
            if (cats.Count == 0)
            {
                FailedSites.Add(site.Key);
                HomeStatus = $"「{site.Name}」未返回分类，该站点可能不可用";
                IsHomeLoading = false;
                return;
            }
        }
        catch (Exception ex)
        {
            FailedSites.Add(site.Key);
            var reason = ex is NotSupportedException ? ex.Message : $"拉取失败：{ex.Message}";
            HomeStatus = $"「{site.Name}」不可用 · {reason}";
            IsHomeLoading = false;
            return;
        }

        FailedSites.Remove(site.Key);
        foreach (var c in cats) Categories.Add(c);
        await SelectCategoryAsync(cats[0]);
    }

    /// <summary>切换分类并拉取第一页影片（仅当前站点）；分页状态复位</summary>
    [RelayCommand]
    public async Task SelectCategoryAsync(VodCategory? category)
    {
        if (category == null) return;
        SelectedCategoryId = category.Id;
        _currentCategory = category;
        _currentPage = 1;
        HasMoreItems = true;
        IsHomeLoading = true;
        HomeStatus = $"正在加载「{category.Name}」…";
        Items.Clear();

        var items = new List<VodItem>();
        if (CurrentSite != null)
        {
            try { items = await _provider.GetItemsAsync(CurrentSite, category, 1); }
            catch { }
        }

        foreach (var it in items) Items.Add(it);
        HomeStatus = Items.Count == 0
            ? $"{CurrentSite?.Name ?? "当前源"} · {category.Name} · 暂无影片"
            : $"{CurrentSite!.Name} · {category.Name} · 已加载 {Items.Count} 部";
        IsHomeLoading = false;
    }

    /// <summary>滚动到底自动加载下一页（CollectionView RemainingItemsThresholdReached）</summary>
    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    public async Task LoadMoreAsync()
    {
        if (_loadingMore || CurrentSite == null || _currentCategory == null) return;
        _loadingMore = true;
        LoadMoreCommand.NotifyCanExecuteChanged();
        try
        {
            var next = _currentPage + 1;
            HomeStatus = $"{CurrentSite.Name} · {_currentCategory.Name} · 加载第 {next} 页…";
            var items = await _provider.GetItemsAsync(CurrentSite, _currentCategory, next);

            if (items.Count == 0)
            {
                HasMoreItems = false;
                HomeStatus = $"{CurrentSite.Name} · {_currentCategory.Name} · 已全部加载（{Items.Count} 部）";
                return;
            }

            _currentPage = next;
            foreach (var it in items) Items.Add(it);
            HomeStatus = $"{CurrentSite.Name} · {_currentCategory.Name} · 已加载 {Items.Count} 部";
        }
        catch
        {
            HasMoreItems = false;
        }
        finally
        {
            _loadingMore = false;
            LoadMoreCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanLoadMore() => !_loadingMore && HasMoreItems && !IsHomeLoading;
}
