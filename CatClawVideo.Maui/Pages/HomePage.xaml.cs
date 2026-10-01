using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;
using CatClawVideo.Maui.Services;
using CatClawVideo.Maui.ViewModels;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 首页：MacCMS 真实源（分类 + 影片列表）。
/// 海报卡点击 → 观看页（携带源/影片参数拉真实详情与选集）。
///
/// <para><b>遥控器 / 键盘导航</b>：三层焦点 —— 切换源 → 分类 chips → 海报墙。
/// ↑↓ 在层间与层内移动，←→ 在分类 chips 内横向移动、在海报墙内按列移动，
/// OK 激活，Back 逐层退出（海报 → 分类 → 切换源 → 顶部 tab）。
/// 焦点态由本页显式驱动（列表项与 chip 都不是原生可聚焦控件）。</para>
/// </summary>
public partial class HomePage : ContentView, ITabView, IRemoteKeyHandler
{
    private readonly HomeViewModel _vm;

    // ─────────── 焦点分层 ───────────
    private const int LayerTopNav = 0;
    private const int LayerSwitchSite = 1;
    private const int LayerChips = 2;
    private const int LayerPosters = 3;
    private int _layer = LayerSwitchSite;

    private int _chipIndex;
    private int _posterIndex;
    private int _posterColumns = 6;

    // ── 主页行流焦点（主页模式下 LayerPosters 的语义：行 × 行内列）──
    private int _rowIndex;
    private int _colIndex;

    /// <summary>分类 chip 的焦点壳（与 Categories 一一对应，随集合变化重建）。</summary>
    private readonly List<Border> _chipShells = new();

    public HomePage(HomeViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = _vm;

        // 分类集合变化 / 选中项变化时刷新 chip 高亮
        _vm.Categories.CollectionChanged += (_, _) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                RebuildChipShells();
                UpdateChipStyles();
            });
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HomeViewModel.SelectedCategoryId))
            {
                MainThread.BeginInvokeOnMainThread(UpdateChipStyles);
                MainThread.BeginInvokeOnMainThread(UpdateFilterUi);
                MainThread.BeginInvokeOnMainThread(ApplyModeVisual);   // 主页行流 / 分类网格 切换
            }
            if (e.PropertyName == nameof(HomeViewModel.FilterSummary))
                MainThread.BeginInvokeOnMainThread(UpdateFilterUi);
        };

        // 海报墙布局：与历史/收藏页一致，统一走 PosterLayoutHelper（固定卡片尺寸，列数自适应）
        PosterGrid.SizeChanged += (_, _) =>
        {
            ApplyPosterLayout();
            CaptureColumns();
        };

        // 主页推荐行：卡片尺寸与海报墙完全同一套（同一资源、同一 cap）——
        // 用户要求主页行的海报大小/排列与片库网格一致。网格不可见（Size=0）时
        // 它的 SizeChanged 不触发，行流自己按当前可用面积算一次。
        HomeRowsScroll.SizeChanged += (_, _) =>
        {
            var w = HomeRowsScroll.Width;
            var h = HomeRowsScroll.Height;
            if (w <= 0 || h <= 0) return;
#if WINDOWS
            PosterLayoutHelper.Apply(w, h, cap: 252);
#else
            PosterLayoutHelper.Apply(w, h);
#endif
        };

        // 列表数据变化后（首次加载/翻页）刷新列数并复位越界焦点
        _vm.Items.CollectionChanged += (_, _) => MainThread.BeginInvokeOnMainThread(OnItemsChanged);

        // 主页行流变化后复位越界的行流焦点（行剔除/重建时 _rowIndex 可能越界）
        _vm.HomeRows.CollectionChanged += (_, _) => MainThread.BeginInvokeOnMainThread(OnHomeRowsChanged);

        // 应用初始模式视觉（默认主页：行流可见、分类条隐藏、切换胶囊高亮「主页」）
        ApplyModeVisual();

        StartColdStartOverlay();
    }

    // ═══════════ 冷启动遮罩 ═══════════
    //
    // ART guest（QEMU TCG）冷启动实测 41~46 秒（解 590MB initrd + ART 运行时 boot），
    // 期间海报墙全空、只有站点条一行小字——体感「白屏卡死」（2026-09-27 用户反馈）。
    // 遮罩分阶段显示真实进度；首页数据（分类）就绪即淡出进入主界面。
    // 轮询（400ms）而非事件接线：订阅/引擎/首页数据三个信号分属三层，轮询最省接线。

    private IDispatcherTimer? _coldStartTimer;
    private readonly DateTime _coldStartUtc = DateTime.UtcNow;

    private void StartColdStartOverlay()
    {
        _coldStartTimer = Dispatcher.CreateTimer();
        _coldStartTimer.Interval = TimeSpan.FromMilliseconds(400);
        _coldStartTimer.Tick += (_, _) => UpdateColdStartOverlay();
        _coldStartTimer.Start();
        UpdateColdStartOverlay();
    }

    private void UpdateColdStartOverlay()
    {
        // 数据就绪（分类已上屏）= 冷启动结束：停表并淡出进主界面
        if (_vm.Categories.Count > 0)
        {
            _coldStartTimer?.Stop();
            _coldStartTimer = null;
            // 就绪 ≠ 立刻消失：先跳 100%（用户要求「一下子加载到 100% 再进程序」），
            // 停 ~0.5s 让最后一帧可见，再淡出。直接淡出会让数字永远停在 80 多。
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                ColdStartPct.Text = "100%";
                _ = ColdStartProgress.ProgressTo(1.0, 150, Easing.Linear);
                await Task.Delay(500);
                await FadeOutColdStartOverlayAsync();
            });
            return;
        }
        MainThread.BeginInvokeOnMainThread(ApplyColdStartStatus);
    }

    private void ApplyColdStartStatus()
    {
        var siteCount = SiteRegistry.Playable.Count();
        var elapsed = (DateTime.UtcNow - _coldStartUtc).TotalSeconds;

        // ── 进度条 + 百分比（分段锚定实测阶段，冷启动各段耗时来自 108/本机日志）──
        // · 0~8s   内核解包 + init（快）：        5% → 30%（用户要的前段「0 1 2 3 4 10 20」快推）
        // · 8~45s  ART boot（36s 大头，慢）：    30% → 80%
        // · >45s   桥收尾/站点加载：              80% → 97% 极慢爬（每秒 ~0.35%，观感仍在推进）
        // · 数据上屏（分类就绪）：                 瞬间 100% → 淡出
        // 是假进度（无真实百分比信号），但各段与实测一致，不会 40s 卡在同一个数上。
        double p;
        if (siteCount == 0) p = 5;
        else if (elapsed <= 8) p = 5 + elapsed / 8.0 * 25;
        else if (elapsed <= 45) p = 30 + (elapsed - 8) / 37.0 * 50;
        else p = Math.Min(97, 80 + (elapsed - 45) * 0.35);

        if (_vm.Categories.Count > 0) p = 100;
        p = Math.Round(p, 0, MidpointRounding.AwayFromZero);
        ColdStartPct.Text = $"{p:0}%";
        _ = ColdStartProgress.ProgressTo(p / 100.0, 380, Easing.Linear);

        ColdStartStatus.Text = siteCount == 0
            ? "正在恢复订阅与站点…"
            : SiteRegistry.JarSpiderAvailable
#if ANDROID
                // Android 的 jar 桥是进程内 DexClassLoader（没有 QEMU），冷启动只是 dex 加载，秒级
                ? "正在启动爬虫运行时…"
#else
                ? elapsed switch
                {
                    < 8 => "正在启动视频引擎（内核加载）…",
                    < 45 => "正在启动视频引擎（ART 运行时，约 40 秒）…",
                    _ => "正在加载首页数据…",
                }
#endif
                : "正在加载首页数据…";
    }

    private async Task FadeOutColdStartOverlayAsync()
    {
        if (!ColdStartOverlay.IsVisible) return;
        await ColdStartOverlay.FadeTo(0, 350, Easing.CubicOut);
        ColdStartOverlay.IsVisible = false;
    }

    public Task OnTabShownAsync()
    {
        ApplyPosterLayout();
        CaptureColumns();
        // 初始层 = 顶栏：本页不抢焦点，交给顶栏「首页」tab
        // （MainPage.ShowTab 紧接着会调 FocusTopNav，这里保持不亮即可，避免闪一下切换源环）。
        _layer = LayerTopNav;
        RenderFocus();
        RemoteKeyRouter.Push(this);
        // 设置里切了「主页推荐内容」/ 换站后：回首页 tab 时行流按需重建
        _ = _vm.RefreshHomeRecIfStaleAsync();
        return _vm.LoadHomeCommand.ExecuteAsync(null);
    }

    // ═══════════════════════ 布局 ═══════════════════════

    /// <summary>海报墙布局：与历史/收藏页同一套 PosterLayoutHelper（Android 卡高 182 / Windows 252，宽 2:3，列数自适应）</summary>
    private void ApplyPosterLayout()
    {
#if WINDOWS
        PosterLayoutHelper.Apply(PosterGrid, PosterGrid.Width, PosterGrid.Height, cap: 252);
#else
        PosterLayoutHelper.Apply(PosterGrid, PosterGrid.Width, PosterGrid.Height);
#endif
    }

    /// <summary>记录当前列数（方向键上下移动需要按列换算索引）。</summary>
    private void CaptureColumns()
    {
        if (PosterGrid.ItemsLayout is GridItemsLayout g && g.Span > 0)
            _posterColumns = g.Span;
    }

    private void OnItemsChanged()
    {
        CaptureColumns();
        if (_posterIndex >= _vm.Items.Count) _posterIndex = Math.Max(0, _vm.Items.Count - 1);
        RenderFocus();
    }

    // ═══════════════════════ 分类 chips ═══════════════════════

    /// <summary>
    /// 刷新分类 chip 引用：焦点外壳已由 XAML 模板静态声明（外层 Border=壳，内层 Border=chip），
    /// 集合变化后直接遍历建立索引即可。**禁止在此重排/重挂视图**——旧版运行时包壳
    /// （把 chip 从 BindableLayout 摘出再塞进新建 Border）会重挂原生视图，
    /// Android 上切站点后 TapGestureRecognizer 整体失效（点击分类无反应，真机复现）。
    /// </summary>
    // ═══════════════════ 分类筛选（对位 TVBox GridFilterDialog）═══════════════════

    /// <summary>
    /// 刷新「筛选」入口：站点没返回 filters 就整块隐藏；有条件时把已选项直接写在按钮上。
    /// <para>两级动作表（选组 → 选值）而不是自绘弹层：手机与桌面都能用、不需要新的焦点层，
    /// 且筛选本来就是低频操作。要改成 TVBox 那种一行一组的弹层面板时，只需要换这里的交互壳。</para>
    /// </summary>
    void UpdateFilterUi()
    {
        var groups = _vm.CurrentFilters;
        // 筛选是「分类」的附属能力：主页行流没有当前分类，整块隐藏（网格视图才有）
        FilterShell.IsVisible = !_vm.IsHomeMode && groups.Count > 0;
        var summary = _vm.FilterSummary;
        FilterLabel.Text = string.IsNullOrEmpty(summary) ? "筛选" : summary;
    }

    /// <summary>本页是挂在 Shell 页里的 ContentView，动作表要走宿主 Page。</summary>
    Page? HostPage()
    {
        VisualElement? v = this;
        while (v is not null and not Page) v = v.Parent as VisualElement;
        return v as Page;
    }

    private async void OnFilterTapped(object? sender, TappedEventArgs e)
    {
        var groups = _vm.CurrentFilters;
        var host = HostPage();
        if (groups.Count == 0 || host is null) return;

        var names = groups.Select(g => g.Name).Append("清除全部筛选").ToArray();
        var pick = await host.DisplayActionSheetAsync("按哪一项筛选？", "取消", null, names);
        if (pick is null || pick == "取消") return;

        if (pick == "清除全部筛选")
        {
            await _vm.ClearFiltersAsync();
            return;
        }
        var group = groups.Find(g => g.Name == pick);
        if (group is null) return;

        var choices = group.Values.Select(v => v.Display).ToArray();
        var value = await host.DisplayActionSheetAsync($"{group.Name} · 选一个", "取消", "不限", choices);
        if (value is null || value == "取消") return;

        if (value == "不限") await _vm.SetFilterAsync(group.Key, null);
        else await _vm.SetFilterAsync(group.Key, Array.Find(group.Values.ToArray(), v => v.Display == value));
    }

    private void RebuildChipShells()
    {
        _ = Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(30), () =>
        {
            try
            {
                _chipShells.Clear();
                foreach (var child in CategoryChipHost.Children)
                {
                    if (child is Border shell && shell.Content is Border)
                        _chipShells.Add(shell);
                }

                if (_chipIndex >= _chipShells.Count) _chipIndex = Math.Max(0, _chipShells.Count - 1);
                UpdateChipStyles();
            }
            catch { }
        });
    }

    /// <summary>选中分类 chip 用主题色实底 + 白字；焦点另加描边（与选中态独立，可并存）。</summary>
    private void UpdateChipStyles()
    {
        var res = Application.Current?.Resources;
        var active = res?["PrimaryColor"] as Color ?? Colors.Purple;
        var inactive = res?["ChipInactiveColor"] as Color ?? Colors.Gray;
        var activeText = Colors.White;
        var inactiveText = res?["TextSecondaryColor"] as Color ?? Colors.Gray;

        for (int i = 0; i < _chipShells.Count; i++)
        {
            bool focused = _layer == LayerChips && i == _chipIndex;
            _chipShells[i].Stroke = focused ? (res?["PrimaryColor"] as Color ?? Colors.Purple) : Colors.Transparent;
            _chipShells[i].Scale = focused ? 1.06 : 1.0;
        }

        foreach (var shell in _chipShells)
        {
            if (shell.Content is not Border chip) continue;
            bool on = (chip.BindingContext as VodCategory)?.Id == _vm.SelectedCategoryId;
            chip.BackgroundColor = on ? active : inactive;
            if (chip.Content is Label label)
                label.TextColor = on ? activeText : inactiveText;
        }
    }

    // ═══════════════════════ 焦点渲染 ═══════════════════════

    private void RenderFocus()
    {
        // 切换源焦点环
        var primary = Application.Current?.Resources["PrimaryColor"] as Color ?? Colors.Purple;
        SwitchSiteShell.Stroke = _layer == LayerSwitchSite ? primary : Colors.Transparent;
        SwitchSiteShell.Scale = _layer == LayerSwitchSite ? 1.05 : 1.0;

        UpdateChipStyles();

        // 海报墙焦点：只点亮当前项（网格=一维索引；主页行流= 行×列），清掉其余
        if (!_vm.IsHomeMode)
        {
            for (int i = 0; i < _vm.Items.Count; i++)
                _vm.Items[i].IsFocused = _layer == LayerPosters && i == _posterIndex;
        }
        else
        {
            for (int r = 0; r < _vm.HomeRows.Count; r++)
                for (int c = 0; c < _vm.HomeRows[r].Items.Count; c++)
                    _vm.HomeRows[r].Items[c].IsFocused =
                        _layer == LayerPosters && r == _rowIndex && c == _colIndex;
        }
    }

    private void ClearPosterFocus()
    {
        for (int i = 0; i < _vm.Items.Count; i++)
            _vm.Items[i].IsFocused = false;
        foreach (var row in _vm.HomeRows)
            for (int c = 0; c < row.Items.Count; c++)
                row.Items[c].IsFocused = false;
    }

    private void FocusSwitchSite()
    {
        _layer = LayerSwitchSite;
        RenderFocus();
    }

    private void FocusChips(int index = 0)
    {
        if (_chipShells.Count == 0) { FocusSwitchSite(); return; }
        _layer = LayerChips;
        _chipIndex = Math.Clamp(index, 0, _chipShells.Count - 1);
        RenderFocus();
        ScrollToChip(_chipIndex);
    }

    /// <summary>
    /// 把焦点 chip 滚进可视区。分类行是横向 ScrollView，chip 数量随源变化（十几个很常见），
    /// 不滚的话焦点会跑出屏幕 —— 尤其是「切换源 ← 回最右 chip」这种一次跳到末项的移动。
    /// </summary>
    private void ScrollToChip(int index)
    {
        try
        {
            if (index >= 0 && index < _chipShells.Count)
                _ = CategoryScroll.ScrollToAsync(_chipShells[index], ScrollToPosition.MakeVisible, animated: false);
        }
        catch { }
    }

    private void FocusPosters(int index = 0)
    {
        if (!_vm.IsHomeMode)
        {
            if (_vm.Items.Count == 0) { FocusChips(); return; }
            _posterIndex = Math.Clamp(index, 0, _vm.Items.Count - 1);
        }
        else
        {
            if (_vm.HomeRows.Count == 0) { FocusSwitchSite(); return; }
            _rowIndex = 0;
            _colIndex = Math.Clamp(index, 0, _vm.HomeRows[0].Items.Count - 1);
        }
        _layer = LayerPosters;
        RenderFocus();
        ScrollToPoster();
    }

    /// <summary>把焦点海报滚入可视区（否则遥控器移动时焦点会跑出屏幕）。
    /// 主页行流暂不做行内跟随滚动（鼠标/触屏为主；遥控用户可切分类用完整网格导航）。</summary>
    private void ScrollToPoster()
    {
        if (_vm.IsHomeMode) return;
        try
        {
            if (_posterIndex >= 0 && _posterIndex < _vm.Items.Count)
                PosterGrid.ScrollTo(_posterIndex, position: ScrollToPosition.MakeVisible, animate: false);
        }
        catch { }
    }

    // ═══════════════════════ IRemoteKeyHandler ═══════════════════════

    /// <summary>
    /// 被主壳层要求接管焦点（顶栏按 ↓）。
    ///
    /// <para>2026-09-19 用户建议：首页最常用的是挑内容，「切换源」是低频操作 ——
    /// 让 ↓ 一步就到内容区，省掉一次多余的按键。切换源改为从 chip 行两端进：
    /// 最左 chip <c>←</c>、最右 chip <c>→</c>（见 <see cref="TryMove"/>）。</para>
    ///
    /// <para>落点按视图分流：网格 → 记忆的分类 chip；主页行流 → 第一行（TVBox 首页就是直接逛内容）。</para>
    /// </summary>
    public void FocusContent()
    {
        if (_vm.IsHomeMode) FocusPosters(0);
        else FocusChips(Math.Max(0, _chipIndex));
    }

    /// <summary>
    /// 顶栏抢走焦点：本页**所有**焦点层一起熄灭（切换源环 / 分类 chip / 海报墙），
    /// 并把层标记为「顶栏」。
    ///
    /// <para>原先只清了海报墙 —— 因为当时进入这条路径的唯一方式是「从内容区按 ↑ 上顶栏」，
    /// 起点必然在内容层。现在**启动与切 tab 也由顶栏持有焦点**（见 <c>MainPage.ShowTab</c>），
    /// 而本页的初始层停在「切换源」上，只清海报就会留下「顶栏首页 + 首页切换源」双高亮
    /// （2026-09-19 用户截图）。</para>
    /// </summary>
    public void BlurContent()
    {
        _layer = LayerTopNav;
        ClearPosterFocus();
        RenderFocus();
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
                return Activate();

            case RemoteKey.Back:
                return GoBack();
        }
        return false;
    }

    private bool TryMove(RemoteKey dir)
    {
        switch (_layer)
        {
            case LayerTopNav:
                return false;

            case LayerSwitchSite:
                if (dir == RemoteKey.Down)
                {
                    // ↓ 进内容区：网格 → 分类 chips；主页 → 行流第一行
                    if (_vm.IsHomeMode) FocusPosters(0);
                    else FocusChips(Math.Max(0, _chipIndex));
                    return true;
                }
                if (dir == RemoteKey.Up) { _layer = LayerTopNav; RenderFocus(); return false; }  // 交还顶栏
                // ← 退回分类行最右 chip（2026-09-19 用户反馈：从「切换源」按 ← 出不去）。
                // 与「最右 chip → 上到切换源」成对 —— 两边互为对方的出口。
                if (dir == RemoteKey.Left && !_vm.IsHomeMode && _chipShells.Count > 0)
                {
                    FocusChips(_chipShells.Count - 1);
                    return true;
                }
                return true;   // → 右侧无内容，吃掉

            case LayerChips:
                switch (dir)
                {
                    case RemoteKey.Left:
                        if (_chipIndex <= 0) { FocusSwitchSite(); return true; }
                        _chipIndex--;
                        RenderFocus();
                        return true;

                    case RemoteKey.Right:
                        if (_chipIndex < _chipShells.Count - 1)
                        {
                            _chipIndex++;
                            RenderFocus();
                        }
                        else
                        {
                            // 最右侧 chip 再往右 → 上到「切换源」（2026-09-19 用户建议）。
                            // 与 FocusContent 的「↓ 进分类」正好成对：切换源 ↓ 会回到刚才这个 chip。
                            FocusSwitchSite();
                        }
                        return true;

                    case RemoteKey.Up:
                        FocusSwitchSite();
                        return true;

                    case RemoteKey.Down:
                        ClearPosterFocus();
                        FocusPosters(0);
                        return true;
                }
                return true;

            case LayerPosters when _vm.IsHomeMode:
                {
                    // 主页行流：←→ 行内移动，↑↓ 行间移动（列号 clamp 到目标行范围）。
                    // 行内不做跟随滚动（见 ScrollToPoster 注释）。
                    int last = Math.Max(0, _vm.HomeRows[_rowIndex].Items.Count - 1);
                    switch (dir)
                    {
                        case RemoteKey.Left:
                            if (_colIndex <= 0) { ClearPosterFocus(); FocusSwitchSite(); return true; }
                            _colIndex--;
                            RenderFocus();
                            return true;

                        case RemoteKey.Right:
                            if (_colIndex >= last) return true;   // 行尾
                            _colIndex++;
                            RenderFocus();
                            return true;

                        case RemoteKey.Up:
                            if (_rowIndex <= 0) { ClearPosterFocus(); FocusSwitchSite(); return true; }
                            _rowIndex--;
                            _colIndex = Math.Min(_colIndex, Math.Max(0, _vm.HomeRows[_rowIndex].Items.Count - 1));
                            RenderFocus();
                            return true;

                        case RemoteKey.Down:
                            if (_rowIndex >= _vm.HomeRows.Count - 1) return true;   // 已到底
                            _rowIndex++;
                            _colIndex = Math.Min(_colIndex, Math.Max(0, _vm.HomeRows[_rowIndex].Items.Count - 1));
                            RenderFocus();
                            return true;
                    }
                    return true;
                }

            case LayerPosters:
                {
                    int cols = Math.Max(1, _posterColumns);
                    switch (dir)
                    {
                        case RemoteKey.Left:
                            if (_posterIndex % cols == 0) { FocusChips(_chipIndex); return true; }  // 行首 → 回 chips
                            _posterIndex--;
                            RenderFocus();
                            ScrollToPoster();
                            return true;

                        case RemoteKey.Right:
                            if (_posterIndex >= _vm.Items.Count - 1) return true;
                            _posterIndex++;
                            RenderFocus();
                            ScrollToPoster();
                            return true;

                        case RemoteKey.Up:
                            if (_posterIndex - cols < 0) { ClearPosterFocus(); FocusChips(_chipIndex); return true; }
                            _posterIndex -= cols;
                            RenderFocus();
                            ScrollToPoster();
                            return true;

                        case RemoteKey.Down:
                            if (_posterIndex + cols > _vm.Items.Count - 1) return true;   // 已到底
                            _posterIndex += cols;
                            RenderFocus();
                            ScrollToPoster();
                            return true;
                    }
                    return true;
                }
        }
        return false;
    }

    /// <summary>OK：切换源 → 弹源选择；分类 chip → 切分类；海报 → 进观看页。</summary>
    private bool Activate()
    {
        switch (_layer)
        {
            case LayerSwitchSite:
                OnSwitchSiteTapped(this, new TappedEventArgs(null));
                return true;

            case LayerChips:
                if (_chipIndex >= 0 && _chipIndex < _vm.Categories.Count)
                    _ = _vm.SelectCategoryAsync(_vm.Categories[_chipIndex]);
                return true;

            case LayerPosters when _vm.IsHomeMode:
                if (_rowIndex >= 0 && _rowIndex < _vm.HomeRows.Count &&
                    _colIndex >= 0 && _colIndex < _vm.HomeRows[_rowIndex].Items.Count)
                    OpenItem(_vm.HomeRows[_rowIndex].Items[_colIndex]);
                return true;

            case LayerPosters:
                if (_posterIndex >= 0 && _posterIndex < _vm.Items.Count)
                    OpenItem(_vm.Items[_posterIndex]);
                return true;
        }
        return false;
    }

    /// <summary>Back：海报 → 分类（网格）/ 切换源（主页）→ 交还顶栏。</summary>
    private bool GoBack()
    {
        switch (_layer)
        {
            case LayerPosters:
                ClearPosterFocus();
                // 主页行流视图直接回站点条；网格按原路径回 chips
                if (_vm.IsHomeMode) FocusSwitchSite();
                else FocusChips(_chipIndex);
                return true;
            case LayerChips:
                FocusSwitchSite();
                return true;
            case LayerSwitchSite:
                _layer = LayerTopNav;
                RenderFocus();
                return false;   // 交还主壳层
        }
        return false;
    }

    // ═══════════════════════ 交互入口（鼠标 / 触屏 / OK 共用）═══════════════════════

    /// <summary>「切换源」点击 → 数据源选择弹窗（参考影视仓），选择后切换首页数据源</summary>
    /// <summary>首页右侧直播入口 → 直播间（未配置源时页面自身给出「配置直播源」引导）</summary>
    private async void OnLiveTapped(object? sender, TappedEventArgs e)
    {
        try { await Shell.Current.GoToAsync("live"); } catch { }
    }

    private async void OnSwitchSiteTapped(object? sender, TappedEventArgs e)
    {
        var sites = _vm.PlayableSites;
        if (sites.Count == 0)
        {
            _vm.HomeStatus = "暂无可用站点，请先在 设置 → 订阅源管理 添加订阅";
            return;
        }
        var dialog = new SitePickerDialogPage(sites, _vm.Site?.Key,
            site => MainThread.BeginInvokeOnMainThread(() => _ = _vm.SelectSiteCommand.ExecuteAsync(site)),
            _vm.FailedSites);
        await Shell.Current.Navigation.PushModalAsync(dialog);
    }

    // ═══════════════════════ 主页 / 分类视图分流 ═══════════════════════

    /// <summary>按视图（主页行流 / 分类网格）显隐内容区，筛选入口只在网格视图有意义。</summary>
    private void ApplyModeVisual()
    {
        var home = _vm.IsHomeMode;
        HomeRowsScroll.IsVisible = home;
        PosterGrid.IsVisible = !home;
        CategoryScroll.IsVisible = true;   // 分类 chips 常显（TVBox 的分类 tab 形态），首位固定「主页」
        UpdateFilterUi();
    }

    /// <summary>行流集合变化后复位越界的行流焦点索引（空行剔除/重建时 _rowIndex/_colIndex 可能越界）。</summary>
    private void OnHomeRowsChanged()
    {
        if (_vm.HomeRows.Count == 0) { _rowIndex = _colIndex = 0; return; }
        _rowIndex = Math.Clamp(_rowIndex, 0, _vm.HomeRows.Count - 1);
        var cols = _vm.HomeRows[_rowIndex].Items.Count;
        _colIndex = Math.Clamp(_colIndex, 0, Math.Max(0, cols - 1));
        if (_layer == LayerPosters && _vm.IsHomeMode) RenderFocus();
    }

    /// <summary>分类 chip 点击 → 拉取该分类影片</summary>
    private async void OnCategoryTapped(object? sender, TappedEventArgs e)
    {
        // 诊断：分类点击无反应问题——记录点击是否触发、绑定上下文是否解析成功
        if (sender is VisualElement ve)
            DiagLog.Write($"[cat-tap] 触发 sender={ve.GetType().Name} ctx={(ve.BindingContext?.GetType().Name ?? "null")}");
        else
            DiagLog.Write("[cat-tap] 触发 sender=null");
        if ((sender as VisualElement)?.BindingContext is not VodCategory cat)
        {
            DiagLog.Write("[cat-tap] 绑定上下文不是 VodCategory，忽略");
            return;
        }
        DiagLog.Write($"[cat-tap] 切换分类 id={cat.Id} name={cat.Name}");
        await _vm.SelectCategoryAsync(cat);
    }

    /// <summary>海报卡点击 → 观看页</summary>
    private void OnPosterTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as VisualElement)?.BindingContext is not VodItem item) return;
        OpenItem(item);
    }

    /// <summary>进入观看页（带源定位参数；type 必传——猫爪源等无 api 特征的源靠它路由）</summary>
    private void OpenItem(VodItem item)
    {
        // ⓪ 主页推荐行的特殊卡（TVBox 语义）：
        //    历史卡 → 直接回观看页续看（卡片上带着与 HistoryPage 同一套回跳参数）；
        //    豆瓣卡 → 跳搜索页跨源搜片名（豆瓣条目没有源，TVBox 是详情搜不到再快搜，我们一步到位）
        if (item.Tag == "history" && item.Action.Length > 0)
        {
            try { Shell.Current.GoToAsync(item.Action); } catch { }
            return;
        }
        if (item.Tag == "douban")
        {
            var q = Uri.EscapeDataString(item.Title);
            try { Shell.Current.GoToAsync($"search?q={q}"); } catch { }
            return;
        }

        if (_vm.Site is null) return;

        // TVBox GridFragment.onItemClick 的优先级照搬：action 先于 tag。
        // ① 带 action 的卡片是「操作入口」（网盘登录/清除 Cookie/排序…），交给爬虫的
        //    action(String) —— 只有这条路会弹 jar 的原生对话框与扫码二维码。
        //    （旧实现用「数字 id + csp_*Guard」猜，真机确认 8 张卡全带 action，按字段判更准。）
        if (item.Action.Length > 0)
        {
            _ = CatClawVideo.Maui.Services.SpiderUiHost.OpenDriveEntryAsync(_vm.Site, item);
            return;
        }

        // ② tag=folder/cover = 网盘里的一层目录：点它要用本条目 ID 当分类 ID 重新拉列表
        //    （TVBox changeView），喂给 detailContent/playerContent 会把目录路径当播放地址，
        //    表现为「播放失败：MalformedURLException: no protocol: /movies/137018/@folder」。
        //    主页行流里点目录卡要先切到该分类网格——列表变化发生在网格里，行流视图看不到。
        if (item.Tag is "folder" or "cover")
        {
            // SelectCategoryAsync 对普通分类会自动退场行流（HomeRows.Clear），网格视图接手展示
            _ = _vm.SelectCategoryAsync(new VodCategory { Id = item.Id, Name = item.Title });
            return;
        }

        var query = $"watch?title={Uri.EscapeDataString(item.Title)}" +
                    $"&sourceKey={Uri.EscapeDataString(item.SourceKey)}" +
                    $"&type={_vm.Site.Type}" +
                    $"&api={Uri.EscapeDataString(_vm.Site.Api)}" +
                    $"&itemId={Uri.EscapeDataString(item.Id)}" +
                    $"&year={Uri.EscapeDataString(item.Year ?? "")}" +
                    $"&remarks={Uri.EscapeDataString(item.Remarks ?? "")}" +
                    $"&desc={Uri.EscapeDataString(item.Description ?? "")}" +
                    // 封面透传：观看页不再拉详情，播放历史的海报靠它
                    $"&cover={Uri.EscapeDataString(item.Cover ?? "")}";
        Shell.Current.GoToAsync(query);
    }
}
