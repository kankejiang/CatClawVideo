using Microsoft.Maui.Controls.Shapes;
using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;
using CatClawVideo.Maui.Controls;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 观看页（详情 + 播放合并）：小窗播放器（58% 宽，21:9 手机横屏适配）
/// + 信息面板（片名/评分/简介/线路/操作）+ 选集网格 + 相关推荐。
/// 真实数据：路由携带 sourceKey/api/itemId，进入后拉播放线路与选集，小窗直接播放当前集；
/// 「全屏播放」跳全屏播放器页（携带真实直链）。
/// </summary>
public partial class WatchPage : ContentPage, IQueryAttributable
{
    private readonly IVodSourceProvider _provider;

    private VodSiteInfo _site = new();
    private VodItem _item = new();
    private List<VodPlaySource> _sources = [];
    private VodEpisode? _currentEpisode;

    private bool _playing;
    private bool _seeking;
    private bool _loaded;

    public WatchPage(IVodSourceProvider provider)
    {
        InitializeComponent();
        _provider = provider;

        Player.PositionChanged += (_, _) => MainThread.BeginInvokeOnMainThread(UpdateProgress);
        Player.MediaOpened += (_, _) => MainThread.BeginInvokeOnMainThread(UpdateProgress);
        Player.StateChanged += (_, _) => MainThread.BeginInvokeOnMainThread(UpdatePlayIcon);
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("title", out var t) && t is string title) _item.Title = title;
        if (query.TryGetValue("sourceKey", out var sk) && sk is string sourceKey) _site.Key = sourceKey;
        if (query.TryGetValue("type", out var tp) && tp is string typeStr && int.TryParse(typeStr, out var type))
            _site.Type = type;
        if (query.TryGetValue("api", out var apiObj) && apiObj is string api) _site.Api = api;
        if (query.TryGetValue("itemId", out var idObj) && idObj is string itemId) _item.Id = itemId;
        if (query.TryGetValue("year", out var y) && y is string year && year.Length > 0) _item.Year = year;
        if (query.TryGetValue("remarks", out var r) && r is string remarks && remarks.Length > 0) _item.Remarks = remarks;
        if (query.TryGetValue("desc", out var d) && d is string desc && desc.Length > 0) _item.Description = desc;

        TitleLabel.Text = _item.Title;
        var meta = $"{(_item.Year.Length > 0 ? _item.Year + " · " : "")}{(_item.Remarks.Length > 0 ? _item.Remarks + " · " : "")}{_site.Name}";
        MetaLabel.Text = meta;
        DescLabel.Text = _item.Description is { Length: > 0 } descText
            ? System.Net.WebUtility.HtmlDecode(descText)
            : "暂无简介";
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (!_loaded)
        {
            _loaded = true;
            _ = LoadSourcesAsync();
        }
        else if (_playing)
        {
            Player.Play();
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        Player.Pause();
        _playing = false;
        UpdatePlayIcon();
    }

    /// <summary>拉播放线路与选集（真数据），默认播第一线路第一集</summary>
    private async Task LoadSourcesAsync()
    {
        BufferingIndicator.IsVisible = true;
        try
        {
            _sources = await _provider.GetPlaySourcesAsync(_site, _item);

            LinesHost.Children.Clear();
            for (int i = 0; i < _sources.Count; i++)
            {
                var source = _sources[i];
                var index = i;
                var pill = new Border
                {
                    StrokeThickness = 0,
                    StrokeShape = new RoundRectangle { CornerRadius = 8 },
                    Padding = new Thickness(14, 6),
                    BackgroundColor = i == 0
                        ? (Color)Application.Current!.Resources["PrimaryColor"]
                        : (Color)Application.Current!.Resources["ChipInactiveColor"],
                    Content = new Label
                    {
                        Text = source.Name,
                        FontSize = 12,
                        TextColor = i == 0 ? Colors.White : (Color)Application.Current!.Resources["TextSecondaryColor"],
                    },
                };
                var tap = new TapGestureRecognizer();
                tap.Tapped += (_, _) => SelectSource(index);
                pill.GestureRecognizers.Add(tap);
                LinesHost.Children.Add(pill);
            }

            if (_sources.Count > 0)
                SelectSource(0);
            else
                await ShowTipAsync("该影片暂无可播放线路");
        }
        catch
        {
            await ShowTipAsync("线路加载失败");
        }
        finally
        {
            BufferingIndicator.IsVisible = false;
        }
    }

    /// <summary>切换播放线路：重建选集并播第一集</summary>
    private void SelectSource(int index)
    {
        if (index < 0 || index >= _sources.Count) return;
        var source = _sources[index];

        // 线路 pill 高亮
        for (int i = 0; i < LinesHost.Children.Count; i++)
        {
            if (LinesHost.Children[i] is Border pill)
            {
                pill.BackgroundColor = i == index
                    ? (Color)Application.Current!.Resources["PrimaryColor"]
                    : (Color)Application.Current!.Resources["ChipInactiveColor"];
                if (pill.Content is Label l)
                    l.TextColor = i == index ? Colors.White : (Color)Application.Current!.Resources["TextSecondaryColor"];
            }
        }

        EpisodeGrid.ItemsSource = source.Episodes;
        EpisodeGrid.SelectionChanged -= OnEpisodeSelected;
        EpisodeGrid.SelectionChanged += OnEpisodeSelected;

        if (source.Episodes.Count > 0)
            PlayEpisode(source.Episodes[0]);
    }

    /// <summary>选集点击：小窗切换播放该集</summary>
    private void OnEpisodeSelected(object? sender, SelectionChangedEventArgs e)
    {
        EpisodeGrid.SelectedItem = null;
        if (e.CurrentSelection.FirstOrDefault() is VodEpisode ep)
            PlayEpisode(ep);
    }

    /// <summary>小窗播放指定集</summary>
    private void PlayEpisode(VodEpisode episode)
    {
        _currentEpisode = episode;
        Player.Source = episode.Url;
        Player.Play();
        _playing = true;
        UpdatePlayIcon();
    }

    private void ShowBuffering(bool on) => BufferingIndicator.IsVisible = on;

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

    private async Task ShowTipAsync(string message)
    {
        try
        {
            await DisplayAlertAsync("提示", message, "确定");
        }
        catch { }
    }

    private void OnPlayFullClicked(object? sender, EventArgs e)
    {
        var episodeName = _currentEpisode?.Name ?? "";
        var url = _currentEpisode?.Url ?? "";
        if (url.Length == 0) return;
        Shell.Current.GoToAsync($"player?title={Uri.EscapeDataString(_item.Title + " · " + episodeName)}&url={Uri.EscapeDataString(url)}");
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
