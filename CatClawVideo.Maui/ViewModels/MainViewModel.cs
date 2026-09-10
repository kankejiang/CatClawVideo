using CommunityToolkit.Mvvm.ComponentModel;

namespace CatClawVideo.Maui.ViewModels;

/// <summary>主页面 ViewModel：底部导航的 tab 状态。</summary>
public partial class MainViewModel : ObservableObject
{
    /// <summary>Tab 定义：图标资源名 + 标题 + 页面索引</summary>
    public record TabDef(string Icon, string Title)
    {
        public string IconResource => Icon;
    }

    public IReadOnlyList<TabDef> Tabs { get; } =
    [
        new("ic_home.svg", "首页"),
        new("ic_history.svg", "历史"),
        new("ic_favorite.svg", "收藏"),
        new("ic_download_white.svg", "下载"),
        new("ic_folder.svg", "本地"),
        new("ic_settings.svg", "设置"),
    ];

    [ObservableProperty]
    private int _selectedTabIndex;

    partial void OnSelectedTabIndexChanged(int value)
    {
        TabChanged?.Invoke(value);
    }

    /// <summary>tab 切换通知（MainPage 换页时带上滑动方向做过渡动画）</summary>
    public event Action<int>? TabChanged;

    public void SelectTab(int index)
    {
        if (index >= 0 && index < Tabs.Count && SelectedTabIndex != index)
            SelectedTabIndex = index;
    }
}
