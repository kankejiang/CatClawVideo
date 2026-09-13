using CatClaw.Shared.Js;

namespace CatClawVideo.Core.Services;

/// <summary>
/// <see cref="Interfaces.IJsRuntimeService"/> 默认实现：共享基类 <see cref="JsRuntimeServiceBase"/>
/// + 猫爪影视的约束参数（递归上限 2000 / 默认超时 60 秒）。
/// <para>
/// 与猫爪音乐的 JsRuntimeService 仅构造参数不同，其余逻辑统一在共享库维护。
/// </para>
/// </summary>
public sealed class JsRuntimeService : JsRuntimeServiceBase, Interfaces.IJsRuntimeService
{
    public JsRuntimeService()
        : base(
            recursionLimit: 2000,
            defaultTimeout: TimeSpan.FromSeconds(60),
            unavailableMessage: "JS 运行时（Jint）不可用")
    {
    }
}
