using System.IO;

namespace CatClawVideo.Maui.Services;

/// <summary>
/// 应用内更新的「下载 + 安装」（对位 TVBox 的 <c>AppUpgradeUtils</c> 与 REQUEST_INSTALL_PACKAGES 那条链）。
///
/// <para>只有安卓有这条路：桌面端是解压版目录，覆盖文件即可，没有「安装包」概念。
/// 之前这里的「立即下载」只是把用户丢去浏览器，装完还得自己回桌面换目录 —— 那是半条链。</para>
/// </summary>
public static class AppInstaller
{
    public static bool Supported
    {
        get
        {
#if ANDROID
            return true;
#else
            return false;
#endif
        }
    }

    /// <summary>安装包落点：缓存目录（FileProvider 的 cache-path 覆盖得到）。</summary>
    public static string ApkPath => Path.Combine(FileSystem.CacheDirectory, "catclaw-update.apk");

    /// <summary>是否已被授予「安装未知应用」。</summary>
    public static bool HasInstallPermission()
    {
#if ANDROID
        return Platforms.Android.ApkInstaller.HasInstallPermission();
#else
        return true;
#endif
    }

    /// <summary>去系统页授权（这个权限不能程序自己给）。</summary>
    public static bool OpenInstallPermissionSettings()
    {
#if ANDROID
        return Platforms.Android.ApkInstaller.OpenInstallPermissionSettings();
#else
        return false;
#endif
    }

    /// <summary>下载安装包，进度是 0~1；拿不到长度时不报进度（返回的字节数仍然有效）。</summary>
    public static async Task<(long Bytes, bool HasTotal)> DownloadAsync(
        string url, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ApkPath)!);
        using var http = Core.Services.Doh.NewClient(180);
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        // 先写临时名再换名：下到一半断掉不该留下一个「看起来完整」的半截 apk
        var tmp = ApkPath + ".part";
        await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        {
            var buf = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }
        if (File.Exists(ApkPath)) File.Delete(ApkPath);
        File.Move(tmp, ApkPath);
        return (new FileInfo(ApkPath).Length, total > 0);
    }

    /// <summary>把下好的包交给系统安装器。</summary>
    public static (bool Ok, string Message) Install()
    {
#if ANDROID
        var r = Platforms.Android.ApkInstaller.Install(ApkPath);
        return (r.Ok, r.Message);
#else
        return (false, "这台设备不支持应用内安装");
#endif
    }
}
