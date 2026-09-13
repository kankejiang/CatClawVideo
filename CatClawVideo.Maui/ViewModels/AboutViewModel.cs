using CatClawVideo.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CatClawVideo.Maui.ViewModels;

/// <summary>
/// 关于页 ViewModel：展示应用版本与版权信息，提供免责声明、开源协议、
/// 猫爪音乐跳转、检查更新等入口（对齐猫爪音乐 AboutViewModel 的结构）。
/// </summary>
public partial class AboutViewModel : ObservableObject
{
    private const string ReleasesUrl = "https://github.com/kankejiang/CatClawVideo/releases";

    private readonly IUpdateService? _updateService;

    public AboutViewModel(IUpdateService? updateService = null)
    {
        _updateService = updateService;
    }

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

    /// <summary>版权声明（起始年 2026；与当前年同年前只显示一个年份，避免 "2026-2026"）</summary>
    [ObservableProperty]
    private string _copyright = GetCopyright();

    private static string GetCopyright()
    {
        var y = DateTime.Now.Year;
        return y <= 2026
            ? "© 2026 CatClawVideo. All rights reserved."
            : $"© 2026-{y} CatClawVideo. All rights reserved.";
    }

    /// <summary>应用包名（区分 Debug/Release 包）</summary>
    [ObservableProperty]
    private string _packageId = GetPackageId();

    private static string GetPackageId()
    {
#if WINDOWS
        // Windows 未打包应用下 AppInfo.PackageName 返回的是 exe 名（CatClawVideo.Maui），
        // 不是产品包名；直接显示 csproj 里的 ApplicationId
        return "com.catclaw.video";
#else
        try { return AppInfo.Current?.PackageName ?? "com.catclaw.video"; }
        catch { return "com.catclaw.video"; }
#endif
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

    /// <summary>是否正在检查更新（检查期间禁用按钮防重复点击）</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckUpdateCommand))]
    private bool _isCheckingUpdate;

    /// <summary>检查更新按钮文案（检查中切换为提示语）</summary>
    [ObservableProperty]
    private string _checkUpdateButtonText = "⟳  检查更新";

    private bool CanCheckUpdate() => !IsCheckingUpdate;

    /// <summary>
    /// 检查更新：调用 GitHub Release 接口比较版本。
    /// 发现新版本时弹窗展示更新说明，确认后打开与当前平台匹配的安装包直链（无则退回 Releases 页）。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCheckUpdate))]
    private async Task CheckUpdateAsync()
    {
        IsCheckingUpdate = true;
        CheckUpdateButtonText = "正在检查...";
        try
        {
            if (_updateService is null)
            {
                // 无更新服务时退回旧行为：直接打开 Releases 页
                await Launcher.OpenAsync(new Uri(ReleasesUrl));
                return;
            }

            var result = await _updateService.CheckUpdateAsync();
            if (result is not null)
            {
                var notes = string.IsNullOrWhiteSpace(result.ReleaseNotes)
                    ? ""
                    : $"\n\n{result.ReleaseNotes}";
                var go = await Shell.Current.DisplayAlertAsync("发现新版本",
                    $"最新版本 v{result.LatestVersion}（当前 {Version}）{notes}",
                    "立即下载", "以后再说");
                if (go)
                    await Launcher.OpenAsync(new Uri(result.DownloadUrl ?? result.ReleasePageUrl));
            }
            else
            {
                await Shell.Current.DisplayAlertAsync("检查更新",
                    $"已是最新版本（当前 {Version}）", "好的");
            }
        }
        catch
        {
            // 网络 / 接口异常（GitHub API 限流、无 Release、超时等）
            await Shell.Current.DisplayAlertAsync("检查更新",
                "检查失败，请检查网络连接后重试。\n\n也可前往 GitHub Releases 页面手动查看。",
                "好的");
        }
        finally
        {
            IsCheckingUpdate = false;
            CheckUpdateButtonText = "⟳  检查更新";
        }
    }
}
