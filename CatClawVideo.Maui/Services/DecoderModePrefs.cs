using CatClawVideo.Maui.Controls;

namespace CatClawVideo.Maui.Services;

/// <summary>
/// 解码模式的持久化：设置页写、播放页读。对位 TVBox 的 <c>IJK_CODEC</c>（ijk硬解/ijk软解）与
/// <c>PLAY_TYPE</c>（内核选择）—— 我们没有多内核，所以把「换内核才能解决的兼容问题」收敛成硬/软解一档。
/// </summary>
public static class DecoderModePrefs
{
    const string Key = "decoder_mode";

    public static VideoDecoderMode Load() =>
        Enum.TryParse<VideoDecoderMode>(Preferences.Default.Get(Key, nameof(VideoDecoderMode.Auto)), out var m)
            ? m : VideoDecoderMode.Auto;

    public static void Save(VideoDecoderMode mode) => Preferences.Default.Set(Key, mode.ToString());

    public static string Label(VideoDecoderMode mode) => mode switch
    {
        VideoDecoderMode.Hardware => "强制硬解",
        VideoDecoderMode.Software => "强制软解",
        _ => "自动",
    };

    /// <summary>点一下轮一档（自动 → 硬解 → 软解 → 自动）。</summary>
    public static VideoDecoderMode Next(VideoDecoderMode mode) => mode switch
    {
        VideoDecoderMode.Auto => VideoDecoderMode.Hardware,
        VideoDecoderMode.Hardware => VideoDecoderMode.Software,
        _ => VideoDecoderMode.Auto,
    };
}

/// <summary>默认画面比例的持久化（对位 TVBox <c>PLAY_SCALE</c>）。</summary>
public static class AspectPrefs
{
    const string Key = "default_aspect";

    public static VideoAspect Load() =>
        Enum.TryParse<VideoAspect>(Preferences.Default.Get(Key, nameof(VideoAspect.AspectFit)), out var a)
            ? a : VideoAspect.AspectFit;

    public static void Save(VideoAspect aspect) => Preferences.Default.Set(Key, aspect.ToString());

    public static string Label(VideoAspect aspect) => aspect switch
    {
        VideoAspect.AspectFill => "填满裁切",
        VideoAspect.Fill => "拉伸填满",
        _ => "保持比例（AspectFit）",
    };

    public static VideoAspect Next(VideoAspect a) => a switch
    {
        VideoAspect.AspectFit => VideoAspect.AspectFill,
        VideoAspect.AspectFill => VideoAspect.Fill,
        _ => VideoAspect.AspectFit,
    };
}

/// <summary>默认倍速的持久化（存档位下标而不是值，避免浮点串来回漂移）。</summary>
public static class SpeedPrefs
{
    const string Key = "default_speed_index";

    public static double Load()
    {
        var idx = Preferences.Default.Get(Key, 2);   // 2 = 1.0×
        return idx >= 0 && idx < VideoPlayerView.SpeedPresets.Length
            ? VideoPlayerView.SpeedPresets[idx] : 1.0;
    }

    public static void Save(double speed)
    {
        var idx = Array.IndexOf(VideoPlayerView.SpeedPresets, speed);
        if (idx >= 0) Preferences.Default.Set(Key, idx);
    }

    public static string Label(double speed) => $"{speed:0.##}×";

    public static double Next(double speed)
    {
        var idx = Array.IndexOf(VideoPlayerView.SpeedPresets, speed);
        return VideoPlayerView.SpeedPresets[(idx + 1) % VideoPlayerView.SpeedPresets.Length];
    }
}

/// <summary>
/// 播放历史条数上限（对位 TVBox <c>HISTORY_NUM</c>）。存 Preferences，
/// 同时回写 <see cref="CatClawVideo.Data.VideoDatabase.MaxHistoryEntries"/> —— 启动时 Load 一次即可。
/// </summary>
public static class HistoryCap
{
    const string Key = "history_max";
    const int Fallback = 500;
    static readonly int[] Options = [100, 200, 500, 1000];

    public static int Load()
    {
        var v = Preferences.Default.Get(Key, Fallback);
        if (!Options.Contains(v)) v = Fallback;
        CatClawVideo.Data.VideoDatabase.MaxHistoryEntries = v;
        return v;
    }

    public static int Next(int current)
    {
        var i = System.Array.IndexOf(Options, current);
        return Options[(i + 1) % Options.Length];
    }

    public static string Label(int value) => value + " 条";

    public static void Save(int value)
    {
        Preferences.Default.Set(Key, value);
        CatClawVideo.Data.VideoDatabase.MaxHistoryEntries = value;
    }
}
