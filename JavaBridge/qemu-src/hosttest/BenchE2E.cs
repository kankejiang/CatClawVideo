// 端到端数据面基准：真实 QEMU + 迅雷引擎写入 → 宿主直读 vs 经 SLIRP 的 HTTP 通道。
//
// 为什么需要它：宿主侧基准只覆盖通道的一半（读）；另一半是 guest 侧的写入。
// 本模式起真实 VM，用**宿主自己产的合成直链**喂给 guest 引擎（不依赖外网），
// 于是可以：
//   ① 验证 harness 的 blk_write 在真实链路里确实落盘（宿主能读到非零、可校验的字节）
//   ② 测「任务下发 → 宿主块设备首次可读」的端到端首写延迟
//   ③ 对**同一段已落盘数据**做两条通道的同场对照：
//        A: HTTP 经 SLIRP（http://127.0.0.1:<mediaPort>/... → hostfwd → guest 代理 → 引擎）
//        B: 块设备直读（SparseBlockStore.ReadAt）
//      这才是「qemu 引擎和宿主之间」传输速度/延迟/并发数的直接答案。
//
// 用法:
//   dotnet run -c Release --project hosttest -- xfer-e2e <runtimeDir> [fileMB] [waitMB] [mediaPort]
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CatClawVideo.Core.Services.QemuThunder;
using Microsoft.Win32.SafeHandles;

internal static class BenchE2E
{
    private const long MB = 1024 * 1024;
    private const int CtrlPort = 18080;          // 烧死在 initrd 里，改不了
    /// <summary>合成源端口：固定，好让 guest 里的 busybox wget（initrd 基准补丁）能直接访问。</summary>
    private const int BenchPort = 18099;

    /// <summary>
    /// 源数据的 backing 文件。**存在就让合成源从磁盘真实读，而不是在内存里算出来** ——
    /// 否则测的只是 CPU + loopback，与「数据要落盘、内存只做缓存」的真实形态不符。
    /// </summary>
    private static readonly string BackingPath =
        Environment.GetEnvironmentVariable("BENCH_SRC")
        ?? Path.Combine(Path.GetTempPath(), "catclaw-bench-src.bin");

    private static readonly List<string> LogLines = [];

    private static void Log(string m)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {m}";
        LogLines.Add(line);
        Console.WriteLine(line);
    }

    /// <param name="extUrl">非空则让引擎去下这个**外部直链**（如系统镜像 ISO），
    /// 只考核「引擎能否把大文件下完 + tmpfs 容量够不够」；合成源仍会起、只用于宿主自测。</param>
    public static async Task<int> RunAsync(string runtimeDir, long fileMb, long waitMb, int mediaPort, string? extUrl = null)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        var fileBytes = fileMb * MB;
        var imgPath = Path.Combine(Path.GetTempPath(), "catclaw-e2e-bench.img");

        // ★ 源数据落盘：有 backing 文件就用它（真实磁盘读路径），否则回退内存生成
        var backing = File.Exists(BackingPath) ? BackingPath : null;
        if (backing is not null)
        {
            fileBytes = new FileInfo(backing).Length;
            fileMb = fileBytes / MB;
        }

        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║  数据面基准 · 端到端（QEMU 引擎写入 → 宿主直读  vs  经 SLIRP 的 HTTP）   ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine($"  运行时 : {runtimeDir}");
        Console.WriteLine($"  镜像   : {imgPath}（{fileMb}MB 稀疏）");
        Console.WriteLine($"  媒体口 : {mediaPort}   控制口 : {CtrlPort}");
        Console.WriteLine();

        if (!QemuHostRuntime.IsPresent(runtimeDir)) { Console.WriteLine("✗ 运行时缺失"); return 1; }

        // ── 合成数据源：宿主自产，guest 经 10.0.2.2 拉 ──
        using var src = new SynthSource(fileBytes, BenchPort, Log, backing);
        Log(backing is not null
            ? $"[准备] 合成直链源 http://127.0.0.1:{src.Port}/synth.mp4（{fileMb}MB，**从磁盘文件 {Path.GetFileName(backing)} 真实读**）"
            : $"[准备] 合成直链源 http://127.0.0.1:{src.Port}/synth.mp4（{fileMb}MB，内存生成 —— 未找到 backing 文件）");
        Log($"        同一源也供 guest 内的 busybox wget 访问（http://10.0.2.2:{BenchPort}/bench.bin）");

        // ★ 关键对照：合成源**自身的并发供数上限**（纯宿主、不经 QEMU）。
        //   没有这一步就无法排除「引擎慢是因为测试装置太弱」——
        //   实测 wget 单连接能跑到 59 MB/s，而引擎只有 ~26 MB/s，必须知道宿主侧天花板在哪。
        foreach (var c in new[] { 1, 4, 8, 16, 32 })
        {
            var self = await HostSelfTestAsync(src.Port, 16 * MB, c);
            Log($"[自测] 宿主并发 {c,2} 连接拉合成源 → {self,7:F0} MB/s");
        }
        Console.WriteLine();

        // ★ 模拟产品真正要做的事：**一边读、一边导出到本地磁盘**（数据不停在内存里）。
        //   既实测磁盘写路径，也回答「导出会不会拖慢供数」。
        {
            var sink = Path.Combine(Path.GetTempPath(), "catclaw-sink.bin");
            const int mb = 256;
            var t0 = Stopwatch.GetTimestamp();
            long total = 0;
            using (var h2 = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
            using (var resp = await h2.GetAsync($"http://127.0.0.1:{src.Port}/synth.mp4", HttpCompletionOption.ResponseHeadersRead))
            await using (var s = await resp.Content.ReadAsStreamAsync())
            using (var outFs = new FileStream(sink, FileMode.Create, FileAccess.Write, FileShare.None, 4 << 20))
            {
                var b2 = new byte[1 << 20];
                while (total < (long)mb * MB)
                {
                    var rd2 = await s.ReadAsync(b2);
                    if (rd2 <= 0) break;
                    await outFs.WriteAsync(b2.AsMemory(0, rd2));
                    total += rd2;
                }
                await outFs.FlushAsync();
            }
            var el = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            Log($"[自测] 边读边导出到磁盘 {total / (double)MB:F0}MB / {el / 1000:F2}s"
                + $" = {total / (double)MB / (el / 1000):F0} MB/s（含 Flush）→ {Path.GetFileName(sink)}");
            try { File.Delete(sink); } catch { }
        }
        Console.WriteLine();

        using var rt = new QemuHostRuntime(runtimeDir, mediaPort, Log, "pkg_initrd.gz", "", 0, imgPath, fileBytes);
        if (rt.BlockStore is null) { Log("✗ 块设备未就绪，无法做数据面对照"); return 2; }

        using var ctl = new QemuControlServer(CtrlPort);
        string? playPath = null;
        var playAt = TimeSpan.Zero;
        var swAll = Stopwatch.StartNew();
        ctl.ReportReceived += r =>
        {
            if (r.Ev == "play" && r.Msg.Length > 0) { playPath ??= r.Msg; playAt = swAll.Elapsed; }
            if (r.Ev == "status" && r.Done > 0) LastDone = r.Done;
        };
        ctl.Log += Log;
        ctl.Start();

        // ── 起 VM ──
        if (!await rt.StartAsync()) { Log("✗ QEMU 启动失败"); return 3; }
        if (!await ctl.WaitFirstPollAsync(TimeSpan.FromSeconds(900))) { Log("✗ guest 900s 未轮询（VM 没起来，或 /init 的前置测速段卡住）"); return 4; }
        var bootSec = swAll.Elapsed.TotalSeconds;
        Log($"[1] VM 冷启动 → guest 就绪：{bootSec:F1}s");

        // ── 下发直链任务 ──
        var tTask = swAll.Elapsed;
        var taskUrl = extUrl ?? $"http://10.0.2.2:{src.Port}/synth.mp4";
        var taskName = extUrl is null ? "synth.mp4" : Path.GetFileName(new Uri(extUrl).AbsolutePath);
        ctl.SetCommand($"TASK URL {taskUrl} {taskName}");
        Log($"[2] 已下发{(extUrl is null ? "合成" : "**外部**")}直链任务：{taskName}");
        if (extUrl is not null) Log($"     源：{extUrl}");

        // ── 等引擎把文件下完 ──
        // ★ 关键认知（2026-09-21 实测）：数据面是「**宿主读的时候**顺带落盘」——
        //   harness 在把引擎字节转发给宿主时，另写一份进 virtio-blk。引擎自己下载**不写块设备**
        //   （实测：引擎 done=512MB 时宿主镜像仍是 0 字节，且 harness 的 [blk] 日志一行都没有）。
        //   所以必须先让宿主通过媒体口读一遍（回填），之后对同区间的重读才能走直读。
        const long span = 64 * MB;
        const long off0 = 0;
        long alloc = 0;
        var lastLog = TimeSpan.Zero;
        var deadline = DateTime.UtcNow.AddMinutes(extUrl is null ? 3 : 30);
        var txTarget = extUrl is null ? fileBytes * 9 / 10 : waitMb * MB;
        while (DateTime.UtcNow < deadline && LastDone < txTarget)
        {
            if (swAll.Elapsed - lastLog > TimeSpan.FromSeconds(3))
            {
                lastLog = swAll.Elapsed;
                Log($"    …引擎下载 {LastDone / (double)MB:F0}MB / {fileMb}MB");
            }
            await Task.Delay(200);
        }
        Log($"[2] 引擎下载完成：done={LastDone / (double)MB:F0}MB"
            + $"（耗时 {swAll.Elapsed.TotalSeconds - tTask.TotalSeconds:F1}s）");

        // 外部直链模式：只考核「引擎能否把大文件下完」（tmpfs 容量、任务存活），不做数据面对照
        if (extUrl is not null)
        {
            var secs = Math.Max(0.1, swAll.Elapsed.TotalSeconds - tTask.TotalSeconds);
            var mm = LastDone / (double)MB;
            Console.WriteLine();
            Console.WriteLine("════════════════════════════════════════════════════════════════════════════");
            Console.WriteLine($"  【外部直链模式】引擎下载 {mm:F0}MB / {secs:F1}s = {mm / secs:F1} MB/s");
            Console.WriteLine($"  源：{extUrl}");
            Console.WriteLine($"  guest tmpfs 情况见上方 [init] 行；块设备镜像已落 "
                              + $"{rt.BlockStore.AllocatedBytes() / (double)MB:F0}MB");
            Console.WriteLine("  （外部源不经本机数据面，故跳过回填与块设备对照）");
            Console.WriteLine("════════════════════════════════════════════════════════════════════════════");
            var logPath2 = Path.Combine(Path.GetDirectoryName(imgPath)!, "catclaw-e2e-bench.log");
            try { await File.WriteAllLinesAsync(logPath2, LogLines); } catch { }
            return 0;
        }
        Log($"    此刻块设备已落 {rt.BlockStore.AllocatedBytes() / (double)MB:F0}MB"
            + "   <- 引擎下载不会写块设备（符合设计）");

        if (LastDone < span) { Log("✗ 引擎未下够 64MB"); return 5; }

        var mediaUrl = playPath is not null ? $"http://127.0.0.1:{mediaPort}{playPath}" : null;
        if (mediaUrl is null) { Log("✗ 未收到 play 事件，拿不到媒体口地址"); return 6; }

        // ── 回填：宿主经 HTTP 读一段（唯一能触发 blk_write 的时机）──
        Log($"[3] 回填：宿主经 HTTP 读 [{off0 / MB}MB, {span / MB}MB) —— 这一步才触发 harness 的 pwrite");
        var (fb, tt, n) = await RawGetAsync(mediaUrl, off0, span);
        var slirpMbps = n / (double)MB / (tt / 1000);
        Log($"    HTTP 读 {n / (double)MB:F1}MB：首字节 {fb:F0}ms  总 {tt / 1000:F1}s"
            + $"  ≈{slirpMbps:F1} MB/s   <- 通道 A（经 SLIRP）的实测吞吐");

        var tWait = Stopwatch.StartNew();
        while (tWait.Elapsed < TimeSpan.FromSeconds(60))
        {
            if (RangeAllAvailable(rt.BlockStore, off0, span)) break;
            await Task.Delay(100);
        }
        alloc = rt.BlockStore.AllocatedBytes();
        Log($"[4] 回填后块设备已落 {alloc / (double)MB:F0}MB（等待 {tWait.Elapsed.TotalSeconds:F1}s）");
        if (alloc < 16 * MB) { Log("✗ 块设备仍未落盘，数据面未生效"); return 7; }
        Console.WriteLine();

        // ═══ 通道 A 补充：新连接的固有开销 ═══
        Console.WriteLine("── 通道 A 补充：新连接开销（guest 每次都要重新 getLocalUrl + 换端口）──────────");
        {
            var (fb1, tt1, n1) = await RawGetAsync(mediaUrl, off0 + 4 * MB, 256 * 1024);
            Console.WriteLine($"   新连接读 256KB：首字节 {fb1:F0}ms  总 {tt1:F0}ms  字节 {n1}");
        }
        Console.WriteLine();

        // ═══ 通道 B：块设备直读 ═══
        Console.WriteLine("── 通道 B：块设备直读（宿主直接读 guest 写入的同一物理镜像）─────────────────");
        var buf = new byte[256 * 1024];
        {
            var st = new List<double>();
            var t0 = Stopwatch.GetTimestamp();
            for (var i = 0; i < 20; i++)
            {
                var a = Stopwatch.GetTimestamp();
                rt.BlockStore.ReadAt(off0 + (long)i * buf.Length, buf, 0, buf.Length);
                st.Add((Stopwatch.GetTimestamp() - a) * 1_000_000.0 / Stopwatch.Frequency);
            }
            var wall = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            st.Sort();
            Console.WriteLine($"   单次 256KB：P50 {st[10]:F1}µs  P99 {st[19]:F1}µs"
                              + $"  吞吐 ≈{20 * buf.Length / (double)MB / (wall / 1000):F0} MB/s");

            // 连续读 64MB
            var big = new byte[1024 * 1024];
            var t1 = Stopwatch.GetTimestamp();
            long got = 0;
            for (var o = off0; o < off0 + span; o += big.Length)
            {
                var rd = rt.BlockStore.ReadAt(o, big, 0, big.Length);
                if (rd <= 0) break;
                got += rd;
            }
            var wall2 = (Stopwatch.GetTimestamp() - t1) * 1000.0 / Stopwatch.Frequency;
            Console.WriteLine($"   连续 {span / MB}MB（单线程，1MB 块）：{(wall2 / 1000):F2}s"
                              + $"  ≈{got / (double)MB / (wall2 / 1000):F0} MB/s  字节 {got / (double)MB:F1}MB");

            // 并发扩展
            Console.WriteLine("   并发扩展（256KB 块，各线程读不同段）：");
            foreach (var th in new[] { 2, 4, 8 })
            {
                var per = span / th;
                var ths = new Thread[th];
                long total = 0;
                var barrier = new Barrier(th);
                var w0 = Stopwatch.GetTimestamp();
                for (var k = 0; k < th; k++)
                {
                    var idx = k;
                    ths[idx] = new Thread(() =>
                    {
                        var local = new byte[256 * 1024];
                        var start = off0 + idx * per;
                        barrier.SignalAndWait();
                        var g = 0L;
                        for (var o = start; o < start + per; o += local.Length)
                        {
                            var n = rt.BlockStore.ReadAt(o, local, 0, local.Length);
                            if (n <= 0) break;
                            g += n;
                        }
                        Interlocked.Add(ref total, g);
                    }) { IsBackground = true };
                    ths[idx].Start();
                }
                foreach (var t in ths) t.Join();
                var wall3 = (Stopwatch.GetTimestamp() - w0) * 1000.0 / Stopwatch.Frequency;
                Console.WriteLine($"     {th} 线程：{total / (double)MB / (wall3 / 1000):F0} MB/s");
            }
        }
        Console.WriteLine();

        // ═══ 数据正确性：直读到的字节与合成源是否一致 ═══
        Console.WriteLine("── 数据校验：直读字节 vs 合成源 ═───────────────────────────────────────────");
        {
            var probe = new byte[4096];
            var rn = rt.BlockStore.ReadAt(off0 + 1024 * 1024, probe, 0, probe.Length);
            var ok = rn == probe.Length && SynthSource.Verify(off0 + 1024 * 1024, probe);
            var zeros = probe.Count(b => b == 0);
            Console.WriteLine($"   ReadAt(off+1MB, 4KB) -> {rn} 字节，零字节 {zeros} 个，"
                              + (ok ? "与源**逐字节一致** ✓" : "不一致 ✗"));
            Console.WriteLine("   （零字节 = 读到了未写入的洞；一致 = guest 的 pwrite 偏移映射正确）");
        }
        Console.WriteLine();

        Console.WriteLine("════════════════════════════════════════════════════════════════════════════");
        Console.WriteLine("  说明：通道 A 的首字节含 guest 每次重新武装（getLocalUrl + 新端口）的固有开销；");
        Console.WriteLine("        通道 B 完全不经 guest，故与连接数无关。引擎 P2P/直链供数速度 2.5~17 MB/s，");
        Console.WriteLine("        远低于两者上限 —— 数据面的意义是**交付平滑度**而非提升下载速度。");
        Console.WriteLine($"  VM 控制台日志：{rt.ConsoleLogPath}");
        Console.WriteLine("════════════════════════════════════════════════════════════════════════════");

        var outPath = Path.Combine(Path.GetDirectoryName(imgPath)!, "catclaw-e2e-bench.log");
        try { await File.WriteAllLinesAsync(outPath, LogLines); Console.WriteLine($"  完整日志：{outPath}"); } catch { }
        return 0;
    }

    private static long LastDone;

    /// <summary>宿主自测：<paramref name="conns"/> 条并发连接各拉 <paramref name="bytesPerConn"/> 字节，返回总吞吐 MB/s。</summary>
    private static async Task<double> HostSelfTestAsync(int port, long bytesPerConn, int conns)
    {
        var handler = new SocketsHttpHandler { MaxConnectionsPerServer = 64, PooledConnectionLifetime = Timeout.InfiniteTimeSpan };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
        var url = $"http://127.0.0.1:{port}/synth.mp4";

        var t0 = Stopwatch.GetTimestamp();
        var tasks = new Task<long>[conns];
        for (var i = 0; i < conns; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                await using var s = await resp.Content.ReadAsStreamAsync();
                var buf = new byte[256 * 1024];
                long got = 0;
                while (got < bytesPerConn)
                {
                    var n = await s.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, bytesPerConn - got)));
                    if (n <= 0) break;
                    got += n;
                }
                return got;
            });
        }
        var total = (await Task.WhenAll(tasks)).Sum();
        var el = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        return el <= 0 ? 0 : total / (double)MB / (el / 1000);
    }

    private static bool RangeAllAvailable(SparseBlockStore st, long off, long len)
    {
        const int chunk = 256 * 1024;
        for (long o = off; o < off + len; o += chunk)
            if (!st.IsRangeAvailable(o, chunk)) return false;
        return true;
    }

    /// <summary>裸 HTTP Range 读取（同 hosttest 既有口径：按 Content-Length 读满即停）。</summary>
    private static async Task<(double firstByteMs, double totalMs, long bodyBytes)> RawGetAsync(string url, long from, long count)
    {
        var u = new Uri(url);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(u.Host, u.Port);
        var ns = tcp.GetStream();
        var req = $"GET {u.PathAndQuery} HTTP/1.0\r\nHost: {u.Host}:{u.Port}\r\n"
                  + $"Range: bytes={from}-{from + count - 1}\r\n\r\n";
        var sw = Stopwatch.StartNew();
        await ns.WriteAsync(Encoding.ASCII.GetBytes(req));
        var buf = new byte[65536];
        double first = -1;
        long body = 0;
        long? cl = null;
        var head = new List<byte>();
        using var cts = new CancellationTokenSource(60_000);
        try
        {
            while (true)
            {
                var n = await ns.ReadAsync(buf, cts.Token);
                if (n <= 0) break;
                if (first < 0) first = sw.Elapsed.TotalMilliseconds;
                if (cl is null)
                {
                    for (var i = 0; i < n; i++) head.Add(buf[i]);
                    var idx = IndexOfHeaderEnd(head);
                    if (idx >= 0)
                    {
                        cl = ParseContentLength(Encoding.ASCII.GetString(head.ToArray(), 0, idx));
                        body += head.Count - (idx + 4);
                    }
                }
                else body += n;
                if (cl is { } c && body >= c) break;
            }
        }
        catch (OperationCanceledException) { }
        return (first, sw.Elapsed.TotalMilliseconds, body);
    }

    private static int IndexOfHeaderEnd(List<byte> b)
    {
        for (var i = 0; i + 3 < b.Count; i++)
            if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10) return i;
        return -1;
    }

    private static long? ParseContentLength(string head)
    {
        foreach (var l in head.Split("\r\n"))
        {
            var i = l.IndexOf(':');
            if (i > 0 && l.AsSpan(0, i).Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(l[(i + 1)..].Trim(), out var v)) return v;
        }
        return null;
    }

    // ═══════════════ 合成直链源（宿主自产，不需要外网）═══════════════

    /// <summary>
    /// 极简 HTTP 源：提供 <c>/synth.mp4</c>，支持单段 Range，内容由偏移确定性派生
    /// （<c>byte[i] = (off+i)*31+7</c> 的混合），因此宿主读完可以逐字节校验。
    /// 监听 127.0.0.1 —— QEMU 用户网络的 10.0.2.2（网关=宿主）会转发到这里。
    /// </summary>
    private sealed class SynthSource : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly long _size;
        private readonly CancellationTokenSource _cts = new();
        private readonly Action<string>? _log;
        private readonly SafeFileHandle? _backing;
        private long _connSeq;

        public int Port { get; }

        /// <param name="backingPath">非空则从该**磁盘文件**读数据（真实落盘路径）；为空则内存生成。</param>
        public SynthSource(long size, int port = 0, Action<string>? log = null, string? backingPath = null)
        {
            _size = size;
            _log = log;
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            if (!string.IsNullOrEmpty(backingPath) && File.Exists(backingPath))
            {
                _backing = File.OpenHandle(backingPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                _size = Math.Min(_size, RandomAccess.GetLength(_backing));
            }
            _ = Task.Run(AcceptLoopAsync);
        }

        /// <summary>按偏移生成内容（与 Fill 保持同一算法）。</summary>
        private static byte ByteAt(long off) => (byte)((off * 31 + 7) ^ (off >> 13));

        public static void Fill(long off, Span<byte> dest)
        {
            for (var i = 0; i < dest.Length; i++) dest[i] = ByteAt(off + i);
        }

        public static bool Verify(long off, ReadOnlySpan<byte> data)
        {
            for (var i = 0; i < data.Length; i++) if (data[i] != ByteAt(off + i)) return false;
            return true;
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch { break; }
                _ = Task.Run(() => ServeAsync(c));
            }
        }

        private async Task ServeAsync(TcpClient c)
        {
            using (c)
            {
                var id = Interlocked.Increment(ref _connSeq);
                var t0 = Stopwatch.GetTimestamp();
                var sent = 0L;
                var label = "";
                try
                {
                    var ns = c.GetStream();
                    var hb = new byte[8192];
                    var len = 0;
                    while (len < hb.Length)
                    {
                        var r = await ns.ReadAsync(hb.AsMemory(len));
                        if (r <= 0) break;
                        len += r;
                        if (IndexOfHeaderEnd(hb, len) >= 0) break;
                    }
                    if (len == 0) return;
                    var head = Encoding.ASCII.GetString(hb, 0, len);
                    label = head.Split('\r')[0].Trim();
                    _log?.Invoke($"[src] #{id} <- {label}");

                    long from = 0, to = _size - 1;
                    var hasRange = false;
                    foreach (var line in head.Split("\r\n"))
                    {
                        if (!line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) continue;
                        var spec = line[6..].Trim();
                        if (!spec.StartsWith("bytes=")) continue;
                        spec = spec[6..].Split(',')[0].Trim();
                        var dash = spec.IndexOf('-');
                        if (dash <= 0) continue;
                        _ = long.TryParse(spec[..dash], out from);
                        if (dash + 1 < spec.Length && long.TryParse(spec[(dash + 1)..], out var e2)) to = e2;
                        hasRange = true;
                    }
                    if (from < 0) from = 0;
                    if (to >= _size) to = _size - 1;
                    if (to < from) { await ns.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 416 Range Not Satisfiable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")); return; }

                    var body = to - from + 1;
                    // 无 Range → 200（标准语义，busybox wget 只认 200）；有 Range → 206。
                    var respHead = (hasRange
                            ? $"HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {from}-{to}/{_size}\r\n"
                            : "HTTP/1.1 200 OK\r\n")
                        + $"Content-Length: {body}\r\nContent-Type: video/mp4\r\n"
                        + "Accept-Ranges: bytes\r\nConnection: close\r\n\r\n";
                    await ns.WriteAsync(Encoding.ASCII.GetBytes(respHead));

                    var chunk = new byte[256 * 1024];
                    var off = from;
                    var remain = body;
                    while (remain > 0)
                    {
                        var n = (int)Math.Min(chunk.Length, remain);
                        if (_backing is not null) RandomAccess.Read(_backing, chunk.AsSpan(0, n), off);   // 真实磁盘读
                        else Fill(off, chunk.AsSpan(0, n));                                              // 内存生成
                        await ns.WriteAsync(chunk.AsMemory(0, n));
                        off += n;
                        remain -= n;
                        sent += n;
                    }
                }
                catch (Exception ex) { _log?.Invoke($"[src] #{id} 异常 {ex.GetType().Name}: {ex.Message}"); }
                finally
                {
                    var el = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    if (sent > 0 && el > 50)
                        _log?.Invoke($"[src] #{id} 结束  {sent / (double)MB:F1}MB / {el / 1000:F2}s"
                                     + $"  =  {sent / (double)MB / (el / 1000):F1} MB/s   [{label}]");
                    else
                        _log?.Invoke($"[src] #{id} 结束  sent={sent}B el={el:F0}ms   [{label}]");
                }
            }
        }

        private static int IndexOfHeaderEnd(byte[] b, int len)
        {
            for (var i = 0; i + 3 < len; i++)
                if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10) return i;
            return -1;
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            try { _backing?.Dispose(); } catch { }
            _cts.Dispose();
        }
    }
}
