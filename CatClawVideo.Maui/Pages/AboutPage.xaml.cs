using CatClawVideo.Maui.ViewModels;

namespace CatClawVideo.Maui.Pages;

/// <summary>关于页：品牌信息、简介、免责声明 / 开源协议 / 猫爪音乐 / GitHub / 检查更新。</summary>
public partial class AboutPage : ContentPage
{
    public AboutPage(AboutViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }

    /// <summary>自定义返回（页面隐藏了 Shell 系统返回键）</summary>
    private async void OnBackTapped(object? sender, TappedEventArgs e)
    {
        try { await Shell.Current.GoToAsync(".."); } catch { }
    }
}
