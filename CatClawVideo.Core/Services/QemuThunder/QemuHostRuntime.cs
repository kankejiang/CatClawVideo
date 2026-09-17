using System.Diagnostics;
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
    /// <summary>本实例使用的 initrd 文件名（多实例场景：下载引擎用独立控制口的 pkg_initrd_dl.gz）</summary>
    public string InitrdName { get; }
    public string ExePath => Path.Combine(RuntimeDir, "qemu-system-aarch64.exe");
    public string ConsoleLogPath { get; }

    private readonly Action<string>? _log;
    private Process? _proc;
    private IntPtr _job = IntPtr.Zero;
    private StreamWriter? _fileLog;
    private int _filtered;

    /// <param name="initrdName">initrd 文件名；多实例（如下载专用引擎）传独立控制口的第二份 initrd。</param>
    /// <param name="consoleLogTag">控制台日志文件名后缀（多实例避免互相覆盖）。</param>
    public QemuHostRuntime(string runtimeDir, int mediaPort, Action<string>? log = null,
        string initrdName = "pkg_initrd.gz", string consoleLogTag = "")
    {
        RuntimeDir = runtimeDir;
        MediaPort = mediaPort;
        InitrdName = initrdName;
        _log = log;
        // Debug/Release 隔离（见 AppPaths）
        ConsoleLogPath = AppPaths.LocalOf($"qemu-console{consoleLogTag}.log");
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
            foreach (var a in new[]
            {
                // -m 5120：guest RAM 需容得下 /thunder-data 的 tmpfs（3500m，见 initrd 的 /init）+ 引擎开销；
                //  旧的 4096 + tmpfs 1500m 会在下载 ~1.57GB 时写满 tmpfs，任务以 err=114010 死亡
                "-M", "virt", "-cpu", "max", "-m", "5120", "-smp", "4", "-nographic",
                "-L", "share",
                "-kernel", "pkg_kernel",
                "-initrd", InitrdName,
                "-append", "console=ttyAMA0 rdinit=/init loglevel=4",
                "-netdev", $"user,id=n0,hostfwd=tcp:127.0.0.1:{MediaPort}-:20080",
                "-device", "virtio-net-pci,netdev=n0",
            })
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
