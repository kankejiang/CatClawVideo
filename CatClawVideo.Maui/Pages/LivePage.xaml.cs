using CatClawVideo.Core.Live;
using CatClawVideo.Maui.Controls;
using CatClawVideo.Maui.Services;
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
public partial class LivePage : ContentPage, IRemoteKeyHandler
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
        // EPG 条可点 → 当天完整节目单（EpgService.GetProgramsAsync 此前在 UI 层零调用）
        LblEpgNow.GestureRecognizers.Add(new TapGestureRecognizer
        { Command = new Command(async () => await ShowEpgListAsync()) });

#if WINDOWS
        // Windows 系统标题栏按钮（最小化/最大化/关闭）绘制在内容之上、约占顶部 32px
        // （同 WatchPage.NormalContentPadding 的约定）：全屏页的顶部 chips 与设置面板要避开，
        // 否则 频道/设置/✕ 和系统按钮重合（2026-09-25 用户截图）。
        TopChrome.Margin = new Thickness(0, 36, 0, 0);
        SettingsList.Padding = new Thickness(18, 48, 18, 20);
#elif ANDROID
        // Edge-to-Edge：全屏页从 y=0 起绘，顶部 chips 避开状态栏（同 WatchPage.ApplyTopBarInset）
        TopChrome.Margin = new Thickness(0, SafeAreaHelper.TopInset, 0, 0);
#endif

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
        // 直播间是整屏播放器：方向键=换台/切组，数字键=按频道号换台（对位 TVBox LivePlayActivity）
        RemoteKeyRouter.Push(this);
        StartClock();
        StartEpgTimer();
        _ = EnsureLoadedAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        RemoteKeyRouter.Pop(this);
        // 直播同理：切后台且开了后台播放就别停（听电台的人锁屏继续听是常见用法）
        if (Services.BgPlayPrefs.ShouldKeepPlaying()) return;
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

    // ═══════════════════ 遥控器导航（对位 TVBox LivePlayActivity 的按键处理）═══════════════════

    /// <summary>直播页的「焦点」就是正在播的那一行（BuildChannelList 已按 _current* 高亮），不需要额外高亮层。</summary>
    public void FocusContent() { }

    /// <summary>焦点被外层抢走时丢掉半截频道号，免得回头补一个数字把台跳走。</summary>
    public void BlurContent() => CommitDigits(cancel: true);

    public bool Handle(RemoteKey key)
    {
        if (_groups.Count == 0) return false;

        var digit = RemoteKeyDigits.Of(key);
        if (digit >= 0) return OnDigitKey(digit);

        switch (key)
        {
            case RemoteKey.Up:
            case RemoteKey.Down:
                MoveChannel(key == RemoteKey.Down ? 1 : -1);
                return true;
            case RemoteKey.Left:
            case RemoteKey.Right:
                MoveGroup(key == RemoteKey.Right ? 1 : -1);
                return true;
            case RemoteKey.Enter:
                ToggleDrawer();
                return true;
            case RemoteKey.Back:
                // 抽屉/面板开着时先收起来；否则不消费，交给 OnBackButtonPressed / 系统返回退页
                if (Drawer.IsVisible || SettingsPanel.IsVisible) { CloseOverlays(); return true; }
                return false;
        }
        return false;
    }

    private string _digitInput = "";
    private IDispatcherTimer? _digitTimer;

    /// <summary>数字键逐位累积频道号，2s 内没有新增就提交（TVBox 的 handler_postNumberDelayed 语义）。</summary>
    private bool OnDigitKey(int d)
    {
        _digitInput += d;
        ShowToast("▷ " + _digitInput, 2600);
        ShowBars();

        _digitTimer ??= Dispatcher.CreateTimer();
        _digitTimer.Interval = TimeSpan.FromSeconds(2);
        _digitTimer.IsRepeating = false;
        _digitTimer.Tick -= OnDigitTimeout;
        _digitTimer.Tick += OnDigitTimeout;
        _digitTimer.Stop();
        _digitTimer.Start();
        return true;
    }

    private void OnDigitTimeout(object? sender, EventArgs e) => CommitDigits();

    private void CommitDigits(bool cancel = false)
    {
        _digitTimer?.Stop();
        if (_digitInput.Length == 0) return;
        var text = _digitInput;
        _digitInput = "";
        if (cancel) return;

        var want = int.TryParse(text, out var n) ? n : 0;
        if (want <= 0)
        {
            ShowToast("没有频道号 " + text, 1600);
            return;
        }

        // 频道号是整份直播单里编的序（LiveParser 按出现顺序编号），所以要跨组找
        for (var g = 0; g < _groups.Count; g++)
        {
            var channels = _groups[g].Channels;
            for (var c = 0; c < channels.Count; c++)
            {
                if (channels[c].ChannelNum != want) continue;
                if (_groups[g].NeedsPassword && !_confirmedGroups.Contains(g))
                {
                    ShowToast("「" + _groups[g].GroupName + "」需要密码，请先在频道列表里进入", 2600);
                    return;
                }
                PlayChannel(g, c, changeSource: false);
                return;
            }
        }
        ShowToast("没有频道号 " + want, 1800);
    }

    /// <summary>上下换台（step=+1 向下 / -1 向上）。ReverseKeys 反转方向，CrossGroup 决定到组边界时是否续到邻组。</summary>
    private void MoveChannel(int step)
    {
        if (_source.Prefs.ReverseKeys) step = -step;
        var g = _currentGroup < 0 ? 0 : _currentGroup;

        // 一个台都还没选中过：下=本组第一个，上=本组最后一个
        if (_currentChannel < 0)
        {
            var channels = _groups[g].Channels;
            if (channels.Count > 0) PlayChannel(g, step > 0 ? 0 : channels.Count - 1, changeSource: false);
            return;
        }

        var count = _groups[g].Channels.Count;
        var next = _currentChannel + step;
        if (next >= 0 && next < count)
        {
            PlayChannel(g, next, changeSource: false);
            return;
        }

        if (!_source.Prefs.CrossGroup)
        {
            ShowToast(next < 0 ? "已是本组第一个台" : "已是本组最后一个台", 1500);
            return;
        }
        var (ng, nc) = NeighbourSlot(g, next < 0 ? -1 : count);
        if (ng < 0)
        {
            ShowToast(next < 0 ? "已经是第一个台" : "已经是最后一个台", 1500);
            return;
        }
        PlayChannel(ng, nc, changeSource: false);
    }

    /// <summary>越界后往邻组找第一个可播位（wanted&lt;0=向前找组尾，&gt;=count=向后找组头）；走到头返回 (-1,-1)。</summary>
    private (int Group, int Channel) NeighbourSlot(int groupIndex, int wanted)
    {
        for (var hop = 1; hop <= _groups.Count; hop++)
        {
            var gi = wanted < 0 ? groupIndex - hop : groupIndex + hop;
            if (gi < 0 || gi >= _groups.Count) return (-1, -1);
            var n = _groups[gi].Channels.Count;
            if (n == 0) continue;
            return (gi, wanted < 0 ? n - 1 : 0);
        }
        return (-1, -1);
    }

    /// <summary>左右切组：落到该组第一个台；加密组或空组只打开列表，等用户输密码。</summary>
    private void MoveGroup(int step)
    {
        var g = (_currentGroup < 0 ? 0 : _currentGroup) + step;
        if (g < 0 || g >= _groups.Count)
        {
            ShowToast(g < 0 ? "已经是第一组" : "已经是最后一组", 1500);
            return;
        }
        if (_groups[g].Channels.Count == 0 || (_groups[g].NeedsPassword && !_confirmedGroups.Contains(g)))
        {
            OnGroupTapped(g);
            return;
        }
        PlayChannel(g, 0, changeSource: false);
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
            // 带防盗链头的直播 m3u8 改走内置 go=live 代理：首个列表直连能过，列表里的 .ts / 密钥
            // 裸连就 403 了（表现为「切台后播几秒黑屏」）。走代理后每一跳都继承同一套头。
            var routed = CatClawVideo.Core.Services.GoLiveProxy
                .WrapForLive(url, headers, CatClawVideo.Core.Services.SpiderProxyServer.ActivePort);
            if (routed is not null)
            {
                url = routed;
                Player.Headers = null;   // 头已编码进地址，播放器不用再带
                DiagLog.Write($"[live] 走 go=live 代理（继承 {headers.Count} 个头）");
            }
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

        // 遥控与显示偏好（每一项都真的被读到，不放装饰性开关）
        PrefList.Children.Clear();
        PrefList.Children.Add(MakeChip("换台反转", _source.Prefs.ReverseKeys, () =>
        {
            _source.Prefs.ReverseKeys = !_source.Prefs.ReverseKeys;
            _source.Save();
            BuildSettingsPanel();
        }));
        PrefList.Children.Add(MakeChip("跨组换台", _source.Prefs.CrossGroup, () =>
        {
            _source.Prefs.CrossGroup = !_source.Prefs.CrossGroup;
            _source.Save();
            BuildSettingsPanel();
        }));
        PrefList.Children.Add(MakeChip("显示时钟", _source.Prefs.ShowTime, () =>
        {
            _source.Prefs.ShowTime = !_source.Prefs.ShowTime;
            _source.Save();
            LblClock.IsVisible = _source.Prefs.ShowTime;
            BuildSettingsPanel();
        }));

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
        // 空态下这层全屏手势必须让路：它只负责「点画面切控制条」，而此刻既没有在播的内容，
        // 它又会盖在空态的两个按钮之前参与命中测试，结果是「配置直播源 / 返回」点了没反应。
        TapLayer.InputTransparent = true;
        SetBars(false);
    }

    private void HideEmpty()
    {
        EmptyState.IsVisible = false;
        TapLayer.InputTransparent = false;
    }

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

    /// <summary>
    /// 当天完整节目单（对位 TVBox 直播右侧的 <c>lv_epg</c> 列表）。
    /// <para>TVBox 是遥控器形态所以常驻一列；这里是触屏/桌面，改成点 EPG 条弹一层，
    /// 不占屏幕。<c>▶</c> 标出正在播的那条，与 <c>GetNowNextAsync</c> 用同一套时间判定。</para>
    /// </summary>
    private async Task ShowEpgListAsync()
    {
        var item = CurrentChannelItem();
        if (item is null) return;
        var epgUrl = _source.Prefs.EpgUrl.Length > 0 ? _source.Prefs.EpgUrl : _source.Current?.EpgUrl;
        List<LiveProgram> programs;
        try
        {
            programs = await _epg.GetProgramsAsync(item.ChannelName, epgUrl);
        }
        catch { programs = []; }
        if (programs.Count == 0)
        {
            ShowToast("该频道暂无节目单");
            return;
        }
        var now = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8));
        var shown = programs.Take(80).ToList();
        var lines = shown
            .Select(p => $"{p.StartText}-{p.EndText}  {(p.IsNow(now) ? "▶ " : "")}{p.Title}")
            .ToArray();
        var picked = await DisplayActionSheetAsync($"{item.ChannelName} · 今日节目单（点一条可回看）",
            "关闭", null, lines);
        if (picked is null || picked == "关闭") return;
        var idx = Array.IndexOf(lines, picked);
        if (idx < 0 || idx >= shown.Count) return;
        PlayCatchupAsync(item, shown[idx]);
    }

    /// <summary>
    /// 回看某个节目（对位 TVBox 点 EPG 条目 → <c>buildCatchupUrl</c> → 播放）。
    /// <para>地址由 <see cref="LiveCatchup"/> 按「频道级 catchup → 订阅级 catchup → /PLTV/ 兜底」
    /// 三级构造；构造不出来就明确提示，不能拿空地址去播（表现为「点了没反应」）。</para>
    /// </summary>
    private void PlayCatchupAsync(LiveChannelItem item, LiveProgram p)
    {
        var subCfg = null as CatClawVideo.Core.Live.LiveCatchup.Config;
        if (_source.Current is { } res && res.LivesIndex >= 0 && res.LivesIndex < res.Lives.Count)
            subCfg = res.Lives[res.LivesIndex].Catchup;
        var cfg = CatClawVideo.Core.Live.LiveCatchup.Current(item.CatchupConfig, subCfg);

        var live = item.GetUrl();
        if (!CatClawVideo.Core.Live.LiveCatchup.CanCatchup(live, cfg))
        {
            ShowToast("该频道不支持回看");
            return;
        }
        var url = CatClawVideo.Core.Live.LiveCatchup.Build(live, cfg, p.Start, p.End);
        if (url.Length == 0)
        {
            ShowToast("回看地址构造失败（模板或时间不完整）");
            return;
        }

        var headers = new Dictionary<string, string>();
        foreach (var kv in _source.Prefs.WebHeaders) headers[kv.Key] = kv.Value;
        foreach (var kv in item.GetHeaders()) headers[kv.Key] = kv.Value;

        _playToken++;
        try
        {
            Player.Headers = headers.Count > 0 ? headers : null;
            if (Player.Source == url) Player.Source = null;
            Player.Source = url;
            ShowToast($"回看　{p.Title}", 2000);
        }
        catch (Exception ex)
        {
            ShowToast("回看播放失败：" + ex.Message);
        }
    }

    /// <summary>当前正在播（或焦点所在）的频道项；越界返回 null。</summary>
    LiveChannelItem? CurrentChannelItem()
    {
        if (_currentGroup < 0 || _currentGroup >= _groups.Count) return null;
        var chans = _groups[_currentGroup].Channels;
        return _currentChannel >= 0 && _currentChannel < chans.Count ? chans[_currentChannel] : null;
    }

    private void StartClock()
    {
        _clockTicks = 0;
        Dispatcher.StartTimer(TimeSpan.FromSeconds(1), () =>
        {
            if (!IsLoaded || Window == null) return false;   // 页面已离开
            // 开关**每 tick 重读**：在设置面板里刚拨动，不该等重进直播间才生效
            LblClock.IsVisible = _source.Prefs.ShowTime;
            if (!LblClock.IsVisible) return true;
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
