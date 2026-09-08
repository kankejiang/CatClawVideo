using System.Collections.ObjectModel;
using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CatClawVideo.Maui.ViewModels;

/// <summary>首页 ViewModel：MacCMS 真实源数据（分类 + 影片列表）+ URL 快速播放入口。</summary>
public partial class HomeViewModel : ObservableObject
{
    private readonly IVodSourceProvider _provider;

    /// <summary>公开测试流（Apple HLS 示例，验证 m3u8 播放链路）</summary>
    public const string TestStreamUrl =
        "https://devstreaming-cdn.apple.com/videos/streaming/examples/img_bipbop_adv_example_fmp4/master.m3u8";

    /// <summary>当前影视源站点（订阅解析多站点后可切换）</summary>
    public VodSiteInfo Site { get; } = new()
    {
        Key = "liangzi",
        Name = "量子资源",
        Api = "https://cj.lziapi.com/api.php/provide/vod",
        Type = 1,
        Playable = true,
    };

    [ObservableProperty]
    private string _urlInput = string.Empty;

    /// <summary>URL 非空校验（用于按钮可用态）</summary>
    [ObservableProperty]
    private bool _canPlay;

    [ObservableProperty]
    private bool _isHomeLoading;

    [ObservableProperty]
    private string _homeStatus = "正在加载影片…";

    partial void OnUrlInputChanged(string value) => CanPlay = !string.IsNullOrWhiteSpace(value);

    public ObservableCollection<VodCategory> Categories { get; } = new();
    public ObservableCollection<VodItem> Items { get; } = new();

    private VodCategory? _currentCategory;

    public HomeViewModel(IVodSourceProvider provider)
    {
        _provider = provider;
    }

    /// <summary>首页首载：拉分类目录 → 选第一个分类拉列表</summary>
    [RelayCommand]
    public async Task LoadHomeAsync()
    {
        if (Categories.Count > 0) return; // 常驻页只拉一次
        IsHomeLoading = true;
        HomeStatus = "正在加载分类…";
        try
        {
            var cats = await _provider.GetCategoriesAsync(Site);
            Categories.Clear();
            foreach (var c in cats) Categories.Add(c);
            if (Categories.Count > 0)
                await SelectCategoryAsync(Categories[0]);
            else
                HomeStatus = "该源暂无分类";
        }
        catch
        {
            HomeStatus = "加载失败，请检查网络";
        }
        finally
        {
            IsHomeLoading = false;
        }
    }

    /// <summary>切换分类并拉取第一页影片</summary>
    [RelayCommand]
    public async Task SelectCategoryAsync(VodCategory? category)
    {
        if (category == null) return;
        _currentCategory = category;
        IsHomeLoading = true;
        HomeStatus = $"正在加载「{category.Name}」…";
        Items.Clear();
        try
        {
            var items = await _provider.GetItemsAsync(Site, category, 1);
            foreach (var it in items) Items.Add(it);
            HomeStatus = Items.Count == 0 ? "该分类暂无影片" : "";
        }
        catch
        {
            HomeStatus = "加载失败";
        }
        finally
        {
            IsHomeLoading = false;
        }
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
