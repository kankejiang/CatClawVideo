using CatClawVideo.Core.Live;
using CatClawVideo.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 直播间（TVBox <c>LivePlayActivity</c> 的 MAUI 移植）。
///
/// <para>核心行为对齐 TVBox：</para>
/// <list type="bullet">
/// <item>左侧两列频道抽屉（分组 / 频道，分组名 <c>组名_密码</c> 需弹窗验证）</item>
/// <item>换台复用同一播放器；失败按「换线路 → 换台」自动回退（回退链 = TVBox 的
/// <c>mConnectTimeoutChangeSourceRun</c>）</item>
/// <item>底部 EPG 条（当前 / 下一节目，来自 <see cref="EpgService"/>）</item>
/// <item>设置面板：线路选择 / 超时换源 / 多源切换（lives）/ 重新加载</item>
/// <item>记住上次频道（<c>LIVE_CHANNEL</c> 语义）</item>
/// </list>
/// </summary>
public partial class LivePage : ContentPage
{
    private readonly LiveSourceService _source;
    private readonly EpgService _epg;

    private List<LiveChannelGroup> _groups = new();
    private int _currentGroup = -1;
    private int _currentChannel = -1;
    private readonly HashSet<int> _confirmedGroups = new();

    private string _loadedApi = "";
    private int _sourceTryCount;      // 当前频道已尝试的线路数（对齐 currentLiveChangeSourceTimes）
    private int _autoChannelHops;     // 连续自动换台次数（防死循环，TVBox 无此保护）
    private int _playToken;           // 换台代数：使旧看门狗/回调失效
    private int _toastToken;
    private int _barsToken;
    private int _epgToken;

    public LivePage(LiveSourceService source, EpgService epg)
    {
        InitializeComponent();
        _source = source;
        _epg = epg;

        BtnChannelList.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(ToggleDrawer) });
        BtnSettingsPanel.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(ToggleSettings) });
        BtnClose.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(OnCloseTapped) });

        Player.StateChanged += (_, _) => MainThread.BeginInvokeOnMainThread(OnPlayerStateChanged);
        Player.MediaFailed += (_, _) => MainThread.BeginInvokeOnMainThread(() =>
        {
            ShowToast("播放失败，尝试其他线路…", 1600);
            HandlePlayFailure();
        });
    }

    private LiveChannelItem? CurrentItem =>
        _currentGroup >= 0 && _currentGroup < _groups.Count
        && _currentChannel >= 0 && _currentChannel < _groups[_currentGroup].Channels.Count
            ? _groups[_currentGroup].Channels[_currentChannel]
            : null;

    // ═══════════════════ 生命周期 ═══════════════════

    protected override void OnAppearing()
    {
        base.OnAppearing();
        StartClock();
        StartEpgTimer();
        _ = EnsureLoadedAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        StopTimers();
        try
        {
            Player.Stop();
            Player.Source = null;
            Player.Headers = null;
        }
        catch
        {
        }
    }

    protected override bool OnBackButtonPressed()
    {
        if (Drawer.IsVisible || SettingsPanel.IsVisible)
        {
            CloseOverlays();
            return true;
        }
        _ = Shell.Current.GoToAsync("..");
        return true;
    }

    private async Task EnsureLoadedAsync()
    {
        if (_source.Prefs.ApiUrl.Length == 0 && !LiveSourceService.HasCapturedSubscription)
        {
            ShowEmpty("未配置直播源。点击「配置直播源」，支持 TXT / M3U / JSON，以及含 lives 的 TVBox 订阅。");
            return;
        }
        if (_loadedApi != _source.Prefs.ApiUrl || _groups.Count == 0)
        {
            await LoadSourceAsync();
        }
        else if (CurrentItem != null)
        {
            ReplayCurrent(changeSource: true);
        }
    }

    // ═══════════════════ 加载与结果落地 ═══════════════════

    private async Task LoadSourceAsync()
    {
        ShowToast("加载直播源…", 1500);
        var result = await _source.LoadAsync();
        _loadedApi = _source.Prefs.ApiUrl;
        if (result.Groups.Count == 0)
        {
            ShowEmpty(string.IsNullOrEmpty(result.Error) ? "未解析出任何频道" : result.Error);
            return;
        }
        HideEmpty();
        ApplyResult(result, useLastChannel: true);
    }

    private void ApplyResult(LiveLoadResult result, bool useLastChannel)
    {
        _groups = result.Groups;
        _confirmedGroups.Clear();
        _currentGroup = -1;
        _currentChannel = -1;
        BuildGroupList();
        BuildSettingsPanel();
        LblSourceTag.Text = "当前源：" + _source.CurrentTag;

        var start = (useLastChannel ? FindChannelByName(_source.Prefs.LastChannelName) : null)
                    ?? FindChannelByName("CCTV1")
                    ?? FirstPlayable();
        if (start is null)
        {
            ShowEmpty("直播源里没有可播放的频道");
            return;
        }
        PlayChannel(start.Value.Group, start.Value.Channel, false);
    }

    private (int Group, int Channel)? FindChannelByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        for (var g = 0; g < _groups.Count; g++)
        {
            var channels = _groups[g].Channels;
            for (var c = 0; c < channels.Count; c++)
            {
                if (channels[c].ChannelName == name) return (g, c);
            }
        }
        return null;
    }

    private (int Group, int Channel)? FirstPlayable()
    {
        for (var g = 0; g < _groups.Count; g++)
        {
            if (_groups[g].NeedsPassword || _groups[g].Channels.Count == 0) continue;
            return (g, 0);
        }
        return null;
    }

    // ═══════════════════ 播放（换台 / 换线路 统一入口） ═══════════════════

    private void PlayChannel(int groupIndex, int channelIndex, bool changeSource)
    {
        if (groupIndex < 0 || groupIndex >= _groups.Count) return;
        var group = _groups[groupIndex];
        if (channelIndex < 0 || channelIndex >= group.Channels.Count) return;
        if (!changeSource && groupIndex == _currentGroup && channelIndex == _currentChannel && Player.Source != null) return;

        if (!changeSource)
        {
            _currentGroup = groupIndex;
            _currentChannel = channelIndex;
            _sourceTryCount = 0;
            _autoChannelHops = 0;
            _source.Prefs.LastChannelName = group.Channels[channelIndex].ChannelName;
            _source.Save();
        }

        var item = group.Channels[channelIndex];
        var url = item.GetUrl();
        if (url.Length == 0)
        {
            HandlePlayFailure();
            return;
        }

        _playToken++;
        var headers = new Dictionary<string, string>();
        foreach (var kv in _source.Prefs.WebHeaders) headers[kv.Key] = kv.Value;
        foreach (var kv in item.GetHeaders()) headers[kv.Key] = kv.Value;
        try
        {
            Player.Headers = headers.Count > 0 ? headers : null;
            if (Player.Source == url) Player.Source = null;   // 同址重播需强制重新 ApplySource
            Player.Source = url;
        }
        catch (Exception ex)
        {
            ShowToast("播放器错误：" + ex.Message);
        }

        UpdateChromeTexts();
        BuildGroupList();
        BuildChannelList();
        BuildSettingsPanel();
        ArmWatchdog();
        _ = RefreshEpgAsync();
        ShowBars();
    }

    private void ReplayCurrent(bool changeSource)
    {
        if (_currentGroup >= 0 && _currentChannel >= 0)
            PlayChannel(_currentGroup, _currentChannel, changeSource);
    }

    /// <summary>连接看门狗：超时未进入播放态 → 触发回退（对齐 TVBox 超时换源）。</summary>
    private void ArmWatchdog()
    {
        var token = _playToken;
        var seconds = Math.Clamp(_source.Prefs.TimeoutSeconds, 5, 30);
        Dispatcher.StartTimer(TimeSpan.FromSeconds(seconds), () =>
        {
            if (token != _playToken) return false;
            if (Player.CurrentState is VideoPlayerState.Playing or VideoPlayerState.Paused) return false;
            HandlePlayFailure();
            return false;
        });
    }

    /// <summary>
    /// 回退链（TVBox <c>mConnectTimeoutChangeSourceRun</c> 移植）：
    /// 先换下一条线路；全部线路试完 → 换下一个频道。
    /// </summary>
    private void HandlePlayFailure()
    {
        var item = CurrentItem;
        if (item == null)
        {
            SwitchChannel(1, auto: true);
            return;
        }

        _sourceTryCount++;
        if (_sourceTryCount >= Math.Max(1, item.SourceNum))
        {
            _sourceTryCount = 0;
            _autoChannelHops++;
            if (_autoChannelHops > 8)
            {
                _autoChannelHops = 0;
                ShowToast("多个频道均无法播放，已停止自动换台", 3000);
                return;
            }
            SwitchChannel(1, auto: true);
            return;
        }

        item.NextSource();
        ShowToast($"换线路：{item.GetSourceName()}（{item.SourceIndex + 1}/{item.SourceNum}）", 1600);
        ReplayCurrent(changeSource: true);
    }

    private void OnPlayerStateChanged()
    {
        if (Player.CurrentState == VideoPlayerState.Playing)
        {
            _sourceTryCount = 0;
            _autoChannelHops = 0;
        }
        else if (Player.CurrentState == VideoPlayerState.Failed)
        {
            HandlePlayFailure();
        }
    }

    /// <summary>换台（自动模式跳过加密分组；跨组遵循设置）。</summary>
    private void SwitchChannel(int direction, bool auto)
    {
        if (_groups.Count == 0) return;
        var groupIndex = _currentGroup < 0 ? 0 : _currentGroup;
        if (_currentChannel < 0) { PlayChannel(groupIndex, 0, false); return; }

        var group = _groups[groupIndex];
        var next = _currentChannel + direction;
        if (next >= 0 && next < group.Channels.Count)
        {
            PlayChannel(groupIndex, next, false);
            return;
        }
        if (!auto && !_source.Prefs.CrossGroup)
        {
            ShowToast("已到边界");
            return;
        }

        for (var step = 0; step < _groups.Count; step++)
        {
            groupIndex += direction;
            if (groupIndex < 0) groupIndex = _groups.Count - 1;
            if (groupIndex >= _groups.Count) groupIndex = 0;
            var candidate = _groups[groupIndex];
            if (candidate.Channels.Count == 0) continue;
            if (candidate.NeedsPassword && !_confirmedGroups.Contains(groupIndex)) continue;
            var target = direction > 0 ? 0 : candidate.Channels.Count - 1;
            PlayChannel(groupIndex, target, false);
            return;
        }
        ShowToast("没有可播放的频道");
    }

    // ═══════════════════ 频道抽屉 ═══════════════════

    private void ToggleDrawer()
    {
        if (Drawer.IsVisible) { CloseOverlays(); return; }
        SettingsPanel.IsVisible = false;
        BuildGroupList();
        BuildChannelList();
        Scrim.IsVisible = true;
        Drawer.IsVisible = true;
    }

    private void ToggleSettings()
    {
        if (SettingsPanel.IsVisible) { CloseOverlays(); return; }
        Drawer.IsVisible = false;
        BuildSettingsPanel();
        Scrim.IsVisible = true;
        SettingsPanel.IsVisible = true;
    }

    private void CloseOverlays()
    {
        Drawer.IsVisible = false;
        SettingsPanel.IsVisible = false;
        Scrim.IsVisible = false;
        ScheduleHideBars();
    }

    private void OnScrimTapped(object? sender, TappedEventArgs e) => CloseOverlays();

    private void BuildGroupList()
    {
        GroupList.Children.Clear();
        for (var i = 0; i < _groups.Count; i++)
        {
            var index = i;
            var group = _groups[i];
            var label = (group.NeedsPassword ? "🔒 " : "") + group.GroupName;
            GroupList.Children.Add(MakeRow(label, i == _currentGroup, () => OnGroupTapped(index)));
        }
    }

    private async void OnGroupTapped(int index)
    {
        var group = _groups[index];
        if (group.NeedsPassword && !_confirmedGroups.Contains(index))
        {
            var password = await DisplayPromptAsync("频道组加密", $"请输入「{group.GroupName}」的密码", "进入", "取消");
            if (password == null) return;
            if (password != group.GroupPassword)
            {
                ShowToast("密码错误");
                return;
            }
            _confirmedGroups.Add(index);
        }
        _currentGroup = index;
        BuildGroupList();
        BuildChannelList();

        // 触摸端交互：选中分组后直接播该组第一个频道（当前频道属于该组则保持）
        if (CurrentItem == null || _currentGroup != index || _currentChannel >= group.Channels.Count)
        {
            if (group.Channels.Count > 0) PlayChannel(index, 0, false);
        }
        else if (CurrentItem != null && group.Channels.Count > 0 && _groups[index].Channels.Contains(CurrentItem) == false)
        {
            PlayChannel(index, 0, false);
        }
    }

    private void BuildChannelList()
    {
        ChannelList.Children.Clear();
        if (_currentGroup < 0 || _currentGroup >= _groups.Count) return;
        var channels = _groups[_currentGroup].Channels;
        for (var i = 0; i < channels.Count; i++)
        {
            var index = i;
            var channel = channels[i];
            var active = i == _currentChannel;
            ChannelList.Children.Add(MakeRow($"{channel.ChannelNum}  {channel.ChannelName}", active, () =>
            {
                PlayChannel(_currentGroup, index, false);
                CloseOverlays();
            }));
        }
    }

    // ═══════════════════ 设置面板 ═══════════════════

    private void BuildSettingsPanel()
    {
        // 线路选择
        LineList.Children.Clear();
        if (CurrentItem is { } item)
        {
            for (var i = 0; i < item.SourceNames.Count; i++)
            {
                var index = i;
                LineList.Children.Add(MakeChip(item.SourceNames[i], i == item.SourceIndex, () =>
                {
                    item.SourceIndex = index;
                    _sourceTryCount = 0;
                    ReplayCurrent(changeSource: true);
                    BuildSettingsPanel();
                }));
            }
        }

        // 超时换源
        TimeoutList.Children.Clear();
        foreach (var seconds in new[] { 5, 10, 15, 20, 25, 30 })
        {
            var value = seconds;
            TimeoutList.Children.Add(MakeChip($"{value}s", _source.Prefs.TimeoutSeconds == value, () =>
            {
                _source.Prefs.TimeoutSeconds = value;
                _source.Save();
                BuildSettingsPanel();
            }));
        }

        // 多源切换
        SourceList.Children.Clear();
        var lives = _source.Current?.Lives ?? new List<LiveLivesEntry>();
        if (lives.Count == 0)
        {
            SourceList.Children.Add(MakeChip("（当前源为单源）", false, () => { }));
        }
        else
        {
            for (var i = 0; i < lives.Count; i++)
            {
                var index = i;
                SourceList.Children.Add(MakeChip(lives[i].Name, i == (_source.Current?.LivesIndex ?? 0), () => SwitchLiveSource(index)));
            }
        }
    }

    private async void SwitchLiveSource(int index)
    {
        ShowToast("切换直播源…", 1500);
        var result = await _source.LoadAsync(_source.Prefs.ApiUrl, index);
        if (result.Groups.Count == 0)
        {
            ShowToast(string.IsNullOrEmpty(result.Error) ? "切换失败" : result.Error, 3000);
            return;
        }
        HideEmpty();
        ApplyResult(result, useLastChannel: true);
    }

    // ═══════════════════ 控制条 / 提示 ═══════════════════

    private void OnSurfaceTapped(object? sender, TappedEventArgs e)
    {
        if (Drawer.IsVisible || SettingsPanel.IsVisible)
        {
            CloseOverlays();
            return;
        }
        if (TopChrome.IsVisible) SetBars(false);
        else ShowBars();
    }

    private void ShowBars()
    {
        SetBars(true);
        ScheduleHideBars();
    }

    private void ScheduleHideBars()
    {
        _barsToken++;
        var token = _barsToken;
        Dispatcher.StartTimer(TimeSpan.FromSeconds(6), () =>
        {
            if (token != _barsToken) return false;
            if (Drawer.IsVisible || SettingsPanel.IsVisible)
            {
                ScheduleHideBars();
                return false;
            }
            SetBars(false);
            return false;
        });
    }

    private void SetBars(bool visible)
    {
        TopChrome.IsVisible = visible;
        BottomBar.IsVisible = visible;
        BtnPrev.IsVisible = visible && _groups.Count > 0;
        BtnNext.IsVisible = visible && _groups.Count > 0;
    }

    private void ShowEmpty(string message)
    {
        EmptyDetail.Text = message;
        EmptyState.IsVisible = true;
        SetBars(false);
    }

    private void HideEmpty() => EmptyState.IsVisible = false;

    private void ShowToast(string text, int ms = 2500)
    {
        LblToast.Text = text;
        ToastHost.IsVisible = true;
        _toastToken++;
        var token = _toastToken;
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(ms), () =>
        {
            if (token != _toastToken) return false;
            ToastHost.IsVisible = false;
            return false;
        });
    }

    private void UpdateChromeTexts()
    {
        var item = CurrentItem;
        if (item == null) return;
        LblChannelInfo.Text = $"{item.ChannelNum}  {item.ChannelName}";
        LblBottomTitle.Text = $"{item.ChannelNum}  {item.ChannelName}　·　{item.GetSourceName()}（{item.SourceIndex + 1}/{Math.Max(1, item.SourceNum)}）";
    }

    // ═══════════════════ EPG / 时钟 ═══════════════════

    private async Task RefreshEpgAsync()
    {
        var item = CurrentItem;
        if (item == null) return;
        var token = ++_epgToken;
        LblEpgNow.Text = "节目单加载中…";
        LblEpgNext.Text = "";
        try
        {
            // EPG 地址优先级：用户手填（直播源页）> 源内声明（x-tvg-url / lives 条目 epg）> 内置默认
            var epgUrl = _source.Prefs.EpgUrl.Length > 0 ? _source.Prefs.EpgUrl : _source.Current?.EpgUrl;
            var (now, next) = await _epg.GetNowNextAsync(item.ChannelName, epgUrl);
            if (token != _epgToken) return;
            LblEpgNow.Text = now != null ? $"正在播出　{now.Title}　{now.StartText}-{now.EndText}" : "暂无节目信息";
            LblEpgNext.Text = next != null ? $"接下来　{next.Title}　{next.StartText}-{next.EndText}" : "";
        }
        catch
        {
            if (token == _epgToken) LblEpgNow.Text = "节目单获取失败";
        }
    }

    private void StartClock()
    {
        if (!_source.Prefs.ShowTime) { LblClock.IsVisible = false; return; }
        LblClock.IsVisible = true;
        _clockTicks = 0;
        Dispatcher.StartTimer(TimeSpan.FromSeconds(1), () =>
        {
            if (!IsLoaded || Window == null) return false;   // 页面已离开
            _clockTicks++;
            if (_clockTicks % 30 == 1 || _clockTicks < 3)
                LblClock.Text = DateTime.Now.ToString("HH:mm");
            return true;
        });
    }

    private int _clockTicks;

    private void StartEpgTimer()
    {
        Dispatcher.StartTimer(TimeSpan.FromSeconds(60), () =>
        {
            if (!IsLoaded || Window == null) return false;
            _ = RefreshEpgAsync();
            return true;
        });
    }

    private void StopTimers()
    {
        _playToken++;   // 使看门狗失效
        _barsToken++;
        _toastToken++;
        _epgToken++;
    }

    // ═══════════════════ 事件 ═══════════════════

    private void OnPrevTapped(object? sender, TappedEventArgs e) => SwitchChannel(-1, auto: false);

    private void OnNextTapped(object? sender, TappedEventArgs e) => SwitchChannel(1, auto: false);

    private void OnBackTapped(object? sender, TappedEventArgs e) => _ = Shell.Current.GoToAsync("..");

    private void OnCloseTapped()
    {
        if (Drawer.IsVisible || SettingsPanel.IsVisible) { CloseOverlays(); return; }
        _ = Shell.Current.GoToAsync("..");
    }

    private void OnOpenConfigTapped(object? sender, TappedEventArgs e) => _ = Shell.Current.GoToAsync("livesource");

    private async void OnReloadSourceTapped(object? sender, TappedEventArgs e)
    {
        CloseOverlays();
        await LoadSourceAsync();
    }

    // ═══════════════════ 控件工厂 ═══════════════════

    private static Color Accent =>
        Application.Current?.Resources.TryGetValue("PrimaryColor", out var c) == true && c is Color color
            ? color
            : Color.FromArgb("#F97316");

    private static Border MakeRow(string text, bool active, Action onTap)
    {
        var label = new Label
        {
            Text = text,
            FontSize = 13,
            TextColor = active ? Colors.White : Color.FromArgb("#D9FFFFFF"),
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1,
        };
        var border = new Border
        {
            BackgroundColor = active ? Accent : Color.FromArgb("#1FFFFFFF"),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(10) },
            Padding = new Thickness(10, 8),
            Content = label,
        };
        border.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(onTap) });
        return border;
    }

    private static Border MakeChip(string text, bool active, Action onTap)
    {
        var label = new Label
        {
            Text = text,
            FontSize = 12.5,
            TextColor = active ? Colors.White : Color.FromArgb("#D9FFFFFF"),
        };
        var border = new Border
        {
            BackgroundColor = active ? Accent : Color.FromArgb("#26FFFFFF"),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(14) },
            Padding = new Thickness(12, 6),
            Margin = new Thickness(0, 0, 8, 8),
            Content = label,
        };
        border.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(onTap) });
        return border;
    }
}
