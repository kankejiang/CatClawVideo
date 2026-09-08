using System.Collections.ObjectModel;
using CatClawVideo.Maui.ViewModels;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 首页：分类 chips + 海报网格（订阅源接入前用假数据撑起 UI 骨架）+ 快速播放入口。
/// 海报卡点击 → 观看页（watch 路由）。
/// </summary>
public partial class HomePage : ContentView, ITabView
{
    private readonly HomeViewModel _vm;

    /// <summary>分类 chips（订阅源接入后由源站点分类目录替换）</summary>
    public ObservableCollection<CategoryChip> Categories { get; } = new()
    {
        new("全部", true), new("电影"), new("剧集"), new("综艺"), new("动漫"), new("纪录片"),
    };

    /// <summary>海报网格假数据（占位封面，字段对齐 VodItem 便于接入真实源）</summary>
    public ObservableCollection<VodCard> Cards { get; } = new()
    {
        new("流浪地球 2", "8.7", "2023 · 科幻"),
        new("漫长的季节", "9.1", "2023 · 悬疑"),
        new("狂飙", "8.4", "2023 · 剧情"),
        new("三体", "7.9", "2023 · 科幻"),
        new("繁花", "8.8", "2023 · 剧情"),
        new("隐秘的角落", "8.2", "2020 · 悬疑"),
        new("庆余年 第二季", "8.5", "2024 · 古装"),
        new("大明王朝 1566", "9.3", "2007 · 古装"),
        new("琅琊榜", "8.6", "2015 · 古装"),
        new("去有风的地方", "8.0", "2023 · 偶像"),
        new("不良人 第七季", "8.3", "2024 · 动漫"),
        new("一年一度喜剧大赛", "7.6", "2024 · 综艺"),
    };

    public HomePage(HomeViewModel vm, IThemeService theme)
    {
        InitializeComponent();
        _vm = vm;
        // 分类/海报网格的绑定源是页面自身的集合，VM 仍承载快速播放输入
        CategoryChips.ItemsSource = Categories;
        CategoryChips.SelectionChanged += (sender, e) =>
        {
            if (e.CurrentSelection.FirstOrDefault() is CategoryChip c)
                c.IsSelected = true;
            ((CollectionView)sender).SelectedItem = null;
        };
        PosterGrid.ItemsSource = Cards;
    }

    /// <summary>海报卡点击 → 观看页</summary>
    private void OnPosterTapped(object? sender, TappedEventArgs e)
    {
        var card = ((VerticalStackLayout)sender!).BindingContext as VodCard;
        var title = card?.Title ?? "猫爪影视";
        Shell.Current.GoToAsync($"watch?title={Uri.EscapeDataString(title)}");
    }

    public Task OnTabShownAsync() => Task.CompletedTask;
}

/// <summary>分类 chip（订阅源接入前硬编码）</summary>
public class CategoryChip
{
    public string Name { get; }
    public bool IsSelected { get; set; }

    public CategoryChip(string name, bool isSelected = false)
    {
        Name = name;
        IsSelected = isSelected;
    }
}

/// <summary>海报网格卡（订阅源接入前假数据；字段与 VodItem 展示子集对齐）</summary>
public class VodCard
{
    public string Title { get; }
    public string Score { get; }
    public string Meta { get; }

    public VodCard(string title, string score, string meta)
    {
        Title = title;
        Score = score;
        Meta = meta;
    }
}
