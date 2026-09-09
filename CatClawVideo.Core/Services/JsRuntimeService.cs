using Jint;

namespace CatClawVideo.Core.Services;

/// <summary>
/// <see cref="Interfaces.IJsRuntimeService"/> 默认实现：Jint/Acornima 为普通 NuGet
/// 依赖随宿主分发（Android/Windows 双端纯托管可用）。
/// </summary>
public sealed class JsRuntimeService : Interfaces.IJsRuntimeService
{
    private int _ensured;

    public void EnsureLoaded()
    {
        if (Interlocked.Exchange(ref _ensured, 1) == 1)
            return;
        try
        {
            _ = typeof(Jint.Engine).Assembly;
            _ = typeof(Acornima.Parser).Assembly;
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _ensured, 0);
            throw new InvalidOperationException("JS 运行时（Jint）不可用", ex);
        }
    }

    public Engine CreateEngine(TimeSpan? timeout = null)
    {
        return new Engine(options => options
            .LimitRecursion(2000)
            .TimeoutInterval(timeout ?? TimeSpan.FromSeconds(60)));
    }
}
