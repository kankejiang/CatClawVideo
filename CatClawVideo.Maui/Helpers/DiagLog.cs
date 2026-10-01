namespace CatClawVideo.Maui;

/// <summary>临时链路诊断：写 %APPDATA%/CatClawVideo/diag.log（Windows 无控制台输出）。
/// 用于定位无限滚动/续看等异步链路，稳定后可整体移除。</summary>
public static class DiagLog
{
    /// <summary>home-debug.log 大小上限：超过即轮转保留尾部（此前 guest 的 adbd/logd 噪音
    /// 每 ~200ms 一行刷进该文件，实测涨到 2GB）。</summary>
    private const long MaxLogBytes = 32L * 1024 * 1024;
    private const int TailBytesOnRotate = 1024 * 1024;

    /// <summary>写入计数（避免每行都 stat 文件长度，每 4096 行查一次）。</summary>
    private static int _writes;

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
        // guest 纯噪音行直接丢：adbd/logd 每 ~200ms 刷一行（Server.java pump 注释同源），
        // 对宿主诊断零价值，却把 home-debug.log 刷到 2GB（真要查 guest 日志走 adb logcat）。
        // 二进制乱码转发行（console 流被按文本解出的替换符/控制符）一并丢弃。
        if (msg.Contains("[logd]") ||
            msg.Contains("Waiting for persist.adb.tls_server.enable") ||
            IsGarbageLine(msg)) return;

        try
        {
            // Debug/Release 隔离（见 Core.AppPaths）：Debug 落 CatClawVideo.debug
            var path = CatClawVideo.Core.AppPaths.Of("home-debug.log");
            if (++_writes % 4096 == 0)
            {
                try
                {
                    if (new FileInfo(path) is { Exists: true } f && f.Length > MaxLogBytes)
                        RotateKeepingTail(path);
                }
                catch { }
            }
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
        }
        catch { }

        // 镜像进「诊断日志」（设置页开关控制，关闭时零开销）：
        // 本类是 App 内用得最广的日志出口，镜像后用户一开开关即可拿到完整运行轨迹，
        // 不必再让用户手动去翻 home-debug.log（2026-09-18 用户要求）。
        CatClawVideo.Maui.Services.DiagnosticLog.WriteTagged("D", "App", msg);
    }

    /// <summary>guest console 二进制流的乱码行：含替换字符或多个控制字符（正常日志不含）。</summary>
    private static bool IsGarbageLine(string msg)
    {
        int bad = 0;
        foreach (var ch in msg)
        {
            if (ch == '\uFFFD' || (char.IsControl(ch) && ch != '\t'))
            {
                if (++bad > 2) return true;
            }
        }
        return false;
    }

    /// <summary>轮转：旧文件改名留档（再超限时覆盖），新写入从空文件开始。</summary>
    private static void RotateKeepingTail(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var keep = (int)Math.Min(TailBytesOnRotate, fs.Length);
        fs.Seek(-keep, SeekOrigin.End);
        var tail = new byte[keep];
        fs.ReadExactly(tail);
        fs.Dispose();

        var old = path + ".old";
        File.Delete(old);
        File.Move(path, old);
        File.WriteAllText(path, $"---- 日志超过 {MaxLogBytes / 1024 / 1024}MB，已轮转（旧文件 {old}）----{Environment.NewLine}");
        using var outFs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        outFs.Write(tail);
    }
}
