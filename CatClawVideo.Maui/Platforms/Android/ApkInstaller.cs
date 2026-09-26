using Android.Content;
using Android.Provider;
using AndroidUri = Android.Net.Uri;
using AxFileProvider = AndroidX.Core.Content.FileProvider;

namespace CatClawVideo.Maui.Platforms.Android;

/// <summary>
/// 应用内下载并安装 APK（对位 TVBox 的 <c>AppUpgradeUtils</c> + REQUEST_INSTALL_PACKAGES 那条链）。
///
/// <para>两个 Android 版本坑必须处理，否则表现就是「点了没反应」：</para>
/// <list type="number">
/// <item>7.0+ 不能把 <c>file://</c> 交给系统安装器（<c>FileUriExposedException</c>）→ 走 FileProvider 的
/// <c>content://</c> 并临时授权；</item>
/// <item>8.0+ 安装是「特殊权限」，不在已授予权限里 → 只能把系统设置页打开让用户自己给。</item>
/// </list>
/// </summary>
internal static class ApkInstaller
{
    /// <summary>把下载好的 apk 交给系统安装器。<c>Ok</c> 只代表成功跳出了安装界面。</summary>
    public static InstallOutcome Install(string apkPath)
    {
        var context = Microsoft.Maui.ApplicationModel.Platform.AppContext;
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (context?.PackageName is not { Length: > 0 } pkg || activity is null)
            return new InstallOutcome(false, "拿不到应用上下文");

        try
        {
            var file = new Java.IO.File(apkPath);
            if (!file.Exists()) return new InstallOutcome(false, "安装包不存在（下载未完成？）");

            var authority = pkg + ".catclaw.fileprovider";
            var uri = AxFileProvider.GetUriForFile(context, authority, file);

            var intent = new Intent(Intent.ActionView)
                .SetDataAndType(uri, "application/vnd.android.package-archive");
            intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
            activity.StartActivity(intent);
            return new InstallOutcome(true, "");
        }
        catch (ActivityNotFoundException)
        {
            return new InstallOutcome(false, "这台设备没有能安装 apk 的程序");
        }
        catch (Exception ex)
        {
            // authority / file_paths 配置不对时会抛 IllegalArgumentException，这里把原文带回 UI，
            // 免得只剩「安装失败」四个字（这条链的失败几乎都是配置名对不上）
            return new InstallOutcome(false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>是否已被授予「安装未知应用」。</summary>
    public static bool HasInstallPermission()
    {
        var pm = Microsoft.Maui.ApplicationModel.Platform.AppContext?.PackageManager;
        try { return pm?.CanRequestPackageInstalls() ?? false; }
        catch { return false; }
    }

    /// <summary>打开本应用的「允许安装未知应用」设置页（授权只能由用户在那里点）。</summary>
    public static bool OpenInstallPermissionSettings()
    {
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        var pkg = Microsoft.Maui.ApplicationModel.Platform.AppContext?.PackageName;
        if (activity is null || pkg is not { Length: > 0 }) return false;
        try
        {
            var intent = new Intent(Settings.ActionManageUnknownAppSources,
                AndroidUri.Parse("package:" + pkg));
            intent.AddFlags(ActivityFlags.NewTask);
            activity.StartActivity(intent);
            return true;
        }
        catch (Exception ex)
        {
            Maui.Services.BtFileLog.Write($"[更新] 打不开安装授权页：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}

/// <summary>安装动作的结果（Ok 只代表成功拉起安装界面）。</summary>
internal readonly record struct InstallOutcome(bool Ok, string Message);
