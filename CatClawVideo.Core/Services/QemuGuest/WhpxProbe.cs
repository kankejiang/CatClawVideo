using System.Runtime.InteropServices;

namespace CatClawVideo.Core.Services.QemuGuest;

/// <summary>
/// WHPX（Windows Hypervisor Platform）可用性探测。
///
/// <para><b>为什么不用别的判据</b>：WHPX 用户态 API 由可选功能「Windows 虚拟机监控程序平台」
/// （HypervisorPlatform）提供——<c>WinHvPlatform.dll</c> + <c>winhvr.sys</c> 驱动。它与 Hyper-V
/// 完整功能、任务管理器的「虚拟化已启用」都不是一回事：本机 2026-09-29 实测 Hyper-V 全开、
/// HypervisorPresent=True，但 HypervisorPlatform 没开 → API DLL 整个不存在。最直接的判据就是
/// 调 <c>WHvGetCapability(WHvCapabilityCodeHypervisorPresent)</c>：API 在就返回 S_OK 且值=1；
/// DLL 缺失（功能没开）→ DllNotFoundException。与 QEMU <c>-accel whpx</c> 的运行时判定一致，
/// 且进程内一次探测零开销。</para>
/// <para>⚠ DLL 名是 <b>WinHvPlatform.dll</b>——系统里根本没有叫 whpx.dll 的文件；初版误写导致
/// 探测恒 false（2026-09-29 本机实锤后修正）。</para>
///
/// <para><b>结果进程内缓存</b>：平台功能开关注销才生效，运行中不会变；构造期调用一次即可。</para>
/// </summary>
public static class WhpxProbe
{
    private const uint WHvCapabilityCodeHypervisorPresent = 0x0;
    private static int? _cached;   // 1=可用 0=不可用；null=未探测

    [DllImport("WinHvPlatform.dll", SetLastError = false)]
    private static extern int WHvGetCapability(uint capabilityCode, out uint capabilityValue,
        uint capabilityValueSize, out uint writtenSize);

    /// <summary>WHPX 是否可用（进程内缓存）。</summary>
    public static bool IsAvailable()
    {
        if (_cached is { } v) return v == 1;
        try
        {
            var hr = WHvGetCapability(WHvCapabilityCodeHypervisorPresent, out uint value,
                sizeof(uint), out uint written);
            _cached = hr == 0 && value == 1 && written == sizeof(uint) ? 1 : 0;
        }
        catch (DllNotFoundException) { _cached = 0; }   // HypervisorPlatform 功能未开（WinHvPlatform.dll 不在）
        catch (EntryPointNotFoundException) { _cached = 0; }
        return _cached == 1;
    }
}
