namespace CatClawVideo.Core.Providers;

/// <summary>Core 层静态日志钩子：宿主启动时挂 Sink（MAUI 端 → DiagLog → logcat/文件）。</summary>
public static class CatClawLog
{
    public static Action<string>? Sink;

    public static void Write(string msg)
    {
        try { Sink?.Invoke(msg); } catch { }
    }
}
