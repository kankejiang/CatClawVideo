namespace CatClawVideo.Maui;

/// <summary>临时链路诊断：写 %APPDATA%/CatClawVideo/diag.log（Windows 无控制台输出）。
/// 用于定位无限滚动/续看等异步链路，稳定后可整体移除。</summary>
public static class DiagLog
{
    public static void Write(string msg)
    {
        try
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CatClawVideo");
            Directory.CreateDirectory(dir);
            File.AppendAllText(System.IO.Path.Combine(dir, "home-debug.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
        }
        catch { }
    }
}
