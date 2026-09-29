using System.Runtime.InteropServices;

namespace CatClawVideo.Core.Services.QemuGuest;

/// <summary>
/// WHPX（Windows Hypervisor Platform）可用性探测。
///
/// <para><b>为什么不用别的判据</b>：WHPX 需要「Windows 功能 → 虚拟机平台」
/// （<c>winhvr.sys</c> 驱动 + <c>whpx.dll</c> API），这与 Hyper-V 完整功能、任务管理器
/// 的「虚拟化已启用」都不是一回事——CPU 支持虚拟化 ≠ 平台驱动就位。最直接的判据就是
/// 调 <c>WHvGetCapability(WHvCapabilityCodeHypervisorPresent)</c>：API 在且驱动活着 →
/// 返回 S_OK 且值=1；<c>whpx.dll</c> 缺失（功能没开）→ DllNotFoundException；驱动被关
/// → API 报错。与 QEMU <c>-accel whpx</c> 的运行时判定一致，且进程内一次探测零开销。</para>
///
/// <para><b>结果进程内缓存</b>：平台功能开关注销才生效，运行中不会变；构造期调用一次即可。</para>
/// </summary>
public static class WhpxProbe
{
    private const uint WHvCapabilityCodeHypervisorPresent = 0x0;
    private static int? _cached;   // 1=可用 0=不可用；null=未探测

    [DllImport("whpx.dll", SetLastError = false)]
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
        catch (DllNotFoundException) { _cached = 0; }   // 虚拟机平台功能未开（whpx.dll 不在）
        catch (EntryPointNotFoundException) { _cached = 0; }
        return _cached == 1;
    }
}
