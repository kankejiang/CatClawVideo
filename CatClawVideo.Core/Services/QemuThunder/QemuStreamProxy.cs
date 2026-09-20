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
    private const int BodyStallMs = 8_000;                // 上游响应体停滞上限（超时断开重连换新请求）

    private readonly int _mediaPort;
    private readonly string _path;
    private readonly long _totalSize;
    private readonly string _contentType;
    private readonly Action<string>? _log;

    /// <summary>
    /// 数据面块设备（可选）—— guest 把引擎吐出的字节按文件偏移直接写进宿主镜像，
    /// 这里**直读同一物理文件**（实测 2454~2926 MB/s），完全绕开 SLIRP（40 MB/s）与
    /// harness 的转发循环（18.9 MB/s）。
    ///
    /// <para>为 null（旧运行时 / 未挂块设备）时行为与改动前完全一致，走 HTTP 上游。</para>
    /// </summary>
    private readonly SparseBlockStore? _blockStore;
    /// <summary>块设备命中的字节数（诊断用：证明数据面真的生效了）。</summary>
    private long _blockHits;

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
    private readonly string? _cacheDir;   // 磁盘缓存目录（null = 关闭）：{btcache}/{btih}/{fileIndex}/
    // 磁盘缓存攒写：分块**整块落盘**（StreamCache 以「长度=期望」判完整，半块不落盘防稀疏洞）。
    // pending 是当前未满 4MB 块的攒写缓冲；只有上游线程 Feed，Recenter/KickUpstream 弃置（均持 _sync）。
    private readonly byte[] _diskPending = new byte[StreamCache.ChunkSize];
    private long _diskPendingStart;      // pending[0] 对应的文件绝对偏移（总是块界）
    private int _diskPendingLen;
    private volatile bool _stopped;

    /// <summary>上游累计收到的字节数（引擎看门狗：两个采样点无增长 = 上游断粮）。</summary>
    public long UpstreamTotal => Interlocked.Read(ref _upstreamTotal);

    /// <summary>
    /// 已连续缓冲到的**文件字节偏移**（<c>_base + _len</c>）。
    /// 供播放器把「缓冲位置」换算成时间：磁力流走的是自定义 HTTP + FFmpeg，
    /// 媒体框架不报告 BufferedRanges，只能由宿主自己的读前缓存代理提供。
    /// </summary>
    public long BufferedFrontierBytes
    {
        get { lock (_sync) return _base + _len; }
    }

    /// <summary>当前文件总字节数（缓冲比例换算用）。</summary>
    public long TotalBytes => _totalSize;

    /// <summary>
    /// 从**最慢读者**当前位置到缓存前沿的连续可播字节数。
    ///
    /// <para><b>为什么用它而不是「已下载比例」</b>：磁力是乱序 P2P 下载，`已下载字节比例`
    /// 衡量整片进度，与「当前位置往后还能连续播多久」无关（实测虚高到让缓冲百分比直接
    /// 跳到 100%）。本属性给出的才是播放器真正能吃到的连续数据量 —— 它是**单调、诚实**的：
    /// 引擎供不上数据时它就停住不动。</para>
    /// </summary>
    public long ReaderAheadBytes
    {
        get
        {
            lock (_sync)
            {
                if (_requests.Count == 0) return _len;
                var slowest = _requests.Min(r => r.Pos);
                var ahead = (_base + _len) - slowest;
                return ahead > 0 ? ahead : 0;
            }
        }
    }

    /// <summary>
    /// 当前活跃的缓存代理（同一时刻只允许一个磁力播放会话）。
    /// 播放层据此查询缓冲前沿；非磁力播放时为 null。
    /// </summary>
    public static QemuStreamProxy? Current { get; internal set; }

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

    public QemuStreamProxy(int mediaPort, string path, long totalSize, string contentType, Action<string>? log = null,
        string? cacheDir = null, SparseBlockStore? blockStore = null)
    {
        _mediaPort = mediaPort;
        _path = path;
        _totalSize = Math.Max(totalSize, 1);
        _contentType = contentType;
        _log = log;
        _cacheDir = cacheDir;
        _blockStore = blockStore;
    }

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/s";
        Current = this;
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

    /// <summary>上游数据入缓冲；epoch 不一致（已被重定位）返回 false = 调用方应丢弃本切片。
    /// 同时写穿磁盘缓存（_cacheDir 非 null 时）——落盘失败不影响播放，只影响下次点播速度。</summary>
    private bool Append(long epoch, byte[] data, int offset, int count)
    {
        if (count <= 0) return true;
        long absPos;
        lock (_sync)
        {
            if (epoch != _epoch) return false;
            absPos = _base + _len;
            var copy = new byte[count];
            Buffer.BlockCopy(data, offset, copy, 0, count);
            _chunks.Add(copy);
            _len += count;
        }
        if (_cacheDir is not null) DiskFeed(absPos, data, offset, count);   // 锁外落盘（4MB 写 ~10ms，别挡读者）
        return true;
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
            _diskPendingLen = 0;               // 磁盘攒写同步弃置（新区间与半块不连续）
            try { _upstream?.Close(); } catch { }   // 打断正在进行的上游读取
            moved = true;
        }
        if (moved)
        {
            // 磁盘已覆盖该块（重看/回跳）：数据可直接秒供，不必 KICK 引擎预取（否则引擎白白重下）
            var diskCovered = _cacheDir is not null &&
                StreamCache.ChunkComplete(_cacheDir, _totalSize, Math.Max(0, newBase) / StreamCache.ChunkSize);
            _log?.Invoke($"[proxy] 重定位 → {Math.Max(0, newBase) / 1048576.0:F1}MB（{why}{(diskCovered ? "，磁盘已覆盖" : "")}）");
            if (!diskCovered)
            {
                try { OnRelocate?.Invoke(); } catch { }   // 宿主可借此 KICK 引擎（区间优先下载）
            }
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
            _diskPendingLen = 0;   // 上游打断：磁盘攒写半块弃置（后续重新拉的数据重攒）
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

    // ═══════════ 磁盘缓存（分块整块落盘；重看/换集秒供，2026-09-17 用户要求）═══════════

    /// <summary>向磁盘缓存喂入连续数据流（绝对偏移单调前进）。攒满一个 4MB 分块即整块落盘；
    /// 不连续（重定位换了区间）或回退的数据弃置当前半块——半块永不落盘，保证「块文件长度=完整数据」。</summary>
    private void DiskFeed(long abs, byte[] data, int offset, int count)
    {
        if (_cacheDir is null || count <= 0) return;
        var done = 0;
        while (done < count)
        {
            long pStart; int pLen;
            lock (_sync) { pStart = _diskPendingStart; pLen = _diskPendingLen; }

            if (pLen > 0 && abs + done > pStart + pLen) { DiscardDiskPending(); continue; }   // 跳变：半块弃置重对齐
            if (pLen > 0 && abs + done < pStart + pLen) break;                                // 回退重叠：早已写过

            if (pLen == 0)
            {
                var rem = (int)((abs + done) % StreamCache.ChunkSize);
                if (rem != 0)
                {
                    // 块界之前的零头不缓存（否则块文件带稀疏洞）——只影响缓存覆盖率，不影响正确性
                    done += Math.Min(count - done, StreamCache.ChunkSize - rem);
                    continue;
                }
                lock (_sync) _diskPendingStart = abs + done;   // 从块界起攒
            }

            var take = (int)Math.Min(count - done, StreamCache.ChunkSize - pLen);
            lock (_sync)
            {
                Buffer.BlockCopy(data, offset + done, _diskPending, pLen, take);
                _diskPendingLen = pLen + take;
            }
            done += take;
            if (pLen + take == StreamCache.ChunkSize) FlushDiskChunk();   // 攒满整块 → 落盘
        }
    }

    /// <summary>整块落盘（异步：4MB 写盘 ~10ms，别挡上游续拉；快照复用 pending 不阻塞后续喂入）。</summary>
    private void FlushDiskChunk()
    {
        long chunkIndex; byte[] snapshot;
        lock (_sync)
        {
            if (_diskPendingLen != StreamCache.ChunkSize) return;
            chunkIndex = _diskPendingStart / StreamCache.ChunkSize;
            snapshot = new byte[StreamCache.ChunkSize];
            Buffer.BlockCopy(_diskPending, 0, snapshot, 0, StreamCache.ChunkSize);
            _diskPendingLen = 0;
        }
        var cd = _cacheDir!;
        Task.Run(() => StreamCache.WriteWholeChunk(cd, chunkIndex, snapshot, StreamCache.ChunkSize));
    }

    /// <summary>文件尾的不足 4MB 半块落盘（EOF 时调用）——尾块含 Cues，重看秒开的关键。</summary>
    private void FlushDiskPartial()
    {
        long chunkIndex; byte[] snapshot; int len;
        lock (_sync)
        {
            if (_diskPendingLen == 0) return;
            chunkIndex = _diskPendingStart / StreamCache.ChunkSize;
            len = _diskPendingLen;
            snapshot = new byte[len];
            Buffer.BlockCopy(_diskPending, 0, snapshot, 0, len);
            _diskPendingLen = 0;
        }
        var cd = _cacheDir!;
        Task.Run(() => StreamCache.WriteWholeChunk(cd, chunkIndex, snapshot, len));
    }

    private void DiscardDiskPending() { lock (_sync) _diskPendingLen = 0; }

    /// <summary>从磁盘缓存读 <c>[pos, pos+count)</c>：命中连续完整块则填充 dest，遇未缓存块止步。</summary>
    private int ReadDisk(long pos, byte[] dest, int count)
    {
        if (_cacheDir is null) return 0;
        var done = 0;
        while (done < count)
        {
            var abs = pos + done;
            var ci = abs / StreamCache.ChunkSize;
            if (!StreamCache.ChunkComplete(_cacheDir, _totalSize, ci)) break;
            var within = (int)(abs - ci * StreamCache.ChunkSize);
            var take = (int)Math.Min(count - done, StreamCache.ChunkSize - within);
            var n = StreamCache.ReadChunkData(_cacheDir, ci, within, dest, done, take);
            if (n < take) { done += Math.Max(0, n); break; }   // 读异常/短读：止步，下次重试
            done += n;
        }
        return done;
    }

    private bool DiskCovers(long pos) =>
        _cacheDir is not null && StreamCache.ChunkComplete(_cacheDir, _totalSize, pos / StreamCache.ChunkSize);

    // ── 按需供数预取（2026-09-17）：预等待期间读者尚未 EnterRead，上游循环判「无读者」不拉数据
    //    （slowest<0 → continue），引擎收不到任何读请求 → 按需供数无从触发 → 预等待纯等 20s → 416。
    //    MP4 的 moov 在文件尾：416 = 拿不到 moov = frames:0 = Duration 未知 → 起播即 MediaEnded。
    //    故预等待时必须**主动**对目标块发 ≥256KB 的 Range 读（触发引擎分片级按需供数），
    //    数据攒整块落磁盘缓存，服务循环 ReadDisk 命中秒供。独立连接，与上游顺序拉互不干扰。──

    private readonly object _prefetchSync = new();
    private readonly HashSet<long> _prefetching = [];

    /// <summary>请求落在缓存前沿之外时启动：对 pos 所在 4MB 块整块预取落盘（去重、异步）。</summary>
    private void KickOnDemandPrefetch(long pos)
    {
        if (_cacheDir is null || _stopped) return;
        var ci = pos / StreamCache.ChunkSize;
        lock (_prefetchSync)
        {
            if (!_prefetching.Add(ci)) return;   // 已在拉
        }
        _ = Task.Run(() => PrefetchChunkAsync(ci));
    }

    private async Task PrefetchChunkAsync(long ci)
    {
        var cd = _cacheDir!;
        try
        {
            if (StreamCache.ChunkComplete(cd, _totalSize, ci)) return;
            var from = ci * StreamCache.ChunkSize;
            var to = Math.Min(from + StreamCache.ChunkSize - 1, _totalSize - 1);
            var want = (int)(to - from + 1);
            // 尾块可能不足 256KB（引擎对 <256KB 的小请求不做按需供数）→ 向后扩展凑满，
            // 扩展段与上游循环同思路：读后丢弃，不写入缓存数据
            var ext = want < 256 * 1024 && from > 0 ? Math.Min(from, 256 * 1024 - want) : 0;
            var from2 = from - ext;
            var data = new byte[want];
            var got = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var consecutiveBad = 0;
            while (got < want && !_stopped && sw.ElapsedMilliseconds < 90_000)
            {
                try
                {
                    using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    sock.Connect(IPAddress.Loopback, _mediaPort);
                    // 首轮从 from2（含扩展段）；重试从 from+got（扩展段已消费过）
                    var rangeStart = got == 0 ? from2 : from + got;
                    var req = $"GET {_path} HTTP/1.0\r\nHost: 127.0.0.1:{_mediaPort}\r\nRange: bytes={rangeStart}-{to}\r\n\r\n";
                    sock.Send(Encoding.ASCII.GetBytes(req));

                    // 响应头（15s 等不到 = 引擎无该区间数据且挂着 → 断开退避重试）
                    var head = new List<byte>(4096);
                    var rb = new byte[65536];
                    var hdrEnd = -1;
                    var deadline = Environment.TickCount64 + 15_000;
                    while (head.Count < 262144)
                    {
                        if (!sock.Poll(1_000_000, SelectMode.SelectRead))
                        {
                            if (Environment.TickCount64 > deadline) break;
                            continue;
                        }
                        var n = sock.Receive(rb);
                        if (n <= 0) break;
                        for (var i = 0; i < n; i++) head.Add(rb[i]);
                        hdrEnd = IndexOfHeaderEnd(head);
                        if (hdrEnd >= 0) break;
                    }
                    var status = Encoding.ASCII.GetString(head.ToArray(), 0, Math.Min(24, head.Count));
                    if (hdrEnd < 0 || (!status.Contains("206") && !(status.Contains(" 200") && from2 == 0)))
                    {
                        if (++consecutiveBad > 6) break;
                        await Task.Delay((int)Math.Min(500L * (1L << consecutiveBad), 8000L)).ConfigureAwait(false);
                        continue;
                    }
                    consecutiveBad = 0;

                    // 响应体布局：[from2, to] = 扩展段(ext 字节，读丢) + 目标数据(want 字节)。
                    // 首轮请求从 from2 起（先丢扩展段）；断线重试从 from+got 起（扩展段已消费）
                    var consumedExt = got == 0 ? ext : 0;
                    var ex = head.Count - (hdrEnd + 4);
                    if (ex > 0)
                    {
                        // 头部已带出的 body：先丢弃其中的扩展段前缀，再把目标数据收进 data
                        var headBuf = head.ToArray();
                        var off = hdrEnd + 4;
                        var drop = (int)Math.Min(ex, (int)consumedExt);
                        off += drop; consumedExt -= drop;
                        var n = Math.Min(ex - drop, want - got);
                        if (n > 0)
                        {
                            Buffer.BlockCopy(headBuf, off, data, got, n);
                            got += n;
                        }
                    }
                    while (consumedExt > 0 && !_stopped)
                    {
                        if (!sock.Poll(1_000_000, SelectMode.SelectRead)) continue;
                        var n = sock.Receive(rb);
                        if (n <= 0) break;
                        consumedExt -= n;   // 流上剩余扩展段：读丢
                    }
                    while (got < want && !_stopped)
                    {
                        if (!sock.Poll(1_000_000, SelectMode.SelectRead)) continue;
                        var n = sock.Receive(data, got, want - got, SocketFlags.None);
                        if (n <= 0) break;
                        got += n;
                    }
                }
                catch (Exception)
                {
                    if (++consecutiveBad > 6) break;
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }
            if (got == want)
            {
                StreamCache.WriteWholeChunk(cd, ci, data, want);
                _log?.Invoke($"[proxy] 按需预取块 #{ci}（{from / 1048576.0:F1}MB）完成：{got / 1024}KB / {sw.ElapsedMilliseconds}ms");
            }
            else
            {
                _log?.Invoke($"[proxy] 按需预取块 #{ci} 未完成：{got / 1024}KB / {want / 1024}KB（{sw.ElapsedMilliseconds}ms）——半块弃置");
            }
        }
        finally
        {
            lock (_prefetchSync) _prefetching.Remove(ci);
        }
    }

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
            // 读者已推进到窗口前沿 64MB 之外（磁盘缓存供数 / 快速拖动）：追窗再拉，
            // 避免上游从旧前沿顺序狂拉读者早已消费的数据（重看场景 = 引擎白白重下整文件）
            if (slowest > baseOff + len + CapBytes)
            {
                Recenter(slowest - KeepBackBytes, "读者已由磁盘供数，追窗");
                await Task.Delay(50).ConfigureAwait(false);
                continue;
            }
            var ahead = (baseOff + len) - slowest;
            if (ahead >= CapBytes) { Trim(slowest); await Task.Delay(100).ConfigureAwait(false); continue; }   // 缓冲够前：反压
            if (UpstreamEofLocked()) { await Task.Delay(100).ConfigureAwait(false); continue; }
            if (from >= _totalSize)
            {
                lock (_sync) _upstreamEof = true;
                FlushDiskPartial();   // 文件尾半块落盘（含 Cues，重看秒开）
                continue;
            }
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
                    // ★ 请求区间至少 256KB：引擎对未下载区间的「按需供数」靠分片级读触发
                    //   （rangetest 实测：读 407MB/引擎仅下 88MB，206 真数据 0.3s 返回；
                    //   尾部 256KB 同样可用），而单发 1.2KB 的尾部小请求（如 Cues）不触发。
                    //   ⚠ 引擎拒绝越界 Range（实测 to 越过文件尾即失败）——尾部小 slice 改为
                    //   「向后扩展」：from 前移至覆盖满 256KB，前移段与缓存重叠的部分读后丢弃。
                    var to = Math.Min(from + SliceBytes - 1, _totalSize - 1);
                    long overlapSkip = 0;
                    if (to - from + 1 < 256 * 1024 && from > 0)
                    {
                        overlapSkip = Math.Min(from, 256 * 1024 - (to - from + 1));
                        from -= overlapSkip;
                    }
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

                    var overlapSkipTotal = overlapSkip;
                    long sliceWant = to - from + 1 - overlapSkipTotal;   // 需要新入缓存的字节数（扣除向后扩展的重叠段）
                    long got = 0;
                    var extra = head.Count - (hdrEnd + 4);
                    if (extra > 0)
                    {
                        // 向后扩展产生的重叠段：extra 的前 overlapSkip 部分与缓存重复，不入内存窗。
                        // 但它是真实文件数据（206 的 [from, from+drop)）→ 喂磁盘缓存（绝对偏移写入，无洞问题：
                        // DiskFeed 只从块界攒、半块不落盘）
                        var exOff = hdrEnd + 4;
                        var exLen = (int)extra;
                        if (overlapSkipTotal > 0)
                        {
                            var drop = (int)Math.Min(exLen, overlapSkipTotal);
                            if (_cacheDir is not null) DiskFeed(from, head.ToArray(), exOff, drop);
                            exOff += drop; exLen -= drop;
                        }
                        if (exLen > 0)
                        {
                            if (!Append(epoch, head.ToArray(), exOff, exLen)) abandoned = true;
                            got = exLen;
                        }
                    }
                    Interlocked.Add(ref _upstreamTotal, Math.Max(0, extra));

                    if (overlapSkipTotal > 0 && !abandoned)
                    {
                        // 流上剩余的与缓存重叠字节：读丢（读动作本身用于触发引擎按需供数）；
                        // 磁盘缓存开启时同样喂盘（这些是 from+extra 起的真实文件数据）
                        var discard = new byte[BufSize];
                        var left = overlapSkipTotal - Math.Max(0, extra);
                        long diskAbs = from + extra;
                        while (left > 0 && !_stopped && !abandoned)
                        {
                            if (!sock.Poll(1_000_000, SelectMode.SelectRead)) break;
                            var n = sock.Receive(discard);
                            if (n <= 0) { abandoned = true; break; }
                            Interlocked.Add(ref _upstreamTotal, n);
                            if (_cacheDir is not null) DiskFeed(diskAbs, discard, 0, (int)Math.Min(n, left));
                            diskAbs += n;
                            left -= n;
                        }
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
                        Interlocked.Add(ref _upstreamTotal, n);
                        if (!Append(epoch, buf, 0, n)) { abandoned = true; break; }   // 被重定位
                        got += n;
                    }

                    var reachedEof = false;
                    lock (_sync)
                    {
                        // EOF 判定：请求起点 + 向后扩展重叠段 + 已入缓存字节 覆盖到文件尾
                        reachedEof = !abandoned && epoch == _epoch && from + overlapSkipTotal + got >= _totalSize;
                        if (reachedEof) _upstreamEof = true;
                    }
                    if (reachedEof) FlushDiskPartial();   // 尾部半块落盘（含 Cues）
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
        var diskHit = _cacheDir is not null &&
            StreamCache.ChunkComplete(_cacheDir, _totalSize, first / StreamCache.ChunkSize);
        _log?.Invoke($"[proxy-req] {method} bytes={first}-{last}（窗口 [{_base / 1048576.0:F1}|{(_base + _len) / 1048576.0:F1}MB] 已供出 {_bytesServed / 1048576.0:F1}MB）{(diskHit ? "【磁盘命中】" : "")}");

        // ── Cues 探测的有限预等待：**仅**首开阶段读文件尾（FFmpeg 查 mfra/Cues 这类可选索引）时用。
        //    可选索引拿不到也能正常播，故这里「等一小会儿再空应答」是安全的。
        //    ⚠ 真 seek（拖进度条）**绝不走这条路**：空体会被 FFmpeg 当成 EOF → 播放器跳片尾 →
        //    触发 MediaEnded → 单集影片误弹「已经是最后一集了」（2026-09-18 用户实测）。
        //    真 seek 直接落下面的正常供数路径（注册读者 + Recenter 触发 KICK PREFETCH + 60s 停滞预算），
        //    数据边到边供，响应体绝不空。
        //    ★ 磁盘已覆盖请求块（重看/换集回看）时连预等待都省掉：数据本地秒供。
        {
            long frontier;
            lock (_sync) frontier = _base + _len;
            // Cues 探测 = 首开阶段读文件尾（FFmpeg 查 mfra/Cues 这类**可选**索引）。
            var isCuesProbe = _bytesServed < 32 * 1024 * 1024 && first > _totalSize / 4;
            if (isCuesProbe && first > frontier && !diskHit)
            {
                // Cues（文件尾几 MB）只影响精确 seek；FFmpeg 已配 FastSeek + 关闭 ReadAhead，缺 Cues 也能播。
                // 2026-09-17 实测：死等 18.6s 拿不到也救不回来，纯浪费起播时间 → 预算 6s。
                const int budgetMs = 6_000;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var lastUp = UpstreamTotal;
                var frozenMs = 0;
                var delivered = false;
                while (sw.ElapsedMilliseconds < budgetMs && !_stopped)
                {
                    lock (_sync) frontier = _base + _len;
                    if (first < frontier + 256 * 1024 || DiskCovers(first)) { delivered = true; break; }
                    await Task.Delay(200).ConfigureAwait(false);
                    var up = UpstreamTotal;
                    if (up != lastUp) { frozenMs = 0; lastUp = up; }   // 引擎在按需拉取：等待有效
                    else if ((frozenMs += 200) > 2500) break;          // 上游冻结 2.5s：引擎供不了这个可选索引，别耗
                }
                if (!delivered)
                {
                    // ⚠ 这里回 206 空应答而非 416：416 是**协议级错误**，FFmpeg 的 http 层收到后会判定
                    //   该 AVIO 不可用并放弃整个流（随后零 Range 请求、播放器关连接、上游循环转「无读者」
                    //   永久停车）。2026-09-17 A/B 实测（同一部片、同一台机，唯一变量就是尾探测结果）：
                    //     暖缓存 → 尾探测 206（磁盘命中）→ 持续请求 mdat → 1798~2900 帧/分 正常
                    //     冷缓存 → 尾探测 416 → 之后 0 个 Range 请求 → 6~12 帧 卡死
                    //   mfra/Cues 是**可选**索引（本片 moov 就在文件头，实测 box：ftyp@0 / moov@24 /
                    //   mdat 紧随），拿不到对播放毫无影响 → 206 + Content-Length: 0 让 FFmpeg 当一次
                    //   失败短读跳过即可。**注意此策略只对 Cues 探测成立**（真 seek 用空体 = 跳片尾）。
                    _log?.Invoke($"[proxy] Cues 探测等了 {sw.ElapsedMilliseconds}ms 拿不到 → 206 空应答（不回 416，避免 FFmpeg 弃流）：{first}-{last}");
                    var empty206 = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {first}-{last}/{_totalSize}\r\n" +
                        $"Content-Type: {_contentType}\r\nContent-Length: 0\r\nAccept-Ranges: bytes\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(empty206).ConfigureAwait(false);
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
            // 请求落在窗口外（真 seek / 首开）：重定位到该偏移，触发 OnRelocate → KICK PREFETCH
            // 让引擎优先下载该区间。响应体随后照常流式供数（边到边给），绝不空体返回。
            if (first < baseOff || first > frontier)
                Recenter(first, "播放器请求窗口外");

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

                var want = (int)Math.Min(buf.Length, last - pos + 1);

                // ★① 块设备直读（最高优先级）：guest 已把这段数据按偏移写进宿主镜像，
                //   直读 2454~2926 MB/s，完全不碰 SLIRP / HTTP 上游。
                //   IsRangeAvailable 先判「guest 真的写过这段」——未写区间读到的是全零，
                //   直接用会喂给播放器假数据，所以必须判。
                var n = 0;
                if (_blockStore is not null && _blockStore.IsRangeAvailable(pos, want))
                {
                    n = _blockStore.ReadAt(pos, buf, 0, want);
                    if (n > 0) Interlocked.Add(ref _blockHits, n);
                }

                if (n <= 0) n = ReadAt(pos, buf, want);                                   // ② 内存缓存
                if (n <= 0 && _cacheDir is not null) n = ReadDisk(pos, buf, want);         // ③ 磁盘缓存
                if (n > 0)
                {
                    stallMs = 0;
                    await stream.WriteAsync(buf.AsMemory(0, n)).ConfigureAwait(false);
                    Interlocked.Add(ref _bytesServed, n);
                    pos += n;
                    UpdatePos(rq, pos);
                    continue;
                }
                if (pos < baseNow) { _log?.Invoke("[proxy] 请求被重定位甩下，提前结束（播放器会重发）"); break; }

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
