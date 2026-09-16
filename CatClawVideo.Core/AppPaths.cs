namespace CatClawVideo.Core;

/// <summary>
/// 应用数据路径的**唯一来源**：Debug 与 Release 彻底隔离（2026-09-16 用户要求）。
///
/// <para><b>为什么</b>：此前日志/订阅库/缓存都落在固定目录（<c>%APPDATA%\CatClawVideo</c> 与
/// MAUI 的 <c>%LOCALAPPDATA%\CatClawVideo.Maui\…\Data</c>），Debug 跑一次就会污染正式版的数据
/// （反之亦然）—— 排查时看到的是对方的状态，订阅/历史也会串。</para>
///
/// <para><b>布局</b>（后缀由配置决定：Debug = <c>CatClawVideo.debug</c>，Release = <c>CatClawVideo</c>）：
/// <list type="bullet">
/// <item><c>%APPDATA%\{名}\</c> —— DB、日志、btcache、凭据、网盘缓存</item>
/// <item><c>%LOCALAPPDATA%\{名}\</c> —— QEMU 控制台日志等本机临时物</item>
/// </list></para>
///
/// <para><b>迁移</b>：<see cref="SeedFile"/> 在目标不存在时，从旧位置拷一份（只拷不共享），
/// 保证换目录后订阅/历史/凭据不丢。</para>
/// </summary>
public static class AppPaths
{
    /// <summary>是否 Debug 构建（决定目录后缀）。</summary>
    public static bool IsDebug { get; } =
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>数据目录名（Debug 带 .debug 后缀 → 与正式版互不影响）。</summary>
    public static string FolderName { get; } = IsDebug ? "CatClawVideo.debug" : "CatClawVideo";

    /// <summary>%APPDATA%\{名}：持久数据（DB/日志/缓存/凭据）。</summary>
    public static string DataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName);

    /// <summary>%LOCALAPPDATA%\{名}：本机专属数据（QEMU 控制台日志等）。</summary>
    public static string LocalRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);

    /// <summary>旧版本（未按配置隔离）的共享数据目录：<c>%APPDATA%\CatClawVideo</c>，仅用于首次迁移。</summary>
    public static string LegacyDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CatClawVideo");

    /// <summary>数据根下的文件路径（目录按需创建）。</summary>
    public static string Of(string fileName) => Path.Combine(Ensure(DataRoot), fileName);

    /// <summary>数据根下的子目录（或子目录里的文件）路径（目录按需创建）。</summary>
    public static string Sub(string subDir, string? fileName = null)
    {
        var dir = Ensure(Path.Combine(DataRoot, subDir));
        return fileName is null ? dir : Path.Combine(dir, fileName);
    }

    /// <summary>本机根下的文件路径（目录按需创建）。</summary>
    public static string LocalOf(string fileName) => Path.Combine(Ensure(LocalRoot), fileName);

    /// <summary>可再生的本机缓存根：<c>%LOCALAPPDATA%\{名}\cache</c>（封面、脚本缓存等）。</summary>
    public static string CacheRoot => LocalSub("cache");

    /// <summary>本机根下的子目录（或子目录里的文件）路径（目录按需创建）。</summary>
    public static string LocalSub(string subDir, string? fileName = null)
    {
        var dir = Ensure(Path.Combine(LocalRoot, subDir));
        return fileName is null ? dir : Path.Combine(dir, fileName);
    }

    /// <summary>
    /// 首次运行的数据迁移：若 <paramref name="fileName"/> 在数据根下不存在，
    /// 就从 <paramref name="legacyDirs"/>（旧位置，按序尝试）拷一份过来。只拷不共享，失败静默。
    /// </summary>
    public static void SeedFile(string fileName, params string?[] legacyDirs)
    {
        try
        {
            var target = Of(fileName);
            if (File.Exists(target)) return;
            foreach (var dir in legacyDirs)
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                var src = Path.Combine(dir, fileName);
                if (!File.Exists(src)) continue;
                File.Copy(src, target, overwrite: false);
                return;
            }
        }
        catch { /* 迁移失败不影响启动（只是首次没有历史数据） */ }
    }

    private static string Ensure(string dir)
    {
        try { Directory.CreateDirectory(dir); } catch { }
        return dir;
    }
}
