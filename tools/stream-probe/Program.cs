using System.Net.Sockets;

namespace StreamProbe;

/// <summary>
/// stream-probe：协议 <c>CATCLAW/1</c> 的 Windows 投屏客户端 + 自测模拟服务端（T2 任务包）。
///
/// <para>用法：</para>
/// <code>
///   # 模拟服务端（合成 JPEG 30fps + 打印收到的输入）
///   dotnet run --project tools\stream-probe -- --mock [--port 27183] [--verbose]
///
///   # 客户端（收流显示 + 鼠标键盘回传）
///   dotnet run --project tools\stream-probe -- --connect 127.0.0.1:27183
///          [--dump N] 存前 N 帧到 dump\frame_*.jpg
///          [--auto-input] 连接后自动发一组触摸/按键/滚轮帧（无人值守验收输入链路）
///          [--seconds N] N 秒后自动退出（无人值守验收显示链路）
///          [--verbose] 打印前 20 帧头部（长度/类型/时间）
/// </code>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // 中文输出在重定向下默认走 OEM（GBK）—— 统一 UTF8，日志/验收输出可读
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        bool mock = args.Contains("--mock");
        bool verbose = args.Contains("--verbose");
        int port = 27183;
        string? connect = null;
        int dump = 0, seconds = 0;
        bool autoInput = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length && int.TryParse(args[++i], out var p) && p is > 0 and < 65536:
                    port = p; break;
                case "--connect" when i + 1 < args.Length:
                    connect = args[++i]; break;
                case "--dump" when i + 1 < args.Length && int.TryParse(args[++i], out var d) && d > 0:
                    dump = d; break;
                case "--seconds" when i + 1 < args.Length && int.TryParse(args[++i], out var s2) && s2 > 0:
                    seconds = s2; break;
                case "--auto-input":
                    autoInput = true; break;
                case "--mock":
                    break;   // 开头 args.Contains 已读
                case "--verbose":
                    break;   // 开头 args.Contains 已读；这里只为不落 default
                default:
                    Console.WriteLine($"无法识别的参数：{args[i]}");
                    Console.WriteLine(
                        """
                        用法:
                          stream-probe --mock [--port N] [--verbose]
                          stream-probe --connect host:port [--dump N] [--auto-input] [--seconds N] [--verbose]
                        """);
                    return 2;
            }
        }

        if (mock) return await RunMockAsync(port, verbose);
        if (connect is not null) return RunClient(connect, dump, seconds, autoInput, verbose);

        Console.WriteLine("需要 --mock 或 --connect（用法见 --help）");
        return 2;
    }

    // ── mock 服务端 ───────────────────────────────────────────────────

    private static async Task<int> RunMockAsync(int port, bool verbose)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        try
        {
            await new MockServer(port, verbose).RunAsync(cts.Token);
            return 0;
        }
        catch (OperationCanceledException) { return 0; }
        catch (SocketException ex)
        {
            Console.WriteLine($"[mock] 端口 {port} 监听失败：{ex.Message}（被占用？换 --port）");
            return 1;
        }
    }

    // ── 客户端 ────────────────────────────────────────────────────────

    private static int RunClient(string connect, int dump, int seconds, bool autoInput, bool verbose)
    {
        // host:port 解析（默认端口 27183）
        int port = 27183;
        string host = connect;
        if (connect.Contains(':'))
        {
            var hi = connect.LastIndexOf(':');
            host = connect[..hi];
            if (!int.TryParse(connect[(hi + 1)..], out port)) { Console.WriteLine($"端口解析失败：{connect}"); return 2; }
        }
        host = host.TrimStart('[').TrimEnd(']');   // IPv6 字面量容忍

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        ClientSession session;
        try
        {
            session = new ClientSession(host, port, dump, autoInput, verbose, cts);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[client] 连接失败：{ex.Message}");
            return 1;
        }

        var view = new ViewWindow(session.Info, session.SendFrameAsync, cts.Token);
        session.View = view;

        // 收流线程（后台）
        var recvTask = Task.Run(() => session.ReceiveLoopAsync(cts.Token));

        // UI 定时器：取帧显示 + 帧率统计
        var timer = new System.Windows.Forms.Timer { Interval = 15 };
        timer.Tick += (_, _) => view.Tick();
        timer.Start();

        // --seconds N：无人值守自动退出
        if (seconds > 0)
        {
            var t = new System.Windows.Forms.Timer { Interval = seconds * 1000 };
            t.Tick += (_, _) => { Console.WriteLine($"[client] --seconds {seconds} 到，退出。"); cts.Cancel(); view.Close(); };
            t.Start();
        }

        Application.Run(view);
        timer.Stop();

        cts.Cancel();
        try { recvTask.Wait(1500); } catch { /* EOF/取消是常态 */ }

        Console.WriteLine($"[client] 结束：共收视频帧 {view.FramesTotal}，实测显示帧率 {view.Fps:F1}fps");
        // 只有收流真正异常才算失败；--seconds/窗口关闭/对端断开都是正常退出
        if (recvTask.IsFaulted)
        {
            var ex = recvTask.Exception?.InnerException ?? recvTask.Exception;
            Console.WriteLine($"[client] 收流异常：{ex?.Message}");
            return 1;
        }
        return 0;
    }
}

/// <summary>一次客户端会话：连接、握手、收流、（可选）dump 与 auto-input。</summary>
internal sealed class ClientSession
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly bool _verbose;
    private readonly bool _autoInput;
    private readonly int _dumpCount;
    private readonly H264Decoder _h264;
    private int _dumped;
    private int _verboseFrames;
    private ViewWindow? _view;

    public Protocol.HandshakeInfo Info { get; }

    public ViewWindow? View { set => _view = value; }

    public ClientSession(string host, int port, int dumpCount, bool autoInput, bool verbose, CancellationTokenSource cts)
    {
        _verbose = verbose;
        _autoInput = autoInput;
        _dumpCount = dumpCount;
        _h264 = new H264Decoder(verbose);

        Console.WriteLine($"[client] 连接 {host}:{port} …");
        _tcp = new TcpClient();
        _tcp.ConnectAsync(host, port).Wait(5000, cts.Token);
        _stream = _tcp.GetStream();
        Console.WriteLine($"[client] 已连接，握手（{Protocol.HandshakeRequest}）…");
        Info = Protocol.HandshakeAsClientAsync(_stream, cts.Token).GetAwaiter().GetResult();
        Console.WriteLine($"[client] 握手成功：{Info.Width}x{Info.Height} {Info.Codec}@{Info.Fps}fps");
        if (Info.Codec == "h264") Console.WriteLine("[client] " + H264Decoder.StubNotice);
    }

    /// <summary>发送帧（收流线程与 UI 线程都会用 —— 写锁保护）。</summary>
    public async Task SendFrameAsync(byte type, ReadOnlyMemory<byte> payload)
    {
        await _sendLock.WaitAsync();
        try
        {
            var packet = Protocol.BuildFrame(type, payload.Span);
            await _stream.WriteAsync(packet);
            await _stream.FlushAsync();
        }
        finally { _sendLock.Release(); }
    }

    /// <summary>收流主循环：视频帧 → 显示/dump；心跳/输入帧 → 忽略或记录。</summary>
    public async Task<int> ReceiveLoopAsync(CancellationToken ct)
    {
        if (_autoInput) _ = Task.Run(() => AutoInputAsync(ct), ct);

        while (!ct.IsCancellationRequested)
        {
            byte type;
            byte[] payload;
            try
            {
                (type, payload) = await Protocol.ReadFrameAsync(_stream, ct);
            }
            catch (EndOfStreamException)
            {
                Console.WriteLine("[client] 对端关闭连接。");
                _view?.BeginInvoke(() => _view?.Close());
                return 0;
            }

            var now = DateTime.Now;
            if (_verbose && _verboseFrames < 20)
            {
                _verboseFrames++;
                Console.WriteLine($"[帧] #{_verboseFrames} type=0x{type:X2} len={payload.Length}B @ {now:HH:mm:ss.fff}");
            }

            switch (type)
            {
                case Protocol.TypeVideo when Info.Codec == "jpeg":
                    DumpIfNeeded(payload);
                    // SubmitJpeg 只做解码 + pending 交换（线程安全，无需 UI 线程；
                    // BeginInvoke 在窗口句柄未创建时会抛）
                    _view?.SubmitJpeg(payload);
                    break;

                case Protocol.TypeVideo:
                    // h264：交给解码桩（未实现时只计数，不崩）
                    if (_h264.TryDecode(payload, out var frame) && frame is not null)
                        _view?.SubmitBitmap(frame);
                    break;

                case Protocol.TypeHeartbeat:
                    if (_verbose) Console.WriteLine($"[client] 心跳 ts={Protocol.ParseHeartbeat(payload)}");
                    break;

                case Protocol.TypeClose:
                    Console.WriteLine("[client] 服务端发来 0x00 关闭帧。");
                    try { _view?.BeginInvoke(() => _view?.Close()); } catch { /* 句柄未就绪就算了 */ }
                    return 0;

                default:
                    // 客户端不应收到输入帧/未知帧：按协议按长度丢弃（不崩）
                    if (_verbose) Console.WriteLine($"[client] 跳过帧 type=0x{type:X2}（{payload.Length}B）");
                    break;
            }
        }
        return 0;
    }

    private void DumpIfNeeded(byte[] jpeg)
    {
        if (_dumped >= _dumpCount) return;
        var n = Interlocked.Increment(ref _dumped);
        try
        {
            Directory.CreateDirectory("dump");
            var path = Path.Combine("dump", $"frame_{n:D4}.jpg");
            File.WriteAllBytes(path, jpeg);
            if (n == _dumpCount) Console.WriteLine($"[client] --dump 完成：{_dumpCount} 帧已存 dump\\frame_*.jpg");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[client] dump 失败：{ex.Message}");
        }
    }

    /// <summary>
    /// --auto-input：连接后自动发一组输入帧（模拟「用户在窗口里点击/滚轮/按键」），
    /// 供无人值守验收 —— mock 端应打印出对应的触摸/按键/滚轮行。
    /// </summary>
    private async Task AutoInputAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(2000, ct);   // 等显示稳定
            int w = Info.Width, h = Info.Height;
            Console.WriteLine("[client] auto-input：开始发送预设输入序列…");

            // 触摸：down → move ×2 → up（左上滑到右下）
            ushort ax = (ushort)(w / 4), ay = (ushort)(h / 4);
            ushort bx = (ushort)(w * 3 / 4), by = (ushort)(h * 3 / 4);
            await SendFrameAsync(Protocol.TypeTouch, Protocol.BuildTouchPayload(ax, ay, Protocol.TouchDown));
            await Task.Delay(150, ct);
            await SendFrameAsync(Protocol.TypeTouch, Protocol.BuildTouchPayload((ushort)((ax + bx) / 2), (ushort)((ay + by) / 2), Protocol.TouchMove));
            await Task.Delay(150, ct);
            await SendFrameAsync(Protocol.TypeTouch, Protocol.BuildTouchPayload(bx, by, Protocol.TouchMove));
            await Task.Delay(150, ct);
            await SendFrameAsync(Protocol.TypeTouch, Protocol.BuildTouchPayload(bx, by, Protocol.TouchUp));
            await Task.Delay(400, ct);

            // 按键：A down/up、DPAD_RIGHT down/up
            await SendFrameAsync(Protocol.TypeKey, Protocol.BuildKeyPayload(AndroidKeys.KeycodeA, Protocol.KeyDown));
            await Task.Delay(100, ct);
            await SendFrameAsync(Protocol.TypeKey, Protocol.BuildKeyPayload(AndroidKeys.KeycodeA, Protocol.KeyUp));
            await Task.Delay(200, ct);
            await SendFrameAsync(Protocol.TypeKey, Protocol.BuildKeyPayload(AndroidKeys.KeycodeDpadRight, Protocol.KeyDown));
            await Task.Delay(100, ct);
            await SendFrameAsync(Protocol.TypeKey, Protocol.BuildKeyPayload(AndroidKeys.KeycodeDpadRight, Protocol.KeyUp));
            await Task.Delay(400, ct);

            // 滚轮：上滚 -120、下滚 +120
            await SendFrameAsync(Protocol.TypeWheel, Protocol.BuildWheelPayload(0, -120));
            await Task.Delay(200, ct);
            await SendFrameAsync(Protocol.TypeWheel, Protocol.BuildWheelPayload(0, 120));

            Console.WriteLine("[client] auto-input：序列发送完毕。");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[client] auto-input 失败：{ex.Message}");
        }
    }
}
