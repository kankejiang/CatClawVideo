namespace CatClawVideo.Maui.Pages;

/// <summary>Tab 内容视图约定：切换到该 tab 时由宿主调用（替代 Page.OnAppearing 生命周期）。</summary>
public interface ITabView
{
    /// <summary>tab 被显示时触发（加载/刷新数据）</summary>
    Task OnTabShownAsync();
}
