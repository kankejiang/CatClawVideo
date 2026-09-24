using System.Runtime.InteropServices;
using System.Diagnostics;

namespace CatClawVideo.Core.Services;

/// <summary>
/// Windows Job Object（<c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>）：把子进程绑到自己的生命周期上，
/// 宿主以任何方式结束（正常退出、崩溃、被 kill）时内核连带终止子进程。
///
/// <para><b>为什么必须有</b>：桥 JVM 与 QEMU VM 都是长命子进程，而 .NET 在 Windows 上
/// <b>不会</b>在父进程退出时自动杀掉子进程。实测桌面一次开发会话攒下 <b>17 个</b>遗留
/// <c>java.exe</c>（2026-09-25），其中一个把 <c>bridge.jar</c> 以映射文件方式占住，
/// 后续 <c>JavaBridge\build.cmd</c> 直接 <c>FileSystemException: bridge.jar</c> 重打包失败 ——
/// 也就是「改了桩却编不出新 jar」，只能靠手工 taskkill 解套。</para>
///
/// <para>非 Windows 或 API 失败时返回 <see langword="null"/>，调用方按「尽力而为」降级为普通子进程。</para>
/// </summary>
public sealed class KillOnCloseJob : IDisposable
{
    private IntPtr _job;

    private KillOnCloseJob(IntPtr job) => _job = job;

    /// <summary>最近一次 <see cref="Attach"/> 失败的原因（Win32 错误码或异常描述；成功为 null）。</summary>
    public string? LastError { get; private set; }

    /// <summary>最近一次 <see cref="Create"/> 失败的原因（成功为 null）。</summary>
    public static string? LastCreateError { get; private set; }

    /// <summary>建一个 kill-on-close job；平台不支持或 API 失败返回 <see langword="null"/>。</summary>
    public static KillOnCloseJob? Create()
    {
        LastCreateError = null;
        if (!OperatingSystem.IsWindows()) { LastCreateError = "非 Windows"; return null; }
        var h = CreateJobObjectW(IntPtr.Zero, null);
        if (h == IntPtr.Zero)
        {
            LastCreateError = $"CreateJobObjectW win32={Marshal.GetLastWin32Error()}";
            return null;
        }
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
            if (!SetInformationJobObject(h, JobObjectExtendedLimitInformation, ptr, (uint)size))
            {
                LastCreateError = $"SetInformationJobObject win32={Marshal.GetLastWin32Error()} size={size}";
                CloseHandle(h);
                return null;
            }
        }
        finally { Marshal.FreeHGlobal(ptr); }
        return new KillOnCloseJob(h);
    }

    /// <summary>把 <paramref name="proc"/> 挂进本 job。一个 job 可挂多个进程（重启子进程时复用）。</summary>
    public bool Attach(Process proc)
    {
        LastError = null;
        if (_job == IntPtr.Zero) { LastError = "job 句柄已释放"; return false; }
        try
        {
            if (AssignProcessToJobObject(_job, proc.Handle)) return true;
            var err = Marshal.GetLastWin32Error();
            // 5 = ERROR_ACCESS_DENIED：目标进程已属于另一个不允许嵌套的 job
            // （宿主自身被包在 job 里跑时必现，实测 2026-09-25）
            LastError = err == 5 ? "5/ERROR_ACCESS_DENIED（进程已在别的 job 里）" : err.ToString();
            return false;
        }
        catch (Exception e) { LastError = e.GetType().Name + ": " + e.Message; return false; }
    }

    public void Dispose()
    {
        var h = Interlocked.Exchange(ref _job, IntPtr.Zero);
        if (h != IntPtr.Zero)
        {
            // 关句柄即触发 KILL_ON_JOB_CLOSE：子进程由内核收走
            try { CloseHandle(h); } catch { }
        }
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
        // ⚠ 这三个的型别必须照 Win32（DWORD / ULONG_PTR / DWORD）：
        // 把 ActiveProcessLimit 或 PriorityClass 写成 8 字节，结构体就从 144 变 152，
        // SetInformationJobObject 回 ERROR_BAD_LENGTH(24)，job 静默没建成（实测 2026-09-25）。
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
}
