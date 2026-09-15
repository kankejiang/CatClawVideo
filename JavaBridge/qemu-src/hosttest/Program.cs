// 控制台测试装置：脱离 MAUI 驱动 QemuThunderEngine 走一遍磁力全链路
// （= 实验装置 ctrlserver2.py 的 C# 对照跑，用于验证宿主侧重写无回归）。
//
// 用法:
//   dotnet run --project hosttest -- <runtimeDir> <magnet> [preferName]
//
// 步骤: IsReady → ListFilesAsync（阶段一）→ TryOpenAsync（选片+DL+播放地址+拉流验证）
//       → 最终独立 HTTP 拉 256KB 复核 → 退出码 0=通过。
using System.Diagnostics;
using CatClawVideo.Core.Services.QemuThunder;

var runtimeDir = args.Length > 0 ? args[0] : @"D:\Code\_scratch_tb\qemu-runtime";
if (args.Length < 2)
{
    Console.WriteLine("用法: dotnet run --project hosttest -- <runtimeDir> <magnet> [preferName]");
    return 1;
}
var magnet = args[1];
var prefer = args.Length > 2 ? args[2] : null;

var sw = Stopwatch.StartNew();
void Log(string m) => Console.WriteLine($"[{sw.Elapsed.TotalSeconds,7:F1}s] {m}");

Log($"运行时目录: {runtimeDir}");
Log($"运行时就绪: {QemuHostRuntime.IsPresent(runtimeDir)}");
if (!QemuHostRuntime.IsPresent(runtimeDir)) return 1;

using var engine = new QemuThunderEngine(runtimeDir, Log);
Log($"引擎 IsReady={engine.IsReady}");

Log("══ ListFilesAsync（阶段一：磁力 → .torrent → 文件列表）══");
var files = await engine.ListFilesAsync(magnet, prefer);
if (files is null) { Log("✗ 列文件失败"); return 2; }
Log($"✓ 文件列表 {files.Count} 项:");
foreach (var f in files.Take(30)) Log($"    #{f.Index,3}  {f.Size / 1048576.0,8:F1}MB  {f.Name}");

Log("══ TryOpenAsync（阶段二：选片 → DL → 播放地址 → 拉流验证）══");
var play = await engine.TryOpenAsync(magnet, prefer);
if (play is null) { Log("✗ 起播失败"); return 3; }
Log($"✓ 起播: {play.FileName}  {play.FileLength / 1048576.0:F1}MB  idx={play.FileIndex}  hash={play.InfoHashHex}");
Log($"   URL: {play.Url}");

Log("══ 最终独立复核：HTTP Range 拉 256KB ══");
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
using var req = new HttpRequestMessage(HttpMethod.Get, play.Url);
req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 262143);
using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
using var st = await resp.Content.ReadAsStreamAsync();
var buf = new byte[262144];
var off = 0;
int n;
while (off < buf.Length && (n = await st.ReadAsync(buf.AsMemory(off))) > 0) off += n;
var head = Convert.ToHexString(buf.AsSpan(0, Math.Min(16, off))).ToLowerInvariant();
Log($"HTTP={(int)resp.StatusCode} 字节={off} 头={head}");
Log(off > 1024 ? "🎉 全链路通过（C# 宿主编排 ≡ 实验装置）" : "⚠ 字节不足，验证未通过");
return off > 1024 ? 0 : 4;
