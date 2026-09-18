namespace CatClawVideo.Core.Logging;

/// <summary>
/// 诊断日志服务接口（实现见 MAUI 层 <c>Services.DiagnosticLog</c>）：把运行日志写入
/// <c>logs/debug.log</c>，供用户在设置页开启后抓取现场证据（对齐猫爪音乐的 ILogService 设计）。
/// </summary>
/// <remarks>
/// <para><b>为什么放在 Core</b>：Core/Provider 层不引用 MAUI，但同样需要输出日志
/// —— 接口与门面（<see cref="Log"/>）落在 Core，实现落在 MAUI（要读 Preferences 与文件系统）。</para>
/// </remarks>
public interface ILogService
{
    /// <summary>诊断日志是否已开启（关闭时所有写入为 no-op，不做任何文件 I/O）</summary>
    bool IsEnabled { get; set; }

    /// <summary>调试级别（最详细，链路追踪用）</summary>
    void Debug(string tag, string message);

    /// <summary>信息级别（关键流程节点）</summary>
    void Info(string tag, string message);

    /// <summary>警告级别（可自愈的异常、降级路径）</summary>
    void Warn(string tag, string message);

    /// <summary>错误级别（功能失败、异常）</summary>
    void Error(string tag, string message);

    /// <summary>立即把缓冲区刷盘（结束诊断前调用，保证最后几行不丢）</summary>
    void Flush();
}
