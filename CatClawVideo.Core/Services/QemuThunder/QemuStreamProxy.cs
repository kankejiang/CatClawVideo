using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CatClawVideo.Core.Services.QemuThunder;

/// <summary>
/// 宿主侧「读前缓存代理」：把 QEMU 引擎取流路径的三个固有代价挡在播放器视线之外 ——
/// ① 每条新连接 ~200ms 的 guest 重武装（getLocalUrl）静默空隙；
/// ② guest 代理 accept 串行（播放器第二条连接会被饿死）；
/// ③ 分块小读时有效吞吐被连接开销拖垮（实测 1MB 块 ≈ 4.4MB/s、64KB 块不足 0.5MB/s）。
///
/// <para>工作方式：对播放器暴露普通 HTTP（支持 Range / keep-alive / 任意并发连接），
/// 数据全部从内存缓存出；对上游只用**一条**长连接顺序续拉（<see cref="SliceBytes"/> 切片）。
/// 缓冲随「最慢读者的读位置」滑动回收（<see cref="KeepBackBytes"/> 回退余量），
/// 内存上限约 <see cref="CapBytes"/>。播放器请求落在窗口外（前进跳 moov 尾 / 后退 seek）
/// 时做一次「重定位」：清空缓冲、上游改从所需偏移拉（引擎会按需优先下载该区间）。</para>
/// </summary>
public sealed class QemuStreamProxy : IDisposable
{
    private const long CapBytes = 64 * 1024 * 1024;       // 前向缓存上限
    private const long KeepBackBytes = 8 * 1024 * 1024;   // 已读数据保留余量（供回退小 seek）
    private const long SliceBytes = 32 * 1024 * 1024;     // 上游单次 Range 切片
    private const int BufSize = 256 * 1024;

    private readonly int _mediaPort;
    private readonly string _path;
    private readonly long _totalSize;
    private readonly string _contentType;
    private readonly Action<string>? _log;

    private readonly object _sync = new();
    private readonly List<byte[]> _chunks = [];
    private readonly List<Req> _requests = [];
    private long _base;            // _chunks[0][0] 对应的文件偏移
    private long _len;             // 已缓存字节数（自 _base 起连续）
    private long _epoch;           // 重定位代数（用于打断上游）
    private bool _upstreamEof;     // 当前 base 下已拉到文件尾
    private Socket? _upstream;     // 当前上游 socket（重定位时 close 以打断）
    private long _bytesServed;     // 已向播放器供出的总字节数（首开探测期判定用）
    private long _upstreamTotal;   // 上游累计收到的字节数（看门狗判断「断粮」用）
    private bool _preflightActive; // 预等待进行中（同窗口的其他请求排队等它，禁止 Recenter 乒乓）
    private long _preflightTarget;
    private volatile bool _stopped;

    /// <summary>上游累计收到的字节数（引擎看门狗：两个采样点无增长 = 上游断粮）。</summary>
    public long UpstreamTotal => Interlocked.Read(ref _upstreamTotal);

    /// <summary>是否有播放器读者在读（真实播放中才判断粮，暂停/空闲不算）。</summary>
    public bool HasReaders { get { lock (_sync) return _requests.Count > 0; } }

    /// <summary>读者正在等待「缓存前沿之外」的数据（真饥饿）。暂停时读者也挂着但停在已缓冲区，
    /// 不算饥饿——看门狗用它区分「播放器在等数据」和「暂停不动」。</summary>
    public bool ReaderStarving
    {
        get
        {
            lock (_sync)
            {
                if (_requests.Count == 0) return false;
                var frontier = _base + _len;
                foreach (var r in _requests)
                    if (r.Pos >= frontier - 256 * 1024) return true;   // 读者贴着前沿等数据
                return false;
            }
        }
    }

    /// <summary>上游已拉到当前文件的尾（EOF 后断粮属正常，看门狗跳过）。</summary>
    public bool UpstreamEof => UpstreamEofLocked();

    /// <summary>播放器 seek 重定位回调（宿主借此 KICK 引擎做区间优先下载）。</summary>
    public Action? OnRelocate;

    private TcpListener? _listener;

    private sealed class Req { public long Pos; }

    /// <summary>播放器应使用的地址（127.0.0.1 随机端口）。</summary>
    public string Url { get; private set; } = "";

    public QemuStreamProxy(int mediaPort, string path, long totalSize, string contentType, Action<string>? log = null)
    {
        _mediaPort = mediaPort;
        _path = path;
        _totalSize = Math.Max(totalSize, 1);
        _contentType = contentType;
        _log = log;
    }

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/s";
        _ = Task.Run(UpstreamLoopAsync);
        _ = Task.Run(AcceptLoopAsync);
        _log?.Invoke($"[proxy] 读前缓存代理就绪 {Url}（上游 127.0.0.1:{_mediaPort}）");
    }

    // ═══════════ 缓存（全部在 _sync 下）═══════════

    private int ReadAt(long pos, byte[] dest, int count)
    {
        lock (_sync)
        {
            if (pos < _base || pos >= _base + _len) return 0;
            var skip = (int)(pos - _base);
            var remain = (int)Math.Min(count, _len - skip);
            var done = 0;
            foreach (var chunk in _chunks)
            {
                if (skip >= chunk.Length) { skip -= chunk.Length; continue; }
                var n = Math.Min(chunk.Length - skip, remain - done);
                Buffer.BlockCopy(chunk, skip, dest, done, n);
                done += n;
                skip = 0;
                if (done >= remain) break;
            }
            return done;
        }
    }

    /// <summary>上游数据入缓冲；epoch 不一致（已被重定位）返回 false = 调用方应丢弃本切片。</summary>
    private bool Append(long epoch, byte[] data, int offset, int count)
    {
        if (count <= 0) return true;
        lock (_sync)
        {
            if (epoch != _epoch) return false;
            var copy = new byte[count];
            Buffer.BlockCopy(data, offset, copy, 0, count);
            _chunks.Add(copy);
            _len += count;
            return true;
        }
    }

    /// <summary>按最慢读者回收已消费的前缀（保持 KeepBackBytes 余量）。</summary>
    private void Trim(long slowestPos)
    {
        lock (_sync)
        {
            var target = slowestPos - KeepBackBytes;
            while (_chunks.Count > 0 && _base + _chunks[0].Length <= target)
            {
                _base += _chunks[0].Length;
                _len -= _chunks[0].Length;
                _chunks.RemoveAt(0);
            }
        }
    }

    private void Recenter(long newBase, string why)
    {
        var moved = false;
        lock (_sync)
        {
            if (newBase >= _base && newBase < _base + _len) return;   // 已在窗口内
            _base = Math.Max(0, newBase);
            _chunks.Clear();
            _len = 0;
            _epoch++;
            _upstreamEof = false;
            try { _upstream?.Close(); } catch { }   // 打断正在进行的上游读取
            moved = true;
        }
        if (moved)
        {
            _log?.Invoke($"[proxy] 重定位 → {Math.Max(0, newBase) / 1048576.0:F1}MB（{why}）");
            try { OnRelocate?.Invoke(); } catch { }   // 宿主可借此 KICK 引擎（区间优先下载）
        }
    }

    /// <summary>打断当前上游连接但保留已缓冲数据（epoch 前移使在途切片作废，上游循环随即从窗口前沿重连）。
    /// 用于引擎任务死亡后自动恢复：重新下发 DL 重建任务后，挂死在旧任务上的上游连接不会有数据，
    /// 必须断开重连，guest 重新武装 getLocalUrl 后新连接才续流。</summary>
    public void KickUpstream()
    {
        lock (_sync)
        {
            _epoch++;
            _upstreamEof = false;
            try { _upstream?.Close(); } catch { }
            _log?.Invoke("[proxy] 任务恢复：打断上游连接，等待重连续流");
        }
    }

    private Req EnterRead(long startPos)
    {
        lock (_sync) { var r = new Req { Pos = startPos }; _requests.Add(r); return r; }
    }

    private void ExitRead(Req r) { lock (_sync) _requests.Remove(r); }

    private void UpdatePos(Req r, long pos) { lock (_sync) r.Pos = pos; }

    // ═══════════ 上游：单连接顺序续拉 ═══════════

    private async Task UpstreamLoopAsync()
    {
        var consecutiveShort = 0;
        var consecutiveBad = 0;   // 上游连续异常（404 等）次数：指数退避，避免死循环打爆 guest
        while (!_stopped)
        {
            long epoch, from, baseOff, len;
            long slowest;
            lock (_sync)
            {
                epoch = _epoch; baseOff = _base; len = _len;
                from = baseOff + len;
                slowest = _requests.Count == 0 ? -1 : _requests.Min(r => r.Pos);
            }

            if (slowest < 0) { await Task.Delay(100).ConfigureAwait(false); continue; }          // 无读者：不预读
            var ahead = (baseOff + len) - slowest;
            if (ahead >= CapBytes) { Trim(slowest); await Task.Delay(100).ConfigureAwait(false); continue; }   // 缓冲够前：反压
            if (UpstreamEofLocked()) { await Task.Delay(100).ConfigureAwait(false); continue; }
            if (from >= _totalSize) { lock (_sync) _upstreamEof = true; continue; }
            if (consecutiveShort > 3) { await Task.Delay(500).ConfigureAwait(false); consecutiveShort = 0; }

            // 有读者在缓存窗口以下的位置（被重定位甩下）时也会走到这——它们会自行退出，无需特殊处理
            var abandoned = false;
            try
            {
                using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                sock.Connect(IPAddress.Loopback, _mediaPort);
                lock (_sync) _upstream = sock;
                try
                {
                    // ★ 请求区间至少 256KB（即使逼近/越过文件尾也向越界方向要满 256KB）：
                    //   引擎对未下载区间的「按需供数」靠持续的分片级读触发（rangetest 实测：
                    //   读 407MB/引擎仅下 88MB，206 真数据 0.3s 返回），而单发 1.2KB 的
                    //   尾部小请求（如 Cues）不会触发——按需调度以读得「够多」为前提。
                    //   越界部分引擎按 piece 对齐取数后按实有数据应答（206 截断）。
                    var to = Math.Min(from + SliceBytes - 1, Math.Max(_totalSize - 1, from + 256 * 1024 - 1));
                    var req = $"GET {_path} HTTP/1.0\r\nHost: 127.0.0.1:{_mediaPort}\r\nRange: bytes={from}-{to}\r\n\r\n";
                    sock.Send(Encoding.ASCII.GetBytes(req));

                    // ── 响应头 ──（15s 等不到 = 引擎没有该区间数据且挂着不给应答：
                    // guest 代理 accept 串行，这条连接不释放会饿死所有后续连接，必须超时打断）
                    var head = new List<byte>(4096);
                    var rb = new byte[4096];
                    var hdrEnd = -1;
                    var hdrDeadlineMs = Environment.TickCount64 + 15_000;
                    while (head.Count < 65536)
                    {
                        if (!sock.Poll(1_000_000, SelectMode.SelectRead))
                        {
                            if (Environment.TickCount64 > hdrDeadlineMs) break;
                            continue;
                        }
                        var n = sock.Receive(rb);
                        if (n <= 0) break;
                        Interlocked.Add(ref _upstreamTotal, n);
                        for (var i = 0; i < n; i++) head.Add(rb[i]);
                        hdrEnd = IndexOfHeaderEnd(head);
                        if (hdrEnd >= 0) break;
                    }
                    if (hdrEnd < 0)
                    {
                        consecutiveBad++;
                        var backoff = Math.Min(500L * (1L << Math.Min(consecutiveBad, 4)), 8000);
                        _log?.Invoke($"[proxy] 上游 15s 无响应头（引擎未就绪该区间？），退避 {backoff}ms 重试");
                        await Task.Delay((int)backoff).ConfigureAwait(false);
                        continue;
                    }
                    var status = Encoding.ASCII.GetString(head.ToArray(), 0, Math.Min(24, head.Count));
                    if (!status.Contains("206") && !status.Contains(" 200"))
                    {
                        consecutiveBad++;
                        var backoff = Math.Min(500L * (1L << Math.Min(consecutiveBad, 4)), 8000);
                        _log?.Invoke($"[proxy] 上游应答异常：{status.Trim()}（连续 {consecutiveBad} 次，退避 {backoff}ms）");
                        await Task.Delay((int)backoff).ConfigureAwait(false);
                        continue;
                    }
                    consecutiveBad = 0;

                    long sliceWant = to - from + 1;
                    long got = 0;
                    var extra = head.Count - (hdrEnd + 4);
                    if (extra > 0)
                    {
                        if (!Append(epoch, head.ToArray(), hdrEnd + 4, (int)extra)) abandoned = true;
                        else got = extra;
                    }
                    Interlocked.Add(ref _upstreamTotal, Math.Max(0, extra));

                    if (status.Contains(" 200") && from > 0 && !abandoned)
                    {
                        // 上游没按 Range 返回（200 从 0 开始）→ 丢弃 from 之前的字节（含已带出的部分）
                        var skipLeft = from - got;
                        var sb = new byte[BufSize];
                        while (skipLeft > 0 && !_stopped)
                        {
                            var n = sock.Receive(sb);
                            if (n <= 0) { abandoned = true; break; }
                            skipLeft -= n;
                        }
                        got = 0;
                    }

                    // ── 响应体 → 缓冲 ──
                    var buf = new byte[BufSize];
                    while (!_stopped && !abandoned && got < sliceWant)
                    {
                        bool stale;
                        long curLen, curb, slow;
                        lock (_sync)
                        {
                            stale = epoch != _epoch;
                            curLen = _len; curb = _base;
                            slow = _requests.Count == 0 ? -1 : _requests.Min(r => r.Pos);
                        }
                        if (stale) { abandoned = true; break; }
                        if (slow < 0 || (curb + curLen) - slow >= CapBytes)       // 无读者 / 前读够了：反压
                        {
                            await Task.Delay(100).ConfigureAwait(false);
                            if (slow >= 0) Trim(slow);
                            continue;
                        }
                        if (!sock.Poll(1_000_000, SelectMode.SelectRead)) continue;   // 1s 超时：回循环复查
                        var n = sock.Receive(buf);
                        if (n <= 0) break;                                            // 切片结束 / 对端关闭
                        Interlocked.Add(ref _upstreamTotal, n);
                        if (!Append(epoch, buf, 0, n)) { abandoned = true; break; }   // 被重定位
                        got += n;
                    }

                    lock (_sync)
                    {
                        if (!abandoned && epoch == _epoch && from + got >= _totalSize) _upstreamEof = true;
                    }
                    consecutiveShort = (!abandoned && got < sliceWant) ? consecutiveShort + 1 : 0;
                }
                finally { lock (_sync) _upstream = null; }
            }
            catch (Exception ex)
            {
                if (!_stopped)
                {
                    _log?.Invoke($"[proxy] 上游异常：{ex.GetType().Name}: {ex.Message}");
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }
        }
    }

    private bool UpstreamEofLocked() { lock (_sync) return _upstreamEof; }

    // ═══════════ 下游：播放器 HTTP 服务 ═══════════

    private async Task AcceptLoopAsync()
    {
        while (!_stopped)
        {
            Socket client;
            try { client = await _listener!.AcceptSocketAsync().ConfigureAwait(false); }
            catch { break; }
            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(Socket client)
    {
        using (client)
        using (var stream = new NetworkStream(client, ownsSocket: false))
        {
            try
            {
                var head = new byte[8192];
                while (!_stopped)
                {
                    var len = 0;
                    while (len < head.Length)
                    {
                        var n = await ReadWithTimeoutAsync(stream, head.AsMemory(len), 30_000).ConfigureAwait(false);
                        if (n <= 0) return;
                        len += n;
                        if (IndexOfHeaderEnd(head, len) >= 0) break;
                    }
                    if (len == 0) return;

                    var text = Encoding.ASCII.GetString(head, 0, len);
                    var lines = text.Split("\r\n");
                    var parts = lines[0].Split(' ');
                    if (parts.Length < 2) return;
                    var method = parts[0];
                    var rangeHeader = GetHeader(lines, "Range");
                    var connHeader = GetHeader(lines, "Connection");
                    var keepAlive = !(connHeader?.Contains("close", StringComparison.OrdinalIgnoreCase) ?? false);
                    if (lines[0].Contains("HTTP/1.0", StringComparison.OrdinalIgnoreCase) && !keepAlive)
                        keepAlive = false;

                    var (start, end, hasRange) = ParseRange(rangeHeader);
                    await ServeAsync(stream, method, start, end, hasRange).ConfigureAwait(false);
                    if (!keepAlive) return;
                }
            }
            catch { /* 单连接异常不影响服务 */ }
        }
    }

    private async Task ServeAsync(NetworkStream stream, string method, long start, long end, bool hasRange)
    {
        var first = Math.Min(Math.Max(0, start), _totalSize - 1);
        var last = Math.Min(end, _totalSize - 1);
        if (last < first) last = first;

        // 请求可见性：验证「avio 层 seek 是否真的走到协议层」（Seekable 修复的证据链）
        _log?.Invoke($"[proxy-req] {method} bytes={first}-{last}（窗口 [{_base / 1048576.0:F1}|{(_base + _len) / 1048576.0:F1}MB] 已供出 {_bytesServed / 1048576.0:F1}MB）");

        // ── 预等待：请求落在缓存前沿之外时（首开读 Cues / 拖进度条），先给引擎「按需下载该区间」
        //    的时间。引擎收到未下载区间的读请求会优先拉取对应分片（=「seek 到哪下到哪」的物理
        //    基础，KICK PREFETCH 进一步强化）。数据到位才回 206；超时回 416 干净拒绝。
        //    ⚠ 绝不能 206 后再掐断：那会让 avio 记住「流坏了」，此后一切 seek 退化成
        //    Soft-seeking drain（顺序丢读整个未下载区间 = 卡死黑屏，实测 15:36 会话）。
        //    ⚠ 同一时刻只允许一个预等待窗口：并发请求（主打开 + Cues 探测）若各自 Recenter
        //    会乒乓清掉对方缓存 → 主流被截断（"File ended prematurely at pos 5812" 实测）。
        {
            long frontier;
            lock (_sync) frontier = _base + _len;
            var needWait = false;
            lock (_sync)
            {
                if (first > frontier)
                {
                    if (_preflightActive && _preflightTarget != first)
                    {
                        needWait = true;   // 别人在等另一个区间：等它的结果，不要抢窗口
                    }
                    else
                    {
                        _preflightActive = true;
                        _preflightTarget = first;
                    }
                }
            }
            if (needWait)
            {
                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                while (sw2.ElapsedMilliseconds < 30_000 && !_stopped)
                {
                    lock (_sync) frontier = _base + _len;
                    if (first < frontier + 256 * 1024) break;
                    if (!_preflightActive) break;
                    await Task.Delay(200).ConfigureAwait(false);
                }
                lock (_sync) frontier = _base + _len;
                if (first > frontier)
                {
                    _log?.Invoke($"[proxy] 等待他人预等待超时仍无数据 → 416：{first}-{last}");
                    var err416b = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 416 Requested Range Not Satisfiable\r\nContent-Range: bytes */{_totalSize}\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n");
                    await stream.WriteAsync(err416b).ConfigureAwait(false);
                    return;
                }
            }
            else if (first > frontier)
            {
                var isCuesProbe = _bytesServed < 32 * 1024 * 1024 && first > _totalSize / 4;
                var budgetMs = isCuesProbe ? 20_000 : 45_000;   // Cues 在文件尾（几 MB），健康 swarm 1-3s 可达
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var lastUp = UpstreamTotal;
                var frozenMs = 0;
                var delivered = false;
                while (sw.ElapsedMilliseconds < budgetMs && !_stopped)
                {
                    lock (_sync) frontier = _base + _len;
                    if (first < frontier + 256 * 1024) { delivered = true; break; }
                    await Task.Delay(200).ConfigureAwait(false);
                    var up = UpstreamTotal;
                    if (up != lastUp) { frozenMs = 0; lastUp = up; }   // 引擎在按需拉取：等待有效
                    else if ((frozenMs += 200) > 8000) break;          // 上游冻结 8s：引擎供不了数，早失败
                }
                lock (_sync) _preflightActive = false;
                if (!delivered)
                {
                    _log?.Invoke($"[proxy] 远端区间引擎未按需供数（{(isCuesProbe ? "Cues 探测" : "拖动 seek")}，等了 {sw.ElapsedMilliseconds}ms）→ 416 干净拒绝：{first}-{last}");
                    var err416 = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 416 Requested Range Not Satisfiable\r\nContent-Range: bytes */{_totalSize}\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n");
                    await stream.WriteAsync(err416).ConfigureAwait(false);
                    return;
                }
                _log?.Invoke($"[proxy] 远端区间已按需就绪（{sw.ElapsedMilliseconds}ms）：{first}");
            }
        }

        var rq = EnterRead(first);
        try
        {
            long baseOff, frontier;
            lock (_sync) { baseOff = _base; frontier = _base + _len; }
            if ((first < baseOff || first > frontier) && !_preflightActive)
                Recenter(first, "播放器请求窗口外");   // 预等待进行中时不抢窗口（防乒乓）

            var length = last - first + 1;
            var sb = new StringBuilder(256);
            // ★ 统一回 206 + Content-Range（即使请求没带 Range）：FFmpeg 的 http 层据此判定
            //   「流可 seek」。若首个 200 响应，FFmpeg 对远端 Cues 的 seek 会退化成
            //   「Soft-seeking by draining 1.9GB」——顺序丢读整个文件，永远开不了播（实测）。
            sb.Append("HTTP/1.1 206 Partial Content\r\n").Append($"Content-Range: bytes {first}-{last}/{_totalSize}\r\n");
            sb.Append($"Content-Type: {_contentType}\r\n");
            sb.Append($"Content-Length: {length}\r\n");
            sb.Append("Accept-Ranges: bytes\r\n");
            sb.Append("Connection: keep-alive\r\n\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString())).ConfigureAwait(false);

            if (method == "HEAD") return;

            // 等待预算：60s（引擎对 seek 目标区间按需下载，通常数秒～数十秒可出数）。
            // 首开探测期的远端区间已在上面被 416 干净拒绝，走不到这里。
            const int stallBudget = 60_000;

            var buf = new byte[BufSize];
            var pos = first;
            var stallMs = 0;
            while (pos <= last && !_stopped)
            {
                long baseNow, frontierNow;
                lock (_sync) { baseNow = _base; frontierNow = _base + _len; }
                if (pos < baseNow) { _log?.Invoke("[proxy] 请求被重定位甩下，提前结束（播放器会重发）"); break; }

                var n = ReadAt(pos, buf, (int)Math.Min(buf.Length, last - pos + 1));
                if (n > 0)
                {
                    stallMs = 0;
                    await stream.WriteAsync(buf.AsMemory(0, n)).ConfigureAwait(false);
                    Interlocked.Add(ref _bytesServed, n);
                    pos += n;
                    UpdatePos(rq, pos);
                    continue;
                }
                if (UpstreamEofLocked() && pos >= frontierNow) break;   // 正常短读（文件尾）
                await Task.Delay(30).ConfigureAwait(false);
                stallMs += 30;
                if (stallMs >= stallBudget) { _log?.Invoke($"[proxy] 播放器请求等待数据超时（{stallBudget / 1000}s）"); break; }
            }
        }
        finally { ExitRead(rq); }
    }

    // ═══════════ 小工具 ═══════════

    private (long start, long end, bool hasRange) ParseRange(string? range)
    {
        if (string.IsNullOrWhiteSpace(range) || !range.StartsWith("bytes="))
            return (0, _totalSize - 1, false);
        var spec = range["bytes=".Length..].Split(',')[0].Trim();   // 多段只取第一段
        var dash = spec.IndexOf('-');
        if (dash < 0) return (0, _totalSize - 1, false);
        var a = spec[..dash];
        var b = spec[(dash + 1)..];
        if (a.Length == 0 && long.TryParse(b, out var suffix))       // bytes=-N（末尾 N 字节）
            return (Math.Max(0, _totalSize - suffix), _totalSize - 1, true);
        if (!long.TryParse(a, out var s)) return (0, _totalSize - 1, false);
        long e;
        if (b.Length == 0 || !long.TryParse(b, out e)) e = _totalSize - 1;
        return (s, e, true);
    }

    private static string? GetHeader(string[] lines, string name)
    {
        foreach (var l in lines)
        {
            var i = l.IndexOf(':');
            if (i <= 0) continue;
            if (l.AsSpan(0, i).Equals(name, StringComparison.OrdinalIgnoreCase)) return l[(i + 1)..].Trim();
        }
        return null;
    }

    private static int IndexOfHeaderEnd(byte[] buf, int len)
    {
        for (var i = 0; i + 3 < len; i++)
            if (buf[i] == 13 && buf[i + 1] == 10 && buf[i + 2] == 13 && buf[i + 3] == 10) return i;
        return -1;
    }

    private static int IndexOfHeaderEnd(List<byte> buf)
    {
        var n = buf.Count;
        for (var i = 0; i + 3 < n; i++)
            if (buf[i] == 13 && buf[i + 1] == 10 && buf[i + 2] == 13 && buf[i + 3] == 10) return i;
        return -1;
    }

    private static async Task<int> ReadWithTimeoutAsync(NetworkStream s, Memory<byte> dst, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try { return await s.ReadAsync(dst, cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return -1; }
    }

    public static string ContentTypeFor(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".mp4" or ".m4v" => "video/mp4",
            ".mkv" => "video/x-matroska",
            ".ts" or ".m2ts" => "video/mp2t",
            ".avi" => "video/x-msvideo",
            ".mov" => "video/quicktime",
            ".flv" => "video/x-flv",
            ".wmv" => "video/x-ms-wmv",
            ".webm" => "video/webm",
            ".rmvb" or ".rm" => "application/vnd.rn-realmedia",
            _ => "application/octet-stream",
        };

    public void Dispose()
    {
        if (_stopped) return;
        _stopped = true;
        try { _listener?.Stop(); } catch { }
        lock (_sync)
        {
            try { _upstream?.Close(); } catch { }
            _chunks.Clear();
        }
    }
}
