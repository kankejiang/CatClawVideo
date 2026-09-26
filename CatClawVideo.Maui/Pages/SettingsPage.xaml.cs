using CatClawVideo.Core.Interfaces;
using CatClawVideo.Maui.Services;
using CatClawVideo.Maui.ViewModels;
using Microsoft.Maui.Controls.Shapes;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 设置页：左导航 + 内容区（横屏通用，遥控器优先）。
///
/// <para>布局与焦点导航均在代码后置构建，原因见 <see cref="FocusableRow"/>：
/// 需要统一的显式焦点态与键盘/遥控路由，用 XAML 表达反而更绕。</para>
///
/// <para>按键语义（<see cref="RemoteKey"/>）：↑↓ 栏内移动；←→ 调值或切换栏；
/// OK 激活；Back 由内容区 → 左导航 → 交还主壳层（退出设置）。</para>
/// </summary>
public partial class SettingsPage : ContentView, ITabView, IRemoteKeyHandler
{
    private readonly SettingsViewModel _vm;
    private readonly IThemeService _theme;
    private readonly CatClawVideo.Data.VideoDatabase _db;

    // ─────────── 焦点分层 ───────────
    private const int LayerTopNav = 0;
    private const int LayerRail = 1;
    private const int LayerContent = 2;
    private int _layer = LayerRail;

    /// <summary>当前选中的分类索引（与侧栏高亮一致）。</summary>
    private int _sectionIndex = -1;

    /// <summary>左导航项（与 <see cref="Sections"/> 一一对应）。</summary>
    private readonly List<FocusableRow> _nav = new();

    /// <summary>当前分类下的可聚焦内容行。</summary>
    private List<FocusableRow> _content = new();

    private static readonly (string Key, string Title, string Glyph)[] AllSections =
    [
        ("source", "内容源", "🧩"),
        ("play",   "播放",   "▶"),
        ("diag",   "诊断日志", "📄"),
        ("about",  "关于",   "ℹ"),
    ];

    /// <summary>当前露出的分区：未开开发者模式时藏掉「诊断日志」（对位 TVBox 连按 4 次 0 才显出调试开关）。</summary>
    private static (string Key, string Title, string Glyph)[] Sections = AllSections;

    private static bool DevMode
    {
        get { try { return Preferences.Default.Get("dev_mode", false); } catch { return false; } }
        set { try { Preferences.Default.Set("dev_mode", value); } catch { } }
    }

    private static void RefreshSections() =>
        Sections = DevMode ? AllSections : [.. AllSections.Where(x => x.Key != "diag")];

    // ─────────── 控件引用 ───────────
    private Border? _rail;
    private Grid? _main;
    private Label? _crumb;
    private Label? _pageTitle;
    private Border? _groupHost;
    private ScrollView? _contentScroll;
    private VerticalStackLayout? _contentStack;
    private StepControl? _cacheStep;
    private TogglePill? _swLog;
    private FocusableRow? _focused;
    private int _serverRowIndex = -1;

    public SettingsPage(SettingsViewModel vm, IThemeService theme, CatClawVideo.Data.VideoDatabase db)
    {
        InitializeComponent();          // 必须：创建 XAML 中的 Root 容器
        _vm = vm;
        _theme = theme;
        _db = db;
        BindingContext = _vm;

        BuildLayout();
        _theme.Applied += RefreshThemeColors;
    }

    // ═══════════════════════ 布局 ═══════════════════════

    private void BuildLayout()
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(216)),
                new ColumnDefinition(GridLength.Star),
            },
        };

        // ── 左导航 ──
        RefreshSections();
        var railStack = new VerticalStackLayout { Spacing = 4 };
        railStack.Add(new Label
        {
            Text = "设 置",
            FontSize = 12,
            FontFamily = "OpenSansSemibold",
            TextColor = ThemeColor("TextHintColor", Color.FromArgb("#868CAE")),
            Margin = new Thickness(12, 4, 12, 14),
        });
        for (int i = 0; i < Sections.Length; i++)
        {
            var s = Sections[i];
            // RailOnly：侧栏只用「图标 + 标题」，用完整 6 列会把标题宽度挤没（中文竖排）
            var row = new FocusableRow { Glyph = s.Glyph, Title = s.Title, RailOnly = true };
            int idx = i;
            row.Activated += (_, _) => { SelectSection(idx); FocusRows(); };
            _nav.Add(row);
            railStack.Add(row);
        }

        _rail = new Border
        {
            StrokeThickness = 0,
            Padding = new Thickness(12, 20, 12, 20),
            VerticalOptions = LayoutOptions.Fill,
            BackgroundColor = ThemeColor("TabBarBackgroundColor", Color.FromArgb("#CC1A1838")),
            Content = railStack,
        };
        grid.Add(_rail, 0, 0);

        // ── 内容区 ──
        _crumb = new Label
        {
            FontSize = 11.5,
            FontFamily = "OpenSansSemibold",
            TextColor = ThemeColor("TextHintColor", Color.FromArgb("#868CAE")),
        };
        _pageTitle = new Label
        {
            FontSize = 20,
            FontFamily = "OpenSansSemibold",
            Margin = new Thickness(0, 3, 0, 16),
            TextColor = ThemeColor("TextPrimaryColor", Colors.White),
        };

        _contentStack = new VerticalStackLayout { Spacing = 0 };
        _groupHost = new Border
        {
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            Stroke = ThemeBrush("DividerColor", Colors.Transparent),
            BackgroundColor = ThemeColor("CardBackgroundColor", Color.FromArgb("#332F5A")),
            Content = _contentStack,
        };

        // 内容区必须是 Auto,Auto,* 三行：中间那行放 ScrollView，只有 Grid 的 * 行
        // 才会把 ScrollView 约束到「剩余高度」—— 放进 VerticalStackLayout 的话，
        // 栈布局按无限高度测量子元素，ScrollView 会报告内容的完整高度、永远不滚动。
        _contentScroll = new ScrollView
        {
            Content = _groupHost,
            VerticalScrollBarVisibility = ScrollBarVisibility.Never,
        };

        _main = new Grid
        {
            Padding = new Thickness(30, 22, 30, 22),
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),   // 面包屑
                new RowDefinition(GridLength.Auto),   // 页标题
                new RowDefinition(GridLength.Star),   // 设置行（可滚动）
            },
        };
        _main.Add(_crumb, 0, 0);
        _main.Add(_pageTitle, 0, 1);
        _main.Add(_contentScroll, 0, 2);

        grid.Add(_main, 1, 0);   // 第 1 列 = 内容区（第 0 列是左导航）

        Root.Add(grid);
    }

    /// <summary>
    /// 自适应：按**宽高两个维度**收紧（原先只看宽度，高度不足时底部设置行被切掉且无法滚动）。
    ///
    /// <para>手机横屏（1440×3200 @ 560dpi → 逻辑 914×411dp）就是典型：宽度 914dp 判为
    /// compact，但 411dp 的高度减去顶栏（约 56dp）与标题区后，只剩不到 300dp，
    /// 放不下「缓存 / 小窗 / 比例 / 倍速 / 焦点样式」五行 —— 之前没有滚动容器，
    /// 底部的行就直接被裁掉了（2026-09-19 用户反馈「没有自适应手机布局」）。</para>
    ///
    /// <para>竖屏手机（411×914dp）走同一套：宽度窄 → 侧栏收窄，高度够就不额外收紧。</para>
    /// </summary>
    private void UpdateResponsive()
    {
        try
        {
            var w = Width > 0 ? Width : (Window?.Width ?? 0);
            var h = Height > 0 ? Height : (Window?.Height ?? 0);

            bool narrow = w > 0 && w < 960;          // 横向空间紧张
            bool shortH = h > 0 && h < 560;          // 纵向空间紧张（手机横屏）

            if (_rail is not null) _rail.WidthRequest = narrow ? 158 : 216;

            if (_main is not null)
            {
                _main.Padding = narrow || shortH
                    ? new Thickness(16, 10, 16, 10)
                    : new Thickness(30, 22, 30, 22);
            }

            if (_pageTitle is not null)
            {
                _pageTitle.FontSize = shortH ? 15 : narrow ? 16 : 20;
                _pageTitle.Margin = shortH ? new Thickness(0, 2, 0, 8) : new Thickness(0, 3, 0, 16);
            }
            if (_crumb is not null)
            {
                _crumb.FontSize = narrow ? 10.5 : 11.5;
                // 矮屏（手机横屏）藏掉面包屑：侧栏已经高亮当前分类了，这里的分类名是冗余的，
                // 而它占掉的那一行高度在 411dp 屏上很宝贵（每行设置约 60dp）
                _crumb.IsVisible = !shortH;
            }

            double density = shortH ? 0.86 : narrow ? 0.92 : 1.0;

            foreach (var n in _nav) n.ApplyDensity(density);
            foreach (var r in _content) r.ApplyDensity(density);

            // 尺寸变了，焦点行可能已被挤出可视区：重新滚一次
            EnsureFocusVisible();
        }
        catch { }
    }

    /// <summary>
    /// 把当前焦点行滚进可视区（矮屏下焦点会走到屏幕外）。
    ///
    /// <para>必须**延后一帧**再滚：内容刚重建 / 尺寸刚变化时，焦点行的边界还没测量出来
    /// （宽高为 0），此时 <c>ScrollToAsync</c> 会算出一个错误的偏移，把内容整体推上去 ——
    /// 表现为首行被页标题压住一半（2026-09-19 实测）。</para>
    /// </summary>
    private void EnsureFocusVisible()
    {
        try
        {
            if (_contentScroll is null || _focused is null) return;
            if (_layer != LayerContent) return;   // 侧栏行不在这个 ScrollView 里

            var target = _focused;
            Dispatcher.Dispatch(() =>
            {
                try
                {
                    if (target.Width <= 0) return;   // 还没测量完，等下一次
                    _ = _contentScroll.ScrollToAsync(target, ScrollToPosition.MakeVisible, animated: false);
                }
                catch { }
            });
        }
        catch { }
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        UpdateResponsive();
    }

    // ═══════════════════════ 分类内容 ═══════════════════════

    private void SelectSection(int index)
    {
        if (index < 0 || index >= Sections.Length) return;

        _content.Clear();
        _contentStack!.Clear();
        _cacheStep = null;
        _swLog = null;
        _serverRowIndex = -1;

        for (int i = 0; i < Sections.Length; i++)
            _nav[i].IsActive = i == index;

        _sectionIndex = index;
        BuildContent(Sections[index].Key);
        _crumb!.Text = Sections[index].Title;
        _pageTitle!.Text = Sections[index].Title;

        // 重建出来的行是默认密度：矮屏 / 窄屏要立刻按当前尺寸再收紧一次，
        // 否则切分类后字号会跳回原大小（2026-09-19 加滚动时一并补上）
        UpdateResponsive();
    }

    private void BuildContent(string key)
    {
        switch (key)
        {
            case "source": BuildSourceSection(); break;
            case "play": BuildPlaySection(); break;
            case "diag": BuildDiagnosticSection(); break;
            case "about": BuildAboutSection(); break;
        }
    }

    // ─────────── 内容源 ───────────

    private void BuildSourceSection()
    {
        int rows = 0;

        // 单一入口：添加订阅（粘贴地址 / 导入文件）与站点管理都在源配置页完成，不再拆成两行
        var sub = AddRow("🧩", "订阅源管理", "添加订阅（粘贴地址 / 导入本地源文件）并管理站点", ref rows);
        sub.ValueText = "进入";
        sub.ShowArrow = true;
        sub.Activated += async (_, _) => await GoAsync("sourceconfig");

        var bgRow = AddRow("▶", "后台继续播放", "切到后台或锁屏时声音不断（通知栏可控制）；分片仍走原链路", ref rows);
        var bgSwitch = new TogglePill { IsOn = Services.BgPlayPrefs.IsOn };
        bgSwitch.Toggled += (_, on) =>
        {
            Services.BgPlayPrefs.IsOn = on;
            _ = AskNotificationPermissionAsync();
            bgRow.ValueText = on ? "已开启" : "";
        };
        bgRow.Trailing = bgSwitch;
        bgRow.Activated += (_, _) => bgSwitch.Toggle();

        var dohRow = AddRow("🌐", "加密 DNS（DoH）", "解析被污染时用；流媒体分片不走它，避免拖慢起播", ref rows,
            Services.DohPrefs.Label(Services.DohPrefs.Load()));
        dohRow.Activated += async (_, _) =>
        {
            var next = Services.DohPrefs.Next(Services.DohPrefs.Load());
            Services.DohPrefs.Save(next);
            Core.Services.Doh.Selector = next;          // 立刻生效（解析器每次查询现读）
            dohRow.ValueText = Services.DohPrefs.Label(next);
            if (Core.Services.Doh.Selected is { } ep && Core.Services.Doh.Endpoints.Count > 1)
            {
                var names = Core.Services.Doh.Endpoints.Select(x => x.Name).ToArray();
                var pick = await HostPage()?.DisplayActionSheetAsync("选哪个 DoH 服务商", "取消", null, names);
                var idx = Array.IndexOf(names, pick);
                if (idx >= 0)
                {
                    Services.DohPrefs.Save(idx + 1);
                    Core.Services.Doh.Selector = idx + 1;
                    dohRow.ValueText = Services.DohPrefs.Label(idx + 1);
                }
            }
        };

        var rcRow = AddRow("⇄", "局域网遥控", "同一网络里用浏览器推片进来（带 token；不开文件浏览 / 上传 / 改配置）",
            ref rows, RemoteControlSummary());
        rcRow.ShowArrow = true;
        rcRow.Activated += async (_, _) => await ShowRemoteControlAsync();

        // 配置包（对位 TVBox BackupDialog 的 bak_*.json）：换机迁移用。放在源分区，
        // 因为它带走的第一等重要东西就是订阅列表。
        var expRow = AddRow("⇧", "导出配置包", "订阅 + 设置 + 直播/搜索源偏好打成一个 json（刻意不含站点账号口令）", ref rows);
        expRow.ShowArrow = true;
        expRow.Activated += async (_, _) => await ExportBundleAsync();

        var impRow = AddRow("⇩", "导入配置包", "合并式：只补没有的订阅、按名覆盖设置项，不清空现有数据", ref rows);
        impRow.ShowArrow = true;
        impRow.Activated += async (_, _) => await ImportBundleAsync();

        // 订阅壁纸（对位 TVBox 设置页「下载壁纸 / 还原」）：地址由订阅的 wallpaper 字段给
        var wpRow = AddRow("🖼", "设为桌面壁纸", "取订阅里的 wallpaper；会说明这张来自哪个订阅", ref rows);
        wpRow.ShowArrow = true;
        wpRow.Activated += async (_, _) => await ApplyWallpaperAsync();

        var wpBack = AddRow("↩", "还原桌面壁纸", "清回系统默认壁纸", ref rows);
        wpBack.ShowArrow = true;
        wpBack.Activated += async (_, _) => await RestoreWallpaperAsync();

        var status = AddRow("◎", "站点状态", "", ref rows);
        _serverRowIndex = rows - 1;
        RefreshSiteStatus();
    }

    private void RefreshSiteStatus()
    {
        try
        {
            if (_serverRowIndex < 0 || _serverRowIndex >= _content.Count) return;
            int total = Core.Models.SiteRegistry.Sites.Count;
            int playable = Core.Models.SiteRegistry.Playable.Count();
            _content[_serverRowIndex].Subtitle = $"共 {total} 个站点，其中可播 {playable} 个";
        }
        catch { }
    }

    // ─────────── 播放 ───────────

    private void BuildPlaySection()
    {
        int rows = 0;

        var cache = AddRow("◍", "磁力缓存上限", "边下边播的落盘数据；超限自动清最旧", ref rows);
        _cacheStep = new StepControl();
        _cacheStep.SetText(ReadCacheGb(), "GB");
        _cacheStep.Decrease += (_, _) => ShiftCache(-1);
        _cacheStep.Increase += (_, _) => ShiftCache(+1);
        cache.Trailing = _cacheStep;
        // 遥控器：焦点在整行上，← → 直接调值
        cache.DecreaseCommand = new Command(() => ShiftCache(-1));
        cache.IncreaseCommand = new Command(() => ShiftCache(+1));

        AddRow("▭", "小窗尺寸（21:9 手机横屏）", "", ref rows, "58% 宽 · 可展开全宽");

        // ↓ 下面三行原本是「只有文案、没有命令」的装饰行（parity 盘点里的自家收尾项），
        //   现在接成真开关：点一下轮一档，值存 Preferences，播放页起播时读。
        var aspectRow = AddRow("▣", "默认画面比例", "变形拉伸只在片源比例与屏幕差太多时用", ref rows,
            Services.AspectPrefs.Label(Services.AspectPrefs.Load()));
        aspectRow.Activated += (_, _) =>
        {
            var next = Services.AspectPrefs.Next(Services.AspectPrefs.Load());
            Services.AspectPrefs.Save(next);
            aspectRow.ValueText = Services.AspectPrefs.Label(next);
        };

        var speedRow = AddRow("⏩", "默认倍速", "每次起播的初始倍速，播放中仍可临时调", ref rows,
            Services.SpeedPrefs.Label(Services.SpeedPrefs.Load()));
        speedRow.Activated += (_, _) =>
        {
            var next = Services.SpeedPrefs.Next(Services.SpeedPrefs.Load());
            Services.SpeedPrefs.Save(next);
            speedRow.ValueText = Services.SpeedPrefs.Label(next);
        };

        var decRow = AddRow("⚙", "解码模式", "花屏/绿屏/无声时换一档试试（对位 TVBox 的硬解/软解切换）", ref rows,
            Services.DecoderModePrefs.Label(Services.DecoderModePrefs.Load()));
        decRow.Activated += (_, _) =>
        {
            var next = Services.DecoderModePrefs.Next(Services.DecoderModePrefs.Load());
            Services.DecoderModePrefs.Save(next);
            decRow.ValueText = Services.DecoderModePrefs.Label(next);
        };

        var histRow = AddRow("🕮", "历史条数上限", "超过就按「最早看过」删；同一部剧本来就合并成一条", ref rows,
            Services.HistoryCap.Label(Services.HistoryCap.Load()));
        histRow.Activated += (_, _) =>
        {
            var next = Services.HistoryCap.Next(Services.HistoryCap.Load());
            Services.HistoryCap.Save(next);
            histRow.ValueText = Services.HistoryCap.Label(next);
        };

        AddRow("◉", "电视遥控焦点样式", "", ref rows, "白色描边 + 放大");

        // m3u8 去广告：移植 TVBox 的 M3u8.purify，默认关（与 TVBox HawkConfig.M3U8_PURIFY 一致）。
        // 放在播放区而不是源区，是因为它改变的是「拿到播放列表之后怎么处理」。
        var purifyRow = AddRow("✂", "m3u8 去广告", "清洗点播列表：删广告段与少数派路径；疑似误删会整体回退原文", ref rows);
        var purifySwitch = new TogglePill { IsOn = CatClawVideo.Core.Services.M3u8Purifier.Enabled };
        purifySwitch.Toggled += (_, on) =>
        {
            CatClawVideo.Core.Services.M3u8Purifier.Enabled = on;
            try { Preferences.Default.Set("m3u8_purify", on); } catch { }
        };
        purifyRow.Trailing = purifySwitch;
        purifyRow.Activated += (_, _) => purifySwitch.Toggle();
    }

    private static long ReadCacheGb()
    {
        try
        {
            return (long)Preferences.Default.Get("stream_cache_gb",
                (int)Core.Services.QemuThunder.StreamCachePrefs.DefaultGb);
        }
        catch { return Core.Services.QemuThunder.StreamCachePrefs.DefaultGb; }
    }

    private void ShiftCache(int dir)
    {
        try
        {
            var opts = Core.Services.QemuThunder.StreamCachePrefs.OptionsGb;
            int idx = Array.IndexOf(opts, ReadCacheGb());
            if (idx < 0) idx = Array.IndexOf(opts, Core.Services.QemuThunder.StreamCachePrefs.DefaultGb);

            idx = Math.Clamp(idx + dir, 0, opts.Length - 1);
            var gb = opts[idx];

            Preferences.Default.Set("stream_cache_gb", (int)gb);
            Core.Services.QemuThunder.StreamCachePrefs.SetGb(gb);
            _cacheStep?.SetText(gb, "GB");
            DiagLog.Write($"[缓存] 上限改为 {gb}GB");
        }
        catch { }
    }

    // ─────────── 局域网遥控 ───────────

    /// <summary>本机能拿到的局域网 IPv4 地址（遥控要用它，回环地址没意义所以排除）。</summary>
    static string FirstLanAddress()
    {
        try
        {
            foreach (var a in System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName()))
            {
                if (a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                if (System.Net.IPAddress.IsLoopback(a)) continue;
                if (a.ToString().StartsWith("169.254.", StringComparison.Ordinal)) continue;   // APIPA：没网时也有地址
                return a.ToString();
            }
        }
        catch { }
        return "127.0.0.1";
    }

    static string RemoteControlSummary()
    {
        var port = Core.Services.SpiderProxyServer.ActivePort;
        return port == 0 ? "代理未启动" : "端口 " + port;
    }

    private async Task ShowRemoteControlAsync()
    {
        var port = Core.Services.SpiderProxyServer.ActivePort;
        var token = Core.Services.RemoteControlHub.Token;
        if (port == 0)
        {
            if (HostPage() is { } p0)
                await p0.DisplayAlertAsync("局域网遥控", "本机代理还没起来（遥控与它同端口），稍后再试。", "好");
            return;
        }
        var ip = FirstLanAddress();
        var text = "遥控网页：http://" + ip + ":" + port + "/rc/?token=" + token
            + Environment.NewLine + Environment.NewLine
            + "探测：http://" + ip + ":" + port + "/rc/ping"
            + Environment.NewLine + Environment.NewLine
            + "推一条待播：http://" + ip + ":" + port + "/rc/push?token=" + token
            + "&url=<播放地址>&name=<片名>"
            + Environment.NewLine + Environment.NewLine
            + "当前播放：http://" + ip + ":" + port + "/rc/media?token=" + token
            + Environment.NewLine + Environment.NewLine
            + "口令：" + token
            + Environment.NewLine + "（TVBox 的遥控是全程无鉴权的，这里除 ping 外都要带口令）";
        if (HostPage() is not { } page) return;
        var copy = await page.DisplayAlertAsync("局域网遥控", text, "复制网页地址", "关闭");
        if (copy)
        {
            try { await Clipboard.Default.SetTextAsync("http://" + ip + ":" + port + "/rc/?token=" + token); } catch { }
        }
    }

    /// <summary>Android 13+ 通知权限：没给的话前台服务照样跑，但通知栏没有控制条 —— 用户体验差一档。</summary>
    private static async Task AskNotificationPermissionAsync()
    {
#if ANDROID
        try
        {
            var status = await Microsoft.Maui.ApplicationModel.Permissions
                .CheckStatusAsync<Microsoft.Maui.ApplicationModel.Permissions.PostNotifications>();
            if (status != Microsoft.Maui.ApplicationModel.PermissionStatus.Denied) return;
            await Microsoft.Maui.ApplicationModel.Permissions
                .RequestAsync<Microsoft.Maui.ApplicationModel.Permissions.PostNotifications>();
        }
        catch (Exception ex) { BtFileLog.Write($"[后台播放] 通知权限请求异常：{ex.Message}"); }
#endif
    }

    // ─────────── 订阅壁纸（对位 TVBox「下载壁纸 / 还原」）───────────

    private static readonly HttpClient WallpaperHttp = new() { Timeout = TimeSpan.FromSeconds(25) };

    // SettingsPage 是 ContentView，DisplayAlert* 不在它身上 → 沿父链找宿主页（见 HostPage()）
    private Task ShowWallpaperAsync(string message) =>
        HostPage() is { } page ? page.DisplayAlertAsync("订阅壁纸", message, "好") : Task.CompletedTask;

    private async Task ApplyWallpaperAsync()
    {
        if (!CatClawVideo.Maui.Services.WallpaperService.Supported)
        {
            await ShowWallpaperAsync("当前平台没有系统壁纸接口，设不了桌面壁纸。");
            return;
        }
        var found = CatClawVideo.Core.Providers.TvBoxConfigStore.AnyWallpaper();
        if (found is null)
        {
            await ShowWallpaperAsync("已加载的订阅里没有 wallpaper 字段。\n这个地址由订阅自身提供，换一条带该字段的订阅再试。");
            return;
        }
        // 多订阅并存时壁纸不唯一，所以必须把来源订阅一起报出来
        var msg = await CatClawVideo.Maui.Services.WallpaperService.ApplyAsync(found.Value.Url, WallpaperHttp);
        await ShowWallpaperAsync($"来源「{found.Value.Key}」\n{msg}");
    }

    private Task RestoreWallpaperAsync() =>
        ShowWallpaperAsync(CatClawVideo.Maui.Services.WallpaperService.Restore());

    // ─────────── 配置包导出 / 导入 ───────────

    private async Task ExportBundleAsync()
    {
        var page = HostPage();
        try
        {
            var json = await Services.SettingsBackup.ExportAsync(_db);
            var path = Core.AppPaths.Sub("backups", Services.SettingsBackup.FileName);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, json);
            try
            {
                await Microsoft.Maui.ApplicationModel.DataTransfer.Share.RequestAsync(
                    new Microsoft.Maui.ApplicationModel.DataTransfer.ShareFileRequest
                    {
                        Title = "配置包",
                        File = new Microsoft.Maui.ApplicationModel.DataTransfer.ShareFile(path),
                    });
                return;
            }
            catch
            {
                // 桌面端没有可用的分享通道 —— 不报错，退回到「告诉你文件在哪」
            }
            if (page is not null)
                await page.DisplayAlertAsync("配置包已生成", path, "好");
        }
        catch (Exception ex)
        {
            if (page is not null) await page.DisplayAlertAsync("导出失败", ex.Message, "好");
        }
    }

    private async Task ImportBundleAsync()
    {
        var page = HostPage();
        try
        {
            var picked = await Microsoft.Maui.Storage.FilePicker.PickAsync(
                new Microsoft.Maui.Storage.PickOptions
                {
                    PickerTitle = "选择配置包 json",
                    FileTypes = new Microsoft.Maui.Storage.FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                    {
                        [DevicePlatform.WinUI] = new[] { ".json" },
                        [DevicePlatform.Android] = new[] { "*/*" },
                    }),
                });
            if (picked is null) return;
            string text;
            await using (var stream = await picked.OpenReadAsync())
            using (var reader = new System.IO.StreamReader(stream))
                text = await reader.ReadToEndAsync();

            if (page is null) return;
            if (!await page.DisplayAlertAsync("导入配置包",
                    "合并式导入：新增缺失的订阅、按名覆盖同名设置项，不会清空现有数据。继续？", "导入", "取消"))
                return;

            var r = await Services.SettingsBackup.ImportAsync(_db, text);
            await page.DisplayAlertAsync("导入完成",
                r.Summary + System.Environment.NewLine + "新订阅要重新进入「源配置」页才会拉取站点。", "好");
        }
        catch (Exception ex)
        {
            if (page is not null) await page.DisplayAlertAsync("导入失败", ex.Message, "好");
        }
    }

    // ─────────── 诊断日志 ───────────

    private void BuildDiagnosticSection()
    {
        int rows = 0;

        // 开关行：OK 直接切换（不跳页），与「查看日志」拆成两行，避免一次按键两种含义
        var row = AddRow("📄", "记录 Debug 级日志", "开启后出问题时导出交开发者排查", ref rows);
        _swLog = new TogglePill { IsOn = Services.DiagnosticLog.Instance?.IsEnabled ?? false };
        _swLog.Toggled += OnDiagnosticLogToggled;
        row.Trailing = _swLog;
        row.Activated += (_, _) => _swLog.Toggle();

        var view = AddRow("🔍", "查看 / 导出日志", "筛选、导出诊断包", ref rows);
        view.ShowArrow = true;
        view.Activated += async (_, _) => await GoAsync("diagnosticlog");
    }

    private void OnDiagnosticLogToggled(object? sender, bool on)
    {
        try
        {
            if (Services.DiagnosticLog.Instance is not { } log) return;
            log.IsEnabled = on;
            if (on) log.Flush();
        }
        catch { }
    }

    // ─────────── 关于 ───────────

    private static DateTime _aboutTapAt;
    private static int _aboutTaps;

    /// <summary>本页是 ContentView（tab 内容），弹窗得找承载它的 Page。</summary>
    ContentPage? HostPage()
    {
        for (VisualElement? v = this; v is not null; v = v.Parent as VisualElement)
            if (v is ContentPage page) return page;
        return null;
    }

    private void BuildAboutSection()
    {
        int rows = 0;

        var row = AddRow("ℹ", "猫爪影视", "开源协议 · 免责声明 · 检查更新", ref rows);
        try { row.ValueText = $"v{AppInfo.Current?.VersionString ?? "0.0.0"}"; } catch { }
        row.ShowArrow = true;
        row.Activated += async (_, _) =>
        {
            // 连点 4 次「关于」（相邻两次间隔 ≤2s）= 开 / 关开发者模式。
            // TVBox 用的是遥控器连按 4 次「0」，触屏没有 0 键可连按，版本号行是最接近的落点。
            var now = DateTime.UtcNow;
            _aboutTaps = (now - _aboutTapAt).TotalSeconds <= 2 ? _aboutTaps + 1 : 1;
            _aboutTapAt = now;
            if (_aboutTaps < 4)
            {
                await GoAsync("about");
                return;
            }
            _aboutTaps = 0;
            DevMode = !DevMode;
            RefreshSections();
            if (HostPage() is { } host)
                await host.DisplayAlertAsync("开发者模式",
                    DevMode ? "已开启。「诊断日志」分区会在下次进入设置页时出现。"
                            : "已关闭。「诊断日志」分区会在下次进入设置页时隐藏。",
                    "好");
        };
    }

    // ═══════════════════════ 行工厂 ═══════════════════════

    /// <summary>新增一行；行之间自动补分隔线。<paramref name="rows"/> 为本分类内的行序号。</summary>
    private FocusableRow AddRow(string glyph, string title, string subtitle,
        ref int rows, string? value = null)
    {
        if (rows > 0)
        {
            _contentStack!.Add(new BoxView
            {
                HeightRequest = 1,
                Color = ThemeColor("DividerColor", Colors.Transparent),
            });
        }

        var row = new FocusableRow { Glyph = glyph, Title = title, Subtitle = subtitle };
        if (!string.IsNullOrEmpty(value)) row.ValueText = value;

        _contentStack!.Add(row);
        _content.Add(row);
        rows++;
        return row;
    }

    private static async Task GoAsync(string route)
    {
        try { await Shell.Current.GoToAsync(route); } catch { }
    }

    // ═══════════════════════ 焦点 ═══════════════════════

    private void SetFocus(FocusableRow? row)
    {
        if (ReferenceEquals(_focused, row)) return;
        _focused?.SetHighlighted(false);
        _focused = row;
        row?.SetHighlighted(true);

        // 焦点可能落在屏幕外（矮屏横屏，如手机），必须跟着滚 —— 否则用户看不见焦点在哪
        EnsureFocusVisible();
    }

    private int RailIndex() => _focused is null ? -1 : _nav.IndexOf(_focused);

    private int ContentIndex() => _focused is null ? -1 : _content.IndexOf(_focused);

    private void FocusRail()
    {
        _layer = LayerRail;
        if (_nav.Count == 0) return;
        // 从内容区返回时高亮当前分类，而不是最后一个落点
        int i = RailIndex() >= 0 ? RailIndex() : Math.Max(0, _sectionIndex);
        SetFocus(_nav[Math.Clamp(i, 0, _nav.Count - 1)]);
    }

    /// <summary>
    /// 焦点进入内容区的第一行。
    ///
    /// <para><b>命名注意</b>：不能叫 <c>FocusContent</c> —— 本类实现的
    /// <see cref="IRemoteKeyHandler.FocusContent"/> 是公共无参方法，而带默认参数的
    /// <c>FocusContent(int index = 0)</c> 在重载决议中会输给它（无可选参数的优先），
    /// 导致类内所有 <c>FocusContent()</c> 调用实际都退回侧栏。</para>
    /// </summary>
    private void FocusRows(int index = 0)
    {
        if (_content.Count == 0) { FocusRail(); return; }
        _layer = LayerContent;
        SetFocus(_content[Math.Clamp(index, 0, _content.Count - 1)]);
    }

    /// <summary>把侧栏焦点同步到已选分类（从内容区返回时用）。</summary>
    private void SyncRailHighlight()
    {
        int i = Math.Clamp(_sectionIndex < 0 ? 0 : _sectionIndex, 0, _nav.Count - 1);
        SetFocus(_nav[i]);
        _layer = LayerRail;
    }

    // ═══════════════════════ IRemoteKeyHandler ═══════════════════════

    public void FocusContent() => FocusRail();

    /// <summary>
    /// 顶栏抢走焦点：清掉侧栏/内容区的高亮，并把当前层标记为「顶栏」。
    ///
    /// <para>不清理就会出现「顶栏 tab 与侧栏项同时亮」两个焦点
    /// （2026-09-19 用户截图）；标记层为顶栏后，本页的方向键会直接交还外层，
    /// 由顶栏接管，不会出现两边抢键。</para>
    /// </summary>
    public void BlurContent()
    {
        _layer = LayerTopNav;
        SetFocus(null);
    }

    public bool Handle(RemoteKey key)
    {
        switch (key)
        {
            case RemoteKey.Up:
            case RemoteKey.Down:
            case RemoteKey.Left:
            case RemoteKey.Right:
                return TryMove(key);

            case RemoteKey.Enter:
                if (_layer == LayerTopNav) return false;
                if (_layer == LayerRail)
                {
                    EnterContent(RailIndex());
                    return true;
                }
                _focused?.Activate();
                return true;

            case RemoteKey.Back:
                if (_layer == LayerContent) { FocusRail(); return true; }
                if (_layer == LayerRail) { _layer = LayerTopNav; return false; }   // 交还主壳层退出设置
                return false;
        }
        return false;
    }

    /// <summary>
    /// 方向键移动。<c>false</c> = 本层无处可去，交由外层接手
    /// （这正是「内容区 → 侧栏 → 顶部 tab」走出路径的实现方式）。
    /// </summary>
    /// <summary>进入指定分类的内容区（分类未变则不重建，避免无谓的 UI 重建）。</summary>
    private void EnterContent(int sectionIndex)
    {
        int idx = Math.Max(0, sectionIndex);
        if (idx != _sectionIndex) SelectSection(idx);
        FocusRows();
    }

    private bool TryMove(RemoteKey dir)
    {
        if (_layer == LayerTopNav) return false;

        if (_layer == LayerRail)
        {
            int cur = RailIndex();
            switch (dir)
            {
                case RemoteKey.Up:
                    if (cur <= 0) return false;                 // 已在侧栏顶部 → 交还顶栏
                    SetFocus(_nav[cur - 1]);
                    return true;

                case RemoteKey.Down:
                    if (cur < 0) { SetFocus(_nav[0]); return true; }
                    if (cur >= _nav.Count - 1) return true;      // 已在底部，吃掉
                    SetFocus(_nav[cur + 1]);                     // 栏内换分类（不跳进内容区）
                    return true;

                case RemoteKey.Left:
                    return false;                               // 交回顶栏

                case RemoteKey.Right:
                    EnterContent(cur);
                    return true;
            }
            return false;
        }

        // 内容区
        int i = ContentIndex();
        switch (dir)
        {
            case RemoteKey.Up:
                if (i <= 0) { FocusRail(); return true; }        // 首行再上 → 回侧栏
                SetFocus(_content[i - 1]);
                return true;

            case RemoteKey.Down:
                if (i < 0 || i >= _content.Count - 1) return true;   // 已到底，吃掉
                SetFocus(_content[i + 1]);
                return true;

            case RemoteKey.Left:
                if (_focused?.Decrease() == true) return true;   // ← 调值（步进器行）
                FocusRail();                                     // 普通行 → 回侧栏
                return true;

            case RemoteKey.Right:
                _focused?.Increase();                            // 无可调值则原地不动
                return true;
        }
        return false;
    }

    // ═══════════════════════ 生命周期 ═══════════════════════

    public Task OnTabShownAsync()
    {
        // 主题锁定蓝色 + 深色（外观 UI 已移除，进入设置页即纠正历史存储值）
        _vm.SelectedTheme = Core.Interfaces.AppTheme.Blue;
        _vm.SelectedDarkMode = DarkModeSetting.Dark;

        SelectSection(0);
        _layer = LayerRail;
        FocusRail();
        RemoteKeyRouter.Push(this);
        return Task.CompletedTask;
    }

    // ═══════════════════════ 主题 ═══════════════════════

    private bool _refreshing;

    private void RefreshThemeColors()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            if (_rail is not null)
                _rail.BackgroundColor = ThemeColor("TabBarBackgroundColor", Color.FromArgb("#CC1A1838"));
            if (_groupHost is not null)
            {
                _groupHost.BackgroundColor = ThemeColor("CardBackgroundColor", Color.FromArgb("#332F5A"));
                _groupHost.Stroke = ThemeBrush("DividerColor", Colors.Transparent);
            }
            if (_crumb is not null) _crumb.TextColor = ThemeColor("TextHintColor", Color.FromArgb("#868CAE"));
            if (_pageTitle is not null) _pageTitle.TextColor = ThemeColor("TextPrimaryColor", Colors.White);

            foreach (var n in _nav) n.RefreshThemeColors();
            foreach (var r in _content) r.RefreshThemeColors();
            _swLog?.RefreshThemeColors();
        }
        catch { }
        finally { _refreshing = false; }
    }

    private static Color ThemeColor(string key, Color fallback)
    {
        try
        {
            if (Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c)
                return c;
        }
        catch { }
        return fallback;
    }

    private static Brush ThemeBrush(string key, Color fallback)
    {
        try
        {
            if (Application.Current?.Resources.TryGetValue(key, out var v) == true)
            {
                if (v is Brush b) return b;
                if (v is Color c) return new SolidColorBrush(c);
            }
        }
        catch { }
        return new SolidColorBrush(fallback);
    }
}

/// <summary>
/// 行内步进器（◀ 值 ▶）。遥控器把焦点放在整行上，用 ← → 直接调值：
/// 比「进入 → 弹选择器 → 确认」少两次按键，是电视端的关键交互差异。
/// </summary>
public sealed class StepControl : ContentView
{
    private readonly Label _value;
    private readonly Border _box;

    /// <summary>点 ◀ / 按 ←。</summary>
    public event EventHandler? Decrease;

    /// <summary>点 ▶ / 按 →。</summary>
    public event EventHandler? Increase;

    public StepControl()
    {
        _value = new Label
        {
            FontSize = 13.5,
            FontFamily = "OpenSansSemibold",
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalOptions = LayoutOptions.Center,
            MinimumWidthRequest = 70,
            TextColor = Res("TextPrimaryColor", Colors.White),
        };

        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(32)),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(new GridLength(32)),
            },
        };
        grid.Add(Glyph("◀", () => Decrease?.Invoke(this, EventArgs.Empty)), 0, 0);
        grid.Add(_value, 1, 0);
        grid.Add(Glyph("▶", () => Increase?.Invoke(this, EventArgs.Empty)), 2, 0);

        _box = new Border
        {
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 9 },
            Stroke = new SolidColorBrush(Res("DividerColor", Colors.Transparent)),
            BackgroundColor = Res("ChipInactiveColor", Colors.Transparent),
            HeightRequest = 34,
            Content = grid,
        };
        Content = _box;
    }

    /// <summary>设置显示文本（值 + 单位）。</summary>
    public void SetText(long value, string unit) => _value.Text = $"{value} {unit}";

    /// <summary>主题切换后刷新配色。</summary>
    public void RefreshThemeColors()
    {
        _value.TextColor = Res("TextPrimaryColor", Colors.White);
        _box.Stroke = new SolidColorBrush(Res("DividerColor", Colors.Transparent));
        _box.BackgroundColor = Res("ChipInactiveColor", Colors.Transparent);
    }

    private static Label Glyph(string text, Action onTap)
    {
        var label = new Label
        {
            Text = text,
            FontSize = 12,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            TextColor = Res("TextHintColor", Colors.Gray),
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => onTap();
        label.GestureRecognizers.Add(tap);
        return label;
    }

    private static Color Res(string key, Color fallback)
    {
        try
        {
            if (Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c)
                return c;
        }
        catch { }
        return fallback;
    }
}

/// <summary>
/// 自绘开关（胶囊 + 圆点）。
///
/// <para>为什么不用原生 <c>Switch</c>：它在 Android 上是可聚焦控件，
/// 会抢走遥控器方向键，破坏我们「显式路由 + 显式焦点态」的统一导航；
/// 自绘后既不影响按键路由，外观也能与设置页其余部分保持一致。</para>
/// </summary>
public sealed class TogglePill : ContentView
{
    private readonly Border _track;
    private readonly BoxView _knob;
    private bool _on;

    public event EventHandler<bool>? Toggled;

    public bool IsOn
    {
        get => _on;
        set { _on = value; Render(); }
    }

    public TogglePill()
    {
        _knob = new BoxView
        {
            WidthRequest = 18,
            HeightRequest = 18,
            CornerRadius = 9,
            Color = Colors.White,
            VerticalOptions = LayoutOptions.Center,
        };
        _track = new Border
        {
            WidthRequest = 44,
            HeightRequest = 24,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            Padding = new Thickness(3),
            Content = new Grid { Children = { _knob } },
        };
        Content = _track;

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => Toggle();
        _track.GestureRecognizers.Add(tap);

        Render();
    }

    /// <summary>切换并通知（OK 键与点击都走这里）。</summary>
    public void Toggle()
    {
        IsOn = !_on;
        Toggled?.Invoke(this, _on);
    }

    /// <summary>主题切换后刷新配色。</summary>
    public void RefreshThemeColors() => Render();

    private void Render()
    {
        _track.BackgroundColor = _on
            ? Res("PrimaryColor", Color.FromArgb("#9B7ED8"))
            : Res("ChipInactiveColor", Colors.Transparent);
        _knob.HorizontalOptions = _on ? LayoutOptions.End : LayoutOptions.Start;
    }

    private static Color Res(string key, Color fallback)
    {
        try
        {
            if (Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c)
                return c;
        }
        catch { }
        return fallback;
    }
}
