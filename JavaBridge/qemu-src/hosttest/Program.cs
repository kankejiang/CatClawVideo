// 控制台测试装置：脱离 MAUI 驱动 QemuThunderEngine 走磁力全链路 / 量化取流性能。
//
// 用法:
//   dotnet run --project hosttest -- <runtimeDir> <magnet> [preferName]
//     全链路：IsReady → ListFilesAsync → TryOpenAsync → 独立 HTTP 拉 256KB 复核（退出码 0=通过）
//
//   dotnet run --project hosttest -- bench <runtimeDir> <magnet> [preferName]
//     取流基准（模拟播放器读法）：先测原始媒体口，再经「读前缓存代理」复测，同场对比。
//       B0 微请求×5   —— 纯「连接 + 重新武装」开销
//       B1/B2 16MB 读×2 —— 大块读吞吐
//       B3 1MB 分块×8  —— 播放器式小块顺序读的每次耗时
//       B4 单连接连续读 32MB —— 持续吞吐
//       B5 并行双连接  —— 第二条连接是否被串行饿死
//
//   dotnet run --project hosttest -- proxy-bench <upstreamUrl> <totalSize>
//     独立代理基准：对任意活着的媒体口 URL 起缓存代理并只跑代理侧基准。
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using CatClawVideo.Core.Services.QemuThunder;

var argList = args.ToList();
var mode = argList.Count > 0 && (argList[0] == "bench" || argList[0] == "proxy-bench" || argList[0] == "download" || argList[0] == "seektest" || argList[0] == "rangetest") ? argList[0] : "";
if (mode.Length > 0) argList.RemoveAt(0);

var sw = Stopwatch.StartNew();
void Log(string m) => Console.WriteLine($"[{sw.Elapsed.TotalSeconds,7:F1}s] {m}");

if (mode == "proxy-bench")
{
    if (argList.Count < 2)
    {
        Console.WriteLine("用法: dotnet run --project hosttest -- proxy-bench <upstreamUrl> <totalSize>");
        return 1;
    }
    var u = new Uri(argList[0]);
    using var proxy0 = new QemuStreamProxy(u.Port, u.PathAndQuery, long.Parse(argList[1]), "video/mp4", Log);
    proxy0.Start();
    await BenchAsync(proxy0.Url, "proxy");
    return 0;
}

var runtimeDir = argList.Count > 0 ? argList[0] : @"D:\Code\_scratch_tb\qemu-runtime";
if (argList.Count < 2)
{
    Console.WriteLine("用法: dotnet run --project hosttest -- [bench] <runtimeDir> <magnet> [preferName]");
    return 1;
}
var magnet = argList[1];
var prefer = argList.Count > 2 ? argList[2] : null;

Log($"运行时目录: {runtimeDir}（就绪={QemuHostRuntime.IsPresent(runtimeDir)}）");
if (!QemuHostRuntime.IsPresent(runtimeDir)) return 1;

using var engine = new QemuThunderEngine(runtimeDir, Log);
Log($"引擎 IsReady={engine.IsReady}");

// ═══ 下载模式：磁力 → 选中文件独占下载 → 媒体口导出本机 ═══
if (mode == "download")
{
    var dest = Path.Combine(Path.GetTempPath(), "hosttest-dl-test.mkv");
    try { if (File.Exists(dest)) File.Delete(dest); } catch { }
    try { if (File.Exists(dest + ".part")) File.Delete(dest + ".part"); } catch { }
    Log("══ 磁力下载模式：引擎独占下载 → 导出本机 ══");
    var lastPct = -1;
    var ok = await engine.DownloadToFileAsync(magnet, prefer,
        destPathFor: pickName => dest,
        progress: (done, total) =>
        {
            if (total <= 0) return;
            var pct = (int)(done * 100 / total);
            if (pct != lastPct && pct % 5 == 0) { lastPct = pct; Log($"  下载进度 {pct}%（{done / 1048576.0:F0}/{total / 1048576.0:F0}MB）"); }
        },
        ct: CancellationToken.None);
    if (!ok) { Log("✗ 磁力下载失败"); return 5; }
    if (File.Exists(dest + ".part")) File.Move(dest + ".part", dest);   // 引擎契约：写 .part，调用方改名
    var fi = new FileInfo(dest);
    Log($"✓ 下载完成：{dest}（{fi.Length / 1048576.0:F1}MB）");
    // 抽验文件头：MKV EBML 魔数 1A 45 DF A3
    var head = new byte[4];
    await using (var fs = File.OpenRead(dest)) await fs.ReadExactlyAsync(head);
    var magic = Convert.ToHexString(head).ToLowerInvariant();
    Log(magic == "1a45dfa3" ? "🎉 文件头校验通过（MKV）" : $"⚠ 文件头异常：{magic}");
    return magic == "1a45dfa3" ? 0 : 6;
}

// ═══ seektest 模式：验证引擎「读位置驱动的区间优先」——任务下载中持续读 50% 位置 ═══
if (mode == "seektest")
{
    Log("══ seektest：下载启动后持续读 50% 区间，观察引擎是否把分片调度跳过去 ══");
    long dTotal = 0, dDone = 0;
    var dlTask = Task.Run(() => engine.DownloadToFileAsync(magnet, prefer,
        destPathFor: _ => Path.Combine(Path.GetTempPath(), "hosttest-seektest.mkv"),
        progress: (d, t) => { dDone = d; dTotal = t; },
        ct: CancellationToken.None));
    var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(8);
    while (DateTime.UtcNow < deadline && dDone < 60 * 1024 * 1024)
    {
        Log($"  顺序下载中 {dDone / 1048576.0:F0}MB");
        await Task.Delay(5000);
    }
    if (dDone < 60 * 1024 * 1024) { Log("✗ 下载未启动或过慢"); return 7; }
    var mediaUrl = engine.LastDirectMediaUrl;
    if (string.IsNullOrEmpty(mediaUrl)) { Log("✗ 拿不到下载文件的媒体口地址"); return 8; }

    var off = (long)(dTotal * 0.5);
    Log($"  开始持续读 50% 位置（{off / 1048576.0:F0}MB），每轮 256KB；同时观察顺序进度是否跳变");
    var doneAtStart = dDone;
    for (var i = 1; i <= 40; i++)
    {
        var (fb, tt, bytes) = await RawGetAsync(mediaUrl, off, 262144);
        Log($"  读第{i,2}轮: bytes={bytes} TTFB={fb,5:F0}ms total={tt,5:F0}ms | 引擎进度 {dDone / 1048576.0:F0}MB（起点 {doneAtStart / 1048576.0:F0}MB）");
        if (bytes < 1024) { Log("  ✗ 该区间读不到字节"); break; }
        off += bytes;   // 模拟播放器连续读
        await Task.Delay(1000);
    }
    Log(dDone > doneAtStart + (long)(dTotal * 0.3)
        ? "🎉 引擎进度大幅跳变 → 读位置驱动区间优先【有效】"
        : "⚠ 引擎进度未跳变 → 引擎不支持读位置驱动的区间优先（顺序下载不受 seek 影响）");
    return 0;
}

// ═══ rangetest 模式：判定引擎对未下载区间的 Range 请求是真供数还是忽略 Range 从头供数 ═══
if (mode == "rangetest")
{
    Log("══ rangetest：下载 60MB 后，对比读 0 位置与读 50% 位置的响应（状态码/内容哈希）══");
    long dTotal = 0, dDone = 0;
    var dlTask = Task.Run(() => engine.DownloadToFileAsync(magnet, prefer,
        destPathFor: _ => Path.Combine(Path.GetTempPath(), "hosttest-rangetest.mkv"),
        progress: (d, t) => { dDone = d; dTotal = t; },
        ct: CancellationToken.None));
    var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(8);
    while (DateTime.UtcNow < deadline && dDone < 60 * 1024 * 1024)
    {
        Log($"  顺序下载中 {dDone / 1048576.0:F0}MB");
        await Task.Delay(5000);
    }
    if (dDone < 60 * 1024 * 1024) { Log("✗ 下载未启动或过慢"); return 7; }
    var mediaUrl = engine.LastDirectMediaUrl;
    if (string.IsNullOrEmpty(mediaUrl)) { Log("✗ 拿不到媒体口地址"); return 8; }

    var off50 = (long)(dTotal * 0.5);
    Log($"  引擎已下 {dDone / 1048576.0:F0}MB；请求 0 与 {off50 / 1048576.0:F0}MB 各 256KB");
    var (sA, hA, fA, bA) = await RawGetHashed(mediaUrl, 0, 262144);
    Log($"  读 0MB    : HTTP={sA} 前16字节={fA} md5={hA[..8]} bytes={bA}");
    var (sB, hB, fB, bB) = await RawGetHashed(mediaUrl, off50, 262144);
    Log($"  读 50%    : HTTP={sB} 前16字节={fB} md5={hB[..8]} bytes={bB}");
    if (sB.Contains("206") && hA != hB)
        Log("🎉 HTTP 206 且内容不同 → 引擎对未下载区间【真供数】（附加源/按需拉取）→ seek 优先可实现");
    else if (sB.Contains("206") && hA == hB)
        Log("⚠ 206 但内容相同 → 引擎忽略 Range 从 0 供数（假 206）");
    else
        Log($"⚠ 状态 {sB.Trim()} → 引擎不按 Range 供数（可能 200 从头或拒绝）");
    return 0;
}

Log("══ 阶段一：磁力 → 文件列表 ══");
var files = await engine.ListFilesAsync(magnet, prefer);
if (files is null) { Log("✗ 列文件失败"); return 2; }
Log($"✓ 文件列表 {files.Count} 项");

Log("══ 阶段二：起播 ══");
var play = await engine.TryOpenAsync(magnet, prefer);
if (play is null) { Log("✗ 起播失败"); return 3; }
Log($"✓ 起播: {play.FileName}  idx={play.FileIndex}");
Log($"   URL: {play.Url}");

if (mode != "bench")
{
    Log("══ 最终独立复核：HTTP Range 拉 256KB ══");
    var (fb, tt, bytes) = await RawGetAsync(play.Url, 0, 262144);
    Log($"字节≈{bytes}（含响应头） 首字节={fb:F0}ms 总耗时={tt:F0}ms");
    Log(bytes > 1024 ? "🎉 全链路通过" : "⚠ 字节不足，验证未通过");
    return bytes > 1024 ? 0 : 4;
}

// ═══════════ 基准模式：原始 vs 代理 ═══════════
Log("");
Log("══ 基准 A：直连媒体口（对照）══");
await BenchAsync(engine.LastDirectMediaUrl ?? play.Url, "raw");

Log("══ 基准 B：读前缓存代理 ══");
using (var proxy = new QemuStreamProxy(new Uri(play.Url).Port, new Uri(play.Url).PathAndQuery,
           play.FileLength, QemuStreamProxy.ContentTypeFor(play.FileName), Log))
{
    proxy.Start();
    Log($"  代理 URL: {proxy.Url}");
    await BenchAsync(proxy.Url, "proxy");
}
Log("基准完成");
return 0;

// ═══════════ 工具 ═══════════

// 按 Content-Length 读满即停（引擎响应完可能保持连接 30s 不关，等 EOF 会假性超时）；全程 20s 上限。
async Task<(double firstByteMs, double totalMs, long bodyBytes)> RawGetAsync(string url, long from, long count)
{
    var u = new Uri(url);
    using var tcp = new TcpClient();
    await tcp.ConnectAsync(u.Host, u.Port);
    var ns = tcp.GetStream();
    var rangeEnd = count > 0 ? from + count - 1 : from + 1024L * 1024 * 1024;
    var req = $"GET {u.PathAndQuery} HTTP/1.0\r\nHost: {u.Host}:{u.Port}\r\nRange: bytes={from}-{rangeEnd}\r\n\r\n";
    var swl = Stopwatch.StartNew();
    await ns.WriteAsync(Encoding.ASCII.GetBytes(req));
    var buf = new byte[65536];
    double first = -1;
    long body = 0;
    long? cl = null;
    var header = new List<byte>();
    using var cts = new CancellationTokenSource(20_000);
    try
    {
        while (true)
        {
            var n = await ns.ReadAsync(buf, cts.Token);
            if (n <= 0) break;
            if (first < 0) first = swl.Elapsed.TotalMilliseconds;
            if (cl is null)
            {
                for (var i = 0; i < n; i++) header.Add(buf[i]);
                var idx = IndexOfHeaderEnd(header);
                if (idx >= 0)
                {
                    cl = ParseContentLength(Encoding.ASCII.GetString(header.ToArray(), 0, idx));
                    body += header.Count - (idx + 4);
                }
            }
            else body += n;
            if (cl is { } c && body >= c) break;
        }
    }
    catch (OperationCanceledException) { }
    return (first, swl.Elapsed.TotalMilliseconds, body);
}

static int IndexOfHeaderEnd(List<byte> b)
{
    for (var i = 0; i + 3 < b.Count; i++)
        if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10) return i;
    return -1;
}

static long? ParseContentLength(string head)
{
    foreach (var l in head.Split("\r\n"))
    {
        var i = l.IndexOf(':');
        if (i > 0 && l.AsSpan(0, i).Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(l[(i + 1)..].Trim(), out var v)) return v;
    }
    return null;
}

/// <summary>带状态行与内容哈希的 Range 读取（rangetest 用）：返回 (状态行首段, body md5, body 前16字节hex, body 长度)。
/// 20s 上限；响应头后的 body 全部计入哈希（Content-Length 或到连接关闭）。</summary>
async Task<(string status, string md5, string head16, long bytes)> RawGetHashed(string url, long from, int count)
{
    var u = new Uri(url);
    using var tcp = new TcpClient();
    await tcp.ConnectAsync(u.Host, u.Port);
    var ns = tcp.GetStream();
    var req = $"GET {u.PathAndQuery} HTTP/1.0\r\nHost: {u.Host}:{u.Port}\r\nRange: bytes={from}-{from + count - 1}\r\n\r\n";
    await ns.WriteAsync(Encoding.ASCII.GetBytes(req));
    var buf = new byte[65536];
    var head = new List<byte>(4096);
    var body = new List<byte>(count);
    var headEnd = -1;
    using var cts = new CancellationTokenSource(20_000);
    while (true)
    {
        int n;
        try { n = await ns.ReadAsync(buf, cts.Token); } catch { break; }
        if (n <= 0) break;
        for (var i = 0; i < n; i++)
        {
            if (headEnd < 0)
            {
                head.Add(buf[i]);
                if (i + 3 < n && buf[i] == 13 && buf[i + 1] == 10 && buf[i + 2] == 13 && buf[i + 3] == 10) headEnd = head.Count;
            }
            else body.Add(buf[i]);
        }
        if (body.Count >= count) break;
    }
    var headText = Encoding.ASCII.GetString(head.ToArray());
    var status = headText.Split('\r')[0].Trim();
    var md5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(body.ToArray())).ToLowerInvariant();
    var first16 = Convert.ToHexString(body.Take(16).ToArray()).ToLowerInvariant();
    return (status, md5, first16, body.Count);
}

async Task BenchAsync(string url, string tag)
{
    const long MB = 1024 * 1024;

    Log($"  ── B0 微请求 ×5（纯连接开销）[{tag}] ──");
    var b0 = new List<double>();
    for (var i = 0; i < 5; i++)
    {
        var (fb, tt, _) = await RawGetAsync(url, 0, 1024);
        b0.Add(tt);
        Console.WriteLine($"    #{i + 1} 首字节={fb:F0}ms 总={tt:F0}ms");
    }
    Log($"  → B0 中位={b0.OrderBy(x => x).ElementAt(2):F0}ms");

    Log($"  ── B1/B2 16MB ×2 [{tag}] ──");
    for (var i = 0; i < 2; i++)
    {
        var (fb, tt, bytes) = await RawGetAsync(url, 0, 16 * MB);
        Log($"    #{i + 1} 首字节={fb:F0}ms 总={tt:F0}ms 读≈{bytes / (double)MB:F1}MB ≈{bytes / (double)MB / (tt / 1000):F1}MB/s");
    }

    Log($"  ── B3 1MB 分块顺序 ×8 [{tag}] ──");
    var b3 = new List<double>();
    for (var i = 0; i < 8; i++)
    {
        var (fb, tt, _) = await RawGetAsync(url, i * MB, MB);
        b3.Add(tt);
        Console.WriteLine($"    #{i + 1} @{i}MB 首字节={fb:F0}ms 总={tt:F0}ms");
    }
    Log($"  → B3 中位={b3.OrderBy(x => x).ElementAt(4):F0}ms 最大={b3.Max():F0}ms");

    Log($"  ── B4 单连接连续读 32MB [{tag}] ──");
    {
        var u = new Uri(url);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(u.Host, u.Port);
        var ns = tcp.GetStream();
        await ns.WriteAsync(Encoding.ASCII.GetBytes($"GET {u.PathAndQuery} HTTP/1.0\r\nHost: {u.Host}:{u.Port}\r\nRange: bytes=0-{64 * MB}\r\n\r\n"));
        var buf = new byte[65536];
        long total = 0;
        double first = -1;
        var swl = Stopwatch.StartNew();
        using var cts4 = new CancellationTokenSource(30_000);
        try
        {
            while (total < 32 * MB)
            {
                var n = await ns.ReadAsync(buf, cts4.Token);
                if (n <= 0) break;
                if (first < 0) first = swl.Elapsed.TotalMilliseconds;
                total += n;
            }
        }
        catch (OperationCanceledException) { }
        Log($"    读 {total / (double)MB:F1}MB 首字节={first:F0}ms 总={swl.Elapsed.TotalMilliseconds:F0}ms ≈{total / (double)MB / (swl.Elapsed.TotalMilliseconds / 1000):F1}MB/s");
    }

    Log($"  ── B5 并行双连接 [{tag}] ──");
    {
        var u = new Uri(url);
        using var tcpA = new TcpClient();
        await tcpA.ConnectAsync(u.Host, u.Port);
        var nsA = tcpA.GetStream();
        await nsA.WriteAsync(Encoding.ASCII.GetBytes($"GET {u.PathAndQuery} HTTP/1.0\r\nHost: {u.Host}:{u.Port}\r\nRange: bytes=0-{64 * MB}\r\n\r\n"));
        var bufA = new byte[65536];
        long got = 0;
        while (got < 2 * MB)
        {
            var n = await nsA.ReadAsync(bufA);
            if (n <= 0) break;
            got += n;
        }
        using var tcpB = new TcpClient();
        await tcpB.ConnectAsync(u.Host, u.Port);
        var nsB = tcpB.GetStream();
        await nsB.WriteAsync(Encoding.ASCII.GetBytes($"GET {u.PathAndQuery} HTTP/1.0\r\nHost: {u.Host}:{u.Port}\r\nRange: bytes=0-1023\r\n\r\n"));
        var swB = Stopwatch.StartNew();
        var bufB = new byte[8192];
        var nb = 0;
        using var ctsB = new CancellationTokenSource(12_000);
        try
        {
            while (true)
            {
                var n = await nsB.ReadAsync(bufB, ctsB.Token);
                if (n <= 0) break;
                nb += n;
            }
        }
        catch (OperationCanceledException) { }
        Log($"    A 持读 {got / 1024:F0}KB 时，B（1KB 请求）耗时={swB.Elapsed.TotalMilliseconds:F0}ms 收到≈{nb}字节");
        Log($"    （B 接近 B0 中位 = 并发 OK；>5000ms = 被串行阻塞）");
    }
}
