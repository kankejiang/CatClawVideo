using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CatClawVideo.Maui.ViewModels;

/// <summary>播放页 ViewModel：播放状态与自定义控制条的数据源（MediaElement 实例由页面持有）。</summary>
public partial class VideoPlayerViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _isBuffering;

    [ObservableProperty]
    private bool _isFailed;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    /// <summary>控制条是否可见（点按画面切换，播放中自动隐藏）</summary>
    [ObservableProperty]
    private bool _controlsVisible = true;

    /// <summary>当前播放位置（秒，由页面 MediaElement 事件回写）</summary>
    [ObservableProperty]
    private double _positionSeconds;

    /// <summary>总时长（秒）</summary>
    [ObservableProperty]
    private double _durationSeconds;

    /// <summary>拖动进度中（拖动时暂停 Position 回写，避免抖动）</summary>
    [ObservableProperty]
    private bool _isSeeking;

    [ObservableProperty]
    private bool _isLandscape;

    public string PositionLabel => FormatTime(PositionSeconds);
    public string DurationLabel => FormatTime(DurationSeconds);

    partial void OnPositionSecondsChanged(double value) => OnPropertyChanged(nameof(PositionLabel));
    partial void OnDurationSecondsChanged(double value) => OnPropertyChanged(nameof(DurationLabel));

    [RelayCommand]
    private void ToggleControls() => ControlsVisible = !ControlsVisible;

    [RelayCommand]
    private void ToggleLandscape()
    {
#if ANDROID
        var app = Application.Current as App;
        app?.ToggleLandscape();
        IsLandscape = app?.ManualLandscape ?? false;
#endif
    }

    /// <summary>秒 → mm:ss / h:mm:ss</summary>
    public static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes:D2}:{t.Seconds:D2}";
    }
}
