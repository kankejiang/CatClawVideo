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

    /// <summary>公开测试流（Apple HLS 示例，验证 m3u8 播放链路）</summary>
    public const string TestStreamUrl =
        "https://devstreaming-cdn.apple.com/videos/streaming/examples/img_bipbop_adv_example_fmp4/master.m3u8";

    /// <summary>当前生效站点（UI 显示/详情页拉取用；无可用源时为 null）</summary>
    public VodSiteInfo? Site => CurrentSite;

    [ObservableProperty]
    private VodSiteInfo? _currentSite;

    [ObservableProperty]
    private string _urlInput = string.Empty;

    /// <summary>URL 非空校验（用于按钮可用态）</summary>
    [ObservableProperty]
    private bool _canPlay;

    [ObservableProperty]
    private bool _isHomeLoading;

    [ObservableProperty]
    private string _homeStatus = string.Empty;

    /// <summary>当前选中分类 ID（分类 chip 高亮用；改用 BindableLayout 后由本属性驱动）</summary>
    [ObservableProperty]
    private string _selectedCategoryId = string.Empty;

    partial void OnUrlInputChanged(string value) => CanPlay = !string.IsNullOrWhiteSpace(value);

    public ObservableCollection<VodCategory> Categories { get; } = new();
    public ObservableCollection<VodItem> Items { get; } = new();

    public HomeViewModel(IVodSourceProvider provider)
    {
        _provider = provider;
        // 订阅变化后允许首页重新拉一次（常驻页，之前以 Categories.Count>0 跳过）
        SiteRegistry.Changed += () =>
            MainThread.BeginInvokeOnMainThread(() => { if (!IsHomeLoading) _ = LoadHomeCommand.ExecuteAsync(null); });
    }

    /// <summary>首页首载：逐个可用站点拉分类目录，取第一个成功者 → 选第一个分类拉列表</summary>
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

        var cats = new List<VodCategory>();
        VodSiteInfo? usedSite = null;
        foreach (var site in sites)
        {
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
        Categories.Clear();
        foreach (var c in cats) Categories.Add(c);

        await SelectCategoryAsync(cats[0]);
    }

    /// <summary>切换分类并拉取第一页影片（仅当前站点）</summary>
    [RelayCommand]
    public async Task SelectCategoryAsync(VodCategory? category)
    {
        if (category == null) return;
        SelectedCategoryId = category.Id;
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
            : $"{CurrentSite!.Name} · {category.Name} · 共 {Items.Count} 部";
        IsHomeLoading = false;
    }

    [RelayCommand]
    private async Task PlayAsync()
    {
        var url = UrlInput?.Trim();
        if (string.IsNullOrEmpty(url)) return;
        await NavigateToPlayerAsync(url, "网页播放");
    }

    [RelayCommand]
    private Task PlayTestStreamAsync() =>
        NavigateToPlayerAsync(TestStreamUrl, "HLS 测试流");

    private static async Task NavigateToPlayerAsync(string url, string title)
    {
        await Shell.Current.GoToAsync($"player?title={Uri.EscapeDataString(title)}&url={Uri.EscapeDataString(url)}");
    }
}
