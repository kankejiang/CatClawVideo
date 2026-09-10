using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.Maui.Controls.Shapes;
using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;
using CatClawVideo.Core.Services;
using CatClawVideo.Data;
using CatClawVideo.Maui.Controls;
using CatClawVideo.Maui.Services;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 观看页（主流播放器布局）：播放器 + 右侧选集栏（等高滚动）+ 底部信息区。
/// 路由携带 sourceKey/type/api/itemId，进入后拉播放线路与选集；
/// 播放统一走 ResolvePlayUrlAsync（web 源实时解析直链、磁力拦截、防盗链参数）。
/// </summary>
public partial class WatchPage : ContentPage, IQueryAttributable
{
    private readonly IVodSourceProvider _provider;
    private readonly VideoDatabase _db;

    private VodSiteInfo _site = new();
    private VodItem _item = new();
    private List<VodPlaySource> _sources = [];
    private VodEpisode? _currentEpisode;
    private int _currentSourceIndex;
    private int _currentEpisodeIndex = -1;
    private readonly List<EpisodeRow> _episodeRows = [];

    /// <summary>选集分页：每页 20 集（2 列 × 10 行）/ 当前页（0 基）/ 当前页已渲染格的可视件（高亮用）</summary>
    private const int EpisodesPerPage = 20;
    private int _episodePage;
    private readonly List<(Border Border, Label Name, Border Num, EpisodeRow Row)> _pageVisuals = [];

    private bool _playing;
    private bool _seeking;
    private bool _loaded;
    private bool _descExpanded;
    private string _descFull = string.Empty;

    /// <summary>最近一次成功解析的播放请求（全屏复用解析结果；原始 episode.Url 未必可播）</summary>
    private PlayRequest? _resolvedPlay;

    /// <summary>播放代次：快速切集/切线路时废弃过期解析结果，防止旧响应覆盖新播放</summary>
    private int _playGeneration;

    /// <summary>线路芯片（SelectSource 高亮用；LinesHost 首位可能是"线路"前缀标签）</summary>
    private readonly List<Border> _lineChips = [];

    /// <summary>播放历史会话（观看页播放也落库，海报墙封面来自 _item.Cover）</summary>
    private readonly VideoPlaybackManager _playback;

    /// <summary>BT 引擎（网速徽章数据源；非 BT 播放时不显示）</summary>
    private readonly BtStreamService? _bt;

    /// <summary>下载管理器（磁力资源行"下载"按钮入口）</summary>
    private readonly DownloadManager? _downloads;

    /// <summary>网速徽章：BT 会话 infoHash / 刷新定时器 / 鼠标悬浮开关</summary>
    private string? _btInfoHex;
    private IDispatcherTimer? _speedTimer;

    /// <summary>控制层自动隐藏：鼠标移出播放框（或手指离开）后 3s 隐藏；播放中且非拖动时才生效</summary>
    private IDispatcherTimer? _controlsHideTimer;
    private const double ControlsHideSeconds = 3.0;

    /// <summary>原地全屏：同一播放器实例放大铺满，不新开页面/不重新拉流</summary>
    private bool _isFullscreen;

    /// <summary>播放历史跳转携带的续看定位：选集加载后自动选该集，MediaOpened 后 seek</summary>
    private string? _resumeEpisodeName;
    private double _resumePosition;

    public WatchPage(IVodSourceProvider provider, VideoDatabase db, VideoPlaybackManager playback,
        BtStreamService? bt = null, DownloadManager? downloads = null)
    {
        InitializeComponent();
        _provider = provider;
        _db = db;
        _playback = playback;
        _bt = bt;
        _downloads = downloads;

        Player.PositionChanged += (_, _) => MainThread.BeginInvokeOnMainThread(UpdateProgress);
        Player.MediaOpened += (_, _) => MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateProgress();

            // 播放历史续看：媒体就绪（时长已知）后一次性 seek 到上次位置
            if (_resumePosition > 0)
            {
                var pos = _resumePosition;
                _resumePosition = 0;
                _resumeEpisodeName = null;
                if (Player.Duration == TimeSpan.Zero || pos < Player.Duration.TotalSeconds - 1)
                    Player.Seek(TimeSpan.FromSeconds(pos));
            }
        });
        Player.StateChanged += (_, _) => MainThread.BeginInvokeOnMainThread(UpdatePlayIcon);
        // 播放失败必须有可见反馈（此前磁力/解析失败静默，用户以为"没反应"）
        Player.MediaFailed += (_, _) => MainThread.BeginInvokeOnMainThread(async () =>
        {
            ShowBuffering(false);
            _playing = false;
            UpdatePlayIcon();
            try { await ShowTipAsync($"播放失败：{Player.ErrorMessage ?? "格式或网络错误"}"); }
            catch { }
        });

        // 网速徽章：鼠标移入播放画面显示（BT 播放时），0.8s 刷新
        // 指针手势挂在 ControlsOverlay 上：该层本身**常驻不隐藏**（隐藏的是它的子元素），
        // 否则控制层一旦隐藏，其自身手势失效，控件就再也唤不出来。
        var pointer = new PointerGestureRecognizer();
        pointer.PointerEntered += OnPlayerPointerEntered;
        pointer.PointerMoved += OnPlayerPointerMoved;
        pointer.PointerExited += OnPlayerPointerExited;
        ControlsOverlay.GestureRecognizers.Add(pointer);
        _speedTimer = Dispatcher.CreateTimer();
        _speedTimer.Interval = TimeSpan.FromMilliseconds(800);
        _speedTimer.IsRepeating = true;
        _speedTimer.Tick += (_, _) => UpdateSpeedBadge();

        // 控制层自动隐藏：鼠标移出播放框 / 手指离开后 3s 隐藏
        _controlsHideTimer = Dispatcher.CreateTimer();
        _controlsHideTimer.Interval = TimeSpan.FromSeconds(ControlsHideSeconds);
        _controlsHideTimer.IsRepeating = false;
        _controlsHideTimer.Tick += (_, _) => HideControlsIfIdle();
    }

    private void OnPlayerPointerEntered(object? sender, PointerEventArgs e)
    {
        // 鼠标进入播放框：控制层常亮（取消倒计时，等鼠标离开再重新计时）
        ShowControls();

        _btInfoHex = BtStreamService.ExtractInfoHash(_resolvedPlay?.Url);
        if (_bt == null || _btInfoHex == null) return; // 非 BT 播放不显示
        UpdateSpeedBadge();
        SpeedBadge.IsVisible = true;
        _speedTimer?.Start();
    }

    /// <summary>鼠标在播放框内移动：保持控制层可见（离开播放框才开始 3s 倒计时）</summary>
    private void OnPlayerPointerMoved(object? sender, PointerEventArgs e) => ShowControls();

    private void OnPlayerPointerExited(object? sender, PointerEventArgs e)
    {
        SpeedBadge.IsVisible = false;
        _speedTimer?.Stop();

        // 鼠标移出播放框：3s 后隐藏控制层（暂停/拖动中不隐藏）
        RestartControlsHideTimer();
    }

    private void UpdateSpeedBadge()
    {
        if (_bt == null || _btInfoHex == null) { SpeedBadge.IsVisible = false; return; }
        SpeedLabel.Text = FormatSpeed(_bt.GetDownloadSpeed(_btInfoHex));
    }

    private static string FormatSpeed(long bps) =>
        bps >= 1048576 ? $"{bps / 1048576.0:F1} MB/s"
        : bps >= 1024 ? $"{bps / 1024.0:F0} KB/s"
        : $"{bps} B/s";

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("title", out var t) && t is string title) _item.Title = title;
        if (query.TryGetValue("sourceKey", out var sk) && sk is string sourceKey)
        {
            _site.Key = sourceKey;
            _item.SourceKey = sourceKey;
        }
        if (query.TryGetValue("type", out var tp) && tp is string typeStr && int.TryParse(typeStr, out var type))
            _site.Type = type;
        if (query.TryGetValue("api", out var apiObj) && apiObj is string api) _site.Api = api;
        if (query.TryGetValue("itemId", out var idObj) && idObj is string itemId) _item.Id = itemId;
        if (query.TryGetValue("cover", out var cv) && cv is string cover && cover.Length > 0) _item.Cover = cover;
        if (query.TryGetValue("year", out var y) && y is string year && year.Length > 0) _item.Year = year;
        if (query.TryGetValue("remarks", out var r) && r is string remarks && remarks.Length > 0) _item.Remarks = remarks;
        if (query.TryGetValue("desc", out var d) && d is string desc && desc.Length > 0) _item.Description = desc;

        // 播放历史跳转携带的续看定位（选集名 + 上次位置）
        if (query.TryGetValue("resumeEp", out var re) && re is string resumeEp && resumeEp.Length > 0)
            _resumeEpisodeName = resumeEp;
        if (query.TryGetValue("pos", out var posObj) && posObj is string posStr &&
            double.TryParse(posStr, System.Globalization.CultureInfo.InvariantCulture, out var pos) && pos > 0)
            _resumePosition = pos;

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

        // 回显收藏状态（收藏表查重）
        _ = LoadFavoriteStateAsync();
    }

    private static void SetBadge(Border badge, Label label, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) { badge.IsVisible = false; return; }
        label.Text = text;
        badge.IsVisible = true;
    }

    /// <summary>布局完成按播放器实际宽度设置 16:9 高度（窗口缩放自适应；全屏时铺满整页）</summary>
    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (_isFullscreen)
        {
            PlayerHost.HeightRequest = Math.Max(200, height);
            return;
        }
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
#if WINDOWS
        HookEscKey(attach: true);
#endif
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
#if WINDOWS
        HookEscKey(attach: false);
#endif
        if (_isFullscreen) SetFullscreen(false);
        Player.Pause();
        _playing = false;
        UpdatePlayIcon();
        _speedTimer?.Stop();
        SpeedBadge.IsVisible = false;

        // 播放历史落库（观看页会话收尾）
        try { _playback.EndSession(Player.Position.TotalSeconds, Player.Duration.TotalSeconds); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Watch] 历史记录失败: {ex.Message}"); }
    }

    /// <summary>拉播放线路与选集（真数据），默认播第一线路第一集</summary>
    private async Task LoadSourcesAsync()
    {
        BufferingIndicator.IsVisible = true;
        try
        {
            _sources = await _provider.GetPlaySourcesAsync(_site, _item);

            // 线路芯片（右侧选集栏上方）：点击切换线路并重载选集；多线路时加"线路"前缀提示可切换
            LinesHost.Children.Clear();
            _lineChips.Clear();
            if (_sources.Count > 1)
                LinesHost.Children.Add(new Label
                {
                    Text = "线路",
                    FontSize = 10.5,
                    TextColor = (Color)Application.Current!.Resources["TextHintColor"],
                    VerticalOptions = LayoutOptions.Center,
                });
            for (int i = 0; i < _sources.Count; i++)
            {
                var index = i;
                var chip = new Border
                {
                    StrokeThickness = 0,
                    StrokeShape = new RoundRectangle { CornerRadius = 8 },
                    Padding = new Thickness(12, 5),
                    BackgroundColor = i == 0 ? (Color)Application.Current!.Resources["PrimaryColor"] : (Color)Application.Current!.Resources["ChipInactiveColor"],
                    VerticalOptions = LayoutOptions.Center,
                    Content = new Label
                    {
                        Text = DisplayNameFor(_sources[i].Name),
                        FontSize = 11.5,
                        TextColor = i == 0 ? Colors.White : (Color)Application.Current!.Resources["TextSecondaryColor"],
                    },
                };
                var tap = new TapGestureRecognizer();
                tap.Tapped += (_, _) => SelectSource(index);
                chip.GestureRecognizers.Add(tap);
                LinesHost.Children.Add(chip);
                _lineChips.Add(chip);
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

        // 线路芯片高亮（_lineChips 与 _sources 索引一一对应）
        for (int i = 0; i < _lineChips.Count; i++)
        {
            var chip = _lineChips[i];
            chip.BackgroundColor = i == index
                ? (Color)Application.Current!.Resources["PrimaryColor"]
                : (Color)Application.Current.Resources["ChipInactiveColor"];
            if (chip.Content is Label l)
                l.TextColor = i == index ? Colors.White : (Color)Application.Current.Resources["TextSecondaryColor"];
        }

        // 重建选集模型：全量行模型各挂一次高亮回调，可视件按当前页渲染（一页 20 集，2 列）
        EpisodeListHost.Children.Clear();
        _episodeRows.Clear();
        _episodePage = 0;
        for (int i = 0; i < source.Episodes.Count; i++)
        {
            var row = new EpisodeRow(i, source.Episodes[i].Name);
            row.CurrentChanged += () => ApplyRowHighlight(row);
            _episodeRows.Add(row);
        }
        RenderEpisodePage();

        EpisodeCountLabel.Text = source.Episodes.Count > 1
            ? $"共 {source.Episodes.Count} 集 · {DisplayNameFor(source.Name)}"
            : DisplayNameFor(source.Name);

        if (source.Episodes.Count > 0)
        {
            // 播放历史跳转：与首页进入一致——还原信息区并直接播放；
            // 差异是播的是**上次看的那集**（高亮），且 MediaOpened 后续到上次位置。
            // 选集列表在侧栏随时可见，想换集直接点。
            var resumeRow = _resumeEpisodeName is { Length: > 0 }
                ? _episodeRows.FirstOrDefault(r =>
                      string.Equals(r.Name.Trim(), _resumeEpisodeName.Trim(), StringComparison.Ordinal))
                : null;
            PlayEpisodeByRow(resumeRow ?? _episodeRows[0]);
            if (resumeRow == null)
            {
                // 找不到续看集（换线路选集名不同）：作废续看位置，避免误 seek
                _resumeEpisodeName = null;
                _resumePosition = 0;
            }
        }
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

    /// <summary>渲染当前分页的选集格（一页 20 集：2 列 × 10 行）并刷新翻页条可见性</summary>
    private void RenderEpisodePage()
    {
        if (_currentSourceIndex < 0 || _currentSourceIndex >= _sources.Count) return;
        var source = _sources[_currentSourceIndex];
        int count = source.Episodes.Count;
        int totalPages = (int)Math.Ceiling(count / (double)EpisodesPerPage);

        EpisodeListHost.Children.Clear();
        EpisodeListHost.RowDefinitions.Clear();
        _pageVisuals.Clear();

        int start = _episodePage * EpisodesPerPage;
        int end = Math.Min(count, start + EpisodesPerPage);
        for (int i = start; i < end; i++)
        {
            var row = _episodeRows[i];
            int slot = i - start;
            int r = slot / 2, c = slot % 2;
            if (c == 0) EpisodeListHost.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var (cell, border, name, num) = BuildEpisodeCell(i, source.Episodes[i], row);
            EpisodeListHost.Add(cell, c, r);
            _pageVisuals.Add((border, name, num, row));
            HighlightRow(border, name, num, row);
        }

        // 翻页条：≤1 页整条隐藏；首页无上一页、末页无下一页
        bool multi = totalPages > 1;
        EpisodePager.IsVisible = multi;
        if (multi)
        {
            PagerPrev.IsVisible = _episodePage > 0;
            PagerNext.IsVisible = _episodePage < totalPages - 1;
            PagerLabel.Text = $"{_episodePage + 1}/{totalPages}";
        }
    }

    /// <summary>构建单个选集格：卡片（序号块 + 集名，点击播放）；
    /// 磁力资源额外附"▶ 播放 / ⬇ 下载"按钮行（按钮在卡片手势区外，避免与卡片点击冲突）</summary>
    private (VisualElement Cell, Border Border, Label Name, Border Num) BuildEpisodeCell(
        int index, VodEpisode episode, EpisodeRow row)
    {
        var isMagnet = episode.Url?.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) == true;

        var border = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            Padding = new Thickness(8, 8),
            BindingContext = row,
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitionCollection
        {
            new ColumnDefinition(28), new ColumnDefinition(GridLength.Star),
        }, ColumnSpacing = 8 };
        var num = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 6 },
            BackgroundColor = Color.FromArgb("#14FFFFFF"),
            WidthRequest = 26, HeightRequest = 26, VerticalOptions = LayoutOptions.Center,
        };
        num.Content = new Label
        {
            Text = (index + 1).ToString(),
            FontSize = 11,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalOptions = LayoutOptions.Center,
            TextColor = (Color)Application.Current!.Resources["TextSecondaryColor"],
        };
        var nameLabel = new Label
        {
            FontSize = 12.5,
            VerticalOptions = LayoutOptions.Center,
            LineBreakMode = LineBreakMode.TailTruncation,
            Text = episode.Name,
            TextColor = (Color)Application.Current.Resources["TextSecondaryColor"],
        };
        grid.Add(num, 0);
        grid.Add(nameLabel, 1);
        border.Content = grid;
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => PlayEpisodeByRow(row);
        border.GestureRecognizers.Add(tap);

        VisualElement cell = border;

        // 磁力资源：卡片下方附"播放 / 下载"按钮行（在卡片手势识别区外，点击互不干扰）
        if (isMagnet)
        {
            var actions = new HorizontalStackLayout { Spacing = 6, HorizontalOptions = LayoutOptions.Center };
            actions.Add(BuildCellChip("▶", "播放", icPlay: true, () => PlayEpisodeByRow(row)));
            actions.Add(BuildCellChip("⬇", "下载", icPlay: false, () => _ = DownloadEpisodeAsync(episode)));
            var wrapper = new VerticalStackLayout { Spacing = 2 };
            wrapper.Add(border);
            wrapper.Add(actions);
            cell = wrapper;
        }

        return (cell, border, nameLabel, num);
    }

    /// <summary>选集格内的小操作 chip（图标 + 文字）</summary>
    private Border BuildCellChip(string icon, string text, bool icPlay, Action onTap)
    {
        var chip = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            BackgroundColor = (Color)Application.Current!.Resources["ChipInactiveColor"],
            Padding = new Thickness(10, 3),
            HorizontalOptions = LayoutOptions.Center,
            Content = new HorizontalStackLayout { Spacing = 4 },
        };
        var stack = (HorizontalStackLayout)chip.Content!;
        stack.Children.Add(new Image
        {
            Source = icPlay ? "ic_play.png" : "ic_download_white.png",
            WidthRequest = 10, HeightRequest = 10,
            VerticalOptions = LayoutOptions.Center,
        });
        stack.Children.Add(new Label
        {
            Text = text,
            FontSize = 10.5,
            TextColor = (Color)Application.Current.Resources["TextSecondaryColor"],
            VerticalOptions = LayoutOptions.Center,
        });
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => onTap();
        chip.GestureRecognizers.Add(tap);
        return chip;
    }

    /// <summary>磁力资源下载：入队下载管理器整包下载该磁力</summary>
    private async Task DownloadEpisodeAsync(VodEpisode episode)
    {
        if (_downloads == null)
        {
            await ShowTipAsync("下载管理器不可用");
            return;
        }
        var name = string.IsNullOrWhiteSpace(_item.Title) ? episode.Name : $"{_item.Title} {episode.Name}";
        _downloads.EnqueueMagnet(episode.Url!, name);
        await ShowTipAsync($"「{episode.Name}」已加入下载队列（设置 → 下载管理 查看）");
    }

    /// <summary>行高亮刷新入口：仅当前页已渲染的行有可视件（不在本页的行事件静默）</summary>
    private void ApplyRowHighlight(EpisodeRow row)
    {
        foreach (var v in _pageVisuals)
        {
            if (!ReferenceEquals(v.Row, row)) continue;
            HighlightRow(v.Border, v.Name, v.Num, row);
            return;
        }
    }

    private void OnPrevPageTapped(object? sender, EventArgs e)
    {
        if (_episodePage > 0)
        {
            _episodePage--;
            RenderEpisodePage();
        }
    }

    private void OnNextPageTapped(object? sender, EventArgs e)
    {
        if (_currentSourceIndex < 0 || _currentSourceIndex >= _sources.Count) return;
        int totalPages = (int)Math.Ceiling(_sources[_currentSourceIndex].Episodes.Count / (double)EpisodesPerPage);
        if (_episodePage < totalPages - 1)
        {
            _episodePage++;
            RenderEpisodePage();
        }
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

        // 当前集不在本页时自动翻页（重渲染时按 IsCurrent 应用高亮）
        int target = row.Index / EpisodesPerPage;
        if (target != _episodePage)
        {
            _episodePage = target;
            RenderEpisodePage();
        }

        await PlayEpisodeAsync(episodes[row.Index]);
    }

    /// <summary>小窗播放指定集：统一走 ResolvePlayUrlAsync（web 源实时解析直链/BT 流式代理）</summary>
    private async Task PlayEpisodeAsync(VodEpisode episode)
    {
        // 播放历史续看：只有播的正是续看集才应用上次位置；换集则作废
        if (_resumeEpisodeName is { Length: > 0 } &&
            !string.Equals(episode.Name.Trim(), _resumeEpisodeName.Trim(), StringComparison.Ordinal))
        {
            _resumeEpisodeName = null;
            _resumePosition = 0;
        }

        _currentEpisode = episode;
        var generation = ++_playGeneration;
        ShowBuffering(true);
        try
        {
            var play = await _provider.ResolvePlayUrlAsync(_site, episode);
            if (generation != _playGeneration) return; // 已被后续点击取代，丢弃过期解析
            _resolvedPlay = play;
            Player.Source = play.Url;
            Player.Play();
            _playing = true;

            // 起播后唤出控制层；鼠标离开播放框（或手指离开）即 3s 后自动隐藏
            ShowControls();
            RestartControlsHideTimer();

            // 播放历史落库（BT 代理地址是会话内瞬态链接，重启后失效，不落库）。
            // 带上来源定位与集名：历史卡才能跳回观看页详情并自动选中该集续看。
            // 标题只存影片名（不含集名）——历史按影片合并，集名单独落 EpisodeName 列。
            if (!play.Url.Contains("/stream/", StringComparison.OrdinalIgnoreCase))
                _playback.BeginSession(_item.Title, play.Url, _item.Cover,
                    sourceKey: _site.Key, itemType: _site.Type, itemApi: _site.Api,
                    itemId: _item.Id, episodeName: episode.Name,
                    category: _item.Category, year: _item.Year,
                    remarks: _item.Remarks, description: _item.Description);
        }
        catch (NotSupportedException ex)
        {
            if (generation == _playGeneration) await ShowTipAsync(ex.Message);
        }
        catch
        {
            if (generation == _playGeneration) await ShowTipAsync("该集解析失败，请换集或换线路");
        }
        finally
        {
            if (generation == _playGeneration)
            {
                ShowBuffering(false);
                UpdatePlayIcon();
            }
        }
    }

    private void ShowBuffering(bool on) => BufferingIndicator.IsVisible = on;

    /// <summary>线路展示名兼容映射：存量静态源数据里的"磁力下载"统一显示为"磁力播放"（点击即 BT 流式播放）</summary>
    private static string DisplayNameFor(string name) =>
        name == "磁力下载" ? "磁力播放" : name;

    private void OnBackTapped(object? sender, TappedEventArgs e) => Shell.Current.GoToAsync("..");

    private void OnSurfaceTapped(object? sender, TappedEventArgs e)
    {
        // 触摸播放框：先唤出控制层（手指离开后 3s 才隐藏），再切换播放状态
        ShowControls();
        OnPlayPauseClicked(sender, e);
    }

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

        // 动作后控制层保持可见：暂停时常驻，播放中则 3s 后隐藏
        ShowControls();
        RestartControlsHideTimer();
    }

    /// <summary>简介展开/收起</summary>
    private void OnDescToggleTapped(object? sender, TappedEventArgs e)
    {
        _descExpanded = !_descExpanded;
        DescLabel.MaxLines = _descExpanded ? int.MaxValue : 2;
        DescToggle.Text = _descExpanded ? "收起 ▴" : "展开 ▾";
    }

    /// <summary>收藏/取消收藏（VideoDatabase favorites 表，按 sourceKey+itemId 查重）</summary>
    private async void OnFavoriteClicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_item.SourceKey) || string.IsNullOrEmpty(_item.Id))
        {
            await ShowTipAsync("该影片暂不支持收藏");
            return;
        }
        try
        {
            var existing = await _db.FindFavoriteAsync(_item.SourceKey, _item.Id);
            if (existing != null)
            {
                await _db.RemoveFavoriteAsync(existing);
                FavoriteButton.Text = "收藏";
                await ShowTipAsync("已取消收藏");
            }
            else
            {
                await _db.AddFavoriteAsync(_item);
                FavoriteButton.Text = "已收藏";
                await ShowTipAsync("已加入收藏");
            }
        }
        catch
        {
            await ShowTipAsync("收藏操作失败");
        }
    }

    /// <summary>进入页面时回显收藏状态</summary>
    private async Task LoadFavoriteStateAsync()
    {
        if (string.IsNullOrEmpty(_item.SourceKey) || string.IsNullOrEmpty(_item.Id)) return;
        try
        {
            var existing = await _db.FindFavoriteAsync(_item.SourceKey, _item.Id);
            FavoriteButton.Text = existing != null ? "已收藏" : "收藏";
        }
        catch { }
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

    /// <summary>全屏切换：同一播放器实例原地放大铺满（不新开页面、不重新拉流、进度天然连续）</summary>
    private void OnToggleFullscreenClicked(object? sender, EventArgs e) => SetFullscreen(!_isFullscreen);

    /// <summary>原地全屏：隐藏顶栏/选集/信息区，播放器铺满整页；窗口切 FullScreen（Win）/ 横屏（Android）</summary>
    private void SetFullscreen(bool on)
    {
        _isFullscreen = on;
        TopBarGrid.IsVisible = !on;
        SidebarPanel.IsVisible = !on;
        InfoArea.IsVisible = !on;
        RecommendHint.IsVisible = !on;
        MainArea.ColumnDefinitions = on
            ? new ColumnDefinitionCollection { new ColumnDefinition(GridLength.Star) }
            : new ColumnDefinitionCollection { new ColumnDefinition(GridLength.Star), new ColumnDefinition(300) };
        ContentStack.Padding = on ? new Thickness(0) : new Thickness(24, 14, 24, 28);
        FullScreenButton.Source = on ? "ic_fullscreen_exit.png" : "ic_fullscreen.png";
        if (on) _ = ContentScroll.ScrollToAsync(0, 0, false);
#if WINDOWS
        App.SetWindowFullscreen(on);
#elif ANDROID
        if (on) (Application.Current as App)?.ForceLandscape();
        else (Application.Current as App)?.ReleaseLandscape();
#endif

        // 切换全屏后唤出控制层；鼠标移出播放框即按 3s 倒计时隐藏
        ShowControls();
    }

#if WINDOWS
    /// <summary>Esc 退出全屏：挂到原生窗口 Content 根元素（与 MainPage 键盘导航同通道）</summary>
    private void HookEscKey(bool attach)
    {
        try
        {
            var native = (Window?.Handler?.PlatformView as Microsoft.UI.Xaml.Window)
                ?? (Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Microsoft.UI.Xaml.Window);
            if (native?.Content is Microsoft.UI.Xaml.UIElement root)
            {
                root.KeyDown -= OnPlatformKeyDown;
                if (attach) root.KeyDown += OnPlatformKeyDown;
            }
        }
        catch { }
    }

    private void OnPlatformKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && _isFullscreen)
        {
            e.Handled = true;
            MainThread.BeginInvokeOnMainThread(() => SetFullscreen(false));
        }
    }
#endif

    private void UpdatePlayIcon()
    {
        var icon = _playing ? "ic_pause.png" : "ic_play.png";
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

    private void OnSeekStarted(object? sender, EventArgs e)
    {
        // 拖动进度中：控制层常驻，倒计时作废
        _seeking = true;
        _controlsHideTimer?.Stop();
    }

    private void OnSeekCompleted(object? sender, EventArgs e)
    {
        _seeking = false;
        var total = Player.Duration.TotalSeconds;
        if (total > 0)
            Player.Seek(TimeSpan.FromSeconds(ProgressBar.Value / 100 * total));

        // 松手后重新开始 3s 倒计时
        RestartControlsHideTimer();
    }

    // ════════════════ 控制层自动隐藏 ════════════════

    /// <summary>显示控制层并取消倒计时（鼠标仍在播放框内时保持常亮）</summary>
    private void ShowControls()
    {
        _controlsHideTimer?.Stop();
        SetControlsVisible(true);
    }

    /// <summary>从当前时刻起重新计时 3s（鼠标移出播放框 / 手指离开 / 拖动结束时调用）</summary>
    private void RestartControlsHideTimer()
    {
        _controlsHideTimer?.Stop();
        if (CanAutoHideControls()) _controlsHideTimer?.Start();
    }

    /// <summary>仅"播放中且非拖动"才自动隐藏——暂停/拖动时常驻，否则进度条与时长都看不见</summary>
    private bool CanAutoHideControls() => _playing && !_seeking;

    private void HideControlsIfIdle()
    {
        if (CanAutoHideControls()) SetControlsVisible(false);
    }

    /// <summary>
    /// 控制层显隐：只切换子元素，ControlsOverlay 本身常驻不隐藏。
    /// 该层承载指针手势与画面点击，若整体隐藏则手势随之失效，控件层将无法被再次唤出。
    /// </summary>
    private void SetControlsVisible(bool on)
    {
        ControlBar.IsVisible = on;
        CenterPlayButton.IsVisible = on;
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
