namespace CatClawVideo.Maui.Services;

/// <summary>
/// 后台继续播放的开关（对位 TVBox 的 <c>MusicPlaybackService</c> 那条能力）。
/// 默认关：开着它意味着「离开播放页声音还在」，得让用户自己选。
/// </summary>
public static class BgPlayPrefs
{
    const string Key = "bg_play";

    public static bool IsOn
    {
        get
        {
            try { return Preferences.Default.Get(Key, false); } catch { return false; }
        }
        set
        {
            try { Preferences.Default.Set(Key, value); } catch { }
        }
    }

    /// <summary>页面消失时该不该留着继续放：既要开关开着，也要这次真的是切后台（不是导航走）。</summary>
    public static bool ShouldKeepPlaying()
    {
#if ANDROID
        return IsOn && MainActivity.IsInBackground;
#else
        return false;
#endif
    }
}
