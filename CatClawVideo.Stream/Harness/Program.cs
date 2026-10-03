using System.Diagnostics;
using CatClawVideo.Streaming;

// N4 台架：拿 CatClawVideo.Stream（= 远程画面页用的同一个库）连真实 T3 服务端，
// 按 UI 的"最新帧优先"策略做 30Hz 显示泵，逐 5 秒吐 CSV 供验收曲线与 jpeg/h264 对照表使用。
//
//   dotnet run --project CatClawVideo.Stream/Harness -c Release -- `
//       --host 10.0.0.108 --ports 27383 --seconds 1800 --csv .zwork/n4-soak.csv
//
// --ports 按"优先顺序"给（自动优先 h264 = 把 h264 的端口写在前面）；
// --input-every 每 N 秒发一次点击 + 滚轮，作为"输入真的进了 Android"的一侧证据。

var host = "10.0.0.108";
var ports = new List<int> { 27383 };
var seconds = 30;
var csv = "";
var inputEvery = 0;

for (var i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--host": host = args[++i]; break;
        case "--ports":
            ports = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
            break;
        case "--seconds": seconds = int.Parse(args[++i]); break;
        case "--csv": csv = args[++i]; break;
        case "--input-every": inputEvery = int.Parse(args[++i]); break;
    }
}

var client = new StreamClient();
long received = 0, badPayload = 0, shown = 0, replaced = 0;
long pendingTick = -1;                    // 待显示帧的到达时刻（-1 = 没有）
double latSum = 0, latMax = 0;
var latencies = new List<double>();
var codec = "";

client.FrameArrived += (_, e) =>
{
    if (e.Type != Protocol.TypeVideo) return;
    Interlocked.Increment(ref received);
    // 自证"收到的确实是视频负载"：jpeg 以 FFD8 开头，h264 以 00 00 00 01 / 00 00 01 开头
    var p = e.Payload;
    var ok = codec == "jpeg"
        ? p.Length > 3 && p[0] == 0xFF && p[1] == 0xD8
        : p.Length > 4 && p[0] == 0 && p[1] == 0 && (p[2] == 1 || (p[2] == 0 && p[3] == 1));
    if (!ok) Interlocked.Increment(ref badPayload);
    if (Interlocked.Exchange(ref pendingTick, e.ReceivedTickMs) >= 0) Interlocked.Increment(ref replaced);
};

using var cts = new CancellationTokenSource();
cts.CancelAfter(TimeSpan.FromSeconds(seconds));

try
{
    var info = await client.ConnectPreferredAsync(host, ports, cts.Token);
    codec = info.Codec;
    Console.WriteLine($"已连 {host}:{client.Current.Port} codec={info.Codec} {info.Width}x{info.Height}@{info.Fps} " +
                      $"本机可解码={CodecSupport.CanDecode(info.Codec)}");
}
catch (Exception ex)
{
    Console.WriteLine("连接失败：" + ex.Message);
    return 2;
}

var run = client.RunAsync(cts.Token);
var pump = Task.Run(async () =>
{
    // 显示泵：30Hz 取走最新帧（模拟渲染层 vsync），量"收流→上屏"的管线延迟
    while (!cts.Token.IsCancellationRequested)
    {
        var t = Interlocked.Exchange(ref pendingTick, -1);
        if (t >= 0)
        {
            var lat = Environment.TickCount64 - t;
            Interlocked.Increment(ref shown);
            latSum += lat;
            if (lat > latMax) latMax = lat;
            lock (latencies) if (latencies.Count < 40000) latencies.Add(lat);
        }
        await Task.Delay(33);
    }
});

var sw = Stopwatch.StartNew();
var lastRowSec = -1;
var lastInputSec = -1;
Console.WriteLine("t_s,phase,fps_recv,frames,bytes,mbps,interval_ms,maxgap_ms,reconnects,shown,dropped,lat_avg_ms,lat_max_ms,privMB,badPayload");
using (var w = csv.Length == 0 ? null : new StreamWriter(csv, false))
{
    w?.WriteLine("t_s,phase,fps_recv,frames,bytes,mbps,interval_ms,maxgap_ms,reconnects,shown,dropped,lat_avg_ms,lat_max_ms,privMB,badPayload");
    while (!cts.Token.IsCancellationRequested)
    {
        await Task.Delay(500);
        var t = (int)sw.Elapsed.TotalSeconds;
        var snap = client.Current;

        if (inputEvery > 0 && t - lastInputSec >= inputEvery)
        {
            lastInputSec = t;
            var cx = Math.Max(1, snap.ServerWidth / 2);
            var cy = Math.Max(1, snap.ServerHeight / 2);
            await client.SendTouchAsync(cx, cy, Protocol.TouchDown);
            await client.SendTouchAsync(cx, cy, Protocol.TouchMove);
            await client.SendTouchAsync(cx, cy, Protocol.TouchUp);
            await client.SendWheelAsync(0, -300);
            Console.WriteLine($"[{t}s] 已发 tap({cx},{cy}) down/move/up + wheel(0,-300)");
        }

        if (t % 5 != 0 || t == lastRowSec) continue;
        lastRowSec = t;
        var priv = Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0;
        var mbps = snap.BytesReceived * 8.0 / Math.Max(1, t) / 1e6;
        var row = $"{t},{snap.Phase},{snap.ReceivedFps:F1},{snap.FramesReceived}," +
                  $"{snap.BytesReceived},{mbps:F2},{snap.MeanFrameIntervalMs:F0},{snap.MaxFrameGapMs:F0}," +
                  $"{snap.ReconnectCount},{Interlocked.Read(ref shown)},{Interlocked.Read(ref replaced)}," +
                  $"{(shown == 0 ? 0 : latSum / shown):F1},{latMax:F0},{priv:F1},{Interlocked.Read(ref badPayload)}";
        w?.WriteLine(row);
        w?.Flush();
        Console.WriteLine(row);
    }
}

await client.StopAsync();
await run;
await pump;

double p95;
lock (latencies) p95 = latencies.Count == 0 ? 0 : latencies.OrderBy(x => x).ElementAt((int)(latencies.Count * 0.95));
Console.WriteLine($"结束：视频帧={Interlocked.Read(ref received)} 上屏={Interlocked.Read(ref shown)} " +
                  $"被覆盖(丢帧)={Interlocked.Read(ref replaced)} 重连={client.Current.ReconnectCount} " +
                  $"延迟 avg={(shown == 0 ? 0 : latSum / shown):F1}ms p95={p95:F1}ms max={latMax:F0}ms " +
                  $"畸形负载={Interlocked.Read(ref badPayload)} 末次错误={client.Current.LastError ?? "无"}");
return 0;
