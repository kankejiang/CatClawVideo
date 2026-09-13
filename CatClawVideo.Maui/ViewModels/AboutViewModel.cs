using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CatClawVideo.Maui.ViewModels;

/// <summary>
/// 关于页 ViewModel：展示应用版本与版权信息，提供免责声明、开源协议、
/// 猫爪音乐跳转等入口（对齐猫爪音乐 AboutViewModel 的结构）。
/// </summary>
public partial class AboutViewModel : ObservableObject
{
    /// <summary>应用版本号（带 v 前缀，从 AppInfo 读取，失败回退 v1.0.0）</summary>
    [ObservableProperty]
    private string _version = GetAppVersionString();

    /// <summary>获取应用版本号字符串（带 v 前缀；AppInfo 会补足 4 段版本，这里裁到 3 段更易读）</summary>
    private static string GetAppVersionString()
    {
        try
        {
            var v = AppInfo.Current?.VersionString;
            if (string.IsNullOrEmpty(v)) return "v1.0.0";
            var parts = v.Split('.');
            if (parts.Length == 4 && parts[3] == "0") v = string.Join('.', parts, 0, 3);
            return $"v{v}";
        }
        catch
        {
            return "v1.0.0";
        }
    }

    /// <summary>版权声明（年份动态取系统时间，避免硬编码过期）</summary>
    [ObservableProperty]
    private string _copyright = $"© 2026-{DateTime.Now.Year} CatClawVideo. All rights reserved.";

    /// <summary>应用包名（区分 Debug/Release 包）</summary>
    [ObservableProperty]
    private string _packageId = GetPackageId();

    private static string GetPackageId()
    {
        try { return AppInfo.Current?.PackageName ?? "com.catclaw.video"; }
        catch { return "com.catclaw.video"; }
    }

    /// <summary>查看免责声明：本项目仅为播放器壳，不内置任何片源</summary>
    [RelayCommand]
    private async Task ViewDisclaimerAsync()
    {
        try
        {
            await Shell.Current.DisplayAlertAsync("免责声明",
                "本项目仅为播放器壳，不内置任何片源，也不提供、不存储、不上传任何影视内容。\n\n" +
                "所有源均由用户自行配置，用户需对所添加的源及观看内容承担全部责任。\n\n" +
                "本项目与 TVBox / 影视仓及其他第三方源无隶属关系。", "我知道了");
        }
        catch { }
    }

    /// <summary>查看开源协议：在系统浏览器打开 GitHub 上的 LICENSE 文件</summary>
    [RelayCommand]
    private async Task ViewLicenseAsync()
    {
        try
        {
            await Launcher.OpenAsync(new Uri("https://github.com/kankejiang/CatClawVideo/blob/master/LICENSE"));
        }
        catch { }
    }

    /// <summary>打开本仓库 GitHub 页面</summary>
    [RelayCommand]
    private async Task OpenGitHubAsync()
    {
        try
        {
            await Launcher.OpenAsync(new Uri("https://github.com/kankejiang/CatClawVideo"));
        }
        catch { }
    }

    /// <summary>打开猫爪音乐（同作者的音频端）</summary>
    [RelayCommand]
    private async Task OpenMusicAsync()
    {
        try
        {
            await Launcher.OpenAsync(new Uri("https://github.com/kankejiang/CatClawMusic"));
        }
        catch { }
    }
}
