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
var mode = argList.Count > 0 && (argList[0] == "bench" || argList[0] == "proxy-bench" || argList[0] == "download") ? argList[0] : "";
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
