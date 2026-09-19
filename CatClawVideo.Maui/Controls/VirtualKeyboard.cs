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
/// <para><b>布局</b>：左 QWERTY 字母区（3 行 10/9/7）＋ 右小键盘数字区（3 列 × 4 行
/// <c>789/456/123/0⌫搜索</c>）。QWERTY 而非字母顺序：物理键盘与手机输入法的肌肉记忆
/// 可直接复用（用户在电视上搜索时往往一边看手机回忆片名）。</para>
///
/// <para><b>焦点由本控件自己管理</b>（不依赖平台焦点系统，与 <see cref="FocusableRow"/>
/// 同一思路）：这样 Android 遥控器与 Windows 键盘能得到完全一致的行为与视觉，
/// 也让「跨区导航」这类规则能自己说了算。</para>
/// </summary>
public sealed class VirtualKeyboard : ContentView
{
    // ─────────── 键位定义 ───────────

    /// <summary>字母区三行（QWERTY）。长度 10 / 9 / 7。</summary>
    private static readonly string[] LetterRows = ["QWERTYUIOP", "ASDFGHJKL", "ZXCVBNM"];

    /// <summary>数字区四行三列（小键盘）。末行是三功能键。</summary>
    private static readonly string[][] DigitRows =
    [
        ["7", "8", "9"],
        ["4", "5", "6"],
        ["1", "2", "3"],
        [KeyZero, KeyBackspace, KeySearch],
    ];

    private const string KeyZero = "0";
    private const string KeyBackspace = "⌫";
    private const string KeySearch = "搜索";

    /// <summary>数字区中「功能键」集合（走不同的事件，不是「输入字符」）。</summary>
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

    private enum Area { Letters, Digits }

    private Area _area = Area.Letters;
    private int _row;   // 所在区的行号
    private int _col;   // 所在区的列号
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

    /// <summary>需要避开区域内的空格键位（QWERTY 第 3 行只有 7 键，靠右留白）。</summary>
    private string CurrentKey => _area == Area.Letters
        ? LetterRows[_row][_col].ToString()
        : DigitRows[_row][_col];

    /// <summary>当前所在区当前行的列数。</summary>
    private int CurrentRowLength => _area == Area.Letters ? LetterRows[_row].Length : DigitRows[_row].Length;

    // ─────────── 视觉 ───────────

    private readonly Grid _root = new()
    {
        ColumnSpacing = 14,
        RowDefinitions = [new RowDefinition(GridLength.Star)],
    };

    /// <summary>键帽视图（按 [区][行][列] 索引，供焦点重绘）。</summary>
    private readonly List<List<List<Border>>> _keys = [[], []];

    /// <summary>每个键帽里的文本（改字号/颜色时要拿它）。</summary>
    private readonly Dictionary<Border, Label> _keyLabels = [];

    private const double KeyHeight = 46;
    private const double KeyRadius = 9;

    public VirtualKeyboard()
    {
        _root.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(10, GridUnitType.Star)));
        _root.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(4, GridUnitType.Star)));

        _root.Add(BuildLetterArea(), 0, 0);
        _root.Add(BuildDigitArea(), 1, 0);

        Content = _root;
        Loaded += (_, _) => RenderFocus();
    }

    // ─────────── 构建 ───────────

    /// <summary>字母区：QWERTY 三行，行 2、3 缩进对齐物理键盘（视觉错位，导航仍是规整网格）。</summary>
    private View BuildLetterArea()
    {
        var stack = new VerticalStackLayout { Spacing = 6, VerticalOptions = LayoutOptions.Fill };
        var indents = new[] { 0.0, 0.5, 1.5 };   // 半键 / 一键半的缩进

        for (int r = 0; r < LetterRows.Length; r++)
        {
            var row = new Grid
            {
                ColumnSpacing = 6,
                VerticalOptions = LayoutOptions.Fill,
                Margin = new Thickness(indents[r] * 46, 0, 0, 0),   // 按半键宽换算缩进
            };
            var cells = new List<Border>();
            for (int c = 0; c < LetterRows[r].Length; c++)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                var key = BuildKey(LetterRows[r][c].ToString(), primary: false);
                row.Add(key, c, 0);
                cells.Add(key);
            }
            _keys[(int)Area.Letters].Add(cells);
            stack.Add(row);
        }
        return stack;
    }

    /// <summary>数字区：小键盘 3 列 × 4 行，末行 <c>0 / ⌫ / 搜索</c>（0 贴左，同物理小键盘）。</summary>
    private View BuildDigitArea()
    {
        var grid = new Grid
        {
            ColumnSpacing = 6,
            RowSpacing = 6,
            VerticalOptions = LayoutOptions.Fill,
        };
        for (int c = 0; c < 3; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (int r = 0; r < DigitRows.Length; r++)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));

        for (int r = 0; r < DigitRows.Length; r++)
        {
            var cells = new List<Border>();
            for (int c = 0; c < DigitRows[r].Length; c++)
            {
                var text = DigitRows[r][c];
                var key = BuildKey(text, primary: text == KeySearch);
                grid.Add(key, c, r);
                cells.Add(key);
            }
            _keys[(int)Area.Digits].Add(cells);
        }
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
            HeightRequest = KeyHeight,
            Content = label,
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
        if (!TryLocate(box, out var area, out var r, out var c)) return;
        _area = area;
        _row = r;
        _col = c;
        IsEngaged = true;
        RenderFocus();
        Activate();
    }

    private bool TryLocate(Border box, out Area area, out int row, out int col)
    {
        for (int a = 0; a < 2; a++)
            for (int r = 0; r < _keys[a].Count; r++)
                for (int c = 0; c < _keys[a][r].Count; c++)
                    if (ReferenceEquals(_keys[a][r][c], box))
                    {
                        area = (Area)a; row = r; col = c;
                        return true;
                    }
        area = Area.Letters; row = 0; col = 0;
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

        // 字母区最左 → 无路可走；数字区最左 → 回字母区同行
        if (_area == Area.Digits)
        {
            _area = Area.Letters;
            _row = Math.Min(_row, LetterRows.Length - 1);
            _col = LetterRows[_row].Length - 1;
            RenderFocus();
            return true;
        }
        return false;
    }

    public bool MoveRight()
    {
        if (_col < CurrentRowLength - 1) { _col++; RenderFocus(); return true; }

        // 字母区行尾 → 进数字区同序号行（字母 3 行 / 数字 4 行，故夹取）
        if (_area == Area.Letters)
        {
            _area = Area.Digits;
            _row = Math.Min(_row, DigitRows.Length - 1);
            _col = 0;
            RenderFocus();
            return true;
        }

        // 数字区行尾 → 交还内容区
        FocusExitRight?.Invoke(this, EventArgs.Empty);
        return false;
    }

    public bool MoveUp()
    {
        if (_row > 0) { _row--; ClampColumn(); RenderFocus(); return true; }
        return false;   // 已在顶行：交还外层（上方是输入框）
    }

    public bool MoveDown()
    {
        if (_row < RowCount - 1) { _row++; ClampColumn(); RenderFocus(); return true; }

        // 末行再往下 → 交还内容区
        FocusExitDown?.Invoke(this, EventArgs.Empty);
        return false;
    }

    private int RowCount => _area == Area.Letters ? LetterRows.Length : DigitRows.Length;

    /// <summary>换行后列位可能越界（各行长度不同）：夹到该行最后一列。</summary>
    private void ClampColumn()
    {
        var max = CurrentRowLength - 1;
        if (_col > max) _col = max;
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

        for (int a = 0; a < 2; a++)
            for (int r = 0; r < _keys[a].Count; r++)
                for (int c = 0; c < _keys[a][r].Count; c++)
                {
                    var box = _keys[a][r][c];
                    var label = _keyLabels[box];
                    var isCurrent = _engaged && a == (int)_area && r == _row && c == _col;
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
