using CatClawVideo.Maui.ViewModels;

namespace CatClawVideo.Maui.Pages;

/// <summary>关于页：品牌信息、简介、免责声明 / 开源协议 / 猫爪音乐 / GitHub / 检查更新。</summary>
public partial class AboutPage : ContentPage
{
    private const string ReleasesUrl = "https://github.com/kankejiang/CatClawVideo/releases";

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

    /// <summary>
    /// 检查更新：当前无内置更新服务，改为给出当前版本并跳转 GitHub Releases 页面。
    /// TODO: 接入与猫爪音乐一致的 IUpdateService 后改为版本比较 + 弹窗提示。
    /// </summary>
    private async void OnCheckUpdateClicked(object? sender, EventArgs e)
    {
        var ver = (BindingContext as AboutViewModel)?.Version ?? "v0.0.0";
        var go = await Shell.Current.DisplayAlertAsync("检查更新",
            $"当前版本 {ver}\n\n将打开 GitHub Releases 页面查看最新发布版本。", "前往查看", "取消");
        if (!go) return;
        try { await Launcher.OpenAsync(new Uri(ReleasesUrl)); } catch { }
    }
}
