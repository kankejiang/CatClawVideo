using System.Diagnostics;
using CatClawVideo.Maui.Controls;
using CatClawVideo.Maui.Services;
using CatClawVideo.Streaming;
using Microsoft.Maui.Controls;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 远程投屏页：连接表单（地址/端口/编码）→ 一键连接 → 画面 + 键鼠注入 → 状态栏 → 断线自动重连 → Esc 退出。
/// 帧的接收/解码/上屏与网络计量都走 <see cref="StreamClient"/>（与命令行台架同一套代码），
/// 页面只负责把帧交给 <see cref="RemoteView"/> 并把实测数字显示出来。
/// </summary>
[QueryProperty(nameof(Host), "host")]
[QueryProperty(nameof(Port), "port")]
[QueryProperty(nameof(Codec), "codec")]
[QueryProperty(nameof(AutoConnect), "auto")]
public partial class RemotePage : ContentPage
{
    // 路由参数：给"记忆最近一次成功连接后开机直达画面"（N4-2）与自动化验收用
    public string? Host { get; set; }
    public string? Port { get; set; }
    public string? Codec { get; set; }
    public string? AutoConnect { get; set; }

    private const int H264DefaultPort = 27183;
    private const int JpegDefaultPort = 27383;
    private const int SoakLogEverySeconds = 60;

    private StreamClient? _client;
    private CancellationTokenSource? _loop;
    private IDispatcherTimer? _ticker;
    private long _shown, _dropped, _received;
    private double _latSum, _latMax;
    private long _bytesAtLastTick;
    private int _ticksSinceSoakLog;
    private bool _connectedOnce;

    public RemotePage()
    {
        InitializeComponent();
        PickCodec.SelectedIndex = RemoteStreamPrefs.Codec switch { "jpeg" => 1, "h264" => 2, _ => 0 };

        Video.TouchRequested += OnTouchRequested;
        Video.WheelRequested += OnWheelRequested;
        Video.KeyRequested += OnKeyRequested;
        Video.EscapePressed += OnEscapePressed;
        Video.FrameDisplayed += OnFrameDisplayed;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        var last = RemoteStreamPrefs.LastSuccess;
        EntryHost.Text = Host ?? last?.Host ?? RemoteStreamPrefs.Host;
        EntryPort.Text = Port ?? (last?.Port ?? RemoteStreamPrefs.Port).ToString();
        if (Codec is "jpeg" or "h264") PickCodec.SelectedIndex = Codec == "jpeg" ? 1 : 2;
        LblProbe.Text = last is null ? "还没成功连过；填好地址点「一键连接」。"
                                     : $"上次成功连接：{last.Value.Host}:{last.Value.Port}";
        if (AutoConnect == "1")
        {
            // 开机直达：先按上次的成功端点连一次；不通就退回表单让用户改
            OnConnectClicked(null, EventArgs.Empty);
            return;
        }
        _ = ProbeAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        StopAll();
    }

    private (string Host, List<int> Ports, string Want) ReadForm()
    {
        var host = (EntryHost.Text ?? "").Trim();
        if (host.Length == 0) host = RemoteStreamPrefs.DefaultHost;
        if (!int.TryParse((EntryPort.Text ?? "").Trim(), out var port) || port <= 0 || port > 65535)
            port = RemoteStreamPrefs.DefaultPort;
        var want = PickCodec.SelectedIndex switch { 1 => "jpeg", 2 => "h264", _ => "auto" };
        // auto = 默认优先 h264（把 h264 的常用端口摆在前面），本机解不了时按端口表依次回落
        var ports = want == "auto"
            ? new[] { port, H264DefaultPort, JpegDefaultPort }.Distinct().ToList()
            : new List<int> { port };
        return (host, ports, want);
    }

    /// <summary>启动自检：先 TCP 探测，能连就报端口上的 codec，让"没起服务端"在点之前就被讲清楚。</summary>
    private async Task ProbeAsync()
    {
        var (host, ports, _) = ReadForm();
        LblProbe.Text = $"正在探测 {host}:{string.Join('/', ports)} …";
        var probes = new List<string>();
        foreach (var p in ports)
        {
            var one = await ProbeOneAsync(host, p);
            probes.Add(one);
            if (one.Contains("可解码=是")) break;
        }
        LblProbe.Text = string.Join("；", probes);
    }

    private static async Task<string> ProbeOneAsync(string host, int port)
    {
        try
        {
            await using var c = new StreamClient();
            var info = await c.ConnectOnceAsync(host, port, new CancellationTokenSource(2500).Token);
            var can = CodecSupport.CanDecode(info.Codec);
            await c.StopAsync();
            return $"{port} 在线 {info.Width}x{info.Height} {info.Codec}@{info.Fps} 可解码={(can ? "是" : "否")}";
        }
        catch (Exception ex)
        {
            return $"{port} 不通（{ShortError(ex)}）";
        }
    }

    private async void OnConnectClicked(object? sender, EventArgs e)
    {
        BtnConnect.IsEnabled = false;
        try
        {
            var (host, ports, want) = ReadForm();
            RemoteStreamPrefs.Save(host, ports[0], want);
            StopAll();
            var client = new StreamClient();
            _client = client;
            // 必须先挂事件再握手：Phase 只在"变化"时发事件，握手内部就会把 Connecting→Streaming 走完，
            // 晚挂一行就等于把"已连接"那次跳过去，状态栏永远停在"连接中 …"。
            client.FrameArrived += OnFrameArrived;
            client.PhaseChanged += OnPhaseChanged;
            LblState.Text = "连接中 …";
            Protocol.HandshakeInfo info;
            try
            {
                info = await client.ConnectPreferredAsync(host, ports, CancellationToken.None);
            }
            catch (Exception ex)
            {
                LblState.Text = "未连接";
                LblHint.Text = ChineseHint(ex);
                await ShowHint(ex);
                return;
            }

            Video.ServerWidth = info.Width;
            Video.ServerHeight = info.Height;
            Video.IsVisible = true;
            ToolBar.IsVisible = true;
            ConnectPanel.IsVisible = false;
            LblRemote.Text = $"{host}:{client.Current.Port} {info.Codec} {info.Width}x{info.Height}@{info.Fps}";
            RemoteStreamPrefs.SaveSuccess(host, client.Current.Port);
            _connectedOnce = true;
            Interlocked.Exchange(ref _received, 0);
            Interlocked.Exchange(ref _shown, 0);
            Interlocked.Exchange(ref _dropped, 0);

            _loop = new CancellationTokenSource();
            _ = Task.Run(() => client.RunAsync(_loop.Token));
            _ticker = Dispatcher.CreateTimer();
            _ticker.Interval = TimeSpan.FromSeconds(1);
            _ticker.Tick += OnTick;
            _ticker.Start();
            DiagLog.Write($"[remote] 已连接 {host}:{client.Current.Port} codec={info.Codec} {info.Width}x{info.Height}@{info.Fps}");
            // 画面拿到焦点才能收键盘事件（Esc / 方向键 / 字母数字）
            Video.Focus();
        }
        finally
        {
            BtnConnect.IsEnabled = true;
        }
    }

    /// <summary>把协议层的英文异常翻成用户能照着做的中文提示。</summary>
    private static string ChineseHint(Exception ex) => ex switch
    {
        OperationCanceledException => "108 未启动服务端：先在 108 上执行 " +
            $"bash serve.sh -d --port {JpegDefaultPort} --codec jpeg --fps 15（或点「帮我拉起服务端」）",
        _ => ex.Message.Contains("可解码") || ex.Message.Contains("codec")
            ? $"服务端在跑，但这条链路本机还解不了：{ex.Message}（h264 解码器由 T2 在写；先用 --codec jpeg 起服务端）"
            : $"连不上：{ex.Message}。请确认 108 可达、端口没写错，或在 108 上执行 " +
              $"bash serve.sh -d --port {JpegDefaultPort} --codec jpeg --fps 15",
    };

    private async Task ShowHint(Exception ex)
    {
        LblHint.Text = ChineseHint(ex);
        await Task.CompletedTask;
    }

    private static string ShortError(Exception ex) =>
        ex is TimeoutException ? "超时" : ex.InnerException?.Message ?? ex.Message;

    private void OnFrameArrived(object? sender, FrameArrivedEventArgs e)
    {
        if (e.Type != Protocol.TypeVideo) return;
        Interlocked.Increment(ref _received);
        var payload = e.Payload;
        var tick = e.ReceivedTickMs;
        // 收流线程 → UI 线程：交给控件的"最新帧优先"策略（被覆盖的计入丢帧）
        Dispatcher.Dispatch(() => Video.SubmitJpeg(payload, tick));
    }

    private void OnFrameDisplayed(double latencyMs)
    {
        Interlocked.Increment(ref _shown);
        _latSum += latencyMs;
        if (latencyMs > _latMax) _latMax = latencyMs;
    }

    private void OnPhaseChanged(object? sender, StreamPhase phase) =>
        Dispatcher.Dispatch(() =>
        {
            LblState.Text = phase switch
            {
                StreamPhase.Streaming => "已连接",
                StreamPhase.Connecting => "连接中",
                StreamPhase.WaitingRetry => "断线，退避重连中 …",
                StreamPhase.Failed => "连接失败",
                StreamPhase.Closed => "已断开",
                _ => "未连接",
            };
            if (phase == StreamPhase.WaitingRetry) DiagLog.Write("[remote] 断线，进入指数退避重连");
        });

    private void OnTick(object? sender, EventArgs e)
    {
        var client = _client;
        if (client is null) return;
        var snap = client.Current;
        var shownNow = Interlocked.Read(ref _shown);
        var recvNow = Interlocked.Read(ref _received);
        var shownDelta = shownNow - _lastShown;
        var recvDelta = recvNow - _lastRecv;
        _lastShown = shownNow;
        _lastRecv = recvNow;
        // 丢帧 = 这一秒收到的帧里，没来得及上屏就被更新帧覆盖的那几帧
        Interlocked.Add(ref _dropped, Math.Max(0, recvDelta - shownDelta));

        LblFps.Text = $"fps {shownDelta}";
        LblDrops.Text = $"丢帧 {Interlocked.Read(ref _dropped)}";
        var avg = shownNow == 0 ? 0 : _latSum / shownNow;
        LblLatency.Text = $"延迟 {avg:F0}ms(峰 {_latMax:F0})";
        var bytesNow = snap.BytesReceived;
        LblBitrate.Text = $"{(bytesNow - _bytesAtLastTick) * 8.0 / 1e3:F0} kbps";
        _bytesAtLastTick = bytesNow;
        LblScale.Text = $"缩放 {Video.RasterizationScale:F2}× 图像 {Video.ImageRect.Width:F0}×{Video.ImageRect.Height:F0}DIP";
        if (snap.LastError is { Length: > 0 } err) LblHint.Text = err;

        if (++_ticksSinceSoakLog >= SoakLogEverySeconds)
        {
            _ticksSinceSoakLog = 0;
            var priv = Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0;
            var perFrameKb = recvNow == 0 ? 0 : snap.BytesReceived * 8.0 / recvNow / 1e3;
            DiagLog.Write($"[remote-soak] 收={recvNow} 显示={shownNow} 丢={Interlocked.Read(ref _dropped)} " +
                          $"延迟均={avg:F1}ms 峰={_latMax:F0}ms 重连={snap.ReconnectCount} 私有内存={priv:F1}MB " +
                          $"每帧={perFrameKb:F0}kbps 状态={snap.Phase}");
        }
    }

    private long _lastShown, _lastRecv;

    private void OnTouchRequested(int x, int y, byte action) =>
        _ = _client?.SendTouchAsync(x, y, action);

    private void OnWheelRequested(int dx, int dy) => _ = _client?.SendWheelAsync(dx, dy);

    private void OnKeyRequested(uint keycode, byte action)
    {
        // 每次按键落一行诊断：映射对不对、有没有被丢弃，只有服务端日志和本机日志对得上才能定案
        DiagLog.Write($"[remote] 注入按键 {AndroidKeycodes.Name(keycode)}(0x{keycode:X}) action={action}");
        _ = _client?.SendKeyAsync(keycode, action);
    }

    private void OnHomeClicked(object? sender, EventArgs e) => SendAndroidKey(AndroidKeycodes.KeycodeHome);

    private void OnBackClicked(object? sender, EventArgs e) => SendAndroidKey(AndroidKeycodes.KeycodeBack);

    private void SendAndroidKey(uint code)
    {
        var c = _client;
        if (c is null) return;
        _ = c.SendKeyAsync(code, Protocol.KeyDown);
        _ = c.SendKeyAsync(code, Protocol.KeyUp);
    }

    private async void OnReconnectClicked(object? sender, EventArgs e)
    {
        StopAll();
        await Task.Yield();
        OnConnectClicked(sender, e);
    }

    private void OnEscapePressed() => Dispatcher.Dispatch(() => _ = ExitAsync());

    private async Task ExitAsync()
    {
        StopAll();
        await Shell.Current.GoToAsync("..");
    }

    private async void OnDisconnectClicked(object? sender, EventArgs e) => await ExitAsync();

    /// <summary>
    /// 「帮我拉起服务端」：直接起 serve.py 而**不走 serve.sh** ——
    /// serve.sh 里有 restart container 的兜底步骤，会打断同一台 108 上别人正在跑的流（实测 27183/27283 在服务中）。
    /// </summary>
    private async void OnStartServerClicked(object? sender, EventArgs e)
    {
        var (_, ports, _) = ReadForm();
        var port = ports[0];
        BtnStartServer.IsEnabled = false;
        LblProbe.Text = $"正在通过 ssh 拉起 108 上的 jpeg 服务端（端口 {port}）…";
        try
        {
            var psi = new ProcessStartInfo("ssh",
                $"-o ConnectTimeout=8 -o BatchMode=yes root@10.0.0.108 " +
                "\"cd /root/waydroid-stream && nohup python3 serve.py --port {0} --codec jpeg --fps 15 " +
                "> /tmp/catclaw-stream-{0}.log 2>&1 & sleep 2; ss -ltn | grep -c ':{0} '".Replace("{0}", port.ToString()))
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) { LblHint.Text = "本机没有 ssh 命令，无法代拉起服务端。"; return; }
            var outText = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            var listening = outText.Trim();
            LblProbe.Text = listening.StartsWith("1") || listening.Contains("1")
                ? $"服务端已监听 {port}，可以点「一键连接」了"
                : $"拉起结果未确认（ssh 输出：{outText.Trim()}）；可在 108 上手工执行 bash serve.sh -d --port {port} --codec jpeg --fps 15";
        }
        catch (Exception ex)
        {
            LblHint.Text = $"ssh 拉起失败：{ShortError(ex)}";
        }
        finally
        {
            BtnStartServer.IsEnabled = true;
        }
    }

    private void StopAll()
    {
        _ticker?.Stop();
        if (_ticker is not null) _ticker.Tick -= OnTick;
        _ticker = null;
        _loop?.Cancel();
        var client = _client;
        _client = null;
        if (client is not null)
        {
            client.FrameArrived -= OnFrameArrived;
            client.PhaseChanged -= OnPhaseChanged;
            _ = client.StopAsync().ContinueWith(_ => client.DisposeAsync().AsTask());
        }
        if (_connectedOnce)
        {
            Video.IsVisible = false;
            ToolBar.IsVisible = false;
            ConnectPanel.IsVisible = true;
            _connectedOnce = false;
        }
        LblState.Text = "未连接";
        LblRemote.Text = "";
    }
}
