using System.Collections.ObjectModel;
using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CatClawVideo.Maui.ViewModels;

/// <summary>
/// 首页 ViewModel：MacCMS 真实源数据（分类 + 影片列表）+ URL 快速播放入口。
/// 内置量子/非凡双源：加载失败自动切换备用源（量子源连接间歇性不稳，实测有时 SSL/超时）。
/// </summary>
public partial class HomeViewModel : ObservableObject
{
    private readonly IVodSourceProvider _provider;

    /// <summary>公开测试流（Apple HLS 示例，验证 m3u8 播放链路）</summary>
    public const string TestStreamUrl =
        "https://devstreaming-cdn.apple.com/videos/streaming/examples/img_bipbop_adv_example_fmp4/master.m3u8";

    /// <summary>内置双源（按优先级排序，失败自动切换）</summary>
    private static readonly VodSiteInfo[] BuiltinSites =
    [
        new() { Key = "liangzi", Name = "量子资源", Api = "https://cj.lziapi.com/api.php/provide/vod", Type = 1, Playable = true },
        new() { Key = "feifan", Name = "非凡资源", Api = "http://cj.ffzyapi.com/api.php/provide/vod", Type = 1, Playable = true },
    ];

    /// <summary>当前生效站点（UI 显示/详情页拉取用）</summary>
    public VodSiteInfo Site => CurrentSite;

    [ObservableProperty]
    private VodSiteInfo _currentSite = BuiltinSites[0];

    [ObservableProperty]
    private string _urlInput = string.Empty;

    /// <summary>URL 非空校验（用于按钮可用态）</summary>
    [ObservableProperty]
    private bool _canPlay;

    [ObservableProperty]
    private bool _isHomeLoading;

    [ObservableProperty]
    private string _homeStatus = string.Empty;

    partial void OnUrlInputChanged(string value) => CanPlay = !string.IsNullOrWhiteSpace(value);

    public ObservableCollection<VodCategory> Categories { get; } = new();
    public ObservableCollection<VodItem> Items { get; } = new();

    public HomeViewModel(IVodSourceProvider provider)
    {
        _provider = provider;
    }

    /// <summary>首页首载：双源 failover 拉分类目录 → 选第一个分类拉列表</summary>
    [RelayCommand]
    public async Task LoadHomeAsync()
    {
        if (Categories.Count > 0) return; // 常驻页只拉一次
        IsHomeLoading = true;
        HomeStatus = "正在加载影片…";

        var cats = new List<VodCategory>();
        var usedSite = BuiltinSites[0];
        try
        {
            // 双源 failover：量子失败自动切非凡
            for (int attempt = 0; attempt < BuiltinSites.Length && cats.Count == 0; attempt++)
            {
                usedSite = BuiltinSites[attempt];
                cats = await _provider.GetCategoriesAsync(usedSite);
            }
        }
        catch { }

        if (cats.Count == 0)
        {
            HomeStatus = "所有影视源加载失败，请检查网络后重进页面";
            IsHomeLoading = false;
            return;
        }

        CurrentSite = usedSite;
        OnPropertyChanged(nameof(Site));
        Categories.Clear();
        foreach (var c in cats) Categories.Add(c);

        await SelectCategoryAsync(cats[0]);
    }

    /// <summary>切换分类并拉取第一页影片（当前源失败自动切备用源重试一次）</summary>
    [RelayCommand]
    public async Task SelectCategoryAsync(VodCategory? category)
    {
        if (category == null) return;
        IsHomeLoading = true;
        HomeStatus = $"正在加载「{category.Name}」…";
        Items.Clear();

        var items = new List<VodItem>();
        var failed = new List<string>();
        for (int attempt = 0; attempt < BuiltinSites.Length && items.Count == 0; attempt++)
        {
            var site = attempt == 0
                ? CurrentSite
                : BuiltinSites.First(s => s.Key != CurrentSite.Key);
            if (failed.Contains(site.Name)) continue;
            try
            {
                items = await _provider.GetItemsAsync(site, category, 1);
                if (items.Count == 0) failed.Add(site.Name);
                else CurrentSite = site;
            }
            catch { failed.Add(site.Name); }
        }

        foreach (var it in items) Items.Add(it);
        OnPropertyChanged(nameof(Site));
        HomeStatus = Items.Count == 0
            ? $"暂无影片（{string.Join("、", failed)} 均无数据）"
            : $"{CurrentSite.Name} · {category.Name} · 共 {Items.Count} 部";
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
