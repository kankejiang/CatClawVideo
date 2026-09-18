using Microsoft.Maui.Graphics;

namespace CatClawVideo.Maui.Controls;

/// <summary>
/// 进度轴（自绘）：三层结构 —— 底轨 / <b>已缓冲</b> / 已播放，外加发光滑块与拖拽时间气泡。
///
/// <para><b>为什么不用 <see cref="Slider"/></b>：MAUI 的 Slider 无法表达「已缓冲区间」，
/// 轨道粗细与滑块尺寸也受平台限制（原实现只有一条 40% 白的细线，45 分钟的片子看不出位置）。
/// 自绘后可以做到：悬停/焦点加粗、紫青渐变进度、缓冲区间可视化、拖拽气泡预览。</para>
///
/// <para><b>交互</b>：点击 = 绝对跳转（<c>Tapped</c> 能给出相对坐标）；
/// 拖拽 = 在当前位置基础上相对调节（<c>PanGestureRecognizer</c> 只给增量，拿不到按下点）。
/// 精确到秒的微调由外部的 ±10s 按钮承担。</para>
/// </summary>
public sealed class SeekBarView : GraphicsView
{
    /// <summary>用户请求跳转到指定秒数（拖拽过程中也会持续触发，便于实时预览）。</summary>
    public event EventHandler<double>? SeekRequested;

    /// <summary>开始拖动（外部据此暂停进度回写、阻止控制层自动隐藏）。</summary>
    public event EventHandler? SeekStarted;

    /// <summary>结束拖动（外部据此真正 seek 并恢复自动隐藏）。</summary>
    public event EventHandler? SeekCompleted;

    private readonly BarDrawable _bar = new();
    private double _position;
    private double _duration;
    private double _buffered;
    private bool _highlighted;
    private double _dragRatio = double.NaN;
    private double _panBaseRatio;

    /// <summary>轨道基础粗细（像素）；悬停/焦点时自动加粗。</summary>
    public double TrackThickness { get; set; } = 6;

    public SeekBarView()
    {
        Drawable = _bar;
        HeightRequest = 34;                 // 命中区域要够大（遥控器/触屏都好点）
        VerticalOptions = LayoutOptions.Center;

        var pan = new PanGestureRecognizer();
        pan.PanUpdated += OnPanUpdated;
        GestureRecognizers.Add(pan);

        var tap = new TapGestureRecognizer();
        tap.Tapped += OnTapped;
        GestureRecognizers.Add(tap);
    }

    /// <summary>当前播放位置（秒）。</summary>
    public double Position
    {
        get => _position;
        set { _position = value; Refresh(); }
    }

    /// <summary>总时长（秒）；0 表示未知。</summary>
    public double Duration
    {
        get => _duration;
        set { _duration = value; Refresh(); }
    }

    /// <summary>已缓冲到的位置（秒）。</summary>
    public double Buffered
    {
        get => _buffered;
        set { _buffered = value; Refresh(); }
    }

    /// <summary>焦点高亮（遥控器焦点落在轴上时加粗 + 滑块发光）。</summary>
    public bool IsHighlighted
    {
        get => _highlighted;
        set { _highlighted = value; Refresh(); }
    }

    private void Refresh()
    {
        _bar.SetState(_position, _duration, _buffered, _dragRatio, _highlighted);
        Invalidate();
    }

    // ─────────── 交互 ───────────

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (_duration <= 0) return;
        var p = e.GetPosition(this);
        if (p is null) return;

        var ratio = RatioAt(p.Value.X);
        SeekRequested?.Invoke(this, ratio * _duration);
    }

    private void OnPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        if (_duration <= 0) return;

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _panBaseRatio = _dragRatio = CurrentRatio();
                SeekStarted?.Invoke(this, EventArgs.Empty);
                Refresh();
                break;

            case GestureStatus.Running:
                var width = Math.Max(1, Width);
                _dragRatio = Math.Clamp(_panBaseRatio + e.TotalX / width, 0, 1);
                SeekRequested?.Invoke(this, _dragRatio * _duration);   // 实时预览
                Refresh();
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                var final = _dragRatio;
                _dragRatio = double.NaN;
                Refresh();
                SeekCompleted?.Invoke(this, EventArgs.Empty);
                if (!double.IsNaN(final)) SeekRequested?.Invoke(this, final * _duration);
                break;
        }
    }

    private double CurrentRatio() =>
        _duration > 0 ? Math.Clamp(_position / _duration, 0, 1) : 0;

    /// <summary>把视图内 X 坐标换算成 0~1 比例（两端留出滑块半径，避免滑块被裁）。</summary>
    private double RatioAt(double x)
    {
        var pad = _bar.Pad;
        var usable = Math.Max(1, Width - pad * 2);
        return Math.Clamp((x - pad) / usable, 0, 1);
    }

    // ─────────── 绘制 ───────────

    private sealed class BarDrawable : IDrawable
    {
        private double _position, _duration, _buffered, _dragRatio = double.NaN;
        private bool _highlighted;

        /// <summary>两端留白（滑块半径）—— 比例换算与绘制必须用同一个值。</summary>
        public double Pad { get; private set; } = 8;

        public void SetState(double position, double duration, double buffered, double dragRatio, bool highlighted)
        {
            _position = position;
            _duration = duration;
            _buffered = buffered;
            _dragRatio = dragRatio;
            _highlighted = highlighted;
        }

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            var playRatio = _duration > 0 ? Math.Clamp(_position / _duration, 0, 1) : 0;
            var ratio = double.IsNaN(_dragRatio) ? playRatio : _dragRatio;
            var bufRatio = _duration > 0 ? Math.Clamp(_buffered / _duration, 0, 1) : 0;
            bufRatio = Math.Max(bufRatio, playRatio);

            var active = _highlighted || !double.IsNaN(_dragRatio);
            var h = active ? 10f : 6f;
            var knobR = active ? 9.5f : 7.5f;

            Pad = knobR + 1;
            var left = (float)(dirtyRect.X + Pad);
            var right = (float)(dirtyRect.Right - Pad);
            var width = Math.Max(1, right - left);
            var cy = dirtyRect.Center.Y;
            var top = cy - h / 2;

            // ① 底轨（未缓冲：很淡，只作轨道底色）
            canvas.FillColor = Color.FromArgb("#2EFFFFFF");
            canvas.FillRoundedRectangle(left, top, width, h, h / 2);

            // ② 已缓冲（明显亮于底轨；深色画面上要一眼可辨）
            if (bufRatio > 0.001f)
            {
                canvas.FillColor = Color.FromArgb("#8CFFFFFF");
                canvas.FillRoundedRectangle(left, top, width * (float)bufRatio, h, h / 2);
            }

            // ③ 已播放（主题色 → 青色渐变）
            var played = width * (float)ratio;
            if (played > 0.5f)
            {
                var primary = Res("PrimaryColor", Color.FromArgb("#9B7ED8"));
                var accent = Res("AccentColor", Color.FromArgb("#55D6FF"));
                canvas.SetFillPaint(
                    new LinearGradientPaint
                    {
                        StartColor = primary,
                        EndColor = accent,
                    },
                    new RectF(left, top, played, h));
                canvas.FillRoundedRectangle(left, top, played, h, h / 2);
            }

            // ④ 滑块（拖动/高亮时带发光圈）
            var kx = left + width * (float)ratio;
            if (active)
            {
                canvas.FillColor = Res("PrimaryColor", Color.FromArgb("#9B7ED8")).WithAlpha(0.30f);
                canvas.FillCircle(kx, cy, knobR + 6);
            }
            canvas.FillColor = Colors.White;
            canvas.FillCircle(kx, cy, knobR);

            // ⑤ 拖拽时间气泡
            if (!double.IsNaN(_dragRatio) && _duration > 0)
            {
                var label = FormatTime(ratio * _duration);
                canvas.FontSize = 12;
                canvas.FontColor = Colors.White;

                const float bw = 62f, bh = 24f;
                var bx = Math.Clamp(kx - bw / 2, dirtyRect.X, dirtyRect.Right - bw);
                var by = top - bh - 12;

                canvas.FillColor = Color.FromArgb("#F0141030");
                canvas.FillRoundedRectangle(bx, by, bw, bh, 7);
                canvas.DrawString(label, bx, by, bw, bh, HorizontalAlignment.Center, VerticalAlignment.Center);
            }
        }

        private static string FormatTime(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
                : $"{t.Minutes:D2}:{t.Seconds:D2}";
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
