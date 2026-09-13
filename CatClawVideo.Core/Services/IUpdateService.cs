namespace CatClawVideo.Core.Services;

/// <summary>检查更新结果</summary>
/// <param name="HasUpdate">远端版本是否比本地新</param>
/// <param name="LatestVersion">最新版本号（不含 v 前缀，如 "0.2.0"；已更新到最新时即当前线上版本）</param>
/// <param name="ReleaseNotes">该版本更新说明（已清理 Markdown 标记并截断，可能为 null）</param>
/// <param name="DownloadUrl">与当前平台匹配的安装包直链（无匹配资源时为 null，此时退回 Releases 页）</param>
/// <param name="ReleasePageUrl">GitHub Releases 页面地址</param>
public sealed record UpdateCheckResult(
    bool HasUpdate,
    string LatestVersion,
    string? ReleaseNotes,
    string? DownloadUrl,
    string ReleasePageUrl);

/// <summary>版本更新服务接口</summary>
public interface IUpdateService
{
    /// <summary>
    /// 异步检查 GitHub Release 最新版本信息。
    /// 无论是否有新版本都返回 <see cref="UpdateCheckResult"/>（含最新版本号与更新日志），
    /// 由 <see cref="UpdateCheckResult.HasUpdate"/> 区分；
    /// 响应缺少版本信息时返回 null；
    /// 网络 / 接口异常向上抛出，由调用方决定如何提示。
    /// </summary>
    Task<UpdateCheckResult?> CheckUpdateAsync();
}
