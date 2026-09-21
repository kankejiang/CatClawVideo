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

    /// <summary>本实例使用的 initrd 文件名（多实例场景：下载引擎用独立控制口的 pkg_initrd_dl.gz）</summary>
    public string InitrdName { get; }
    public string ExePath => Path.Combine(RuntimeDir, "qemu-system-aarch64.exe");
    public string ConsoleLogPath { get; }

    private readonly Action<string>? _log;
    private Process? _proc;
    private IntPtr _job = IntPtr.Zero;
    private StreamWriter? _fileLog;
    private int _filtered;

    /// <summary>
    /// guest 的 vCPU 数（<c>-smp</c>）。默认 4。
    ///
    /// <para>QEMU 这里是 **TCG 软件模拟**（ARM64 跑在 x86 上，没有硬件虚拟化加速），
    /// 引擎的下载链路是 CPU 密集型（分片/校验/memcpy），实测下载时 QEMU 已占到
    /// 3.4 个核（4 vCPU 的 85%）—— 多给核对这类负载可能有效。</para>
    /// </summary>
    public int SmpCount { get; set; } = 4;

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

    /// <param name="initrdName">initrd 文件名；多实例（如下载专用引擎）传独立控制口的第二份 initrd。</param>
    /// <param name="consoleLogTag">控制台日志文件名后缀（多实例避免互相覆盖）。</param>
    /// <param name="monitorPort">monitor 监听端口（0 = 不开）。仅绑 127.0.0.1，不对外。</param>
    public QemuHostRuntime(string runtimeDir, int mediaPort, Action<string>? log = null,
        string initrdName = "pkg_initrd.gz", string consoleLogTag = "", int monitorPort = 0,
        string? blockImagePath = null, long blockImageBytes = 0)
    {
        RuntimeDir = runtimeDir;
        MediaPort = mediaPort;
        MonitorPort = monitorPort;
        InitrdName = initrdName;
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
    }

    /// <summary>运行时文件是否齐全（缺一件就视为未部署，引擎判未就绪、静默回落）。</summary>
    public bool IsRuntimePresent => IsPresent(RuntimeDir, InitrdName);

    /// <summary>给定目录是否是一套完整的运行时（扁平布局）。</summary>
    public static bool IsPresent(string runtimeDir, string initrdName = "pkg_initrd.gz") =>
        File.Exists(Path.Combine(runtimeDir, "qemu-system-aarch64.exe"))
        && File.Exists(Path.Combine(runtimeDir, "pkg_kernel"))
        && File.Exists(Path.Combine(runtimeDir, initrdName));

    public bool IsRunning => _proc is { HasExited: false };

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
            var args = new List<string>
            {
                // -m 5120：guest RAM 需容得下 /thunder-data 的 tmpfs（3500m，见 initrd 的 /init）+ 引擎开销；
                //  旧的 4096 + tmpfs 1500m 会在下载 ~1.57GB 时写满 tmpfs，任务以 err=114010 死亡
                "-M", "virt", "-cpu", "max", "-m", "5120", "-smp", SmpCount.ToString(), "-nographic",
                "-L", "share",
                "-kernel", "pkg_kernel",
                "-initrd", InitrdName,
                "-append", "console=ttyAMA0 rdinit=/init loglevel=4",
                "-netdev", $"user,id=n0,hostfwd=tcp:127.0.0.1:{MediaPort}-:20080",
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
            // monitor 通道（仅回环）：宿主靠 stop/cont 冻结/唤醒 VM —— 退出播放页时冻住，
            // 迅雷侧下载立刻停又不必丢任务与已下数据（见 SetPaused）。缺失也只是退化成杀 VM。
            if (MonitorPort > 0)
                args.AddRange(["-monitor", $"tcp:127.0.0.1:{MonitorPort},server,nowait"]);
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += OnLine;
            proc.ErrorDataReceived += OnLine;
            proc.Exited += (_, _) => _log?.Invoke("[qemu] 进程退出");
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

    // ── Job Object（KillOnClose）：宿主挂了 VM 不孤儿 ──
    private void AssignJob(Process proc)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (_job == IntPtr.Zero) _job = CreateKillOnCloseJob();
            if (_job != IntPtr.Zero) AssignProcessToJobObject(_job, proc.Handle);
        }
        catch { /* 尽力而为；失败时退化为常规子进程 */ }
    }

    private static IntPtr CreateKillOnCloseJob()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size))
            {
                CloseHandle(job);
                return IntPtr.Zero;
            }
        }
        finally { Marshal.FreeHGlobal(ptr); }
        return job;
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    public void Dispose()
    {
        Stop();
        if (_job != IntPtr.Zero)
        {
            try { CloseHandle(_job); } catch { }
            _job = IntPtr.Zero;
        }
    }
}
