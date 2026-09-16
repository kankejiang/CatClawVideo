namespace CatClawVideo.Maui;

/// <summary>临时链路诊断：写 %APPDATA%/CatClawVideo/diag.log（Windows 无控制台输出）。
/// 用于定位无限滚动/续看等异步链路，稳定后可整体移除。</summary>
public static class DiagLog
{
    public static void Write(string msg)
    {
        // Android：同时进 logcat（Release 版文件在内部存储，adb 读不到；logcat 免 root 可取）
        // ⚠ 必须编译期隔离：#if ANDROID。OperatingSystem.IsAndroid() 只是**运行时**判断，
        // 编译器仍会解析 Android.Util.Log —— Windows TFM 下没有该命名空间，直接 CS0103。
#if ANDROID
        if (OperatingSystem.IsAndroid())
        {
            try { Android.Util.Log.Info("CatClawDiag", msg); } catch { }
        }
#endif
        try
        {
            // Debug/Release 隔离（见 Core.AppPaths）：Debug 落 CatClawVideo.debug
            File.AppendAllText(CatClawVideo.Core.AppPaths.Of("home-debug.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
        }
        catch { }
    }
}
