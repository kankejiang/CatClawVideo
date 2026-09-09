using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.Maui.Controls.Shapes;
using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;
using CatClawVideo.Maui.Controls;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 观看页（主流播放器布局）：播放器 + 右侧选集栏（等高滚动）+ 底部信息区。
/// 路由携带 sourceKey/type/api/itemId，进入后拉播放线路与选集；
/// 播放统一走 ResolvePlayUrlAsync（web 源实时解析直链、磁力拦截、防盗链参数）。
/// </summary>
public partial class WatchPage : ContentPage, IQueryAttributable
{
    private readonly IVodSourceProvider _provider;

    private VodSiteInfo _site = new();
    private VodItem _item = new();
    private List<VodPlaySource> _sources = [];
    private VodEpisode? _currentEpisode;
    private int _currentSourceIndex;
    private int _currentEpisodeIndex = -1;
    private readonly List<EpisodeRow> _episodeRows = [];

    private bool _playing;
    private bool _seeking;
    private bool _loaded;
    private bool _descExpanded;
    private string _descFull = string.Empty;

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

        // 徽章：清晰度 / 年份 / 分类（无则隐藏）
        SetBadge(RemarksBadge, RemarksBadgeLabel, _item.Remarks);
        SetBadge(YearBadge, YearBadgeLabel, _item.Year);
        SetBadge(CategoryBadge, CategoryBadgeLabel, _item.Category);

        MetaLabel.Text = $"来源：{_site.Name}{(_site.Name.Length > 0 ? " · " : "")}{_site.Key}";
        _descFull = _item.Description is { Length: > 0 } raw ? CleanDesc(raw) : "";
        _descExpanded = false;
        DescToggle.IsVisible = false;
        DescLabel.MaxLines = 2;
        DescLabel.Text = _descFull.Length > 0 ? _descFull : "暂无简介";

        // 简介排版诊断（临时）：记录原始/清洗后文本的不可见字符分布，定位空隙根因后移除
        try
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CatClawVideo");
            Directory.CreateDirectory(dir);
            var rawDesc = _item.Description ?? "";
            var odd = string.Concat(rawDesc.Where(c => c == '\n' || c == '\r' || c == '\u00a0' || c == '\u3000' || c == '\u200b' || c == '\ufeff')
                .Select(c => $"U+{(int)c:X4} "));
            File.WriteAllText(System.IO.Path.Combine(dir, "desc-debug.log"),
                $"[{DateTime.Now:HH:mm:ss}] 原始长度={rawDesc.Length} 清洗后长度={DescLabel.Text.Length} 特殊字符=[{odd}]\n");
        }
        catch { }
    }

    private static void SetBadge(Border badge, Label label, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) { badge.IsVisible = false; return; }
        label.Text = text;
        badge.IsVisible = true;
    }

    /// <summary>布局完成按播放器实际宽度设置 16:9 高度（窗口缩放自适应）</summary>
    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        // 页面宽 - 左右 padding(48) - 选集栏(300) - 列间距(16)
        var playerWidth = width - 48 - 300 - 16;
        if (playerWidth > 100)
            PlayerHost.HeightRequest = Math.Clamp(playerWidth * 9.0 / 16.0, 200, 560);
    }

    /// <summary>简介清洗：HTML 实体解码 + &nbsp; 空段/连续空白折叠为单空格</summary>
    private static string CleanDesc(string text)
    {
        var decoded = System.Net.WebUtility.HtmlDecode(text);
        var cleaned = System.Text.RegularExpressions.Regex.Replace(decoded, @"[\s\u00a0\u3000]+", " ").Trim();
        return cleaned;
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

            // 线路芯片（播放器左上角内嵌）
            LinesHost.Children.Clear();
            for (int i = 0; i < _sources.Count; i++)
            {
                var index = i;
                var chip = new Border
                {
                    StrokeThickness = 0,
                    StrokeShape = new RoundRectangle { CornerRadius = 6 },
                    Padding = new Thickness(10, 4),
                    BackgroundColor = i == 0 ? (Color)Application.Current!.Resources["PrimaryColor"] : Color.FromArgb("#80000000"),
                    Content = new Label
                    {
                        Text = _sources[i].Name,
                        FontSize = 11,
                        TextColor = i == 0 ? Colors.White : Color.FromArgb("#CCCCCC"),
                    },
                };
                var tap = new TapGestureRecognizer();
                tap.Tapped += (_, _) => SelectSource(index);
                chip.GestureRecognizers.Add(tap);
                LinesHost.Children.Add(chip);
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

    /// <summary>切换播放线路：重建右侧选集栏并播第一集</summary>
    private void SelectSource(int index)
    {
        if (index < 0 || index >= _sources.Count) return;
        _currentSourceIndex = index;
        var source = _sources[index];

        // 线路芯片高亮
        for (int i = 0; i < LinesHost.Children.Count; i++)
        {
            if (LinesHost.Children[i] is Border chip)
            {
                chip.BackgroundColor = i == index
                    ? (Color)Application.Current!.Resources["PrimaryColor"]
                    : Color.FromArgb("#80000000");
                if (chip.Content is Label l)
                    l.TextColor = i == index ? Colors.White : Color.FromArgb("#CCCCCC");
            }
        }

        // 重建右侧选集行（ep-row：序号方块 + 集名 + 当前集高亮）
        EpisodeListHost.Children.Clear();
        _episodeRows.Clear();
        for (int i = 0; i < source.Episodes.Count; i++)
        {
            var row = new EpisodeRow(i, source.Episodes[i].Name);
            _episodeRows.Add(row);
            var captured = row;
            var border = new Border
            {
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 8 },
                Padding = new Thickness(10, 9),
                BindingContext = row,
            };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition(28), new ColumnDefinition(GridLength.Star),
            }, ColumnSpacing = 10 };
            var num = new Border
            {
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 6 },
                BackgroundColor = Color.FromArgb("#14FFFFFF"),
                WidthRequest = 26, HeightRequest = 26, VerticalOptions = LayoutOptions.Center,
            };
            num.Content = new Label
            {
                Text = (i + 1).ToString(),
                FontSize = 11,
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalOptions = LayoutOptions.Center,
                TextColor = (Color)Application.Current!.Resources["TextSecondaryColor"],
            };
            var name = new Label
            {
                FontSize = 13,
                VerticalOptions = LayoutOptions.Center,
                LineBreakMode = LineBreakMode.TailTruncation,
                Text = source.Episodes[i].Name,
                TextColor = (Color)Application.Current.Resources["TextSecondaryColor"],
            };
            grid.Add(num, 0);
            grid.Add(name, 1);
            border.Content = grid;
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => PlayEpisodeByRow(captured);
            border.GestureRecognizers.Add(tap);
            EpisodeListHost.Children.Add(border);
            row.CurrentChanged += () => HighlightRow(border, name, num, row);
        }

        EpisodeCountLabel.Text = source.Episodes.Count > 1
            ? $"共 {source.Episodes.Count} 集 · {source.Name}"
            : source.Name;

        if (source.Episodes.Count > 0)
            PlayEpisodeByRow(_episodeRows[0]);
        else
            _ = ShowTipAsync("该线路暂无选集");
    }

    /// <summary>选集行当前态样式（底色/文字色/序号块）</summary>
    private void HighlightRow(Border border, Label name, Border num, EpisodeRow row)
    {
        border.BackgroundColor = row.IsCurrent
            ? (Color)Application.Current!.Resources["PrimaryColor"]
            : (Color)Application.Current.Resources["CardBackgroundColor"];
        name.TextColor = row.IsCurrent ? Colors.White : (Color)Application.Current.Resources["TextSecondaryColor"];
        num.BackgroundColor = row.IsCurrent
            ? (Color)Application.Current.Resources["PrimaryColor"]
            : Color.FromArgb("#14FFFFFF");
        if (num.Content is Label numLabel)
            numLabel.TextColor = row.IsCurrent ? Colors.White : (Color)Application.Current.Resources["TextSecondaryColor"];
    }

    /// <summary>选集行点击</summary>
    private async void PlayEpisodeByRow(EpisodeRow row)
    {
        if (_currentSourceIndex < 0 || _currentSourceIndex >= _sources.Count) return;
        var episodes = _sources[_currentSourceIndex].Episodes;
        if (row.Index < 0 || row.Index >= episodes.Count) return;

        // 行高亮切换
        foreach (var r in _episodeRows) r.IsCurrent = false;
        row.IsCurrent = true;
        _currentEpisodeIndex = row.Index;

        await PlayEpisodeAsync(episodes[row.Index]);
    }

    /// <summary>小窗播放指定集：统一走 ResolvePlayUrlAsync（web 源实时解析直链/磁力拦截）</summary>
    private async Task PlayEpisodeAsync(VodEpisode episode)
    {
        _currentEpisode = episode;
        ShowBuffering(true);
        try
        {
            var play = await _provider.ResolvePlayUrlAsync(_site, episode);
            Player.Source = play.Url;
            Player.Play();
            _playing = true;
        }
        catch (NotSupportedException ex)
        {
            await ShowTipAsync(ex.Message);
        }
        catch
        {
            await ShowTipAsync("该集解析失败，请换集或换线路");
        }
        finally
        {
            ShowBuffering(false);
            UpdatePlayIcon();
        }
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

    /// <summary>简介展开/收起</summary>
    private void OnDescToggleTapped(object? sender, TappedEventArgs e)
    {
        _descExpanded = !_descExpanded;
        DescLabel.MaxLines = _descExpanded ? int.MaxValue : 2;
        DescToggle.Text = _descExpanded ? "收起 ▴" : "展开 ▾";
    }

    /// <summary>收藏（占位：后续接收藏库）</summary>
    private async void OnFavoriteClicked(object? sender, EventArgs e)
    {
        await ShowTipAsync("收藏功能随后续版本上线");
    }

    /// <summary>分享当前播放链接</summary>
    private async void OnShareClicked(object? sender, EventArgs e)
    {
        var url = _currentEpisode?.Url ?? "";
        if (url.Length == 0) return;
        try
        {
            await Microsoft.Maui.ApplicationModel.DataTransfer.Share.RequestAsync(
                new Microsoft.Maui.ApplicationModel.DataTransfer.ShareTextRequest
                {
                    Title = _item.Title,
                    Text = $"{_item.Title} · {_currentEpisode?.Name}\n{url}",
                });
        }
        catch { }
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
        var icon = _playing ? "ic_pause.svg" : "ic_play.svg";
        CenterPlayButton.Source = icon;
        SmallPlayButton.Source = icon;
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

    /// <summary>右侧选集栏行模型（IsCurrent 驱动高亮）</summary>
    public sealed class EpisodeRow(int index, string name) : INotifyPropertyChanged
    {
        public int Index { get; } = index;
        public string Name { get; } = name;

        private bool _isCurrent;
        public bool IsCurrent
        {
            get => _isCurrent;
            set
            {
                if (_isCurrent == value) return;
                _isCurrent = value;
                CurrentChanged?.Invoke();
            }
        }

        /// <summary>高亮样式刷新回调（MAUI 无双向样式绑定，代码后置挂接）</summary>
        public event Action? CurrentChanged;
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
