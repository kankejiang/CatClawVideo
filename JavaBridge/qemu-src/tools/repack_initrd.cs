// CatClawVideo QEMU initramfs 重打包工具（Windows 单文件，dotnet run repack_initrd.cs 直接运行）
//
// 用途：guest 里 /thunder-data 的 tmpfs 尺寸等 /init 参数需要调整时，无需 WSL/交叉编译——
//       本工具解包 pkg_initrd.xz → 修改 /init → 重建 newc cpio（mode/软链/设备逐条目保留）→ gzip 重压。
//       内核 CONFIG_RD_GZIP 支持 gzip initramfs，输出文件名改为 pkg_initrd.gz（QemuHostRuntime 引用）。
//       guest 侧 harness（ctrlloop.c 等改动后经 Linux 主机 NDK 交叉编译出的 aarch64 二进制）
//       也可经本工具直接替换进 initrd（/harness）。
//
// 用法: dotnet run repack_initrd.cs <ThunderRuntime 目录> [新 harness 二进制路径]
//   读取 <dir>\pkg_initrd.xz（或 .gz），输出 <dir>\pkg_initrd.gz（覆盖）。
//   补丁项（在 REPLACEMENTS 里维护，逐条「原串→新串」全文替换 /init）：
//     - tmpfs size=1500m → size=3500m（1500m 会在下载 ~1.57GB 时写满，任务 err=114010 死亡）
//     - 剥离 export EXTRA=1（创建时连发 4 个引擎调用被 A/B 实锤「发了就零速度」；
//       运行时救援/区间优先下载改由宿主 KICK 命令按需单发，见 ctrlloop.c 的 KICK）
//     - 数据面块设备：加载 virtio_blk + 导出 BLK_DEV=/dev/vda（见下，2026-09-21）
//
// 依赖：系统 tar.exe（Windows 10+ 自带的 libarchive bsdtar，支持 xz 解包与清单列举）。
// mode/软链目标来自 `tar -tvf` 清单（NTFS 不保留 POSIX 权限，必须从清单恢复）。
using System.Diagnostics;
using System.Text;
using System.IO.Compression;

var dir = args.Length > 0 ? args[0] : ".";
var harnessPath = args.Length > 1 ? args[1] : null;
var srcXz = Path.Combine(dir, "pkg_initrd.xz");
var srcGz = Path.Combine(dir, "pkg_initrd.gz");
var work = Path.Combine(Path.GetTempPath(), "initrd_repack_" + Path.GetRandomFileName().Replace(".", ""));
Directory.CreateDirectory(work);

// ── ① 解包（优先 xz 原件，否则沿用已有 gz）──
string src = File.Exists(srcXz) ? srcXz : srcGz;
if (!File.Exists(src)) { Console.Error.WriteLine($"找不到 {srcXz} 或 {srcGz}"); return 1; }
Console.WriteLine($"解包 {src} ...");
Run("tar", $"-xf \"{src}\" -C \"{work}\"", null);
var listingPath = Path.Combine(work, ".listing.txt");
Run("tar", $"-tvf \"{src}\"", listingPath);
if (!File.Exists(Path.Combine(work, "init"))) { Console.Error.WriteLine("解包后没有 init"); return 1; }

// ── ② 替换 /harness（可选）──
if (harnessPath is not null)
{
    var harnessDst = Path.Combine(work, "harness");
    if (!File.Exists(harnessDst)) { Console.Error.WriteLine("initrd 里没有 /harness，无法替换"); return 1; }
    File.Copy(harnessPath, harnessDst, overwrite: true);
    Console.WriteLine($"已替换 /harness ← {harnessPath}（{(new FileInfo(harnessPath)).Length / 1024.0:F0}KB）");
}

// ── ③ 补丁 /init ──
var initPath = Path.Combine(work, "init");
var init = File.ReadAllText(initPath);
(string From, string To)[] replacements =
[
    ("size=1500m", "size=3500m"),
    ("已挂 tmpfs（1500m）", "已挂 tmpfs（3500m）"),
    // 剥离历史版本注入过的 EXTRA（创建时连发 allowRes/switchRes/prefetch/requery = A/B 实锤零速度）。
    // ⚠ /init 里 LF/CRLF 混排（实测两种都有），先剥 CRLF 再剥 LF，避免留下悬空 \r。
    ("export EXTRA=1\r\n", ""),
    ("export EXTRA=1\n", ""),
    ("export EXTRA=0\r\n", ""),
    ("export EXTRA=0\n", ""),

    // 数据面块设备：把 virtio_blk 加入 insmod 列表（★ 失败不致命 —— blk_open() 会打印警告
    // 并回退纯转发）。这条天然幂等：替换过之后原串不再存在。
    ("for m in failover net_failover virtio_net; do",
     "for m in failover net_failover virtio_net virtio_blk; do"),
];
foreach (var (from, to) in replacements)
{
    if (!init.Contains(from)) { Console.WriteLine($"跳过（未找到）: {from}"); continue; }
    init = init.Replace(from, to);
    Console.WriteLine($"已替换: {from} → {to}");
}

// ── 数据面块设备：导出 BLK_DEV（harness 的 blk_open 读它；默认值本就是 /dev/vda，
//    显式导出只是让「通道开着」在日志里可见）。⚠ 必须幂等 —— 重复导出虽无害，
//    但会让「工具跑过几遍」这件事从 init 内容上无法判断。
if (!init.Contains("BLK_DEV"))
{
    const string anch = "export CTRL_PORT=\"18080\"";
    var idx = init.IndexOf(anch, StringComparison.Ordinal);
    if (idx >= 0)
    {
        var eol = init.IndexOf('\n', idx);
        if (eol >= 0)
        {
            var cr = init[eol - 1] == '\r' ? "\r\n" : "\n";
            init = init.Insert(eol + 1, $"export BLK_DEV=\"/dev/vda\"{cr}");
            Console.WriteLine("已插入: export BLK_DEV=\"/dev/vda\"");
        }
    }
    else Console.WriteLine("跳过（未找到 CTRL_PORT 锚点，无法插入 BLK_DEV）");
}
else Console.WriteLine("跳过：BLK_DEV 已存在");

File.WriteAllText(initPath, init, new UTF8Encoding(false));

// ── ④ 清单 → newc cpio → gzip ──
const int S_IFDIR = 0x4000, S_IFREG = 0x8000, S_IFLNK = 0xA000;
int ParseMode(string m)
{
    int t = m[0] switch { 'd' => S_IFDIR, 'l' => S_IFLNK, _ => S_IFREG };
    int p = 0;
    for (int i = 1; i <= 9; i++) if (m[i] != '-') p |= 1 << (9 - i);
    return t | p;
}
static byte[] Pad4(byte[] b) => b.Length % 4 == 0 ? b : [.. b, .. new byte[4 - b.Length % 4]];

byte[] Header(string name, int mode, int uid, int gid, int size)
{
    var h = new byte[110];
    Encoding.ASCII.GetBytes("070701").CopyTo(h, 0);
    long[] f = [1, mode, uid, gid, 1, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), size, 0, 0, 0, 0, name.Length + 1, 0];
    for (int i = 0; i < f.Length; i++)
        Encoding.ASCII.GetBytes(Convert.ToString(f[i], 16).PadLeft(8, '0')).CopyTo(h, 6 + i * 8);
    return h;
}
byte[] Entry(string name, int mode, int uid, int gid, byte[] data)
{
    var nameBuf = new byte[name.Length + 1]; Encoding.UTF8.GetBytes(name).CopyTo(nameBuf, 0);
    return [.. Pad4([.. Header(name, mode, uid, gid, data.Length), .. nameBuf]), .. Pad4(data)];
}

var entries = new List<(string Name, int Mode, int Uid, int Gid, bool IsSymlink, string? Target)>();
foreach (var line in File.ReadAllLines(listingPath))
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    var tokens = line.Split((char[]?)null, 9, StringSplitOptions.RemoveEmptyEntries);
    if (tokens.Length < 9) continue;
    var modeStr = tokens[0];
    var name = string.Join(' ', tokens, 8, tokens.Length - 8);
    string? target = null;
    if (modeStr[0] == 'l')
    {
        int i = name.IndexOf(" -> ", StringComparison.Ordinal);
        if (i < 0) continue;
        target = name[(i + 4)..]; name = name[..i];
    }
    entries.Add((name, ParseMode(modeStr), int.Parse(tokens[2]), int.Parse(tokens[3]), modeStr[0] == 'l', target));
}
Console.WriteLine($"清单条目: {entries.Count}");

using var ms = new MemoryStream();
foreach (var e in entries)
{
    byte[] data;
    if (e.IsSymlink) data = Encoding.UTF8.GetBytes(e.Target!);
    else if ((e.Mode & 0xF000) == S_IFDIR) data = [];
    else data = File.ReadAllBytes(Path.Combine(work, e.Name));
    ms.Write(Entry(e.Name, e.Mode, e.Uid, e.Gid, data));
}
{
    var nameBuf = new byte[11]; Encoding.ASCII.GetBytes("TRAILER!!!").CopyTo(nameBuf, 0);
    ms.Write(Pad4([.. Header("TRAILER!!!", 0, 0, 0, 0), .. nameBuf]));
    ms.Write(new byte[512]);
}
ms.Position = 0;
var outPath = Path.Combine(dir, "pkg_initrd.gz");
using (var gz = new GZipStream(File.Create(outPath), CompressionLevel.Optimal))
    ms.CopyTo(gz);
Console.WriteLine($"cpio={ms.Length / 1048576.0:F1}MB → {outPath}（{(new FileInfo(outPath)).Length / 1048576.0:F1}MB）");
Directory.Delete(work, recursive: true);
return 0;

void Run(string exe, string args, string? stdoutPath)
{
    var psi = new ProcessStartInfo(exe, args)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
    };
    var p = Process.Start(psi)!;
    var outText = p.StandardOutput.ReadToEnd();
    var err = p.StandardError.ReadToEnd();
    if (!p.WaitForExit(300_000)) { Console.Error.WriteLine("tar 超时"); return; }
    if (stdoutPath != null) File.WriteAllText(stdoutPath, outText, new UTF8Encoding(false));
    if (err.Length > 0) Console.Error.WriteLine(err);
}
