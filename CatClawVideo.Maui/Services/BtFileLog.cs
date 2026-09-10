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
            var dir = Path.Combine(FileSystem.AppDataDirectory, "logs");
            Directory.CreateDirectory(dir);
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
            lock (Lock)
            {
                File.AppendAllText(Path.Combine(dir, "bt.log"), line + Environment.NewLine);
            }
            System.Diagnostics.Debug.WriteLine("[bt] " + msg);
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
            var dir = Path.Combine(FileSystem.AppDataDirectory, "logs");
            return Path.Combine(dir, "bt.log");
        }
    }
}
