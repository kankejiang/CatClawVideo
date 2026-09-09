using Jint;

namespace CatClawVideo.Core.Interfaces;

/// <summary>
/// 宿主统一 JS 运行时服务（Jint，与猫爪音乐同构）：统一引擎创建约束——
/// 单一版本来源、递归上限、超时控制。
/// </summary>
public interface IJsRuntimeService
{
    /// <summary>确保 Jint/Acornima 程序集已加载（幂等）</summary>
    void EnsureLoaded();

    /// <summary>创建统一约束的 Jint Engine（递归上限 2000 + 超时控制）</summary>
    Engine CreateEngine(TimeSpan? timeout = null);
}
