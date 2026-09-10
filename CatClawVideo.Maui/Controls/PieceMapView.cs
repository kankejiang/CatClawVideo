namespace CatClawVideo.Maui.Controls;

/// <summary>
/// BT 分片地图（Motrix 任务详情"进度"页同款）：把 Bitfield 画成方格矩阵，
/// 绿色=已下载、浅灰=未下载。用 GraphicsView 单次绘制，避免上千个子控件。
/// </summary>
public class PieceMapView : GraphicsView
{
    public static readonly BindableProperty PiecesProperty = BindableProperty.Create(
        nameof(Pieces), typeof(bool[]), typeof(PieceMapView), null,
        propertyChanged: (b, _, _) => ((PieceMapView)b).Invalidate());

    /// <summary>分片完成位图（true = 已下载）</summary>
    public bool[]? Pieces
    {
        get => (bool[]?)GetValue(PiecesProperty);
        set => SetValue(PiecesProperty, value);
    }

    public static readonly BindableProperty DoneColorProperty = BindableProperty.Create(
        nameof(DoneColor), typeof(Color), typeof(PieceMapView), Color.FromArgb("#4CAF50"),
        propertyChanged: (b, _, _) => ((PieceMapView)b).Invalidate());

    /// <summary>已下载分片颜色</summary>
    public Color DoneColor
    {
        get => (Color)GetValue(DoneColorProperty);
        set => SetValue(DoneColorProperty, value);
    }

    public static readonly BindableProperty PendingColorProperty = BindableProperty.Create(
        nameof(PendingColor), typeof(Color), typeof(PieceMapView), Color.FromArgb("#E0E0E0"),
        propertyChanged: (b, _, _) => ((PieceMapView)b).Invalidate());

    /// <summary>未下载分片颜色</summary>
    public Color PendingColor
    {
        get => (Color)GetValue(PendingColorProperty);
        set => SetValue(PendingColorProperty, value);
    }

    public PieceMapView()
    {
        Drawable = new PieceDrawable(this);
    }

    private sealed class PieceDrawable : IDrawable
    {
        private readonly PieceMapView _view;
        public PieceDrawable(PieceMapView view) => _view = view;

        public void Draw(ICanvas canvas, RectF rect)
        {
            var pieces = _view.Pieces;
            if (pieces == null || pieces.Length == 0)
            {
                canvas.FontColor = Colors.Gray;
                canvas.FontSize = 12;
                canvas.DrawString("无分片数据", rect, HorizontalAlignment.Center, VerticalAlignment.Center);
                return;
            }

            const float cell = 6f;   // 单元尺寸（含间距）
            const float gap = 1.2f;
            int cols = Math.Max(1, (int)(rect.Width / cell));
            int rows = (int)Math.Ceiling(pieces.Length / (double)cols);

            // 自适应：行数放不下时缩小单元
            float size = Math.Min(cell - gap, (rect.Height / Math.Max(rows, 1)) - gap);
            if (size < 1f) size = 1f;
            float stepX = size + gap;
            float stepY = size + gap;
            float totalW = cols * stepX;
            float offsetX = rect.X + Math.Max(0, (rect.Width - totalW) / 2f);

            for (int i = 0; i < pieces.Length; i++)
            {
                int r = i / cols, c = i % cols;
                canvas.FillColor = pieces[i] ? _view.DoneColor : _view.PendingColor;
                canvas.FillRoundedRectangle(
                    offsetX + c * stepX, rect.Y + r * stepY, size, size, 1f);
            }
        }
    }
}
