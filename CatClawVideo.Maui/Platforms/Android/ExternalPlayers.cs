using Android.Content;
using Android.OS;
// ⚠ 本文件在 namespace CatClawVideo.Maui.Platforms.Android 里 —— 直接写 Android.Content.PM.X
// 会被解析成 CatClawVideo.Maui.Platforms.Android.Content.PM.X（命名空间段抢先命中），
// 所以这几个类型一律走别名。
using AndroidUri = Android.Net.Uri;
using PackageManager = Android.Content.PM.PackageManager;
using PackageInfoFlags = Android.Content.PM.PackageInfoFlags;

namespace CatClawVideo.Maui.Platforms.Android;

/// <summary>
/// 唤起外部播放器（对位 TVBox <c>player/thirdparty/</c> 的 MXPlayer / ReexPlayer / Kodi / VlcPlayer）。
///
/// <para>四家的头传法互不相同，这是这个功能唯一麻烦的地方：MX 与 Kodi 把请求头拼在 URI 尾巴上
/// （<c>url|K=V&amp;K=V</c>，值要 URLEncode），Reex 收一个 JSON 字符串 extra，VLC 则**根本不收头**
/// —— 所以防盗链源推给 VLC 必然 403，这一点在 UI 上要写明白，不能让用户以为坏了。</para>
///
/// <para>包名与 Activity 类名逐字照抄 TVBox 的常量表（各家都有 pro/免费两包的按顺序试装）。</para>
/// </summary>
internal static class ExternalPlayers
{
    /// <param name="HeaderStyle">这一家怎么收 HTTP 请求头。</param>
    internal sealed record Player(string Id, string Display, string Package, string? Activity, HeaderMode HeaderStyle);

    internal enum HeaderMode { UriPipe, ReexJson, None }

    /// <summary>已知的外部播放器（同一 Id 可能有多个包，按数组顺序取第一个装着的）。</summary>
    static readonly (string Id, string Display, HeaderMode Style, (string Pkg, string? Act)[] Packages)[] Known =
    [
        ("mx", "MX Player", HeaderMode.UriPipe,
            [("com.mxtech.videoplayer.pro", "com.mxtech.videoplayer.ActivityScreen"),
             ("com.mxtech.videoplayer.ad", "com.mxtech.videoplayer.ad.ActivityScreen")]),
        ("reex", "Reex Player", HeaderMode.ReexJson,
            [("xyz.re.player.ex", "xyz.re.player.ex.MainActivity")]),
        ("kodi", "Kodi", HeaderMode.UriPipe,
            [("org.xbmc.kodi", "org.xbmc.kodi.Splash")]),
        ("vlc", "VLC for Android", HeaderMode.None,
            [("org.videolan.vlc", "org.videolan.vlc.gui.video.VideoPlayerActivity")]),
    ];

    /// <summary>本机装了哪几家（Android 11+ 需要 manifest 的 &lt;queries&gt; 声明，否则一律查不到）。</summary>
    public static List<(string Id, string Display)> Detect()
    {
        var found = new List<(string, string)>();
        var pm = Microsoft.Maui.ApplicationModel.Platform.AppContext?.PackageManager;
        if (pm is null) return found;
        foreach (var p in Known)
            if (ResolvePackage(pm, p.Packages) is not null) found.Add((p.Id, p.Display));
        return found;
    }

    static (string Pkg, string? Act)? ResolvePackage(PackageManager pm,
        (string Pkg, string? Act)[] packages)
    {
        foreach (var cand in packages)
        {
            try
            {
                var info = pm.GetApplicationInfo(cand.Pkg, (PackageInfoFlags)0);
                if (info is not null && info.Enabled) return cand;
            }
            catch (Exception)
            {
                // 没装：Android 抛 PackageInfoNotFoundException，属正常路径
            }
        }
        return null;
    }

    /// <summary>
    /// 唤起。<paramref name="headers"/> 是防盗链要带的头（Referer/UA/Cookie），按各家约定传法不同。
    /// <paramref name="positionMs"/> 只有 VLC 支持（对位 TVBox 只给 VlcPlayer 传 progress）。
    /// </summary>
    public static bool Launch(string id, string url, string? title, string? subtitle,
        IReadOnlyDictionary<string, string>? headers, long positionMs)
    {
        var entry = Array.Find(Known, k => k.Id == id);
        if (entry.Id is null) return false;
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        var pm = Microsoft.Maui.ApplicationModel.Platform.AppContext?.PackageManager;
        if (activity is null || pm is null) return false;
        var pkg = ResolvePackage(pm, entry.Packages);
        if (pkg is null) return false;

        try
        {
            var intent = new Intent(Intent.ActionView);
            intent.SetPackage(pkg.Value.Pkg);
            if (pkg.Value.Act is not null) intent.SetClassName(pkg.Value.Pkg, pkg.Value.Act);

            // 头：MX/Kodi 走 `url|K=V&K=V`（值 URLEncode），Reex 走 JSON extra，VLC 无解
            var playUrl = url;
            if (headers is { Count: > 0 } && entry.Style == HeaderMode.UriPipe)
            {
                var sb = new System.Text.StringBuilder(url).Append('|');
                var parts = new List<string>();
                foreach (var kv in headers)
                    parts.Add(kv.Key + "=" + Uri.EscapeDataString(kv.Value));
                sb.Append(string.Join("&", parts));
                playUrl = sb.ToString();
            }
            intent.SetData(AndroidUri.Parse(playUrl));

            if (!string.IsNullOrWhiteSpace(title))
            {
                intent.PutExtra("title", title);
                if (entry.Id is "kodi" or "reex") intent.PutExtra("name", title);
            }
            if (entry.Id == "reex") intent.PutExtra("reex.extra.title", title);

            if (headers is { Count: > 0 } && entry.Style == HeaderMode.ReexJson)
            {
                var json = "{" + string.Join(",", headers.Select(kv =>
                    "\"" + kv.Key.Replace("\\", "") .Replace("\"", "") + "\":\"" +
                    kv.Value.Trim().Replace("\\", "").Replace("\"", "") + "\"")) + "}";
                intent.PutExtra("reex.extra.http_header", json);
            }

            // 字幕：MX 收 Parcelable[]（subs + subs.enable），Kodi/Reex/VLC 各一个字符串 extra
            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                var sub = AndroidUri.Parse(subtitle);
                switch (entry.Id)
                {
                    case "mx":
                        IParcelable[] parcels = [sub!];
                        intent.PutExtra("subs", parcels);
                        intent.PutExtra("subs.enable", parcels);
                        break;
                    case "kodi":
                        intent.PutExtra("subs", subtitle);
                        break;
                    case "reex":
                        intent.PutExtra("reex.extra.subtitle", subtitle);
                        break;
                    case "vlc":
                        intent.PutExtra("subtitles_location", subtitle);
                        break;
                }
            }

            if (entry.Id == "vlc" && positionMs > 0)
            {
                intent.PutExtra("from_start", false);
                intent.PutExtra("position", positionMs);
            }

            intent.AddFlags(ActivityFlags.NewTask);
            activity.StartActivity(intent);
            return true;
        }
        catch (Exception ex)
        {
            Maui.Services.BtFileLog.Write($"[外播] 唤起 {entry.Display} 失败：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}
