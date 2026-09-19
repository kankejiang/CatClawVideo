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

    private static readonly (string Key, string Title, string Glyph)[] Sections =
    [
        ("source", "内容源", "🧩"),
        ("play",   "播放",   "▶"),
        ("diag",   "诊断日志", "📄"),
        ("about",  "关于",   "ℹ"),
    ];

    // ─────────── 控件引用 ───────────
    private Border? _rail;
    private Grid? _main;
    private Label? _crumb;
    private Label? _pageTitle;
    private Border? _groupHost;
    private VerticalStackLayout? _contentStack;
    private StepControl? _cacheStep;
    private TogglePill? _swLog;
    private FocusableRow? _focused;
    private int _serverRowIndex = -1;

    public SettingsPage(SettingsViewModel vm, IThemeService theme)
    {
        InitializeComponent();          // 必须：创建 XAML 中的 Root 容器
        _vm = vm;
        _theme = theme;
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

        _main = new Grid
        {
            Padding = new Thickness(30, 22, 30, 22),
            Children =
            {
                new VerticalStackLayout
                {
                    Children = { _crumb, _pageTitle, _groupHost },
                },
            },
        };
        grid.Add(_main, 1, 0);   // 第 1 列 = 内容区（第 0 列是左导航）

        Root.Add(grid);
    }

    /// <summary>窄横屏收紧（侧栏更窄、字号更小、内边距更少）。只做横屏，无竖屏分支。</summary>
    private void UpdateResponsive()
    {
        try
        {
            var w = Width > 0 ? Width : (Window?.Width ?? 0);
            bool compact = w > 0 && w < 960;

            if (_rail is not null) _rail.WidthRequest = compact ? 158 : 216;
            if (_main is not null) _main.Padding = compact ? new Thickness(18, 14, 18, 14) : new Thickness(30, 22, 30, 22);
            if (_pageTitle is not null) _pageTitle.FontSize = compact ? 16 : 20;
            if (_crumb is not null) _crumb.FontSize = compact ? 10.5 : 11.5;

            foreach (var n in _nav) n.ApplyDensity(compact ? 0.92 : 1.0);
            foreach (var r in _content) r.ApplyDensity(compact ? 0.92 : 1.0);
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
        AddRow("▣", "默认画面比例", "", ref rows, "保持比例（AspectFit）");
        AddRow("⏩", "默认倍速", "", ref rows, "1.0×");
        AddRow("◉", "电视遥控焦点样式", "", ref rows, "白色描边 + 放大");
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

    private void BuildAboutSection()
    {
        int rows = 0;

        var row = AddRow("ℹ", "猫爪影视", "开源协议 · 免责声明 · 检查更新", ref rows);
        try { row.ValueText = $"v{AppInfo.Current?.VersionString ?? "0.0.0"}"; } catch { }
        row.ShowArrow = true;
        row.Activated += async (_, _) => await GoAsync("about");
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
