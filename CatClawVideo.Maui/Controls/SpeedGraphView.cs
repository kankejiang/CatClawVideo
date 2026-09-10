namespace CatClawVideo.Maui.Controls;

/// <summary>
/// 下载速度曲线图（节点图）：把最近 N 个速度采样画成半透明填充折线。
/// GraphicsView 自绘（项目内零依赖）；Samples 变化自动重绘。
/// </summary>
public class SpeedGraphView : GraphicsView
{
    /// <summary>速度采样序列（B/s，等间隔），长度即时间窗口</summary>
    public static readonly BindableProperty SamplesProperty =
        BindableProperty.Create(nameof(Samples), typeof(long[]), typeof(SpeedGraphView),
            Array.Empty<long>(), propertyChanged: OnSamplesChanged);

    public long[] Samples
    {
        get => (long[])GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    private readonly SpeedGraphDrawable _drawable = new();

    public SpeedGraphView()
    {
        Drawable = _drawable;
    }

    private static void OnSamplesChanged(BindableObject d, object oldValue, object newValue)
    {
        if (d is SpeedGraphView view)
        {
            view._drawable.SetSamples(newValue as long[] ?? Array.Empty<long>());
            view.Invalidate();
        }
    }

    private sealed class SpeedGraphDrawable : IDrawable
    {
        private long[] _samples = Array.Empty<long>();

        public void SetSamples(long[] samples) => _samples = samples;

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            // 背景
            canvas.FillColor = Color.FromArgb("#14141820");
            canvas.FillRectangle(dirtyRect);

            if (_samples.Length < 2)
                return;

            var max = Math.Max(_samples.Max(), 1L);
            var w = dirtyRect.Width;
            var h = dirtyRect.Height;
            var stepX = w / (_samples.Length - 1);

            // 速度曲线（底部对齐的填充 + 上沿描线）
            var path = new PathF();
            for (int i = 0; i < _samples.Length; i++)
            {
                var x = dirtyRect.X + i * stepX;
                var y = dirtyRect.Y + h - (float)(_samples[i] / (double)max * (h - 2)) - 1;
                if (i == 0) path.MoveTo(x, y);
                else path.LineTo(x, y);
            }

            // 填充区域（曲线下方）
            var fill = new PathF(path);
            fill.LineTo(dirtyRect.Right, dirtyRect.Bottom);
            fill.LineTo(dirtyRect.Left, dirtyRect.Bottom);
            fill.Close();
            canvas.FillColor = Color.FromArgb("#3342A5F5");
            canvas.FillPath(fill);

            // 描线
            canvas.StrokeColor = Color.FromArgb("#42A5F5");
            canvas.StrokeSize = 1.5f;
            canvas.DrawPath(path);
        }
    }
}
