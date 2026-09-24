using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CatClawVideo.Core.Services;

/// <summary>
/// 「谁在监听这个 TCP 端口」—— 只查本机 IPv4 监听表，不引外部依赖。
/// <para>用途：QEMU VM 是长命子进程。宿主被强杀（<c>taskkill /f</c>、任务管理器结束进程）时
/// 走不到 <c>ProcessExit</c>，而 Windows 的 job object 在宿主自身已属于别的 job 时
/// <c>AssignProcessToJobObject</c> 会直接 <c>win32=5</c> 失败（实测 2026-09-25），
/// 于是遗留 VM 继续占着 hostfwd 端口（18481 等）—— 下一次启动只能退化到 unidbg。
/// 所以启动前先按端口把遗留 VM 找出来收掉。</para>
/// </summary>
public static class TcpListeners
{
    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_LISTENER = 3;

    /// <summary>监听 <paramref name="port"/> 的进程 PID；没人监听返回 0。</summary>
    public static int OwningPid(int port)
    {
        if (!OperatingSystem.IsWindows() || port <= 0) return 0;
        try
        {
            int size = 0;
            _ = GetExtendedTcpTable(IntPtr.Zero, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
            if (size == 0) return 0;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buf, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0)
                    return 0;
                var rows = Marshal.ReadInt32(buf);
                for (var i = 0; i < rows; i++)
                {
                    var row = IntPtr.Add(buf, 4 + i * 24);
                    var localPort = Marshal.ReadInt32(row, 8);
                    // 表里的端口是网络字节序放在低 16 位，必须换回来（对照实测：不换查不到 18481）
                    var host = (localPort & 0xFF) << 8 | (localPort >> 8) & 0xFF;
                    if (host == port) return Marshal.ReadInt32(row, 20);
                }
                return 0;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return 0; }
    }

    /// <summary>
    /// 若 <paramref name="port"/> 正被<b>别的</b>进程监听，结束那个进程并等端口释放。
    /// 只处理 <c>expectedProcessName</c> 匹配的进程名，避免误杀同名端口上的无关服务。
    /// </summary>
    public static bool ReapOwner(int port, string expectedProcessName, int waitMs = 3000)
    {
        var pid = OwningPid(port);
        if (pid == 0 || pid == Environment.ProcessId) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!string.Equals(p.ProcessName, expectedProcessName, StringComparison.OrdinalIgnoreCase))
                return false;
            p.Kill(entireProcessTree: true);
            p.WaitForExit(waitMs);
            return true;
        }
        catch { return false; }   // 进程刚好自己退了
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(IntPtr tcpTable, ref int tableLength,
        bool order, int addressFamily, int tableClass, int reserved);
}
