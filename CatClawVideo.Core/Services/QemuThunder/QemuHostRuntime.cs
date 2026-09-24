using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace CatClawVideo.Core.Services.QemuThunder;

/// <summary>
/// QEMU 运行时进程管理（Windows）：把 ARM64 Android 迅雷下载引擎跑在 QEMU 里。
///
/// <para><b>运行时目录</b>（扁平布局，随应用打包到 <c>ThunderRuntime/</c>）：
/// <c>qemu-system-aarch64.exe</c> + 全部 DLL + <c>share/</c> + <c>pkg_kernel</c> + <c>pkg_initrd.xz</c>。
/// initrd 里烧死了控制口 18080 / 代理口 20080（guest → 10.0.2.2 回连宿主），媒体口由宿主
/// 经 hostfwd 映射（可用 <see cref="MediaPort"/> 变化，不能占用）。</para>
///
/// <para>子进程被放入 KillOnClose 的 Job Object：宿主进程以任何方式退出（含崩溃）时
/// QEMU 都会被系统连带杀掉，不会留下 4GB 的僵尸 VM。</para>
/// </summary>
public sealed class QemuHostRuntime : IDisposable
{
    public string RuntimeDir { get; }
    public int MediaPort { get; }

    /// <summary>QEMU monitor 端口（0 = 未启用）。宿主用它 <c>stop</c>/<c>cont</c> 冻结/唤醒 guest。</summary>
    public int MonitorPort { get; }

    /// <summary>guest 控制口覆盖（0 = initrd 默认 18080）。</summary>
    public int CtrlPort { get; }

    /// <summary>Guard 解密服务端口（0 = 未启用；guest 监听 + 宿主 hostfwd 同号）。</summary>
    public int GuardPort { get; }

    /// <summary>启动磁力覆盖（initrd 里烧死的那条；Guard VM 传 "none"）。</summary>
    public string? MagnetOverride { get; }

    /// <summary>本实例使用的 initrd 文件名（多实例场景：下载引擎用独立控制口的 pkg_initrd_dl.gz）</summary>
    public string InitrdName { get; }
    public string ExePath => Path.Combine(RuntimeDir, "qemu-system-aarch64.exe");
    public string ConsoleLogPath { get; }

    private readonly Action<string>? _log;
    private Process? _proc;
    private KillOnCloseJob? _job;
    private StreamWriter? _fileLog;
    private int _filtered;

    /// <summary>
    /// guest 的 vCPU 数（<c>-smp</c>）。默认 = <c>宿主逻辑核数</c>，**但硬上限 4**。
    ///
    /// <para>QEMU 这里是 **TCG 软件模拟**（ARM64 跑在 x86 上，没有硬件虚拟化加速），
    /// 引擎的下载链路是 CPU 密集型（分片/校验/memcpy）。</para>
    ///
    /// <para><b>为什么不是「用满所有核心」</b>（2026-09-21 实测，详见
    /// <c>docs/qemu-tcg-tuning.md</c> §6/§7）：引擎吞吐在 <b>2~4 vCPU 达到峰值</b>，
    /// 超过 4 不再增长；本机 12 逻辑核（6 物理核）实测 <c>-smp 12</c> 反而降到
    /// <b>0.78×</b> —— 12 条 vCPU 线程抢 6 个物理核，再叠加 SLIRP（单线程）与宿主侧代理，
    /// 严重超订；且 TCG 的翻译块缓存与翻译锁是全局共享的，vCPU 越多撞锁越频繁。</para>
    ///
    /// <para>⚠️ 限制**每实例**速度的是它自己的 vCPU 数，而不是宿主争用：
    /// 2 个实例并存时把每实例从 4 降到 2，合计吞吐反而从 38.1 掉到 29.1 MB/s。
    /// 所以多实例场景交给操作系统调度器分时即可，不要把单实例的 -smp 反向放大。</para>
    ///
    /// <para>下调到宿主核数是为了弱机：4 核以下按实际核数给，避免在 2 核机器上超订。</para>
    /// </summary>
    public int SmpCount { get; set; } = Math.Clamp(Environment.ProcessorCount, 1, 4);

    /// <summary>
    /// 稀疏块设备（数据面）——guest 把引擎吐出的字节按文件偏移直接写进来，宿主**直读同一物理文件**。
    ///
    /// <para><b>为什么</b>：2026-09-21 实测，现有取流路径每层都在白吃带宽：
    /// guest 内环 TCP 432 MB/s → SLIRP 40 MB/s（掉 10.8×）→ harness 转发后仅 <b>18.9 MB/s</b>。
    /// 宿主直读镜像实测 <b>2454~2926 MB/s</b>，且写入侧 84.9~97.7 MB/s 已远高于引擎自身
    /// 2.5~4.8 MB/s 的 P2P 供数速度。</para>
    ///
    /// <para>为 null 时不挂块设备，行为与改动前完全一致（纯 HTTP 通道）——
    /// 这是刻意的：新通道是**增益**，不是替代，任何环境异常都能安全退化。</para>
    /// </summary>
    public SparseBlockStore? BlockStore { get; private set; }

    /// <summary>
    /// 交换区块设备（可选）——**raw 设备，不需要文件系统**，由 guest 侧 <c>mkswap</c> 写签名。
    ///
    /// <para><b>为什么</b>：guest 的 <c>/thunder-data</c> 是 tmpfs，而 tmpfs 的页**可以换出**
    /// （ramfs 不行，这也是当初必须挂 tmpfs 的原因）。给一块宿主盘当 swap 后，已完成/
    /// 离播放头远的冷数据会被换到宿主磁盘上 —— <b>guest RAM 不再随下载量线性增长</b>。
    /// 后果：<c>-m</c> 可以从 5120 降到 ~2560，且 tmpfs 上限能抬高（&gt;3.5GB 的片不再被
    /// <c>err=114010</c> 打死）。附带收益：<c>docs/qemu-tcg-tuning.md</c> §7.2 实测降 <c>-m</c>
    /// 还有约 1.5× 提速。</para>
    ///
    /// <para>为 null 时不挂这块盘，guest 退回纯内存 tmpfs 3500m + <c>-m 5120</c>，
    /// 行为与改动前完全一致 —— 与数据面块设备同样的「增益而非替代」原则。</para>
    /// </summary>
    public SparseBlockStore? SwapStore { get; private set; }

    /// <summary>无 swap 时的 guest RAM（MB）—— 必须容得下 <see cref="DataDirMb"/> 的内存盘。</summary>
    public int GuestMemoryMb { get; set; } = 5120;

    /// <summary>启用 swap 时的 guest RAM（MB）—— 冷页能换出，取值可小得多。</summary>
    public int GuestMemoryMbWithSwap { get; set; } = 2560;

    /// <summary>无 swap 时 <c>/thunder-data</c> 的 tmpfs 大小（MB）。</summary>
    public int DataDirMb { get; set; } = 3500;

    /// <summary>启用 swap 时 <c>/thunder-data</c> 的 tmpfs 大小（MB）—— 上限由 RAM + swap 共同支撑。</summary>
    public int DataDirMbWithSwap { get; set; } = 6144;

    /// <param name="initrdName">initrd 文件名；多实例（如下载专用引擎）传独立控制口的第二份 initrd。</param>
    /// <param name="consoleLogTag">控制台日志文件名后缀（多实例避免互相覆盖）。</param>
    /// <param name="monitorPort">monitor 监听端口（0 = 不开）。仅绑 127.0.0.1，不对外。</param>
    /// <param name="ctrlPort">guest 控制口覆盖（0 = 用 initrd 默认 18080）。多 VM 并存时各用各的，
    /// 经内核 cmdline <c>ctrl=</c> 下发（/init 的 getarg 覆盖）。</param>
    /// <param name="guardPort">Guard 解密服务端口（0 = 不启用）。guest 内 harness 监听 0.0.0.0:该端口，
    /// 宿主经第二条 hostfwd 直连（桥进程的 TCP 客户端走它）；cmdline <c>guardport=</c> 下发。</param>
    /// <param name="magnetOverride">覆盖 initrd 里烧死的启动磁力（Guard VM 传 "none" 避免无谓真下载）。</param>
    public QemuHostRuntime(string runtimeDir, int mediaPort, Action<string>? log = null,
        string initrdName = "pkg_initrd.gz", string consoleLogTag = "", int monitorPort = 0,
        string? blockImagePath = null, long blockImageBytes = 0,
        string? swapImagePath = null, long swapImageBytes = 0,
        int ctrlPort = 0, int guardPort = 0, string? magnetOverride = null)
    {
        RuntimeDir = runtimeDir;
        MediaPort = mediaPort;
        MonitorPort = monitorPort;
        InitrdName = initrdName;
        CtrlPort = ctrlPort;
        GuardPort = guardPort;
        MagnetOverride = magnetOverride;
        _log = log;
        // Debug/Release 隔离（见 AppPaths）
        ConsoleLogPath = AppPaths.LocalOf($"qemu-console{consoleLogTag}.log");

        // 数据面块设备（可选）：传了路径才启用。失败不影响启动 —— 纯 HTTP 通道照旧可用。
        if (!string.IsNullOrEmpty(blockImagePath) && blockImageBytes > 0)
        {
            try
            {
                var store = new SparseBlockStore(blockImagePath!, blockImageBytes);
                store.EnsureCreated();
                BlockStore = store;
                _log?.Invoke($"[qemu] 数据面块设备就绪：{blockImagePath}（{blockImageBytes / 1024 / 1024}MB，稀疏={store.IsSparse}，已占 {store.AllocatedBytes() / 1024 / 1024}MB）");
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[qemu] 数据面块设备不可用（退化为纯 HTTP 通道）：{ex.GetType().Name}: {ex.Message}");
            }
        }

        // 交换区块设备（可选）。建法与数据面镜像相同（稀疏文件，实际只占写入量）——
        // 交换区的实际占用 = 真正被换出的字节数，通常远小于容量。失败即退回纯内存 tmpfs。
        if (!string.IsNullOrEmpty(swapImagePath) && swapImageBytes > 0)
        {
            try
            {
                var sw = new SparseBlockStore(swapImagePath!, swapImageBytes);
                sw.EnsureCreated();
                SwapStore = sw;
                _log?.Invoke($"[qemu] 交换区镜像就绪：{swapImagePath}（{swapImageBytes / 1024 / 1024}MB，稀疏={sw.IsSparse}）");
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[qemu] 交换区不可用（退化为纯内存 tmpfs）：{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>运行时文件是否齐全（缺一件就视为未部署，引擎判未就绪、静默回落）。</summary>
    public bool IsRuntimePresent => IsPresent(RuntimeDir, InitrdName);

    /// <summary>给定目录是否是一套完整的运行时（扁平布局）。</summary>
    public static bool IsPresent(string runtimeDir, string initrdName = "pkg_initrd.gz") =>
        File.Exists(Path.Combine(runtimeDir, "qemu-system-aarch64.exe"))
        && File.Exists(Path.Combine(runtimeDir, "pkg_kernel"))
        && File.Exists(Path.Combine(runtimeDir, initrdName));

    public bool IsRunning => _proc is { HasExited: false };

    /// <summary>
    /// 进程是否已退出（异常安全：对象已 Dispose / 未启动时返回 true，不抛异常）。
    /// <para>与 <see cref="IsRunning"/> 分开是刻意的：IsRunning 对 null 返回 false（"没在跑"），
    /// 而下载循环要的是"**明明起过、现在死了**"这个正向信号 —— 用 null 误判会把主动收尾也当成崩溃。</para>
    /// </summary>
    public bool HasExited
    {
        get
        {
            try { return _proc is null || _proc.HasExited; }
            catch { return true; }
        }
    }

    /// <summary>
    /// QEMU 进程退出时触发（崩溃 / 被系统 OOM 杀掉 / 主动 Stop 都会走）。
    /// <para>旧行为只在 Exited 里打一行日志 —— guest 被系统杀掉后宿主完全失明：下载轮询看不到
    /// 状态变化，一路空转到 180 分钟才报「下载超时」，而进度早已冻结。</para>
    /// </summary>
    public event Action? Died;

    /// <summary>启动 QEMU（不等待 guest 就绪；就绪信号由控制端首次轮询给出）。</summary>
    public Task<bool> StartAsync(CancellationToken ct = default)
    {
        if (IsRunning) return Task.FromResult(true);
        if (!IsRuntimePresent)
        {
            _log?.Invoke($"[qemu] 运行时缺失：{RuntimeDir}");
            return Task.FromResult(false);
        }

        try
        {
            ReapStaleVmOnOurPorts();
            OpenFileLog();

            var psi = new ProcessStartInfo
            {
                FileName = ExePath,
                WorkingDirectory = RuntimeDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            // ── 内存与数据盘取值：有没有 swap 决定给多少 ──
            // 无 swap：tmpfs 全在 RAM ⇒ -m 必须容下它；有 swap：冷页可换出 ⇒ -m 可以小得多。
            var memMb = SwapStore is not null ? GuestMemoryMbWithSwap : GuestMemoryMb;
            var tdataMb = SwapStore is not null ? DataDirMbWithSwap : DataDirMb;

            var netdev = $"user,id=n0,hostfwd=tcp:127.0.0.1:{MediaPort}-:20080";
            // Guard 解密服务：第二条 hostfwd（宿主与 guest 同号直连，桥进程经 127.0.0.1 调用）
            if (GuardPort > 0) netdev += $",hostfwd=tcp:127.0.0.1:{GuardPort}-:{GuardPort}";
            var args = new List<string>
            {
                // -m 5120：guest RAM 需容得下 /thunder-data 的 tmpfs（3500m，见 initrd 的 /init）+ 引擎开销；
                //  旧的 4096 + tmpfs 1500m 会在下载 ~1.57GB 时写满 tmpfs，任务以 err=114010 死亡
                //
                // -cpu cortex-a76（原为 max）+ -accel tcg,tb-size=256,split-wx=off
                //  （2026-09-21 实测，见 docs/qemu-tcg-tuning.md §3）：
                //   CPU 跑分 **1.22×**（B1 中位 67s→55s，4 轮采样分布不重叠）。
                //   · cortex-a76 无 SVE/SME，而 -cpu max 会打开它们 —— TCG 下 SVE 的
                //     状态保存/翻译块占用更重，收益主要来自关掉 SVE；a76 仍具备 NEON/AES/
                //     PMULL/SHA1/SHA2/CRC32/LSE atomics/dotprod，即该引擎会用到的全部能力。
                //   · split-wx=off 关闭 QEMU 的 W^X 双映射保护换取性能 —— **有意为之**，
                //     guest 只跑我们自己的 initrd，可接受。
                //   · tb-size 不是越大越好：512 实测 0.86×、64 实测 0.79×，256 才最佳。
                //   · ⚠️ 切勿引入 thread=single / -icount：实测 MTTCG 值 **4.1×**
                //     （910 vs 221 MB/s），aarch64 + smp>1 默认已开，别关掉。
                //   真实引擎端到端验证：该参数下引擎从本地源稳定下载 3.36GB（30~54 MB/s，err=0）。
                "-M", "virt", "-cpu", "cortex-a76", "-m", memMb.ToString(), "-smp", SmpCount.ToString(), "-nographic",
                "-accel", "tcg,tb-size=256,split-wx=off",
                "-L", "share",
                "-kernel", "pkg_kernel",
                "-initrd", InitrdName,
                "-netdev", netdev,
                "-device", "virtio-net-pci,netdev=n0",
            };

            // ── 数据面块设备（可选）──
            // guest 侧 harness 把引擎吐出的字节按文件偏移写进 /dev/vda，宿主随后**直读同一文件**
            // （实测 2454 MB/s，绕开 SLIRP 的 40 MB/s 与 harness 转发后的 18.9 MB/s）。
            // cache=unsafe：接受「宿主崩溃丢最后若干 MB 未刷数据」换取写吞吐；
            //   丢的区间 IsRangeAvailable 会判为不可用，播放自动回落到 HTTP 通道，不会读到脏数据。
            if (BlockStore is not null)
            {
                args.Add("-drive");
                args.Add($"file={BlockStore.ImagePath},if=none,id=hub0,format=raw,cache=unsafe");
                args.Add("-device");
                args.Add("virtio-blk-pci,drive=hub0");
            }

            // ── 交换区块设备（可选）──
            // raw 块设备即可，**不需要文件系统**（swap 签名由 guest 的 mkswap 写）。
            // 它给 tmpfs 提供换出空间：/thunder-data 的冷页落到宿主盘 ⇒ guest RAM 不随下载量涨。
            if (SwapStore is not null)
            {
                args.Add("-drive");
                args.Add($"file={SwapStore.ImagePath},if=none,id=swap0,format=raw,cache=unsafe");
                args.Add("-device");
                args.Add("virtio-blk-pci,drive=swap0");
            }

            // ★ 块设备名**由宿主认定后经 cmdline 告诉 guest**，不能让 guest 猜：
            //   vda/vdb 取决于挂载顺序，而数据面孔是**可选的** —— 若它缺席，vda 就变成了交换区，
            //   harness 会把引擎字节按文件偏移写进交换区（后果严重）。
            var vdIndex = 0;
            var append = $"console=ttyAMA0 rdinit=/init loglevel=4 tdata={tdataMb}m";
            if (BlockStore is not null) append += $" blkdev=/dev/vd{(char)('a' + vdIndex++)}";
            if (SwapStore is not null) append += $" swapdev=/dev/vd{(char)('a' + vdIndex++)}";
            // Guard VM 的口令与启动磁力经 cmdline 覆盖（/init 的 getarg；缺省与旧行为一致）
            if (CtrlPort > 0) append += $" ctrl={CtrlPort}";
            if (GuardPort > 0) append += $" guardport={GuardPort}";
            if (!string.IsNullOrEmpty(MagnetOverride)) append += $" magnet={MagnetOverride}";
            args.Add("-append");
            args.Add(append);
            _log?.Invoke($"[qemu] guest 内存 {memMb}MB · tmpfs {tdataMb}MB · swap={(SwapStore is null ? "无" : SwapStore.CapacityBytes / 1024 / 1024 + "MB")} · {append}");
            // monitor 通道（仅回环）：宿主靠 stop/cont 冻结/唤醒 VM —— 退出播放页时冻住，
            // 迅雷侧下载立刻停又不必丢任务与已下数据（见 SetPaused）。缺失也只是退化成杀 VM。
            if (MonitorPort > 0)
                args.AddRange(["-monitor", $"tcp:127.0.0.1:{MonitorPort},server,nowait"]);
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += OnLine;
            proc.ErrorDataReceived += OnLine;
            proc.Exited += (_, _) =>
            {
                _log?.Invoke("[qemu] 进程退出");
                try { Died?.Invoke(); } catch { }
            };
            if (!proc.Start()) return Task.FromResult(false);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            AssignJob(proc);

            _proc = proc;
            _filtered = 0;
            _log?.Invoke($"[qemu] 已启动 pid={proc.Id} 媒体口={MediaPort}");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[qemu] 启动失败：{ex.GetType().Name}: {ex.Message}");
            return Task.FromResult(false);
        }
    }

    private void OnLine(object? sender, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;
        try { _fileLog?.WriteLine(e.Data); } catch { }

        // app 日志过滤 JNI 噪声（[jni] 行每次调用都刷屏）；完整控制台进文件
        if (e.Data.Contains("[jni]")) { _filtered++; return; }
        _log?.Invoke(e.Data);
    }

    private void OpenFileLog()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConsoleLogPath)!);
            _fileLog = new StreamWriter(new FileStream(ConsoleLogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8) { AutoFlush = true };
            _fileLog.WriteLine($"\n===== QEMU 启动 {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
        }
        catch { _fileLog = null; }
    }

    /// <summary>停掉 VM（杀进程树）并关掉日志句柄。</summary>
    public void Stop()
    {
        var p = _proc;
        _proc = null;
        if (p is not null)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            try { p.WaitForExit(5000); } catch { }
            try { p.Dispose(); } catch { }
            _log?.Invoke($"[qemu] 已停止（过滤 {_filtered} 行 jni 噪声，完整日志：{ConsoleLogPath}）");
        }
        try { _fileLog?.Flush(); _fileLog?.Dispose(); } catch { }
        _fileLog = null;
    }

    /// <summary>
    /// 冻结 / 唤醒 guest（QEMU monitor 的 <c>stop</c> / <c>cont</c>），成功返回 true。
    ///
    /// <para><b>为什么要冻结而不是杀进程</b>：迅雷 P2SP 任务一旦下发就会自己下个不停，宿主没有
    /// 「只停下载、别丢任务」的开关（guest 侧 harness 的 STOP 原先也没真调 stopTask）。
    /// <c>stop</c> 只冻 vCPU：网络侧对端收不到 ACK 立刻掉速，流量与 CPU 当场归零，
    /// 而任务句柄、/thunder-data 里已下载的数据原样保留 —— 用户回来 <c>cont</c> 即续，
    /// 不必重建任务、不必等 VM 冷启动。</para>
    ///
    /// <para>⚠ 冻久了（分钟级）迅雷的对端连接会被 peer 判死，<c>cont</c> 后需要重新建连才拉得起速度；
    /// 这是「停下来」必须付的代价，且远轻于重建任务。</para>
    ///
    /// <para>端口不可达 / 未启用 monitor 时返回 false，调用方据此退化为杀 VM（保证「不再下载」）。</para>
    /// </summary>
    public bool SetPaused(bool paused)
    {
        if (MonitorPort <= 0 || !IsRunning) return false;
        try
        {
            using var c = new TcpClient();
            c.Connect(IPAddress.Loopback, MonitorPort);
            using var ns = c.GetStream();
            var cmd = Encoding.ASCII.GetBytes(paused ? "stop\n" : "cont\n");
            ns.Write(cmd, 0, cmd.Length);
            ns.Flush();
            // 读回显只用来确认送达；超时不代表失败（命令已发出），故不据此判 false
            try
            {
                c.ReceiveTimeout = 1000;
                var buf = new byte[512];
                _ = ns.Read(buf, 0, buf.Length);
            }
            catch { }
            _log?.Invoke(paused
                ? "[qemu] monitor stop → 冻结 VM（下载立即停，任务与已下数据保留）"
                : "[qemu] monitor cont → 唤醒 VM（继续下载/供数）");
            return true;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[qemu] monitor {(paused ? "stop" : "cont")} 失败：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // ── Job Object（KillOnClose）：宿主挂了 VM 不孤儿。实现见 KillOnCloseJob（桥 JVM 共用同一份）──
    private void AssignJob(Process proc)
    {
        _job ??= KillOnCloseJob.Create();
        if (_job is null)
            _log?.Invoke("[qemu] KillOnClose job 创建失败：应用退出后本 VM 可能变孤儿");
        else if (!_job.Attach(proc))
            _log?.Invoke($"[qemu] VM pid={proc.Id} 未挂进 KillOnClose job（{_job.LastError}）：靠启动前端口回收兜底");
    }

    // ── 启动前回收上一世遗留的 VM（按 hostfwd 端口找它）──
    // 宿主被强杀时 ProcessExit 走不到，job object 又可能因宿主已在别的 job 里而绑不上
    // （win32=5）。遗留 VM 会一直占着 18481 这类 hostfwd 端口，下一次启动只能退化。
    private void ReapStaleVmOnOurPorts()
    {
        foreach (var port in new[] { GuardPort, MediaPort })
        {
            if (port <= 0) continue;
            if (TcpListeners.ReapOwner(port, "qemu-system-aarch64"))
                _log?.Invoke($"[qemu] 端口 {port} 被上一世遗留的 VM 占着，已回收");
        }
    }

    public void Dispose()
    {
        Stop();
        _job?.Dispose();
        _job = null;
    }
}
