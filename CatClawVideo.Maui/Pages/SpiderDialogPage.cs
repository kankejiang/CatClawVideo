using CatClawVideo.Maui.Services;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 爬虫（jar）自定义 View 对话框的宿主渲染页：把桥摊平出来的<b>行 / 格</b>还原成
/// 真机那样的左右两栏（网盘行 = 左边盘名占宽、右边「启用中/停用中」按钮占窄），
/// 而不是 <c>DisplayActionSheet</c> 的一维平铺。
///
/// <para><b>为什么需要</b>：Guard 系 jar 的「已登录+启用中(可进入和搜索自己盘)」不是
/// <c>setItems</c> 给的，是 jar 自己 new 的 LinearLayout 树；桥按容器摊成行后，
/// 只有这里能保住「一行两格」的空间关系（2026-09-24 用户对照真机截图指出）。</para>
///
/// <para><b>关闭方式</b>与 <see cref="SitePickerDialogPage"/> 一致：右上角 ✕、点遮罩空白、
/// Back / Esc；<b>点条目不关框</b>（jar 的自定义视图框在 Android 上就是这个语义，
/// 用户要连续切几家盘），回调后由桥侧决定是否 <c>dismiss</c>。</para>
///
/// <para><b>遥控器</b>：本页把自己压进 <see cref="RemoteKeyRouter"/> 栈顶并全量吃掉按键，
/// ←/→ 在行内移动、↑/↓ 跨行、第一行再按 ↑ 落到 ✕。</para>
/// </summary>
public partial class SpiderDialogPage : ContentPage, IRemoteKeyHandler
{
    private sealed record Cell(int Row, int Col, int Index, string Text);

    private readonly List<Cell> _cells = [];
    private readonly Dictionary<int, Border> _borders = new();     // 展平序号 → 控件
    private readonly int _cols;
    private readonly Action<int> _onPick;
    /// <summary>用户自行关框（✕ / 遮罩 / Back）时的回调；桥主动 dismiss 不会触发（那时由宿主出栈）。</summary>
    private readonly Action? _onCancel;
    private Border _closeBtn = null!;
    private ScrollView _scroll = null!;
    private int _index;
    private bool _onClose;

    /// <summary>条目下标 → 该格的 Label；桥上行 <c>ui-rows</c>（jar 就地改了文字）时按它刷新文本。</summary>
    private readonly Dictionary<int, Label> _cellLabels = new();

    /// <summary>就地刷新各格文字（不重建布局，免得焦点/滚动位置丢）。</summary>
    public void UpdateRows(IReadOnlyList<IReadOnlyList<(string Text, int Index)>> rows)
    {
        foreach (var r in rows)
            foreach (var (text, idx) in r)
                if (_cellLabels.TryGetValue(idx, out var lb) && lb.Text != text) lb.Text = text;
    }

    public SpiderDialogPage(string? title, string? message,
        IReadOnlyList<IReadOnlyList<(string Text, int Index)>> rows, Action<int> onPick, Action? onCancel = null)
    {
        _onPick = onPick;
        _onCancel = onCancel;
        BackgroundColor = Color.FromArgb("#B3000000");
        var res = Application.Current!.Resources;

        for (int r = 0; r < rows.Count; r++)
            for (int c = 0; c < rows[r].Count; c++)
                _cells.Add(new Cell(r, c, rows[r][c].Index, rows[r][c].Text));
        _cols = rows.Count == 0 ? 1 : rows.Max(x => x.Count);

        var card = new Border
        {
            WidthRequest = 640,
            MaximumHeightRequest = 620,
            BackgroundColor = (Color)res["CardBackgroundColor"],
            Stroke = (Color)res["DividerColor"],
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 18 },
            Padding = 22,
        };
        var stack = new VerticalStackLayout { Spacing = 14 };

        // 标题行：居中 + 右上角 ✕（两侧同宽占位保证真居中）
        var titleRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(34)),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(new GridLength(34)),
            },
        };
        titleRow.Add(new Label
        {
            Text = string.IsNullOrWhiteSpace(title) ? "请选择" : title!,
            FontSize = 17,
            FontFamily = "OpenSansSemibold",
            TextColor = (Color)res["TextPrimaryColor"],
            HorizontalOptions = LayoutOptions.Center,
            HorizontalTextAlignment = TextAlignment.Center,
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

        if (!string.IsNullOrWhiteSpace(message))
            stack.Add(new Label
            {
                Text = message!,
                FontSize = 13,
                TextColor = (Color)res["TextSecondaryColor"],
                HorizontalTextAlignment = TextAlignment.Center,
            });

        // 行 / 格网格：每行第一格吃满剩余宽度，其余按内容占窄列 —— 与真机左右分布一致
        var grid = new Grid { RowSpacing = 12, ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (int c = 1; c < _cols; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        foreach (var cell in _cells)
        {
            var btn = new Border
            {
                StrokeThickness = 0,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
                BackgroundColor = (Color)res["ChipInactiveColor"],
                Padding = new Thickness(12, 13),
                MinimumWidthRequest = cell.Col == 0 ? 0 : 96,
                HorizontalOptions = cell.Col == 0 ? LayoutOptions.Fill : LayoutOptions.End,
            };
            var lb = new Label
            {
                Text = cell.Text,
                FontSize = 13.5,
                MaxLines = 1,
                LineBreakMode = LineBreakMode.TailTruncation,
                HorizontalTextAlignment = TextAlignment.Center,
                TextColor = (Color)res["TextPrimaryColor"],
            };
            btn.Content = lb;
            _cellLabels[cell.Index] = lb;
            var idx = cell.Index;
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => Pick(idx);
            btn.GestureRecognizers.Add(tap);
            _borders[FlatOf(cell)] = btn;
            grid.Add(btn, cell.Col, cell.Row);
        }

        _scroll = new ScrollView { MaximumHeightRequest = 480 };
        _scroll.Content = grid;
        stack.Add(_scroll);
        card.Content = stack;

        var root = new Grid { Children = { card } };
        var backdrop = new TapGestureRecognizer();
        backdrop.Tapped += async (_, _) => await CloseAsync();
        root.GestureRecognizers.Add(backdrop);
        Content = root;
    }

    private async Task CloseAsync()
    {
        // 先主动出栈（幂等）：万一某平台不触发 OnDisappearing，按键栈里留着本页
        // 会把方向键 / 返回**永久吃掉**（同 SitePickerDialogPage 的教训）。
        RemoteKeyRouter.Pop(this);
        _onCancel?.Invoke();      // 用户自己关的 → 告诉桥「取消」（桥主动 dismiss 时不走这里）
        try
        {
            if (Navigation.ModalStack.Count > 0) await Navigation.PopModalAsync();
        }
        catch { }
    }

    private void Pick(int index)
    {
        _onPick(index);
        // 点条目不收框：jar 的自定义视图框在 Android 上点完留着（可连续切盘 / 点盘名再弹扫码）。
        // 真要关是桥侧 dismiss() → 宿主收到 ui-dismiss 由 SpiderUiHost 关掉本页。
    }

    /// <summary>格子在其「展平序列」里的位置（焦点与按键移动都用它）。</summary>
    private int FlatOf(Cell c)
    {
        int n = 0;
        foreach (var x in _cells)
        {
            if (x.Row == c.Row && x.Col == c.Col) return n;
            n++;
        }
        return 0;
    }

    // ═══════════════════════ 生命周期（接管按键）═══════════════════════

    protected override void OnAppearing()
    {
        base.OnAppearing();
        RemoteKeyRouter.Push(this);
        RenderFocus();
    }

    protected override void OnDisappearing()
    {
        RemoteKeyRouter.Pop(this);
        base.OnDisappearing();
    }

    public void FocusContent() => RenderFocus();

    public void BlurContent() { }

    // ═══════════════════════ IRemoteKeyHandler ═══════════════════════

    /// <summary>一律返回 true：模态弹窗必须吃掉所有按键，否则按键会操作到背后那页去。</summary>
    public bool Handle(RemoteKey key)
    {
        switch (key)
        {
            case RemoteKey.Left:
                Move(-1);
                return true;
            case RemoteKey.Right:
                Move(1);
                return true;
            case RemoteKey.Up:
                if (_onClose) return true;
                var up = RowOf(_index) - 1;
                if (up < 0) SetCloseFocus(true);
                else MoveTo(up, ColOf(_index));
                return true;
            case RemoteKey.Down:
                if (_onClose) { SetCloseFocus(false); return true; }
                MoveTo(RowOf(_index) + 1, ColOf(_index));
                return true;
            case RemoteKey.Enter:
                if (_onClose) { _ = CloseAsync(); return true; }
                Pick(_cells[_index].Index);
                return true;
            case RemoteKey.Back:
                _ = CloseAsync();
                return true;
        }
        return true;
    }

    private int RowOf(int flat) => flat >= 0 && flat < _cells.Count ? _cells[flat].Row : 0;
    private int ColOf(int flat) => flat >= 0 && flat < _cells.Count ? _cells[flat].Col : 0;

    /// <summary>行内左右移动（不跨行，避免「停用中」按一次右就跳到下一家盘）。</summary>
    private void Move(int delta)
    {
        if (_cells.Count == 0) return;
        int row = RowOf(_index);
        for (int n = _index + delta; n >= 0 && n < _cells.Count && _cells[n].Row == row; n += delta)
        {
            _index = n;
            RenderFocus();
            return;
        }
    }

    /// <summary>跨行移动：落到目标行最接近该列的格子；越界则原地不动。</summary>
    private void MoveTo(int row, int col)
    {
        int best = -1, bestGap = int.MaxValue;
        for (int i = 0; i < _cells.Count; i++)
        {
            if (_cells[i].Row != row) continue;
            int gap = Math.Abs(_cells[i].Col - col);
            if (gap < bestGap) { bestGap = gap; best = i; }
        }
        if (best < 0) return;
        _index = best;
        RenderFocus();
    }

    private void SetCloseFocus(bool onClose)
    {
        _onClose = onClose;
        RenderFocus();
    }

    private void RenderFocus()
    {
        var res = Application.Current!.Resources;
        _closeBtn.Stroke = _onClose ? (Color)res["PrimaryColor"] : Colors.Transparent;
        _closeBtn.StrokeThickness = _onClose ? 2 : 0;
        for (int i = 0; i < _cells.Count; i++)
        {
            if (!_borders.TryGetValue(i, out var b)) continue;
            bool focused = !_onClose && i == _index;
            b.Stroke = focused ? (Color)res["PrimaryColor"] : Colors.Transparent;
            b.StrokeThickness = focused ? 2 : 0;
        }
    }
}
