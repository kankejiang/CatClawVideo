using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;

namespace StreamProbe;

/// <summary>
/// <c>--mock</c> 模拟服务端（T2 自测用，不依赖 T1/T3）：
/// 按协议 <c>CATCLAW/1</c> 握手后，以 30fps 推<b>合成 JPEG</b>（640×360，移动色块 + 帧号文字），
/// 并把收到的输入帧（触摸/按键/滚轮）**打印**到控制台 —— 供验收「输入链路字节序/字段正确」。
/// </summary>
public sealed class MockServer
{
    private readonly int _port;
    private readonly bool _verbose;
    private readonly int _width = 640;
    private readonly int _height = 360;
    private const int Fps = 30;
    private const string Codec = "jpeg";

    /// <summary>收到的输入帧总数（各客户端累计）。</summary>
    private long _inputCount;

    public MockServer(int port, bool verbose)
    {
        _port = port;
        _verbose = verbose;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, _port);
        listener.Start();
        Console.WriteLine($"[mock] CATCLAW/1 模拟服务端就绪：127.0.0.1:{_port}");
        Console.WriteLine($"[mock] 协商画面 {_width}x{_height} {Codec}@{Fps}fps；等待客户端连接…");
        Console.WriteLine("[mock] 客户端的触摸/按键/滚轮会打印在这里。Ctrl+C 退出。");
        while (!ct.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(ct);
            _ = Task.Run(() => ServeAsync(client, ct), ct);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        var remote = client.Client.RemoteEndPoint;
        Console.WriteLine($"\n[mock] 客户端接入：{remote}");
        long sessionId = 0;
        try
        {
            await using var stream = client.GetStream();
            await Protocol.HandshakeAsServerAsync(stream, _width, _height, Codec, Fps, ct);
            Console.WriteLine("[mock] 握手完成（OK 640 360 jpeg 30）");

            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var sender = Task.Run(() => SendLoopAsync(stream, sendCts.Token), sendCts.Token);
            var receiver = Task.Run(() => ReceiveLoopAsync(stream, sendCts.Token), sendCts.Token);
            var done = await Task.WhenAny(sender, receiver);
            sendCts.Cancel();
            try { await done; } catch { /* 关闭路径的取消/EOF 是常态 */ }
            sessionId = Interlocked.Read(ref _inputCount);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or OperationCanceledException)
        {
            if (_verbose) Console.WriteLine($"[mock] 客户端 {remote} 断开：{ex.GetType().Name}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[mock] 客户端 {remote} 异常：{ex}");
        }
        finally
        {
            client.Close();
            Console.WriteLine($"[mock] 客户端 {remote} 断开（本次会话共收输入 {sessionId} 帧）。");
        }
    }

    /// <summary>发送循环：30fps 视频 + 每 5s 心跳。</summary>
    private async Task SendLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        long frameNo = 0;
        var lastBeat = Environment.TickCount64;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (!ct.IsCancellationRequested)
        {
            var jpeg = RenderFrame(frameNo, sw.ElapsedMilliseconds);
            var packet = Protocol.BuildFrame(Protocol.TypeVideo, jpeg);
            await stream.WriteAsync(packet, ct);

            frameNo++;
            if (Environment.TickCount64 - lastBeat >= 5000)
            {
                lastBeat = Environment.TickCount64;
                var beat = Protocol.BuildFrame(Protocol.TypeHeartbeat,
                    Protocol.BuildHeartbeatPayload(Environment.TickCount64));
                await stream.WriteAsync(beat, ct);
                if (_verbose) Console.WriteLine($"[mock] → 心跳 #{Environment.TickCount64}");
            }

            // 30fps 节拍（减去渲染/发送耗时；帧率抖动容忍，验收看 ≥25fps）
            var targetMs = frameNo * 1000 / Fps;
            var waitMs = targetMs - sw.ElapsedMilliseconds;
            if (waitMs > 0) await Task.Delay((int)Math.Min(waitMs, 200), ct);
        }
    }

    /// <summary>接收循环：读帧并打印输入（未知类型按长度跳过）。</summary>
    private async Task ReceiveLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var (type, payload) = await Protocol.ReadFrameAsync(stream, ct);
            Interlocked.Increment(ref _inputCount);
            switch (type)
            {
                case Protocol.TypeTouch:
                {
                    var (x, y, action) = Protocol.ParseTouch(payload);
                    var act = action switch
                    {
                        Protocol.TouchDown => "down",
                        Protocol.TouchUp => "up",
                        Protocol.TouchMove => "move",
                        _ => $"act={action}",
                    };
                    Console.WriteLine($"[输入] 触摸 {act,-4} @ ({x},{y})");
                    break;
                }
                case Protocol.TypeKey:
                {
                    var (code, action) = Protocol.ParseKey(payload);
                    var act = action == Protocol.KeyDown ? "down" : "up";
                    Console.WriteLine($"[输入] 按键 {act,-4} keycode={code} ({AndroidKeys.Name(code)})");
                    break;
                }
                case Protocol.TypeWheel:
                {
                    var (dx, dy) = Protocol.ParseWheel(payload);
                    var dir = dy != 0 ? (dy > 0 ? "下滚" : "上滚") : (dx > 0 ? "右滚" : "左滚");
                    Console.WriteLine($"[输入] 滚轮 dx={dx} dy={dy}（{dir}）");
                    break;
                }
                case Protocol.TypeHeartbeat:
                    if (_verbose) Console.WriteLine($"[输入] 心跳 ts={Protocol.ParseHeartbeat(payload)}");
                    Interlocked.Decrement(ref _inputCount);   // 心跳不计入"输入"
                    break;
                case Protocol.TypeClose:
                    Console.WriteLine("[输入] 对端请求关闭（0x00），断开。");
                    return;
                case Protocol.TypeVideo:
                    // 客户端不该发视频帧；按协议当未知数据处理（丢弃）
                    goto default;
                default:
                    Console.WriteLine($"[输入] 未知类型 0x{type:X2}（{payload.Length}B）—— 按协议跳过。");
                    break;
            }
        }
    }

    /// <summary>渲染一帧合成 JPEG：黑底 + 弹跳色块 + 居中帧号 + 顶部状态行。</summary>
    private byte[] RenderFrame(long frameNo, long elapsedMs)
    {
        using var bmp = new Bitmap(_width, _height);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.FromArgb(18, 18, 24));

        // 弹跳色块（60px 方块，水平 3px/帧、垂直 2px/帧，出界反弹）
        int size = 60;
        int spanX = _width - size, spanY = _height - size;
        int periodX = spanX / 3, periodY = spanY / 2;
        int bx = Bounce(frameNo, periodX) * 3;
        int by = Bounce(frameNo, periodY) * 2;
        using (var brush = new SolidBrush(Color.FromArgb(255, 66, 133, 244)))
            g.FillRectangle(brush, bx, by, size, size);

        // 第二个小色块（反相轨迹，证明画面不是单层贴图）
        int cx = spanX - Bounce(frameNo + 17, periodX) * 3;
        int cy = spanY - Bounce(frameNo + 31, periodY) * 2;
        using (var brush = new SolidBrush(Color.FromArgb(234, 67, 53)))
            g.FillEllipse(brush, cx, cy, 36, 36);

        // 居中帧号（大字）
        string label = $"Frame {frameNo}";
        using var bigFont = new Font("Consolas", 34, FontStyle.Bold);
        var sz = g.MeasureString(label, bigFont);
        using (var white = new SolidBrush(Color.White))
            g.DrawString(label, bigFont, white, (_width - sz.Width) / 2, (_height - sz.Height) / 2);

        // 顶部状态行：分辨率/帧率/运行时长
        using var smallFont = new Font("Consolas", 11);
        using var gray = new SolidBrush(Color.FromArgb(200, 200, 200));
        g.DrawString($"CATCLAW/1 mock  {_width}x{_height}@{Fps}  t={elapsedMs / 1000.0:F1}s  inputs={Interlocked.Read(ref _inputCount)}",
            smallFont, gray, 8, 6);

        // JPEG 编码（质量 70：几十 KB/帧，30fps 下带宽 ~2-4 Mbps）
        using var ms = new MemoryStream();
        var enc = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
        using var encParams = new EncoderParameters(1);
        encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 70L);
        bmp.Save(ms, enc, encParams);
        return ms.ToArray();
    }

    /// <summary>三角波弹跳：n 递增时在 [0, span] 内往返。</summary>
    private static int Bounce(long n, long span)
    {
        if (span <= 0) return 0;
        long m = n % (2 * span);
        return (int)(m < span ? m : 2 * span - m);
    }
}
