using Microsoft.Maui.Controls.Shapes;

namespace CatClawVideo.Maui.Controls;

/// <summary>
/// 虚拟键盘（遥控器 / 触屏通用的自绘键盘）。
///
/// <para><b>为什么需要</b>：搜索页的第一动作是「输入文字」，而遥控器没有文字输入能力。
/// 系统输入法在 TV 上要么不存在、要么是为触屏设计的（遥控器点不动软键盘），
/// 所以键盘必须自己做。手机端同样启用这套键盘 —— 三端 UI 与操作保持一致
/// （手机虽有系统输入法，但本项目约定三端同一套横屏界面）。</para>
///
/// <para><b>布局</b>：数字行 <c>1~0</c> 置顶，其下三行 QWERTY（10/9/7），
/// <c>删除</c> / <c>搜索</c> 接在第 4 行右端 —— 四行统一按 10 个键宽排版。
/// QWERTY 而非字母顺序：物理键盘与手机输入法的肌肉记忆可直接复用
/// （用户在电视上搜索时往往一边看手机回忆片名）。</para>
///
/// <para><b>焦点由本控件自己管理</b>（不依赖平台焦点系统，与 <see cref="FocusableRow"/>
/// 同一思路）：这样 Android 遥控器与 Windows 键盘能得到完全一致的行为与视觉，
/// 也让「跨区导航」这类规则能自己说了算。</para>
/// </summary>
public sealed class VirtualKeyboard : ContentView
{
    // ─────────── 键位定义 ───────────

    /// <summary>一行键位：<c>Keys</c> 为该行键面文本，<c>Offset</c> 为行首缩进（单位＝1 个键宽，可为半键）。</summary>
    private sealed record KeyRow(string[] Keys, double Offset);

    /// <summary>
    /// 键盘共 4 行，**统一按 10 个键宽排版**（行 2 缩进半键、行 3 缩进一键 → 与物理键盘错位一致）：
    /// <code>
    ///  1 2 3 4 5 6 7 8 9 0      ← 数字行置顶，与字母等宽（不再单开一块小键盘）
    ///  Q W E R T Y U I O P
    ///   A S D F G H J K L       ← 左右各缩半键
    ///    Z X C V B N M 删除 搜索   ← 缩进一键后 9 键正好占满剩余宽度，右端与数字行齐平
    /// </code>
    ///
    /// <para><b>为什么数字行置顶</b>（2026-09-19 用户反馈）：原先数字区是字母**右侧**的一块
    /// 3 列 × 4 行小键盘，吃掉约四分之一栏宽，字母只能挤在剩下的宽度里；且两区行数不同
    /// （3 行 vs 4 行），右列下方还空一格。改成数字行置顶后四行共用同一套 10 键宽栅格，
    /// <b>字母区拿到整栏宽度</b>，数字也符合物理键盘的肌肉记忆（打「庆余年 2」不用往下找）。</para>
    ///
    /// <para><c>删除</c> / <c>搜索</c> 放在第 4 行右端而不是数字行尾部：数字行放满 <c>1~0</c>
    /// 十个键后接上它们会变成 12 键、比字母行宽，两边对不齐；而第 4 行原本只有 <c>ZXCVBNM</c>
    /// 且右端留白，缩进收到一键后正好容下两格。</para>
    /// </summary>
    private static readonly KeyRow[] Rows =
    [
        new(["1", "2", "3", "4", "5", "6", "7", "8", "9", "0"], 0),
        new(["Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P"], 0),
        new(["A", "S", "D", "F", "G", "H", "J", "K", "L"], 0.5),
        new(["Z", "X", "C", "V", "B", "N", "M", KeyBackspace, KeySearch], 1.0),
    ];

    /// <summary>
    /// 退格键文案。**刻意不用 <c>⌫</c>（U+232B）**：实测本项目所用字体没有该字形，
    /// 渲染成一个豆腐块方框（2026-09-19 实机截图确认），改用中文「删除」既无字形风险、
    /// 也与同行的「搜索」风格一致。
    /// </summary>
    private const string KeyBackspace = "删除";

    private const string KeySearch = "搜索";

    /// <summary>功能键集合（走不同的事件，不是「输入字符」）。</summary>
    private static bool IsFunctionKey(string k) => k is KeyBackspace or KeySearch;

    // ─────────── 事件 ───────────

    /// <summary>按下一个**字符键**（字母 / 数字）。参数为该字符。</summary>
    public event EventHandler<string>? CharacterPressed;

    /// <summary>按下删除键。</summary>
    public event EventHandler? BackspacePressed;

    /// <summary>按下搜索键（提交）。</summary>
    public event EventHandler? SearchPressed;

    /// <summary>焦点从键盘下边缘继续向下 —— 外部应把焦点交到右侧内容区。</summary>
    public event EventHandler? FocusExitDown;

    /// <summary>焦点从字母区右边缘继续向右 —— 外部应把焦点交到右侧内容区。</summary>
    public event EventHandler? FocusExitRight;

    // ─────────── 焦点模型 ───────────

    private int _row;   // 行号（0 = 数字行）
    private int _col;   // 行内列号
    private bool _engaged;   // 键盘是否持有焦点（false 时画「未聚焦」的淡样式）

    /// <summary>键盘是否持有焦点。外部在把焦点交给内容区时置 false。</summary>
    public bool IsEngaged
    {
        get => _engaged;
        set
        {
            if (_engaged == value) return;
            _engaged = value;
            RenderFocus();
        }
    }

    /// <summary>当前键面文本。</summary>
    private string CurrentKey => Rows[_row].Keys[_col];

    /// <summary>当前行列数（各行不同：10 / 10 / 9 / 9）。</summary>
    private int CurrentRowLength => Rows[_row].Keys.Length;

    // ─────────── 视觉 ───────────

    /// <summary>四行垂直排布，行高均分（Star）→ 键盘整体高度由外部决定，键帽跟着拉伸。</summary>
    private readonly Grid _root = new() { RowSpacing = KeyGap };

    /// <summary>键帽视图（按 [行][列] 索引，供焦点重绘）。</summary>
    private readonly List<List<Border>> _keys = [];

    /// <summary>每个键帽里的文本（改字号/颜色时要拿它）。</summary>
    private readonly Dictionary<Border, Label> _keyLabels = [];

    /// <summary>整行的总宽度，以「键宽」为单位（四行都按它切分 → 键宽严格一致）。</summary>
    private const int TotalKeyWidths = 10;

    /// <summary>键帽之间的横向间隙。用键自身右边距实现 —— 键跨 2 列，
    /// 若用 <c>ColumnSpacing</c> 会在键内部（两列之间）切出一道缝。</summary>
    private const double KeyGap = 6;

    private const double KeyRadius = 9;

    public VirtualKeyboard()
    {
        for (int r = 0; r < Rows.Length; r++)
        {
            _root.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            _root.Add(BuildRow(r), 0, r);
        }

        Content = _root;
        Loaded += (_, _) => RenderFocus();
    }

    // ─────────── 构建 ───────────

    /// <summary>
    /// 构建一行：整行按 <b>10 个键宽</b>切分 —— 行首是若干「缩进列」（空列，只占位不放控件）、
    /// 中间每个键各占 1 列、行尾按需补一个补齐列，使四行的键宽严格一致。
    ///
    /// <para>键的横向间隙用**键自身的右边距**实现：若改用 <c>ColumnSpacing</c>，
    /// 「缩进列」与首键之间会多出一道，四行的间距就不一致了。</para>
    ///
    /// <para>（曾尝试用 <c>Grid.SetColumnSpan(key, 2)</c> 让键跨两个半键列，实测**不生效**：
    /// 键仍只占一列，渲染出来只有 32px 宽、间距却和键一样宽 —— 故改为上面的显式列定义。）</para>
    /// </summary>
    private View BuildRow(int rowIndex)
    {
        var row = Rows[rowIndex];
        var grid = new Grid { ColumnSpacing = 0, VerticalOptions = LayoutOptions.Fill };

        // 行首缩进列（空列，不需要子元素：Star 宽度按定义分配，空着也占位）
        if (row.Offset > 0)
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(row.Offset, GridUnitType.Star)));

        var firstKeyColumn = grid.ColumnDefinitions.Count;
        for (int i = 0; i < row.Keys.Length; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        // 行尾补齐到 10 键宽（第 3 行缩进一键 + 9 键正好 10，无需补）
        var used = row.Offset + row.Keys.Length;
        if (used < TotalKeyWidths)
            grid.ColumnDefinitions.Add(new ColumnDefinition(
                new GridLength(TotalKeyWidths - used, GridUnitType.Star)));

        var cells = new List<Border>();
        for (int i = 0; i < row.Keys.Length; i++)
        {
            var text = row.Keys[i];
            var key = BuildKey(text, primary: text == KeySearch);
            key.Margin = new Thickness(0, 0, KeyGap, 0);   // 横向间隙
            grid.Add(key, firstKeyColumn + i, 0);
            cells.Add(key);
        }
        _keys.Add(cells);
        return grid;
    }

    /// <summary>单个键帽。</summary>
    private Border BuildKey(string text, bool primary)
    {
        var isFn = IsFunctionKey(text);
        var label = new Label
        {
            Text = text,
            // 汉字与 ⌫ 比数字宽，用略小字号保持一致视觉重量
            FontSize = isFn ? 12.5 : 15,
            FontFamily = isFn ? "OpenSansSemibold" : "OpenSansRegular",
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
        };

        var box = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = KeyRadius },
            Content = label,   // 高度不设：由行高（Star）拉伸到外部给的键盘高度
        };
        _keyLabels[box] = label;

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => OnKeyTapped(box);
        box.GestureRecognizers.Add(tap);

        // 记录主色标记（「搜索」按钮用主色实底，与项目其他主按钮一致）
        if (primary) box.ClassId = "primary";
        else if (isFn) box.ClassId = "fn";
        return box;
    }

    /// <summary>点某个键：先把焦点移过去，再触发按键（触屏也走同一套语义）。</summary>
    private void OnKeyTapped(Border box)
    {
        if (!TryLocate(box, out var r, out var c)) return;
        _row = r;
        _col = c;
        IsEngaged = true;
        RenderFocus();
        Activate();
    }

    private bool TryLocate(Border box, out int row, out int col)
    {
        for (int r = 0; r < _keys.Count; r++)
            for (int c = 0; c < _keys[r].Count; c++)
                if (ReferenceEquals(_keys[r][c], box))
                {
                    row = r; col = c;
                    return true;
                }
        row = 0; col = 0;
        return false;
    }

    // ─────────── 焦点移动（外部按键驱动） ───────────

    /// <summary>
    /// 处理方向键。<c>true</c> = 已消费（焦点仍在键盘内）；
    /// <c>false</c> = 焦点想走出键盘，外部应接手（同时会触发
    /// <see cref="FocusExitDown"/> / <see cref="FocusExitRight"/>）。
    /// </summary>
    public bool MoveLeft()
    {
        if (_col > 0) { _col--; RenderFocus(); return true; }
        return false;   // 已在行首：交还外层
    }

    public bool MoveRight()
    {
        if (_col < CurrentRowLength - 1) { _col++; RenderFocus(); return true; }

        // 行尾再往右 → 交还内容区（右栏在键盘右侧）
        FocusExitRight?.Invoke(this, EventArgs.Empty);
        return false;
    }

    public bool MoveUp()
    {
        if (_row > 0) { MoveToRow(_row - 1); return true; }
        return false;   // 已在顶行（数字行）：交还外层（上方是输入框）
    }

    public bool MoveDown()
    {
        if (_row < Rows.Length - 1) { MoveToRow(_row + 1); return true; }

        // 末行再往下 → 交还内容区（左栏下方是「最近搜索」）
        FocusExitDown?.Invoke(this, EventArgs.Empty);
        return false;
    }

    /// <summary>当前键在「10 键宽」栅格里的位置（含行首缩进），跨行对齐靠它。</summary>
    private static double GridPosition(in KeyRow row, int col) => row.Offset + col;

    /// <summary>
    /// 换到目标行并尽量保持**横向位置**：按栅格坐标（含行首缩进）对齐，而不是按列号。
    /// 例如从第 3 行的 <c>A</c>（栅格位 1.5）往下，落到第 4 行的 <c>Z</c>（栅格位 1.5 附近），
    /// 而不是列号相同的 <c>X</c> —— 后者会让手指感觉「跳了一下」。
    /// </summary>
    private void MoveToRow(int target)
    {
        var pos = GridPosition(Rows[_row], _col);
        var col = (int)Math.Round(pos - Rows[target].Offset);
        _row = target;
        _col = Math.Clamp(col, 0, Rows[target].Keys.Length - 1);
        RenderFocus();
    }

    /// <summary>激活当前键（OK / 点击）。</summary>
    public void Activate()
    {
        var key = CurrentKey;
        switch (key)
        {
            case KeyBackspace: BackspacePressed?.Invoke(this, EventArgs.Empty); break;
            case KeySearch: SearchPressed?.Invoke(this, EventArgs.Empty); break;
            default: CharacterPressed?.Invoke(this, key); break;
        }
    }

    // ─────────── 视觉刷新 ───────────

    /// <summary>主题切换后刷新配色。</summary>
    public void RefreshThemeColors() => RenderFocus();

    private void RenderFocus()
    {
        var primary = Res("PrimaryColor", Color.FromArgb("#9B7ED8"));

        for (int r = 0; r < _keys.Count; r++)
        {
            for (int c = 0; c < _keys[r].Count; c++)
            {
                var box = _keys[r][c];
                var label = _keyLabels[box];
                var isCurrent = _engaged && r == _row && c == _col;
                var isPrimary = box.ClassId == "primary";
                var isFn = box.ClassId == "fn";

                if (isCurrent)
                {
                    // 焦点态：与 FocusableRow 同一套（亮紫描边 + 柔光 + 微放大）
                    box.BackgroundColor = primary.WithAlpha(0.30f);
                    box.Stroke = new SolidColorBrush(primary);
                    box.StrokeThickness = 2.5;
                    box.Scale = 1.03;
                    box.Shadow = new Shadow
                    {
                        Brush = new SolidColorBrush(primary),
                        Radius = 14,
                        Offset = new Point(0, 4),
                        Opacity = 0.5f,
                    };
                    label.TextColor = Colors.White;
                }
                else
                {
                    box.StrokeThickness = 0;
                    box.Scale = 1;
                    box.Shadow = null!;
                    box.BackgroundColor = isPrimary
                        ? primary.WithAlpha(_engaged ? 0.92f : 0.45f)
                        : isFn
                            ? Res("CardBackgroundStrongColor", Color.FromArgb("#3A3668"))
                            : Res("CardBackgroundColor", Color.FromArgb("#332F5A"));
                    label.TextColor = isPrimary
                        ? Colors.White
                        : Res("TextSecondaryColor", Color.FromArgb("#BCC0DD"));
                }
            }
        }
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
