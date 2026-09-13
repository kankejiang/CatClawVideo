namespace CatClawVideo.Core.Services;

/// <summary>检查更新结果</summary>
/// <param name="LatestVersion">最新版本号（不含 v 前缀，如 "0.2.0"）</param>
/// <param name="ReleaseNotes">更新说明（已清理 Markdown 标记并截断，可能为 null）</param>
/// <param name="DownloadUrl">与当前平台匹配的安装包直链（无匹配资源时为 null，此时退回 Releases 页）</param>
/// <param name="ReleasePageUrl">GitHub Releases 页面地址</param>
public sealed record UpdateCheckResult(
    string LatestVersion,
    string? ReleaseNotes,
    string? DownloadUrl,
    string ReleasePageUrl);

/// <summary>版本更新服务接口</summary>
public interface IUpdateService
{
    /// <summary>
    /// 异步检查 GitHub Release 是否有新版本。
    /// 有新版本时返回 <see cref="UpdateCheckResult"/>；
    /// 已是最新版本（远端版本 ≤ 本地版本）或响应缺少版本信息时返回 null；
    /// 网络 / 接口异常向上抛出，由调用方决定如何提示。
    /// </summary>
    Task<UpdateCheckResult?> CheckUpdateAsync();
}
