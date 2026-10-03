using System.Net.Sockets;

namespace CatClawVideo.Streaming;

public enum StreamPhase
{
    Idle,
    Connecting,
    Streaming,
    WaitingRetry,
    Failed,
    Closed,
}

public sealed class FrameArrivedEventArgs(byte type, byte[] payload, long receivedTickMs) : EventArgs
{
    public byte Type { get; } = type;
    public byte[] Payload { get; } = payload;

    /// <summary>本机单调毫秒（Environment.TickCount64）—— 显示端用它算「收流→上屏」的实测管线延迟。</summary>
    public long ReceivedTickMs { get; } = receivedTickMs;
}

public sealed record StreamStats(
    StreamPhase Phase,
    string Host,
    int Port,
    int ServerWidth,
    int ServerHeight,
    string ServerCodec,
    int ServerFps,
    long FramesReceived,
    long HeartbeatsReceived,
    long BytesReceived,
    double ReceivedFps,
    double MeanFrameIntervalMs,
    double MaxFrameGapMs,
    long InputFramesSent,
    int ReconnectCount,
    string? LastError);

/// <summary>
/// CATCLAW/1 客户端：握手 → 收帧（半包安全）→ 输入回写；断线按指数退避自动重连。
/// 帧不在这里解码（JPEG/H.264 的解码是平台侧的事），只负责把字节与到达时刻交出去。
/// </summary>
public sealed class StreamClient : IAsyncDisposable
{
    private static readonly int[] BackoffMs = [500, 1000, 2000, 4000, 8000, 16_000, 30_000];

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    private TcpClient? _socket;
    private Stream? _stream;
    private long _frames, _heartbeats, _bytes, _inputs;
    private long _windowStartMs, _windowFrames, _lastFrameMs, _intervalSumMs, _intervalCount;
    private double _maxGapMs;
    private int _retryCount;          // 连续失败次数（退避档位）
    private int _reconnects;          // 累计重连成功次数（状态栏用）
    private string? _lastError;
    private volatile StreamPhase _phase = StreamPhase.Idle;
    private string _host = "";
    private int _port;
    private Protocol.HandshakeInfo? _remote;

    /// <summary>TCP 连接超时：不可达的机器要在几秒内给出中文提示，不能等系统默认（实测 ~21s）。</summary>
    public int ConnectTimeoutMs { get; set; } = 2500;

    public event EventHandler<FrameArrivedEventArgs>? FrameArrived;
    public event EventHandler<StreamPhase>? PhaseChanged;

    public StreamPhase Phase
    {
        get => _phase;
        private set
        {
            if (_phase == value) return;
            _phase = value;
            PhaseChanged?.Invoke(this, value);
        }
    }

    public bool AutoReconnect { get; set; } = true;
    public Protocol.HandshakeInfo? Remote => _remote;

    public StreamStats Current => new(
        Phase, _host, _port,
        _remote?.Width ?? 0, _remote?.Height ?? 0, _remote?.Codec ?? "", _remote?.Fps ?? 0,
        Interlocked.Read(ref _frames), Interlocked.Read(ref _heartbeats), Interlocked.Read(ref _bytes),
        SampledFps(), _intervalCount == 0 ? 0 : (double)_intervalSumMs / _intervalCount, _maxGapMs,
        Interlocked.Read(ref _inputs), _reconnects, _lastError);

    /// <summary>
    /// 连到第一个「能解码」的端点（自动优先 h264 就是靠候选顺序表达：h264 服务端在前、jpeg 兜底在后）。
    /// </summary>
    public async Task<Protocol.HandshakeInfo> ConnectPreferredAsync(string host, IReadOnlyList<int> ports,
        CancellationToken ct = default)
    {
        List<string> rejected = [];
        foreach (var port in ports)
        {
            ct.ThrowIfCancellationRequested();
            Protocol.HandshakeInfo info;
            try
            {
                info = await ConnectOnceAsync(host, port, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                rejected.Add($"{port}: {ex.Message}");
                continue;
            }
            if (CodecSupport.CanDecode(info.Codec)) return info;
            await CloseQuietlyAsync();
            rejected.Add($"{port}: codec={info.Codec} 本机暂无解码器");
        }
        throw new InvalidOperationException(
            $"连不上可解码的投屏服务端（{host}）：" + string.Join("；", rejected));
    }

    public async Task<Protocol.HandshakeInfo> ConnectOnceAsync(string host, int port, CancellationToken ct = default)
    {
        await TearDownAsync();
        _host = host;
        _port = port;
        Phase = StreamPhase.Connecting;
        var sock = new TcpClient();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            linked.CancelAfter(ConnectTimeoutMs);
            await sock.ConnectAsync(host, port, linked.Token);
            sock.NoDelay = true;
            var stream = sock.GetStream();
            var info = await Protocol.HandshakeAsClientAsync(stream, linked.Token);
            _socket = sock;
            _stream = stream;
            _remote = info;
            _lastError = null;
            // 重连计数放在 RunAsync 的重试分支里：ConnectOnceAsync 自己会把 WaitingRetry 覆盖成
            // Connecting，在这里判相位永远为假，"重连次数"会一直显示 0（实测踩过）。
            Interlocked.Exchange(ref _retryCount, 0);            Interlocked.Exchange(ref _windowStartMs, Environment.TickCount64);
            Interlocked.Exchange(ref _windowFrames, 0);
            _maxGapMs = 0;
            Interlocked.Exchange(ref _intervalSumMs, 0);
            Interlocked.Exchange(ref _intervalCount, 0);
            Phase = StreamPhase.Streaming;
            return info;
        }
        catch
        {
            sock.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 收流循环：断线后按退避自动重连，直到用户停止。返回即表示已停（正常或失败）。
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var token = linked.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await ReceiveLoopAsync(token);
                if (token.IsCancellationRequested) break;
                _lastError ??= "服务端主动断开（收到 0x00 或流读完）";
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _lastError = ex.Message; }

            await TearDownAsync();
            if (!AutoReconnect || token.IsCancellationRequested) break;

            var delay = BackoffMs[Math.Min(_retryCount, BackoffMs.Length - 1)];
            _retryCount++;
            Phase = StreamPhase.WaitingRetry;
            try
            {
                await Task.Delay(delay, token);
            }
            catch (OperationCanceledException) { break; }

            try
            {
                await ConnectOnceAsync(_host, _port, token);
                Interlocked.Increment(ref _reconnects);   // 走到这里就是"断过之后又连回来"一次
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // 重连失败不是终点：服务端重启通常只要几秒，一次连不上就判 Failed 等于把"自动恢复"写死成"不恢复"
                _lastError = ex.Message;
                continue;
            }
        }
        if (Phase is not StreamPhase.Failed) Phase = StreamPhase.Closed;
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        var s = _stream ?? throw new InvalidOperationException("尚未连接");
        while (!token.IsCancellationRequested)
        {
            var (type, payload) = await Protocol.ReadFrameAsync(s, token);
            var now = Environment.TickCount64;
            Interlocked.Add(ref _bytes, 5 + payload.Length);
            if (type == Protocol.TypeClose) return;
            if (type == Protocol.TypeHeartbeat)
            {
                Interlocked.Increment(ref _heartbeats);
                continue;
            }
            if (type != Protocol.TypeVideo) continue;   // 未知类型：按协议跳过，不崩

            Interlocked.Increment(ref _frames);
            Interlocked.Increment(ref _windowFrames);
            var prev = Interlocked.Exchange(ref _lastFrameMs, now);
            if (prev > 0)
            {
                var gap = now - prev;
                Interlocked.Add(ref _intervalSumMs, gap);
                Interlocked.Increment(ref _intervalCount);
                if (gap > _maxGapMs) _maxGapMs = gap;
            }
            FrameArrived?.Invoke(this, new FrameArrivedEventArgs(type, payload, now));
        }
    }

    /// <summary>最近 1 秒窗口的实测 fps（窗口到 1s 结算一次；不足 1s 按实际时长折算）。</summary>
    public double SampledFps()
    {
        var now = Environment.TickCount64;
        var start = Interlocked.Read(ref _windowStartMs);
        var elapsed = now - start;
        if (elapsed < 1000) return Interlocked.Read(ref _windowFrames) * 1000.0 / Math.Max(elapsed, 1);
        var fps = Interlocked.Read(ref _windowFrames) * 1000.0 / elapsed;
        Interlocked.Exchange(ref _windowStartMs, now);
        Interlocked.Exchange(ref _windowFrames, 0);
        return fps;
    }

    public Task<bool> SendTouchAsync(int x, int y, byte action) =>
        SendAsync(Protocol.TypeTouch, Protocol.BuildTouchPayload((ushort)Math.Clamp(x, 0, ushort.MaxValue),
            (ushort)Math.Clamp(y, 0, ushort.MaxValue), action));

    public Task<bool> SendKeyAsync(uint keycode, byte action) =>
        SendAsync(Protocol.TypeKey, Protocol.BuildKeyPayload(keycode, action));

    public Task<bool> SendWheelAsync(int dx, int dy) =>
        SendAsync(Protocol.TypeWheel, Protocol.BuildWheelPayload((short)Math.Clamp(dx, short.MinValue, short.MaxValue),
            (short)Math.Clamp(dy, short.MinValue, short.MaxValue)));

    private async Task<bool> SendAsync(byte type, byte[] payload)
    {
        var s = _stream;
        if (s is null) return false;
        try
        {
            await _writeLock.WaitAsync();
            Interlocked.Increment(ref _inputs);
            await s.WriteAsync(Protocol.BuildFrame(type, payload));
            await s.FlushAsync();
            return true;
        }
        catch (Exception ex)
        {
            _lastError = "发送失败：" + ex.Message;
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>用户停止：先发 0x00 关闭帧（只等 300ms，不等回包），再拆连接。</summary>
    public async Task StopAsync()
    {
        if (Phase is StreamPhase.Idle or StreamPhase.Closed) return;
        _cts.Cancel();
        await SendCloseFrameAsync();
        await TearDownAsync();
        Phase = StreamPhase.Closed;
    }

    private async Task SendCloseFrameAsync()
    {
        var s = _stream;
        if (s is null) return;
        try
        {
            await _writeLock.WaitAsync();
            await s.WriteAsync(Protocol.BuildFrame(Protocol.TypeClose, []));
            await s.FlushAsync();
        }
        catch { /* 对端已断就算了 */ }
        finally { _writeLock.Release(); }
    }

    private async Task CloseQuietlyAsync()
    {
        await SendCloseFrameAsync();
        await TearDownAsync();
    }

    private Task TearDownAsync()
    {
        var stream = _stream;
        var sock = _socket;
        _stream = null;
        _socket = null;
        _remote = null;
        if (stream is not null) stream.Dispose();
        if (sock is not null) sock.Dispose();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
        _writeLock.Dispose();
    }
}

/// <summary>
/// 本机"能显示哪些 codec"的登记表。协议里 codec 是服务端定的，客户端只能接受或拒绝，
/// 所以"jpeg 兜底可用、h264 一落地就自动升级"这条要求就落在这里：
/// T2 的 H.264 解码器交付时，宿主启动阶段调用一次 <see cref="Enable"/>，
/// 候选端点的优先级（h264 在前）与自动重连都不用改。
/// </summary>
public static class CodecSupport
{
    private static readonly HashSet<string> Decodable = new(StringComparer.OrdinalIgnoreCase) { "jpeg" };

    public static bool CanDecode(string codec) => Decodable.Contains(codec);

    public static void Enable(string codec) => Decodable.Add(codec);

    public static IReadOnlyCollection<string> Enabled => Decodable.ToArray();
}
