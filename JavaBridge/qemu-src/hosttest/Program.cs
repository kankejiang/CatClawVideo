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
//
//   dotnet run --project hosttest -- playtest <runtimeDir> <magnet> [preferName] [durationSec]
//     真实观看模拟（2026-09-17 用户要求）：开播首字节耗时 → 播放 2 分钟 → seek 15 分钟
//     → 再播 2 分钟 → seek 片尾取样，逐段输出耗时/吞吐/最差首字节，用于定位起播与 seek 优化点。
//
//   dotnet run --project hosttest -- guard <runtimeDir> <rawJar> [k=v ...]
//     Guard VM 台架（2026-09-24 网盘 proxyInvoke 排障）：启动 Guard VM → GLOAD 该 raw jar 的
//     guard so → 直发一行 PROXY（默认 do=config）→ 打印 guest 内 so 的返回与上行的 UI 事件。
//     脱离 MAUI/JVM 桥复现「so 弹窗/扫码为何到不了宿主」。
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CatClawVideo.Core.Services.QemuThunder;

var argList = args.ToList();
var mode = argList.Count > 0 && (argList[0] == "bench" || argList[0] == "proxy-bench" || argList[0] == "download" || argList[0] == "seektest" || argList[0] == "rangetest" || argList[0] == "playtest" || argList[0] == "xfer" || argList[0] == "xfer-e2e" || argList[0] == "guard" || argList[0] == "art") ? argList[0] : "";
if (mode.Length > 0) argList.RemoveAt(0);

var sw = Stopwatch.StartNew();
void Log(string m) => Console.WriteLine($"[{sw.Elapsed.TotalSeconds,7:F1}s] {m}");

// ═══ art 模式：ART guest（QEMU 里真 Android ART 跑桥）的宿主侧台架 ═══
// 用法: dotnet run --project hosttest -- art <runtimeDir> <bridgeDir> <jarUrl> [api=csp_MyDriveGuard]
//   走**生产代码**（JavaSpiderRuntime.HomeContentAsync/SearchContentAsync），不复制协议逻辑：
//   QemuArtGuest 起 VM → ArtJarServer 供 jar → 桥在 guest 里由真 ART 吃壳 jar、
//   壳自己 System.load() arm64 ftyguard so 解出真 dex。逐段打延迟，用于 V3 取舍判据。
if (mode == "art") return await ArtBenchAsync(argList);

async Task<int> ArtBenchAsync(List<string> a)
{
    if (a.Count < 3) { Console.WriteLine("参数不足：<runtimeDir> <bridgeDir> <jarUrl> [api]"); return 2; }
    var rtDir = a[0];
    var bDir = a[1];
    var jar = a[2];
    var apiName = a.Count > 3 ? a[3] : "csp_MyDriveGuard";
    var cls = apiName.StartsWith("csp_") ? apiName[4..] : apiName;

    var rt = new CatClawVideo.Core.Providers.JavaSpiderRuntime(bDir, javaExe: "java", log: Log,
        workDir: Path.Combine(Path.GetTempPath(), "catclaw-artbench"));
    rt.ArtRuntimeDir = rtDir;
    rt.ArtGuestMode = true;
    // 构造期那行「桥链路」是按 AppContext.BaseDirectory 判的，台架是覆盖属性后才生效的，
    // 所以这里补一行真实链路，否则日志会把人往「宿主 JRE」的方向带（2026-09-26 踩过）。
    Log("实际桥链路：ART guest（台架覆盖 ArtRuntimeDir）");
    var site = new CatClawVideo.Core.Models.VodSiteInfo
    {
        Key = "artbench", Name = "ART 台架", Api = apiName, Jar = jar, Ext = "", Type = 1,
    };
    Log($"ART guest 台架：runtime={rtDir} 桥目录={bDir} jar={jar} 类={cls}");

    var t1 = Stopwatch.StartNew();
    var home = await rt.HomeContentAsync(site);
    Log($"homeContent（含起 VM + 装载）{t1.ElapsedMilliseconds}ms → {home.Length}B: {home[..Math.Min(200, home.Length)]}");

    var t2 = Stopwatch.StartNew();
    var search = await rt.SearchContentAsync(site, "庆余年", "1");
    Log($"searchContent {t2.ElapsedMilliseconds}ms → {search[..Math.Min(200, search.Length)]}");

    // jar 的原生对话框能不能上行：网盘系（登入自己网盘/排序/推送Cookie）全靠这条。
    // guest 里 android.app.AlertDialog 是框架真类，桥的捕获层要靠 boot classpath 前置才抢得回来，
    // 这条探针就是量它有没有生效（2026-09-26 应用内实测：action 到了桥，ui-dialog 上行 0 次）。
    rt.UiEvent = ev =>
    {
        var name = ev["ev"]?.GetValue<string>() ?? "?";
        var items = (ev["items"] as JsonArray)?.Count ?? 0;
        Log($"★ 上行 UI 事件 {name} seq={ev["seq"]?.ToJsonString()} 标题={ev["title"]?.GetValue<string>() ?? ""} " +
            $"条目={items} 二维码={(ev["qr"] is not null ? "有" : "无")}");
    };
    var t8 = Stopwatch.StartNew();
    try
    {
        var act = await rt.ActionAsync(site, "loginShow");
        Log($"action(loginShow) {t8.ElapsedMilliseconds}ms → {act[..Math.Min(120, act.Length)]}");
    }
    catch (Exception ex)
    {
        var m = ex.Message.Length > 90 ? ex.Message[..90] : ex.Message;
        Log($"action(loginShow) {t8.ElapsedMilliseconds}ms → {ex.GetType().Name}: {m}");
    }

    // 全链路才算 P4 验收：home 只证明壳能解密，分类/详情/播放才证明 jar 的 proxy 自回调
    // 与解析链在 guest 里真的跑起来了（2026-09-26：detail 空列表曾坑在 rig 传参口径上）。
    string Grab(string re, string src) => System.Text.RegularExpressions.Regex.Match(src, re).Groups[1].Value;
    var tid = Grab("\"type_id\"\\s*:\\s*\"([^\"]+)\"", home);
    if (tid.Length == 0) tid = "1";
    var t4 = Stopwatch.StartNew();
    var cat = await rt.CategoryContentAsync(site, tid, "1");
    var vid = Grab("\"vod_id\"\\s*:\\s*\"([^\"]+)\"", cat);
    Log($"categoryContent {t4.ElapsedMilliseconds}ms → {cat.Length}B tid={tid} vid={vid}");
    if (vid.Length == 0) { rt.Shutdown(); return 1; }
    var t5 = Stopwatch.StartNew();
    var det = await rt.DetailContentAsync(site, vid);
    var from = Grab("\"vod_play_from\"\\s*:\\s*\"([^\"]+)\"", det).Split("$$$")[0];
    var eps = Grab("\"vod_play_url\"\\s*:\\s*\"([^\"]+)\"", det).Split("$$$")[0].Split('#');
    // 第二参是剧集串（vod_play_url 里 $ 后那段），与 SpiderVodProvider:211 的 episode.Url 同口径；
    // 传 vod_id 时 jar 认不出地址、直接把入参回显成 url。
    var play = eps.Length > 0 ? eps[0].Split('$').Last() : vid;
    Log($"detailContent {t5.ElapsedMilliseconds}ms → {det.Length}B 线路={from} 集={play[..Math.Min(80, play.Length)]}");
    var t6 = Stopwatch.StartNew();
    var got = await rt.PlayerContentAsync(site, from, play);
    Log($"playerContent {t6.ElapsedMilliseconds}ms → {got[..Math.Min(300, got.Length)]}");

    // 爬虫自有的 do（danmu/ck/config…）必须由 guest 里的壳自己应答：宿主经 slirp 隧道取字节。
    var t7 = Stopwatch.StartNew();
    var via = await rt.ProxyAsync(new Dictionary<string, string> { ["do"] = "ck", ["siteKey"] = site.Key }, ct: default);
    Log($"proxy 隧道 do=ck {t7.ElapsedMilliseconds}ms → " +
        (via is null ? "null（没人接）" : $"{via.Value.Status} {via.Value.Mime} {via.Value.Body?.Length ?? 0}B " +
        $"{System.Text.Encoding.UTF8.GetString(via.Value.Body ?? Array.Empty<byte>())[..Math.Min(120, via.Value.Body?.Length ?? 0)]}"));

    var t3 = Stopwatch.StartNew();
    var prefs = await rt.GetPrefsAsync();
    Log($"getPrefs {t3.ElapsedMilliseconds}ms → {prefs?.ToJsonString() ?? "null"}");

    rt.Shutdown();
    Log("已收尾（VM 应随 Stop 退出）");
    return 0;
}


// ═══ xfer 模式：数据面基准（宿主侧文件通道，不需要 QEMU）═══
// 量化「guest 写入 → 宿主直读」稀疏块设备通道的速度 / 延迟 / 并发扩展。
// 用法: dotnet run -c Release --project hosttest -- xfer [imagePath] [capacityMB] [fillMB]
if (mode == "xfer")
{
    var img = argList.Count > 0 && argList[0].Length > 0
        ? argList[0]
        : Path.Combine(Path.GetTempPath(), "catclaw-xfer-bench.img");
    var capMb = argList.Count > 1 && int.TryParse(argList[1], out var c0) ? c0 : 2048;
    var fillMb = argList.Count > 2 && int.TryParse(argList[2], out var f0) ? f0 : 512;
    return BenchXfer.RunDisk(img, capMb, fillMb);
}

// ═══ xfer-e2e 模式：端到端数据面对照（真实 QEMU）═══
// 用宿主自产合成直链喂 guest 引擎，对同段已落盘数据做「块设备直读 vs HTTP 经 SLIRP」同场对照。
// 用法: dotnet run -c Release --project hosttest -- xfer-e2e <runtimeDir> [fileMB] [waitMB] [mediaPort]
if (mode == "xfer-e2e")
{
    var rtDir = argList.Count > 0 ? argList[0] : @"D:\Code\_scratch_tb\qemu-runtime";
    var fMb = argList.Count > 1 && long.TryParse(argList[1], out var f1) ? f1 : 512;
    var wMb = argList.Count > 2 && long.TryParse(argList[2], out var w1) ? w1 : 128;
    var mPort = argList.Count > 3 && int.TryParse(argList[3], out var m1) ? m1 : 20092;
    // 第 5 参（可选）：**外部直链 URL** —— 让引擎去下真实大文件（如系统镜像 ISO），
    // 用来验证「下载超过旧 tmpfs 1.5G 上限的大文件」这类场景。
    var extUrl = argList.Count > 4 ? argList[4] : null;
    return await BenchE2E.RunAsync(rtDir, fMb, wMb, mPort, extUrl);
}

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

// ═══ guard 模式：Guard VM 台架（脱离 MAUI/JVM 桥直调 guest 内 so）═══
// 用法: dotnet run -c Release --project hosttest -- guard <runtimeDir> <rawJar> [k=v ...]
if (mode == "guard") return await RunGuardAsync();

async Task<int> RunGuardAsync()
{
    if (argList.Count < 2)
    {
        Console.WriteLine("用法: dotnet run -c Release --project hosttest -- guard <runtimeDir> <rawJar> [k=v ...]");
        return 1;
    }
    var rtDir = argList[0];
    var rawJar = argList[1];
    if (!File.Exists(rawJar)) { Log($"✗ raw jar 不存在: {rawJar}"); return 1; }

    // 与 JavaSpiderRuntime 转换管线一致：hash = jar 标识的 SHA256 前 24 位（这里用路径，台架自洽即可）
    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawJar)))[..24].ToLowerInvariant();
    Log($"runtimeDir={rtDir}  jar={Path.GetFileName(rawJar)}  hash={hash}");

    using var gengine = new QemuGuardEngine(rtDir, Log);
    gengine.UiEvent += ev => Log($"  ⬆ UI 事件（so 弹出，宿主待渲染）: {ev.ToJsonString()}");
    gengine.RegisterJar(hash, rawJar);

    var tLoad = sw.Elapsed.TotalSeconds;
    if (!await gengine.EnsureLoadedAsync(hash)) { Log("✗ GLOAD 失败（so 未就绪）"); return 20; }
    Log($"✓ guard so 就绪（{sw.Elapsed.TotalSeconds - tLoad:F1}s）");

    // 组 PROXY 行：PROXY <np>（台架不带 prefs 快照）<nm> k v ...（全 b64，空值以 "." 占位）
    var map = new List<(string K, string V)>();
    for (var i = 2; i < argList.Count; i++)
    {
        var kv = argList[i].Split('=', 2);
        map.Add((kv[0], kv.Length > 1 ? kv[1] : ""));
    }
    if (map.Count == 0) map.Add(("do", "config"));
    if (map.All(p => p.K != "url")) map.Add(("url", "0000"));

    static string B64(string s) => string.IsNullOrEmpty(s) ? "."
        : Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
    var req = new StringBuilder("PROXY 0");
    req.Append(' ').Append(map.Count);
    foreach (var (k, v) in map) req.Append(' ').Append(B64(k)).Append(' ').Append(B64(v));
    Log($"→ {req}");

    using var tcp = new TcpClient();
    await tcp.ConnectAsync("127.0.0.1", QemuGuardEngine.GuardPort);
    var ns = tcp.GetStream();
    await ns.WriteAsync(Encoding.UTF8.GetBytes(req + "\n"));

    // 行式响应（body 的 b64 可达数百 KB）：读到换行或连接关闭，120s 上限
    var resp = new StringBuilder();
    using var ctsG = new CancellationTokenSource(120_000);
    var one = new byte[1];
    try
    {
        while (true)
        {
            var n = await ns.ReadAsync(one, ctsG.Token);
            if (n <= 0) break;
            if (one[0] == (byte)'\n') break;
            if (one[0] != (byte)'\r') resp.Append((char)one[0]);
        }
    }
    catch (OperationCanceledException) { Log("✗ 响应超时"); }
    var text = resp.ToString();
    Log($"← 响应 {text.Length} 字节: {text[..Math.Min(text.Length, 160)]}{(text.Length > 160 ? "…" : "")}");

    if (text.StartsWith("OK3"))
    {
        var parts = text.Split(' ');
        Log($"  status={parts[1]} mime={B64Dec(parts[2])} body={(parts[3] == "-" ? "无（可能仅弹窗）" : B64Dec(parts[3]).Length + " 字符")}");
        if (parts[3] != "-")
        {
            var body = B64Dec(parts[3]);
            var isHtml = body.Contains("<html", StringComparison.OrdinalIgnoreCase) || body.Contains("Cookie", StringComparison.Ordinal);
            Log(isHtml
                ? "  ⚠ so 返回的是 HTML 配置页（贴 Cookie），不是原生对话框 → 弹窗分支仍未走到"
                : "  ✓ so 返回非 HTML 内容");
            var snip = Path.Combine(Path.GetTempPath(), "guard-resp-body.html");
            File.WriteAllText(snip, body);
            Log($"  响应体已存: {snip}");
        }
        return 0;
    }
    Log("✗ so 未返回 OK3 —— 这就是宿主回落到 Pan.proxyInput(HTML) 的原因");
    return 21;
}

static string B64Dec(string s) => s == "." ? "" : Encoding.UTF8.GetString(Convert.FromBase64String(s));
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
    var offTail = Math.Max(0, dTotal - 300 * 1024);   // 尾部 300KB 处（覆盖 Cues 位置）
    Log($"  引擎已下 {dDone / 1048576.0:F0}MB；请求 0 / {off50 / 1048576.0:F0}MB / 尾部{offTail / 1048576.0:F0}MB 各 256KB");
    var (sA, hA, fA, bA) = await RawGetHashed(mediaUrl, 0, 262144);
    Log($"  读 0MB    : HTTP={sA} 前16字节={fA} md5={hA[..8]} bytes={bA}");
    var (sB, hB, fB, bB) = await RawGetHashed(mediaUrl, off50, 262144);
    Log($"  读 50%    : HTTP={sB} 前16字节={fB} md5={hB[..8]} bytes={bB}");
    var (sC, hC, fC, bC) = await RawGetHashed(mediaUrl, offTail, 262144);
    Log($"  读 尾部   : HTTP={sC} 前16字节={fC} md5={hC[..8]} bytes={bC}");
    if (sC.Contains("206") && bC > 100 * 1024)
        Log("🎉 尾部按需供数【可用】→ Cues 可获取 → 真 seek 可实现");
    else
        Log("⚠ 尾部按需供数不可用（最后 piece 跨界或无源）→ seek 仍受限于顺序下载");
    return 0;
}

// playtest 必须**在默认全链路之前**分派：下面那段「阶段一/阶段二」对非 bench 模式会直接
// return，放在它之后的分支永远不可达（2026-09-17 踩过）。
if (mode == "playtest") return await RunPlaytestAsync();

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

// ═══ playtest：真实观看模拟（开播 / 播放 / seek / 片尾）═══
async Task<int> RunPlaytestAsync()
{
    var durSec = argList.Count > 3 && double.TryParse(argList[3], out var d0) ? d0 : 2100.0;   // 默认 35 分钟片长
    Log($"══ playtest：模拟真实观看（片长按 {durSec / 60:F0} 分钟估算码率）══");

    var tReady = sw.Elapsed.TotalSeconds;
    var ready = await engine.EnsureReadyAsync();
    Log($"[1/6] 引擎就绪={ready}（{sw.Elapsed.TotalSeconds - tReady:F1}s）");
    if (!ready) return 10;

    var tOpen = sw.Elapsed.TotalSeconds;
    var opened = await engine.TryOpenAsync(magnet, prefer);
    var openCost = sw.Elapsed.TotalSeconds - tOpen;
    if (opened is null) { Log("✗ TryOpenAsync 失败"); return 11; }
    Log($"[2/6] TryOpenAsync 耗时 {openCost:F1}s → {opened.FileName}"
        + $"（{opened.FileLength / 1048576.0:F0}MB，索引 {opened.FileIndex}）");

    // 与 App 一致：播放经「读前缓存代理」
    using var proxy = new QemuStreamProxy(new Uri(opened.Url).Port, new Uri(opened.Url).PathAndQuery,
        opened.FileLength, QemuStreamProxy.ContentTypeFor(opened.FileName), Log);
    proxy.Start();

    var bytesPerSec = opened.FileLength / durSec;

    // 顺序读「sec 秒内容」的字节量，按 4MB 分块（与播放器/缓存分块一致）
    async Task<double> PlayAsync(long startOffset, double sec, string tag)
    {
        var need = (long)(bytesPerSec * sec);
        var off = startOffset;
        long got = 0;
        double worst = 0;
        var t0 = sw.Elapsed.TotalSeconds;
        while (got < need)
        {
            var chunk = Math.Min(4L * 1024 * 1024, need - got);
            var (f, _, b) = await RawGetAsync(proxy.Url, off, chunk);
            if (b < 1024) break;
            worst = Math.Max(worst, f);
            got += b;
            off += b;
        }
        var el = Math.Max(sw.Elapsed.TotalSeconds - t0, 0.001);
        Log($"      [{tag}] 读 {got / 1048576.0:F1}MB（≈{sec:F0}s 内容）耗时 {el:F1}s"
            + $" | 最差首字节 {worst:F0}ms | 均值 {got * 8 / 1e6 / el:F1} Mbps");
        return got;
    }

    var tFirst = sw.Elapsed.TotalSeconds;
    var (fb, _, nFirst) = await RawGetAsync(proxy.Url, 0, 262144);
    Log($"[3/6] ★开播首字节 {fb:F0}ms（含代理起流；256B 校验 bytes={nFirst}）"
        + $" —— 点击到出画的主体耗时 = TryOpen {openCost:F1}s + 首字节 {fb / 1000:F2}s");
    if (nFirst < 1024) { Log("✗ 首字节失败"); return 12; }

    await PlayAsync(262144, 120, "开头播放 2 分钟");

    var off15 = (long)(opened.FileLength * (15 * 60 / durSec));
    var tSeek = sw.Elapsed.TotalSeconds;
    var (f15, _, b15) = await RawGetAsync(proxy.Url, off15, 262144);
    Log($"[4/6] seek 15 分钟 → 偏移 {off15 / 1048576.0:F0}MB | 首字节 {f15:F0}ms"
        + $" | bytes={b15}（往返 {sw.Elapsed.TotalSeconds - tSeek:F1}s）");
    if (b15 < 1024) { Log("✗ seek 后无数据"); return 13; }

    await PlayAsync(off15 + b15, 120, "15 分钟后播放 2 分钟");

    var offTail = Math.Max(0, opened.FileLength - 4L * 1024 * 1024) + 262144;
    var tTail = sw.Elapsed.TotalSeconds;
    var (ft, _, bt) = await RawGetAsync(proxy.Url, offTail, 262144);
    Log($"[5/6] seek 片尾 → 偏移 {offTail / 1048576.0:F0}MB / {opened.FileLength / 1048576.0:F0}MB"
        + $" | 首字节 {ft:F0}ms | bytes={bt}（往返 {sw.Elapsed.TotalSeconds - tTail:F1}s）");
    Log(bt >= 1024
        ? "[6/6] ✓ 片尾可读 → 尾部按需供数正常（不会出现播到结尾卡死）"
        : "[6/6] ✗ 片尾读不到字节 → 播到结尾会卡住");
    return bt >= 1024 ? 0 : 14;
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
