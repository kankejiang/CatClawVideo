using Android.Content;
using Android.Net.Wifi;
using AContext = Android.Content.Context;
using AApp = Android.App.Application;

namespace CatClawVideo.Maui.Platforms.Android;

/// <summary>
/// SSDP 组播锁。Android 默认丢掉组播接收，<b>不加锁的表现是「永远搜不到设备」而不是报错</b> ——
/// 所以投屏入口第一件事就是它。锁是引用计数的，用完必须放（这里显式成对调用，不用 using 模式，
/// 免得调用方把 Release 漏在异常路径上）。
/// </summary>
public static class WifiMulticast
{
    private static WifiManager.MulticastLock? _lock;

    public static void Acquire()
    {
        // 只判「我们有没有拿到过」：锁设成了非引用计数，重复 Acquire 本就无害，
        // 而 Xamarin 绑定上 MulticastLock 并没有暴露 IsHolding/Holding 成员（编译期已验）。
        if (_lock is not null) return;
        try
        {
            if (AApp.Context.GetSystemService(AContext.WifiService) is not WifiManager wifi) return;
            var lk = wifi.CreateMulticastLock("catclaw_dlna");
            if (lk is null) return;
            lk.SetReferenceCounted(false);
            lk.Acquire();
            _lock = lk;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[投屏] 组播锁申请失败（SSDP 可能搜不到设备）: {ex.Message}");
        }
    }

    public static void Release()
    {
        try { _lock?.Release(); } catch { }
        _lock = null;
    }
}
