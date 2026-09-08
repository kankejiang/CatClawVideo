using System.Collections.ObjectModel;
using CatClawVideo.Maui.ViewModels;
using CatClawVideo.Maui.Controls;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 观看页（详情 + 播放合并）：顶部小窗播放器（58% 宽，21:9 手机横屏适配）
/// + 右侧信息面板（片名/评分/简介/线路/操作）+ 选集网格 + 相关推荐。
/// 订阅源接入前用假数据 + HLS 测试流撑起 UI；「继续播放/选集」跳全屏播放器页。
/// </summary>
public partial class WatchPage : ContentPage
{
    /// <summary>当前观看的影片标题（路由参数）</summary>
    private string _title = "漫长的季节";

    private bool _playing;
    private bool _seeking;

    public WatchPage()
    {
        InitializeComponent();

        // 假数据：选集 + 相关推荐
        var episodes = new ObservableCollection<string>();
        for (var i = 1; i <= 12; i++) episodes.Add(i.ToString("00"));
        EpisodeGrid.ItemsSource = episodes;
        RecommendGrid.ItemsSource = new ObservableCollection<VodCard>
        {
            new("平原上的摩西", "8.7", "2023 · 悬疑"),
            new("沉默的真相", "9.1", "2020 · 悬疑"),
            new("白夜追凶", "8.9", "2017 · 悬疑"),
            new("尘封十三载", "8.4", "2023 · 悬疑"),
            new("胆小鬼", "8.1", "2022 · 悬疑"),
            new("立功·东北旧事", "7.8", "2023 · 剧情"),
        };

        // 小窗播放器：默认加载测试流（订阅源接入后替换为真实选集地址）
        Player.Source = HomeViewModel.TestStreamUrl;
        UpdatePlayIcon();

        Player.PositionChanged += (_, _) => MainThread.BeginInvokeOnMainThread(UpdateProgress);
        Player.MediaOpened += (_, _) => MainThread.BeginInvokeOnMainThread(UpdateProgress);
        Player.StateChanged += (_, _) => MainThread.BeginInvokeOnMainThread(UpdatePlayIcon);
    }

    /// <summary>路由参数：title（来自首页海报卡）</summary>
    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("title", out var t) && t is string title && !string.IsNullOrWhiteSpace(title))
        {
            _title = title;
            SetMainTitle(title);
        }
    }

    private void SetMainTitle(string title)
    {
        // 信息面板片名（假数据固定「漫长的季节」，接入真实源后由 VM 绑定；此处同步路由标题）
        if (_title != "漫长的季节")
            _title = title;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // 回到页面时恢复播放（嵌入播放器常驻本页）
        Player.Play();
        _playing = true;
        UpdatePlayIcon();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // 离开页面暂停（小窗语义：不后台占资源；历史记录由全屏播放页负责）
        Player.Pause();
        _playing = false;
        UpdatePlayIcon();
    }

    private void OnBackTapped(object? sender, TappedEventArgs e) => Shell.Current.GoToAsync("..");

    private void OnSurfaceTapped(object? sender, TappedEventArgs e) => OnPlayPauseClicked(sender, e);

    private void OnPlayPauseClicked(object? sender, EventArgs e)
    {
        if (_playing)
        {
            Player.Pause();
            _playing = false;
        }
        else
        {
            Player.Play();
            _playing = true;
        }
        UpdatePlayIcon();
    }

    private void OnPlayFullClicked(object? sender, EventArgs e)
    {
        // 全屏沉浸播放：复用现有播放器页链路
        Shell.Current.GoToAsync($"player?title={Uri.EscapeDataString(_title + " · 第 4 集")}&url={Uri.EscapeDataString(HomeViewModel.TestStreamUrl)}");
    }

    private void UpdatePlayIcon()
    {
        CenterPlayButton.Source = _playing ? "ic_pause.svg" : "ic_play.svg";
    }

    private void UpdateProgress()
    {
        if (_seeking) return;
        var total = Player.Duration.TotalSeconds;
        PositionLabel.Text = FormatTime(Player.Position);
        DurationLabel.Text = total > 0 ? FormatTime(Player.Duration) : "--:--";
        if (total > 0)
            ProgressBar.Value = Player.Position.TotalSeconds / total * 100;
    }

    private void OnSeekStarted(object? sender, EventArgs e) => _seeking = true;

    private void OnSeekCompleted(object? sender, EventArgs e)
    {
        _seeking = false;
        var total = Player.Duration.TotalSeconds;
        if (total > 0)
            Player.Seek(TimeSpan.FromSeconds(ProgressBar.Value / 100 * total));
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
}
