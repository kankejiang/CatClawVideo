namespace CatClawVideo.Maui.Services;

/// <summary>
/// 唤起外部播放器（对位 TVBox <c>PLAY_TYPE</c> 10–14 与 <c>player/thirdparty/</c>）。
///
/// <para><b>只有安卓做得到</b>：Windows 这一侧 <c>Launcher.LaunchUriAsync</c> 只能递一个 URI，
/// 传不出 Referer/UA 这类请求头，防盗链源交出去必然 403；本项目又是
/// <c>WindowsPackageType=None</c>，没有 full-trust 辅助进程可以代为传参。
/// 所以桌面端不是「暂时没做」，而是做不到 —— UI 上按这个说法直接回绝，不给半残路径。</para>
/// </summary>
public static class ExternalPlayerService
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

    /// <summary>本机装着哪几家（没声明 manifest 的 &lt;queries&gt; 时会一律查不到）。</summary>
    public static List<(string Id, string Display)> Detect()
    {
#if ANDROID
        return Platforms.Android.ExternalPlayers.Detect();
#else
        return [];
#endif
    }

    public static bool Launch(string id, string url, string? title, string? subtitle,
        IReadOnlyDictionary<string, string>? headers, long positionMs)
    {
#if ANDROID
        return Platforms.Android.ExternalPlayers.Launch(id, url, title, subtitle, headers, positionMs);
#else
        return false;
#endif
    }
}
