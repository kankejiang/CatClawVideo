namespace CatClawVideo.Core.Logging;

/// <summary>
/// 全局静态日志门面：任何模块（Core / Maui / 平台层）无需 DI 即可输出诊断日志。
/// 由实现在启动时调用 <see cref="SetProvider"/> 注册。
///
/// <para><b>零开销保证</b>：未注册 provider，或 provider 未开启（<see cref="IsEnabled"/> = false）时，
/// 所有调用立即返回 —— 关闭状态下不要做字符串拼接以外的任何工作。</para>
/// </summary>
public static class Log
{
    private static ILogService? _provider;

    /// <summary>注册日志服务提供者（应用启动时调用一次）</summary>
    public static void SetProvider(ILogService? provider) => _provider = provider;

    /// <summary>诊断日志是否已开启（调用方可据此跳过昂贵的日志内容组装）</summary>
    public static bool IsEnabled => _provider?.IsEnabled ?? false;

    public static void Debug(string tag, string message)
    {
        var p = _provider;
        if (p is null || !p.IsEnabled) return;
        p.Debug(tag, message);
    }

    public static void Info(string tag, string message)
    {
        var p = _provider;
        if (p is null || !p.IsEnabled) return;
        p.Info(tag, message);
    }

    public static void Warn(string tag, string message)
    {
        var p = _provider;
        if (p is null || !p.IsEnabled) return;
        p.Warn(tag, message);
    }

    public static void Error(string tag, string message)
    {
        var p = _provider;
        if (p is null || !p.IsEnabled) return;
        p.Error(tag, message);
    }

    /// <summary>立即刷盘（页面退出、开关关闭、崩溃钩子等时机调用）</summary>
    public static void Flush()
    {
        var p = _provider;
        if (p is null || !p.IsEnabled) return;
        p.Flush();
    }
}
