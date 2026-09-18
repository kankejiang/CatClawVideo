using System.Windows.Input;
using Microsoft.Maui.Controls.Shapes;

namespace CatClawVideo.Maui.Services;

/// <summary>
/// 设置页的可聚焦行（自定义 Layout）。
///
/// <para>承担三件事：</para>
/// <list type="number">
///   <item>统一的外观：图标 + 标题 + 副标题 + 尾部（开关 / 步进器）+ 值 + 箭头；</item>
///   <item>统一的焦点视觉：常态 / 悬停 / 焦点三态（焦点 = 亮紫描边 + 柔光 + 左侧竖条 + 微放大）；</item>
///   <item>统一的交互：OK（Enter / 遥控器确认）触发 <see cref="Activated"/>，←→ 交给 <see cref="DecreaseCommand"/> / <see cref="IncreaseCommand"/> 调值。</item>
/// </list>
///
/// <para>焦点由 <see cref="SetHighlighted"/> 显式驱动（而非平台原生焦点系统）——
/// 这样 Android 遥控器与 Windows 键盘能得到完全一致的行为与视觉。</para>
/// </summary>
public sealed class FocusableRow : ContentView
{
    private readonly Border _root;
    private readonly BoxView _bar;
    private readonly Border _iconHost;
    private readonly Label _iconGlyph;
    private readonly Image _iconImage;
    private readonly Label _title;
    private readonly Label _subtitle;
    private readonly ContentView _trailingHost;
    private readonly Label _value;
    private readonly Label _arrow;

    private bool _hovered;
    private bool _highlighted;
    private bool _isActive;

    /// <summary>OK / 点击 激活。</summary>
    public event EventHandler? Activated;

    public ICommand? ActivateCommand { get; set; }
    public ICommand? DecreaseCommand { get; set; }
    public ICommand? IncreaseCommand { get; set; }

    /// <summary>分组标题行：不参与焦点、不响应点击，仅作视觉分隔。</summary>
    public bool IsGroup { get; set; }

    /// <summary>是否响应激活（false 时 OK / 点击无效果）。</summary>
    public bool IsInteractive { get; set; } = true;

    /// <summary>是否处于「当前选中」（侧栏导航用；与焦点独立，可同时成立）。</summary>
    public bool IsActive
    {
        get => _isActive;
        set { if (_isActive == value) return; _isActive = value; UpdateVisual(); }
    }

    public bool IsHighlighted => _highlighted;

    /// <summary>随行负载（如 SettingsViewModel 中对应的项）。</summary>
    public object? Data { get; set; }

    public FocusableRow()
    {
        _bar = new BoxView
        {
            WidthRequest = 3.5,
            CornerRadius = 2,
            IsVisible = false,
            VerticalOptions = LayoutOptions.Fill,
            Margin = new Thickness(0, 9, 0, 9),
            Color = Res("PrimaryColor", Color.FromArgb("#9B7ED8")),
        };

        _iconGlyph = new Label
        {
            FontSize = 16,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            TextColor = Res("TextPrimaryColor", Colors.White),
        };
        _iconImage = new Image
        {
            Aspect = Aspect.AspectFit,
            IsVisible = false,
            WidthRequest = 18,
            HeightRequest = 18,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
        };
        _iconHost = new Border
        {
            WidthRequest = 40,
            HeightRequest = 40,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            BackgroundColor = Res("ChipInactiveColor", Colors.Transparent),
            Content = new Grid { Children = { _iconGlyph, _iconImage } },
        };

        _title = new Label
        {
            FontSize = 14.5,
            FontFamily = "OpenSansSemibold",
            TextColor = Res("TextPrimaryColor", Colors.White),
        };
        _subtitle = new Label
        {
            FontSize = 11.5,
            Margin = new Thickness(0, 3, 0, 0),
            LineBreakMode = LineBreakMode.WordWrap,
            IsVisible = false,
            TextColor = Res("TextHintColor", Colors.Gray),
        };
        var text = new VerticalStackLayout
        {
            Spacing = 0,
            VerticalOptions = LayoutOptions.Center,
            Children = { _title, _subtitle },
        };

        _trailingHost = new ContentView { VerticalOptions = LayoutOptions.Center, IsVisible = false };
        _value = new Label
        {
            FontSize = 13,
            VerticalOptions = LayoutOptions.Center,
            IsVisible = false,
            TextColor = Res("TextSecondaryColor", Colors.Gray),
        };
        _arrow = new Label
        {
            Text = "›",
            FontSize = 16,
            Opacity = 0.5,
            VerticalOptions = LayoutOptions.Center,
            IsVisible = false,
            TextColor = Res("TextHintColor", Colors.Gray),
        };

        var grid = new Grid
        {
            ColumnSpacing = 14,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),   // 0 焦点竖条
                new ColumnDefinition(GridLength.Auto),   // 1 图标
                new ColumnDefinition(GridLength.Star),   // 2 标题 / 副标题
                new ColumnDefinition(GridLength.Auto),   // 3 尾部（开关 / 步进器）
                new ColumnDefinition(GridLength.Auto),   // 4 值文本
                new ColumnDefinition(GridLength.Auto),   // 5 箭头
            },
        };
        grid.Add(_bar, 0, 0);
        grid.Add(_iconHost, 1, 0);
        grid.Add(text, 2, 0);
        grid.Add(_trailingHost, 3, 0);
        grid.Add(_value, 4, 0);
        grid.Add(_arrow, 5, 0);

        _root = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 11 },
            BackgroundColor = Colors.Transparent,
            Padding = new Thickness(6, 13, 18, 13),
            Content = grid,
        };
        Content = _root;

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => { if (IsInteractive) Activate(); };
        GestureRecognizers.Add(tap);

        var pointer = new PointerGestureRecognizer();
        pointer.PointerEntered += (_, _) => { _hovered = true; UpdateVisual(); };
        pointer.PointerExited += (_, _) => { _hovered = false; UpdateVisual(); };
        GestureRecognizers.Add(pointer);
    }

    // ─────────── 外观属性 ───────────

    public string Glyph
    {
        set { _iconGlyph.Text = value; _iconGlyph.IsVisible = !string.IsNullOrEmpty(value); }
    }

    public string IconSource
    {
        set
        {
            _iconImage.Source = value;
            bool has = !string.IsNullOrEmpty(value);
            _iconImage.IsVisible = has;
            _iconGlyph.IsVisible = !has;
        }
    }

    public Color IconBackground
    {
        set => _iconHost.BackgroundColor = value;
    }

    public Color IconForeground
    {
        set => _iconGlyph.TextColor = value;
    }

    public string Title
    {
        set => _title.Text = value;
    }

    public string Subtitle
    {
        set { _subtitle.Text = value; _subtitle.IsVisible = !string.IsNullOrEmpty(value); }
    }

    public string ValueText
    {
        set { _value.Text = value; _value.IsVisible = !string.IsNullOrEmpty(value); }
    }

    public bool ShowArrow
    {
        set => _arrow.IsVisible = value;
    }

    /// <summary>尾部自定义内容（开关 / 步进器）。</summary>
    public View? Trailing
    {
        set
        {
            _trailingHost.Content = value;
            _trailingHost.IsVisible = value is not null;
        }
    }

    /// <summary>
    /// 按密度系数调整字号与内边距（<c>1.0</c> = 标准横屏；<c>0.92</c> = 窄横屏）。
    /// 只做横屏，无竖屏分支。
    /// </summary>
    public void ApplyDensity(double factor)
    {
        _title.FontSize = 14.5 * factor;
        _subtitle.FontSize = 11.5 * factor;
        _value.FontSize = 13 * factor;
        _arrow.FontSize = 16 * factor;

        bool compact = factor < 0.97;
        _root.Padding = compact ? new Thickness(6, 9, 14, 9) : new Thickness(6, 13, 18, 13);
        _iconHost.WidthRequest = compact ? 32 : 40;
        _iconHost.HeightRequest = compact ? 32 : 40;
        _iconGlyph.FontSize = compact ? 13 : 16;
    }

    // ─────────── 焦点 ───────────

    public void SetHighlighted(bool on)
    {
        if (_highlighted == on) return;
        _highlighted = on;
        UpdateVisual();
    }

    /// <summary>OK / 点击激活（外部调用，如页面统一处理 Enter）。</summary>
    public void Activate()
    {
        if (ActivateCommand?.CanExecute(null) == true) ActivateCommand.Execute(null);
        Activated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>← 调值。</summary>
    public bool Decrease()
    {
        if (DecreaseCommand?.CanExecute(null) != true) return false;
        DecreaseCommand.Execute(null);
        return true;
    }

    /// <summary>→ 调值。</summary>
    public bool Increase()
    {
        if (IncreaseCommand?.CanExecute(null) != true) return false;
        IncreaseCommand.Execute(null);
        return true;
    }

    /// <summary>主题切换后刷新所有取自资源字典的颜色。</summary>
    public void RefreshThemeColors()
    {
        _bar.Color = Res("PrimaryColor", Color.FromArgb("#9B7ED8"));
        _title.TextColor = Res("TextPrimaryColor", Colors.White);
        _subtitle.TextColor = Res("TextHintColor", Colors.Gray);
        _value.TextColor = Res("TextSecondaryColor", Colors.Gray);
        _arrow.TextColor = Res("TextHintColor", Colors.Gray);
        UpdateVisual();
    }

    private void UpdateVisual()
    {
        if (IsGroup)
        {
            _root.BackgroundColor = Colors.Transparent;
            _root.StrokeThickness = 0;
            _bar.IsVisible = false;
            Shadow = null;
            return;
        }

        var primary = Res("PrimaryColor", Color.FromArgb("#9B7ED8"));

        _bar.IsVisible = _highlighted;
        _bar.Color = primary;

        if (_highlighted)
        {
            _root.BackgroundColor = primary.WithAlpha(0.17f);
            _root.Stroke = new SolidColorBrush(primary);
            _root.StrokeThickness = 2.5;
            Scale = 1.02;
            Shadow = new Shadow
            {
                Brush = new SolidColorBrush(primary),
                Radius = 18,
                Offset = new Point(0, 6),
                Opacity = 0.5f,
            };
        }
        else
        {
            _root.StrokeThickness = 0;
            Scale = 1;
            Shadow = null;
            _root.BackgroundColor = _isActive
                ? primary.WithAlpha(0.14f)
                : _hovered ? primary.WithAlpha(0.09f) : Colors.Transparent;
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
