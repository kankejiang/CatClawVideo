using Microsoft.Maui.Graphics;

namespace CatClawVideo.Maui.Controls;

/// <summary>
/// 缓冲指示（自绘）：圆环进度 + 中央百分比。
///
/// <para><b>为什么不用 <see cref="ActivityIndicator"/></b>：转圈只能表达「在忙」，
/// 用户完全不知道还要等多久。磁力边下边播、或长片拖到未缓冲区域时，等待可能十几秒，
/// 必须给出可见的进度感。</para>
///
/// <para><b>两种形态</b>：<see cref="IsIndeterminate"/> = true 时画旋转弧
/// （解析中 / 时长未知，算不出百分比）；false 时画进度环并在中央显示整数百分比。</para>
/// </summary>
public sealed class BufferProgressView : GraphicsView
{
    private readonly RingDrawable _ring = new();
    private double _progress;
    private bool _indeterminate = true;
    private double _spin;
    private IDispatcherTimer? _spinTimer;

    public BufferProgressView()
    {
        Drawable = _ring;
        InputTransparent = true;
        HorizontalOptions = LayoutOptions.Center;
        VerticalOptions = LayoutOptions.Center;
        _ring.SetState(_progress, _indeterminate, _spin);
        StartSpin();
    }

    /// <summary>外径（含描边）</summary>
    public double Diameter
    {
        get => WidthRequest;
        set
        {
            WidthRequest = value;
            HeightRequest = value;
        }
    }

    /// <summary>缓冲进度 0~1（<see cref="IsIndeterminate"/> 为 true 时忽略）。</summary>
    public double Progress
    {
        get => _progress;
        set
        {
            var v = Math.Clamp(value, 0, 1);
            if (Math.Abs(v - _progress) < 0.002) return;   // 抖动抑制：百分比变化才重绘
            _progress = v;
            _ring.SetState(_progress, _indeterminate, _spin);
            Invalidate();
        }
    }

    /// <summary>不确定进度：画旋转弧、不显示百分比。</summary>
    public bool IsIndeterminate
    {
        get => _indeterminate;
        set
        {
            if (_indeterminate == value) return;
            _indeterminate = value;
            _ring.SetState(_progress, _indeterminate, _spin);
            Invalidate();
            if (value) StartSpin();
            else StopSpin();
        }
    }

    // ─────────── 旋转动画（仅不确定态） ───────────

    private void StartSpin()
    {
        try
        {
            if (_spinTimer is null)
            {
                _spinTimer = Dispatcher.CreateTimer();
                _spinTimer.Interval = TimeSpan.FromMilliseconds(60);
                _spinTimer.IsRepeating = true;
                _spinTimer.Tick += (_, _) =>
                {
                    _spin = (_spin + 12) % 360;
                    _ring.SetState(_progress, _indeterminate, _spin);
                    Invalidate();
                };
            }
            if (!_spinTimer.IsRunning) _spinTimer.Start();
        }
        catch { /* Dispatcher 尚未就绪：保持静态弧，不影响功能 */ }
    }

    private void StopSpin()
    {
        try { if (_spinTimer?.IsRunning == true) _spinTimer.Stop(); } catch { }
    }

    // ─────────── 绘制 ───────────

    private sealed class RingDrawable : IDrawable
    {
        private double _progress;
        private bool _indeterminate;
        private double _spin;

        public void SetState(double progress, bool indeterminate, double spin)
        {
            _progress = progress;
            _indeterminate = indeterminate;
            _spin = spin;
        }

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            var size = Math.Min(dirtyRect.Width, dirtyRect.Height);
            if (size <= 4) return;

            var stroke = Math.Max(3f, size * 0.075f);        // 环粗随控件尺寸缩放
            var pad = stroke / 2f + 1f;
            var box = new RectF(
                dirtyRect.Center.X - size / 2f + pad,
                dirtyRect.Center.Y - size / 2f + pad,
                size - pad * 2f,
                size - pad * 2f);

            canvas.StrokeSize = stroke;
            canvas.StrokeLineCap = LineCap.Round;

            // 底环：深色画面上 20% 白足够可见
            canvas.StrokeColor = Color.FromArgb("#33FFFFFF");
            canvas.DrawEllipse(box);

            var primary = Res("PrimaryColor", Color.FromArgb("#9B7ED8"));
            var accent = Res("AccentColor", Color.FromArgb("#55D6FF"));

            if (_indeterminate)
            {
                // 不确定态：90° 弧持续旋转（视觉上等价于原来的转圈，但可控）
                canvas.StrokeColor = primary;
                var start = (float)(_spin % 360.0);
                canvas.DrawArc(box.X, box.Y, box.Width, box.Height, start, start + 90, true, false);
                return;
            }

            if (_progress > 0.002)
            {
                canvas.StrokeColor = Blend(primary, accent, (float)_progress);
                canvas.DrawArc(box.X, box.Y, box.Width, box.Height,
                    -90f, -90f + (float)(_progress * 360.0), true, false);
            }

            // 中央百分比（与圆环同心；用 DrawString 的居中能力避免手工测量字宽）
            canvas.Font = new Microsoft.Maui.Graphics.Font("OpenSansSemibold");
            canvas.FontSize = size * 0.26f;
            canvas.FontColor = Colors.White;
            canvas.DrawString($"{(int)Math.Round(_progress * 100)}%",
                dirtyRect.X, dirtyRect.Y, dirtyRect.Width, dirtyRect.Height,
                HorizontalAlignment.Center, VerticalAlignment.Center);
        }

        /// <summary>两色线性插值（进度环从主题紫渐变到青色，与进度轴观感一致）。</summary>
        private static Color Blend(Color a, Color b, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            return new Color(
                a.Red + (b.Red - a.Red) * t,
                a.Green + (b.Green - a.Green) * t,
                a.Blue + (b.Blue - a.Blue) * t,
                a.Alpha + (b.Alpha - a.Alpha) * t);
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
}
