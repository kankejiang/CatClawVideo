namespace CatClawVideo.Maui.Services;

/// <summary>
/// BT 链路持久化日志（流式播放 + 下载管理器共用）：
/// 写入 {AppData}/logs/bt.log，诊断"下载不动/注册冲突/节点为零"这类只能靠现场证据的问题。
/// </summary>
internal static class BtFileLog
{
    private static readonly object Lock = new();

    public static void Write(string msg)
    {
        try
        {
            var dir = CatClawVideo.Core.AppPaths.Sub("logs");
            Directory.CreateDirectory(dir);
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
            lock (Lock)
            {
                try
                {
                    File.AppendAllText(Path.Combine(dir, "bt.log"), line + Environment.NewLine);
                }
                catch
                {
                    // bt.log 被残留实例以独占方式锁住时（僵尸进程），降级写备份文件，保住现场证据
                    File.AppendAllText(Path.Combine(dir, "bt-fallback.log"), line + Environment.NewLine);
                }
            }
            System.Diagnostics.Debug.WriteLine("[bt] " + msg);

            // 镜像进「诊断日志」（tag=BT）：磁力链路的问题现场（节点数/供数/seek）最需要随
            // 诊断包一起交出来。仅开启时写入，且服务侧有限流，不会把诊断日志刷爆。
            DiagnosticLog.WriteTagged("D", "BT", msg);
        }
        catch
        {
            // 日志失败不影响业务
        }
    }

    /// <summary>日志文件完整路径（设置页"查看日志"或用户排障用）</summary>
    public static string LogFilePath
    {
        get
        {
            var dir = CatClawVideo.Core.AppPaths.Sub("logs");
            return Path.Combine(dir, "bt.log");
        }
    }
}
