using Microsoft.Maui.Controls.Shapes;

namespace CatClawVideo.Maui.Controls;

/// <summary>
/// 播放控件用的图标/文字按钮（自绘）。
///
/// <para>替代零散的 <c>ImageButton</c>：统一尺寸、圆角、hover 反馈，
/// 并支持角标（如 ±10s 的「10」）与纯文字模式（倍速「1.0×」）。
/// 图标直接用字形而非 PNG —— 播放控件里有不少符号资源库里没有（⛶ ❚❚ «»）,
/// 用字形可避免为此新增一批 svg。</para>
///
/// <para><b>文字按钮</b>（<see cref="AutoWidth"/> = true）：宽度按文案自适应，
/// 用于「上一集 / 播放 / 快进10秒」这类中文标签 —— 比符号直观得多。</para>
/// </summary>
public sealed class IconButton : ContentView
{
    private readonly Border _box;
    private readonly Label _glyph;
    private readonly Label _badge;

    private bool _hovered;

    public event EventHandler? Clicked;

    /// <summary>按钮边长（方块高度；文字按钮下即最小高度）。</summary>
    public double Size { get; set; } = 40;

    /// <summary>主按钮（实底主题色）。</summary>
    public bool IsPrimary { get; set; }

    /// <summary>文字模式（字号更小，适合「1.0×」这类）。</summary>
    public bool IsText { get; set; }

    /// <summary>宽度自适应内容（文字按钮用）；高度仍取 <see cref="Size"/>。</summary>
    public bool AutoWidth { get; set; }

    /// <summary>文字按钮的左右内边距。</summary>
    public double HorizontalPadding { get; set; } = 11;

    /// <summary>文字按钮的最小宽度 —— 避免文案长短切换（如「静音」↔「取消静音」）时整排按钮跳动。</summary>
    public double MinWidth { get; set; }

    /// <summary>
    /// 整体缩放（1.0 = 桌面默认）。手机等小屏的播放框本身就小，
    /// 固定的 38~42 按钮 + 13 号字会「吃掉」大半个画面（2026-09-19 实机反馈），
    /// 由宿主按播放框尺寸整体等比缩小。所有基准值保持不变，缩放只在这里统一施加。
    /// </summary>
    public double UiScale { get; set; } = 1.0;

    /// <summary>角标文字（如「10」）；空则不显示。</summary>
    public string? Badge
    {
        get => _badge.IsVisible ? _badge.Text : null;
        set
        {
            _badge.Text = value;
            _badge.IsVisible = !string.IsNullOrEmpty(value);
        }
    }

    public string Tooltip { get; set; } = string.Empty;

    public string Glyph
    {
        get => _glyph.Text ?? string.Empty;
        set
        {
            _glyph.Text = value;
            if (AutoWidth) Render();   // 文案长度变化 → 重算宽度（否则会截断/留白）
        }
    }

    public IconButton(string glyph, string tooltip, Action onClick)
    {
        Tooltip = tooltip;

        _glyph = new Label
        {
            Text = glyph,
            FontSize = 16,
            TextColor = Colors.White,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
        };
        _badge = new Label
        {
            FontSize = 8.5,
            FontFamily = "OpenSansSemibold",
            TextColor = Color.FromArgb("#D9FFFFFF"),
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.End,
            Margin = new Thickness(0, 0, 0, 4),
            IsVisible = false,
        };

        _box = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 13 },
            BackgroundColor = Colors.Transparent,
            Content = new Grid { Children = { _glyph, _badge } },
        };
        Content = _box;

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => onClick();
        GestureRecognizers.Add(tap);

        var pointer = new PointerGestureRecognizer();
        pointer.PointerEntered += (_, _) => { _hovered = true; Render(); };
        pointer.PointerExited += (_, _) => { _hovered = false; Render(); };
        GestureRecognizers.Add(pointer);

        Loaded += (_, _) => Render();
    }

    private void Render()
    {
        var s = UiScale;
        HeightRequest = Size * s;
        if (AutoWidth)
        {
            var text = _glyph.Text ?? string.Empty;
            // 粗估文本宽度：中文按整字、其余按 0.62 字宽（与 OpenSans 常规字面接近）。
            // 只用于设定按钮框宽，字号小、误差无碍；最小宽度兜住短文案的点击区域。
            double textWidth = 0;
            foreach (var c in text) textWidth += c > 0x2E80 ? 13.0 : 7.6;
            WidthRequest = Math.Max(
                MinWidth * s,
                Math.Max(Size * s, (textWidth + HorizontalPadding * 2) * s));
        }
        else
        {
            WidthRequest = Size * s;
        }

        var primary = Res("PrimaryColor", Color.FromArgb("#9B7ED8"));

        if (IsPrimary)
        {
            _box.BackgroundColor = _hovered ? primary.WithAlpha(1f) : primary.WithAlpha(0.92f);
            _box.StrokeThickness = 0;
        }
        else
        {
            _box.BackgroundColor = _hovered ? Color.FromArgb("#2EFFFFFF") : Color.FromArgb("#1AFFFFFF");
            _box.StrokeThickness = 0;
        }

        _glyph.FontSize = (IsText ? 12.5 : (AutoWidth ? 13 : Size >= 46 ? 19 : 16)) * s;
        _glyph.FontFamily = IsText || AutoWidth ? "OpenSansSemibold" : "OpenSansRegular";
        _badge.FontSize = 8.5 * s;
        _badge.Margin = new Thickness(0, 0, 0, 4 * s);
        _box.StrokeShape = new RoundRectangle { CornerRadius = 13 * s };
    }

    /// <summary>设置缩放后重算尺寸与字号（宿主改 <see cref="UiScale"/> 后调用）。</summary>
    public void ApplyScale() => Render();

    /// <summary>主题切换后刷新配色。</summary>
    public void RefreshThemeColors() => Render();

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
