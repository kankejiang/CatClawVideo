using CatClawVideo.Core.Models;
using CatClawVideo.Maui.Services;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 首页数据源选择弹窗（参考影视仓「请选择首页数据源」）：
/// 半透明遮罩 + 居中卡片，两列站点按钮，当前站点主题色描边高亮；
/// 切换失败过的站点置灰标注「不可用」（仍可点击重试）；
/// 点击站点回调 onSelected 并关闭。
///
/// <para><b>关闭方式</b>（2026-09-19 用户反馈「只能点站点才能关，误触后没法退出」）：
/// 右上角 <b>✕ 关闭按钮</b>、<b>点击遮罩空白处</b>、按 <b>Back / Esc</b>。</para>
///
/// <para><b>遥控器可操作</b>（2026-09-19 用户反馈「这个窗口没有焦点，使用遥控器无法关闭」）：
/// 本页把自己推到 <see cref="RemoteKeyRouter"/> 栈顶，并<b>全量吃掉</b>方向键 / 回车 / 返回。
/// 模态弹窗盖在首页上时，按键绝不能再落到下面那层页面去移动看不见的焦点 ——
/// 「按遥控器没反应」的成因正是如此：按键一直在操作背后的首页。</para>
///
/// <para><b>焦点模型</b>：打开时默认落在<b>当前正在用的站点</b>上；两列栅格内
/// ←/→ 左右、↑/↓ 上下；第一行再按 ↑ 落到右上角 <b>✕</b>（回车关闭），✕ 按 ↓ 回到栅格。</para>
/// </summary>
public partial class SitePickerDialogPage : ContentPage, IRemoteKeyHandler
{
    private readonly List<Border> _btns = [];
    private readonly Action<VodSiteInfo> _onSelected;
    private readonly IReadOnlyList<VodSiteInfo> _sites;

    /// <summary>当前正在使用的站点下标（画「当前」描边；与 <see cref="_index"/> 的焦点描边互不冲突）。</summary>
    private readonly int _currentIndex;

    private Border _closeBtn = null!;
    private ScrollView _scroll = null!;

    /// <summary>焦点在栅格第几项。</summary>
    private int _index;

    /// <summary>焦点是否在右上角 ✕ 上。</summary>
    private bool _onClose;

    public SitePickerDialogPage(IEnumerable<VodSiteInfo> sites, string? currentKey,
        Action<VodSiteInfo> onSelected, IReadOnlySet<string>? failedKeys = null)
    {
        _onSelected = onSelected;
        var list = sites.ToList();
        _sites = list;
        // 打开时焦点直接落在「当前正在用的站点」上：既让用户看清现状，也省掉从头找的按键
        _currentIndex = Math.Max(0, list.FindIndex(s => s.Key == currentKey));
        _index = _currentIndex;

        BackgroundColor = Color.FromArgb("#B3000000");

        var res = Application.Current!.Resources;

        var card = new Border
        {
            WidthRequest = 620,
            MaximumHeightRequest = 620,
            BackgroundColor = (Color)res["CardBackgroundColor"],
            Stroke = (Color)res["DividerColor"],
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 18 },
            Padding = 22,
        };

        var stack = new VerticalStackLayout { Spacing = 14 };

        // 标题行：标题居中 + 右上角关闭按钮（标题两侧用同宽占位保证真居中）
        var titleRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(34)),   // 左侧占位，与关闭按钮等宽
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(new GridLength(34)),
            },
        };
        titleRow.Add(new Label
        {
            Text = "请选择首页数据源",
            FontSize = 17,
            FontFamily = "OpenSansSemibold",
            TextColor = (Color)res["TextPrimaryColor"],
            HorizontalOptions = LayoutOptions.Center,
        }, 1, 0);

        _closeBtn = new Border
        {
            WidthRequest = 34,
            HeightRequest = 34,
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 17 },
            BackgroundColor = (Color)res["ChipInactiveColor"],
            Content = new Label
            {
                Text = "✕",
                FontSize = 15,
                TextColor = (Color)res["TextSecondaryColor"],
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
            },
        };
        var closeTap = new TapGestureRecognizer();
        closeTap.Tapped += async (_, _) => await CloseAsync();
        _closeBtn.GestureRecognizers.Add(closeTap);
        titleRow.Add(_closeBtn, 2, 0);

        stack.Add(titleRow);

        // 两列站点按钮网格（站点多时卡片内滚动）
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
            },
            ColumnSpacing = 12,
            RowSpacing = 12,
        };
        for (int i = 0; i < _sites.Count; i++)
        {
            var site = _sites[i];
            bool current = site.Key == currentKey;
            bool failed = failedKeys?.Contains(site.Key) == true;

            var btn = new Border
            {
                StrokeThickness = current ? 2 : 0,
                Stroke = current ? (Color)res["PrimaryColor"] : Colors.Transparent,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
                BackgroundColor = (Color)res["ChipInactiveColor"],
                Padding = new Thickness(10, 12),
                Opacity = failed ? 0.55 : 1,
            };
            btn.Content = new Label
            {
                Text = failed ? site.Name + "（不可用）" : site.Name,
                FontSize = 13.5,
                MaxLines = 1,
                LineBreakMode = LineBreakMode.TailTruncation,
                HorizontalTextAlignment = TextAlignment.Center,
                TextColor = current ? (Color)res["PrimaryColor"]
                    : failed ? (Color)res["TextHintColor"]
                    : (Color)res["TextPrimaryColor"],
            };

            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) =>
            {
                _onSelected(site);
                await CloseAsync();
            };
            btn.GestureRecognizers.Add(tap);

            _btns.Add(btn);
            grid.Add(btn, i % 2, i / 2);
        }

        _scroll = new ScrollView { MaximumHeightRequest = 500 };
        _scroll.Content = grid;
        stack.Add(_scroll);
        card.Content = stack;

        // 根容器铺满整页：承担「点遮罩空白处关闭」。
        // 卡片在自己的范围内会吃掉点击（命中测试不会冒泡到根 Grid），
        // 所以卡片内点站点仍走站点自己的手势，只有点在卡片外才关闭。
        var root = new Grid { Children = { card } };
        var backdrop = new TapGestureRecognizer();
        backdrop.Tapped += async (_, _) => await CloseAsync();
        root.GestureRecognizers.Add(backdrop);

        Content = root;
    }

    // ═══════════════════════ 生命周期（接管按键）═══════════════════════

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // 压到按键栈顶：弹窗期间由本页独占方向键 / 回车 / 返回
        RemoteKeyRouter.Push(this);
        RenderFocus();
        ScrollToFocused();
    }

    protected override void OnDisappearing()
    {
        RemoteKeyRouter.Pop(this);
        base.OnDisappearing();
    }

    /// <summary>模态弹窗始终持有焦点，外层的「送/收焦点」在这里没有意义。</summary>
    public void FocusContent() => RenderFocus();

    public void BlurContent() { }

    // ═══════════════════════ IRemoteKeyHandler ═══════════════════════

    /// <summary>
    /// 一律返回 <c>true</c>：模态弹窗必须吃掉所有按键。
    /// 返回 <c>false</c> 会让按键继续往下冒泡到首页 —— 那正是「按遥控器没反应」的成因
    /// （焦点其实在背后的首页上移动，只是被遮罩挡住了看不见）。
    /// </summary>
    public bool Handle(RemoteKey key)
    {
        switch (key)
        {
            case RemoteKey.Left:
                if (!_onClose && _index % 2 == 1) { _index--; RenderFocus(); ScrollToFocused(); }
                return true;

            case RemoteKey.Right:
                if (!_onClose && _index % 2 == 0 && _index + 1 < _btns.Count)
                {
                    _index++;
                    RenderFocus();
                    ScrollToFocused();
                }
                return true;

            case RemoteKey.Up:
                if (_onClose) return true;              // ✕ 已是最上
                if (_index >= 2) { _index -= 2; RenderFocus(); ScrollToFocused(); }
                else SetCloseFocus(true);               // 第一行再往上 → 右上角 ✕
                return true;

            case RemoteKey.Down:
                if (_onClose) { SetCloseFocus(false); return true; }
                if (_index + 2 < _btns.Count) { _index += 2; RenderFocus(); ScrollToFocused(); }
                return true;

            case RemoteKey.Enter:
                if (_onClose) { _ = CloseAsync(); return true; }
                SelectFocused();
                return true;

            case RemoteKey.Back:
                _ = CloseAsync();
                return true;
        }
        return true;
    }

    /// <summary>回车选中当前焦点站点（「不可用」的也可重试）。</summary>
    private void SelectFocused()
    {
        if (_index < 0 || _index >= _btns.Count) return;
        _onSelected(_sites[_index]);
        _ = CloseAsync();
    }

    private void SetCloseFocus(bool on)
    {
        _onClose = on;
        RenderFocus();
    }

    // ═══════════════════════ 焦点渲染 ═══════════════════════

    private void RenderFocus()
    {
        var res = Application.Current!.Resources;
        var primary = res["PrimaryColor"] as Color ?? Colors.Purple;

        // ✕：焦点态与站点按钮同一套视觉（亮紫描边 + 柔光 + 微放大）
        _closeBtn.Stroke = _onClose ? primary : Colors.Transparent;
        _closeBtn.StrokeThickness = _onClose ? 3 : 0;
        _closeBtn.Scale = _onClose ? 1.08 : 1.0;

        for (int i = 0; i < _btns.Count; i++)
        {
            bool focused = !_onClose && i == _index;
            bool current = i == _currentIndex;

            if (focused)
            {
                _btns[i].Stroke = primary;
                _btns[i].StrokeThickness = 3;
                _btns[i].Scale = 1.04;
                _btns[i].Shadow = new Shadow
                {
                    Brush = new SolidColorBrush(primary),
                    Radius = 14,
                    Offset = new Point(0, 4),
                    Opacity = 0.5f,
                };
            }
            else
            {
                // 非焦点：只有「当前正在用的站点」保留细描边，作为「你在哪儿」的标记
                _btns[i].Stroke = current ? primary : Colors.Transparent;
                _btns[i].StrokeThickness = current ? 2 : 0;
                _btns[i].Scale = 1.0;
                _btns[i].Shadow = null!;
            }
        }
    }

    /// <summary>把焦点项滚进可视区（站点多时卡片内滚动，不滚的话焦点会跑到视野外）。</summary>
    private void ScrollToFocused()
    {
        if (_onClose || _index < 0 || _index >= _btns.Count) return;
        try { _ = _scroll.ScrollToAsync(_btns[_index], ScrollToPosition.MakeVisible, animated: false); }
        catch { }
    }

    /// <summary>关闭弹窗且**不改变**当前数据源（选中站点走的才是 onSelected + 关闭）。</summary>
    private async Task CloseAsync()
    {
        // 先主动出栈（幂等）：万一某平台不触发 OnDisappearing，按键栈里留着本页
        // 会把方向键 / 返回**永久吃掉**，那时连首页都动不了 —— 这是必须堵掉的失效模式。
        RemoteKeyRouter.Pop(this);

        try
        {
            if (Navigation.ModalStack.Count > 0) await Navigation.PopModalAsync();
        }
        catch { }
    }

    /// <summary>
    /// Android 物理返回键 / Windows Esc → 关闭弹窗。
    /// 返回 <c>true</c> 表示已处理，避免继续冒泡把整个页面弹掉。
    /// </summary>
    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync();
        return true;
    }
}
