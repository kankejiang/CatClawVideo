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
    private volatile bool _stopped;

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
            if (_base == 0) PatchDisableSeekHead(copy);   // 仅文件头块需要修补
            _chunks.Add(copy);
            _len += count;
            return true;
        }
    }

    // MKV EBML：SeekHead 元素的 ID（0x114D9B74）的 vint 编码（4D BB 8C）
    private static readonly byte[] SeekHeadIdPattern = { 0x4D, 0xBB, 0x8C };

    /// <summary>
    /// 禁用 MKV 头部的整个 SeekHead 元素（把其 ID 的末字节 0x8C 改成 0x8D → 未知元素，FFmpeg 按尺寸跳过）。
    /// <para>为什么：FFmpeg 的 matroska demuxer 读完头部会按 SeekHead seek 到文件尾读 Cues 索引，
    /// 而引擎是顺序下载、该区间未就绪，媒体口对它挂住/拒绝——播放器永远停在 00:00（实测）。
    /// 禁用后 demuxer 无索引可用，顺序读取 Cluster 边下边播，立即出片；代价是失去精确 seek 索引
    /// （FFmpeg 退化为线性扫描）。只改 1 字节、不改任何长度，文件内其他所有偏移保持有效。</para>
    /// </summary>
    private void PatchDisableSeekHead(byte[] chunk)
    {
        const int scanLen = 16 * 1024;   // SeekHead 是文件第一个元素之后紧邻的元素，必在前几 KB
        var limit = Math.Min(chunk.Length, scanLen) - SeekHeadIdPattern.Length;
        for (var i = 0; i <= limit; i++)
        {
            var ok = true;
            for (var j = 0; j < SeekHeadIdPattern.Length; j++)
                if (chunk[i + j] != SeekHeadIdPattern[j]) { ok = false; break; }
            if (ok)
            {
                chunk[i + SeekHeadIdPattern.Length - 1] = 0x8D;   // SeekHead → 未知元素
                _log?.Invoke("[proxy] 已禁用 MKV SeekHead（顺序播放模式，不再 seek 尾部 Cues）");
                return;
            }
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
        lock (_sync)
        {
            if (newBase >= _base && newBase < _base + _len) return;   // 已在窗口内
            _base = Math.Max(0, newBase);
            _chunks.Clear();
            _len = 0;
            _epoch++;
            _upstreamEof = false;
            try { _upstream?.Close(); } catch { }   // 打断正在进行的上游读取
            _log?.Invoke($"[proxy] 重定位 → {_base / 1048576.0:F1}MB（{why}）");
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
                    var to = Math.Min(from + SliceBytes - 1, _totalSize - 1);
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

        var rq = EnterRead(first);
        try
        {
            long baseOff, frontier;
            lock (_sync) { baseOff = _base; frontier = _base + _len; }
            if (first < baseOff || first > frontier) Recenter(first, "播放器请求窗口外");

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

            // ★ 首开探测期快速失败：FFmpeg（matroska demuxer）读完头部会 seek 到文件尾读 Cues 索引，
            //   该区间引擎通常还没下载（顺序下载），挂住等待 = 播放器永远 00:00。供出数据还很少
            //   （<32MB，只在首开探测期）且请求落在文件前 1/4 之外时，把等待上限压到 5s：
            //   读 Cues 失败 → FFmpeg 放弃索引、回到 0 顺序播放（失去 seek 索引但能立刻出片）。
            var stallBudget = (_bytesServed < 32 * 1024 * 1024 && first > _totalSize / 4) ? 5_000 : 60_000;

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
