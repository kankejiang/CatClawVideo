using System.Drawing.Imaging;

namespace StreamProbe;

/// <summary>
/// 客户端显示窗口（WinForms，独立于 CatClawVideo.Maui）：
/// <list type="bullet">
/// <item>收流线程把 JPEG 帧交给 UI 线程显示（PictureBox 双缓冲，Zoom 保持比例）；</item>
/// <item>鼠标左键 → 触摸 0x10 down/up，移动 → move（16ms 节流），右键 → Android BACK 键，滚轮 → 0x12；</item>
/// <item>键盘 → 0x11（键码语义 = Android keycodes，见 <see cref="AndroidKeys"/>）；</item>
/// <item>坐标按 Zoom 模式的图像矩形换算回服务端分辨率（含 letterbox 边距补偿）。</item>
/// </list>
/// </summary>
public sealed class ViewWindow : Form
{
    private readonly Protocol.HandshakeInfo _info;
    private readonly Func<byte, ReadOnlyMemory<byte>, Task> _sendFrameAsync;
    private readonly PictureBox _box;
    private readonly CancellationToken _ct;

    /// <summary>图像区相对控件区的矩形（Zoom 模式 letterbox 后的实际图像位置，控件像素）。</summary>
    private Rectangle _imageRect = Rectangle.Empty;

    /// <summary>渲染帧缓存：收流线程写入新帧引用，UI 线程定时取走显示（避免每帧 BeginInvoke 风暴）。</summary>
    private Bitmap? _pending;
    private long _framesShown;
    private long _framesTotal;

    private long _lastMoveSentMs;
    private long _lastFpsTick = Environment.TickCount64;
    private int _fpsWindow;

    /// <summary>帧率（客户端实测，供标题栏/验收）。</summary>
    public double Fps { get; private set; }

    public long FramesTotal => Interlocked.Read(ref _framesTotal);

    /// <param name="sendFrameAsync">发送帧回调（写入端有锁，UI 线程可直发）。</param>
    public ViewWindow(Protocol.HandshakeInfo info, Func<byte, ReadOnlyMemory<byte>, Task> sendFrameAsync, CancellationToken ct)
    {
        _info = info;
        _sendFrameAsync = sendFrameAsync;
        _ct = ct;

        Text = $"stream-probe — CATCLAW/1 {info.Width}x{info.Height} {info.Codec}@{info.Fps}";
        // 窗口按设备宽高比自适应：占工作区 ≤85%，且不超过设备尺寸的 1.5 倍
        var wa = Screen.PrimaryScreen.WorkingArea;
        double scale = Math.Min(0.85 * wa.Width / info.Width, 0.85 * wa.Height / info.Height);
        scale = Math.Min(scale, 1.5);
        ClientSize = new Size(Math.Max((int)(info.Width * scale), 640),
                              Math.Max((int)(info.Height * scale), 360));
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        DoubleBuffered = true;

        _box = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(12, 12, 16),
        };
        _box.MouseDown += OnMouseDown;
        _box.MouseUp += OnMouseUp;
        _box.MouseMove += OnMouseMove;
        _box.MouseWheel += OnMouseWheel;
        _box.KeyDown += OnKeyDown;
        _box.KeyUp += OnKeyUp;
        _box.Resize += (_, _) => UpdateImageRect();

        Controls.Add(_box);
    }

    /// <summary>收流线程调用：提交新 JPEG 帧（内部解码为 Bitmap，UI 定时器取走显示）。</summary>
    public void SubmitJpeg(byte[] jpeg)
    {
        try
        {
            using var ms = new MemoryStream(jpeg);
            var bmp = new Bitmap(ms);
            var old = Interlocked.Exchange(ref _pending, bmp);
            old?.Dispose();
            Interlocked.Increment(ref _framesTotal);
        }
        catch (ArgumentException)
        {
            // 畸形 JPEG：跳过该帧，不断链路
        }
    }

    /// <summary>h264 解码成功时直接提交位图（与 SubmitJpeg 同一显示管线）。</summary>
    public void SubmitBitmap(System.Drawing.Bitmap bmp)
    {
        var old = Interlocked.Exchange(ref _pending, bmp);
        old?.Dispose();
        Interlocked.Increment(ref _framesTotal);
    }

    /// <summary>UI 定时器驱动：把 pending 帧放上 PictureBox 并刷新帧率统计。</summary>
    public void Tick()
    {
        var bmp = Interlocked.Exchange(ref _pending, null);
        if (bmp is not null)
        {
            var old = _box.Image;
            _box.Image = bmp;
            old?.Dispose();
            Interlocked.Increment(ref _framesShown);
            _fpsWindow++;
            UpdateImageRect();
        }
        var now = Environment.TickCount64;
        if (now - _lastFpsTick >= 1000)
        {
            Fps = _fpsWindow * 1000.0 / (now - _lastFpsTick);
            _fpsWindow = 0;
            _lastFpsTick = now;
            Text = $"stream-probe — {_info.Width}x{_info.Height} {Fps:F1}fps frame={Interlocked.Read(ref _framesTotal)}";
        }
    }

    /// <summary>计算 Zoom 模式下图像在控件里的实际矩形（用于坐标反算）。</summary>
    private void UpdateImageRect()
    {
        var img = _box.Image;
        if (img is null || _box.Width == 0 || _box.Height == 0) { _imageRect = Rectangle.Empty; return; }
        double scale = Math.Min((double)_box.Width / img.Width, (double)_box.Height / img.Height);
        int w = (int)(img.Width * scale), h = (int)(img.Height * scale);
        _imageRect = new Rectangle((_box.Width - w) / 2, (_box.Height - h) / 2, w, h);
    }

    /// <summary>控件坐标 → 服务端分辨率坐标（letterbox 补偿 + clamp）。</summary>
    private (ushort X, ushort Y) MapToServer(int cx, int cy)
    {
        if (_imageRect.Width <= 0 || _imageRect.Height <= 0) return (0, 0);
        int x = (int)Math.Round((cx - _imageRect.X) * (double)_info.Width / _imageRect.Width);
        int y = (int)Math.Round((cy - _imageRect.Y) * (double)_info.Height / _imageRect.Height);
        return ((ushort)Math.Clamp(x, 0, Math.Max(_info.Width - 1, 0)),
                (ushort)Math.Clamp(y, 0, Math.Max(_info.Height - 1, 0)));
    }

    private void Send(byte type, ReadOnlyMemory<byte> payload)
    {
        if (_ct.IsCancellationRequested) return;
        _ = Task.Run(() => _sendFrameAsync(type, payload));
    }

    private void OnMouseDown(object? s, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            var (x, y) = MapToServer(e.X, e.Y);
            Send(Protocol.TypeTouch, Protocol.BuildTouchPayload(x, y, Protocol.TouchDown));
        }
        else if (e.Button == MouseButtons.Right)
        {
            // 右键 → Android 返回键（投屏常用；协议键码语义见 AndroidKeys）
            Send(Protocol.TypeKey, Protocol.BuildKeyPayload(AndroidKeys.KeycodeBack, Protocol.KeyDown));
            Send(Protocol.TypeKey, Protocol.BuildKeyPayload(AndroidKeys.KeycodeBack, Protocol.KeyUp));
        }
    }

    private void OnMouseUp(object? s, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            var (x, y) = MapToServer(e.X, e.Y);
            Send(Protocol.TypeTouch, Protocol.BuildTouchPayload(x, y, Protocol.TouchUp));
        }
    }

    private void OnMouseMove(object? s, MouseEventArgs e)
    {
        // 16ms 节流：move 事件可到几百 Hz，按 60Hz 上限发
        var now = Environment.TickCount64;
        if (now - _lastMoveSentMs < 16) return;
        _lastMoveSentMs = now;
        var (x, y) = MapToServer(e.X, e.Y);
        Send(Protocol.TypeTouch, Protocol.BuildTouchPayload(x, y, Protocol.TouchMove));
    }

    private void OnMouseWheel(object? s, MouseEventArgs e)
    {
        short dy = (short)Math.Clamp(e.Delta, short.MinValue, short.MaxValue);
        Send(Protocol.TypeWheel, Protocol.BuildWheelPayload(0, dy));
    }

    private void OnKeyDown(object? s, KeyEventArgs e)
    {
        // Esc = 退出投屏（一键启动场景；右键 = Android BACK）
        if (e.KeyCode == Keys.Escape)
        {
            Close();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        uint code = AndroidKeys.FromWinForms(e.KeyCode);
        if (code != AndroidKeys.Unknown)
        {
            Send(Protocol.TypeKey, Protocol.BuildKeyPayload(code, Protocol.KeyDown));
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void OnKeyUp(object? s, KeyEventArgs e)
    {
        uint code = AndroidKeys.FromWinForms(e.KeyCode);
        if (code != AndroidKeys.Unknown)
        {
            Send(Protocol.TypeKey, Protocol.BuildKeyPayload(code, Protocol.KeyUp));
            e.Handled = true;
        }
    }

    /// <summary>窗口关闭：发 0x00 关闭帧（短超时，不等回包），随后收流循环自然退出。</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        try
        {
            if (!_ct.IsCancellationRequested)
                _sendFrameAsync(Protocol.TypeClose, ReadOnlyMemory<byte>.Empty).Wait(300);
        }
        catch { /* 对端已断就算了 */ }
        base.OnFormClosing(e);
    }
}
