using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CatClawVideo.Core;
using CatClawVideo.Core.Logging;

namespace CatClawVideo.Maui.Services;

/// <summary>
/// 诊断日志服务：把运行日志写入 <c>{AppPaths.DataRoot}/logs/debug.log</c>，
/// 由设置页开关控制（默认关闭）。对齐猫爪音乐 <c>LogService</c> 的设计与文件格式。
///
/// <para><b>文件格式</b>（与猫爪音乐一致，便于两边用同一套阅读习惯）：
/// <c>HH:mm:ss.fff⇥[级别][标签] 消息</c>，级别 ∈ D/I/W/E。</para>
///
/// <para><b>为什么不走外部存储</b>：本项目 <see cref="AppPaths"/> 是数据路径的唯一来源
/// （Debug/Release 隔离）。Android 上该目录是应用私有目录、adb 也读不到，
/// 因此导出走「导出诊断包」把文件复制到缓存目录后交系统分享，不另建一份外部路径。</para>
///
/// <para><b>三条性能/安全约束</b>（都是踩过坑才加的）：
/// <list type="number">
/// <item><b>开关关闭即零开销</b>：不做任何字符串拼接与文件 I/O，Release 下无感。</item>
/// <item><b>限流</b>：磁力链路会收到 FFmpeg 逐帧解码日志（实测每秒 30+ 行）直接刷屏，
/// 因此每秒最多写入 <see cref="MaxLinesPerSecond"/> 行，超出部分丢弃并在下一秒汇总一行提示
/// —— 保证关键事件不被淹没（2026-09-18 实测 bt.log 曾被 nal 日志淹没）。</item>
/// <item><b>轮转</b>：单文件超过 <see cref="MaxFileBytes"/> 后转存 <c>debug.1.log</c>（只留一份），
/// 避免长时间开启把磁盘写满。</item>
/// </list></para>
/// </remarks>
public sealed partial class DiagnosticLog : ILogService
{
    /// <summary>开关的持久化键（Preferences）</summary>
    private const string EnabledKey = "diagnostic_log_enabled";

    /// <summary>缓冲达到该条数立即刷盘</summary>
    private const int FlushThreshold = 50;
    /// <summary>后台刷盘间隔（毫秒）</summary>
    private const int FlushIntervalMs = 1000;
    /// <summary>单条日志最大长度（超出截断，防止异常大对象撑爆文件）</summary>
    private const int MaxLineLength = 4000;
    /// <summary>单文件上限 8MB，超过即轮转</summary>
    private const long MaxFileBytes = 8L * 1024 * 1024;
    /// <summary>每秒最多写入行数（限流，防高频日志刷屏）</summary>
    private const int MaxLinesPerSecond = 200;

    private readonly string _logDir;
    private readonly string _logFilePath;
    private readonly string _bakFilePath;

    private readonly ConcurrentQueue<string> _buffer = new();
    private readonly Timer _flushTimer;
    private readonly object _flushLock = new();

    /// <summary>本秒剩余配额</summary>
    private int _secBudget = MaxLinesPerSecond;
    /// <summary>配额所属的秒（自开机起的秒数）</summary>
    private long _secStamp;
    /// <summary>本秒被限流丢弃的条数</summary>
    private int _secDropped;

    private bool _enabled;

    /// <summary>全局单例（供查看页与静态调用方取路径）</summary>
    public static DiagnosticLog? Instance { get; private set; }

    /// <summary>日志文件完整路径（供查看页/导出复用；初始化前为空串）</summary>
    public static string LogFilePath { get; private set; } = "";

    /// <summary>日志所在目录（导出与「打开所在文件夹」用）</summary>
    public static string LogDirectory { get; private set; } = "";

    /// <summary>诊断日志是否开启。变更后立即持久化，并在关闭时先刷盘（保证最后几行不丢）。</summary>
    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            try { Preferences.Default.Set(EnabledKey, value); } catch { }
            if (!value) Flush();
        }
    }

    public DiagnosticLog()
    {
        _logDir = AppPaths.Sub("logs");
        _logFilePath = Path.Combine(_logDir, "debug.log");
        _bakFilePath = Path.Combine(_logDir, "debug.1.log");
        LogFilePath = _logFilePath;
        LogDirectory = _logDir;
        try { Directory.CreateDirectory(_logDir); } catch { }

        // 默认关闭：由设置页开关控制。用户此前开启过则按 Preferences 恢复。
        try { _enabled = Preferences.Default.Get(EnabledKey, false); } catch { _enabled = false; }

        Instance = this;
        Log.SetProvider(this);

        _secStamp = Environment.TickCount64 / 1000;
        _flushTimer = new Timer(_ => FlushInternal(), null, FlushIntervalMs, FlushIntervalMs);
    }

    public void Debug(string tag, string message) => Write("D", tag, message);
    public void Info(string tag, string message) => Write("I", tag, message);
    public void Warn(string tag, string message) => Write("W", tag, message);
    public void Error(string tag, string message) => Write("E", tag, message);

    /// <summary>立即把缓冲区刷盘</summary>
    public void Flush() => FlushInternal();

    /// <summary>带标签的快捷写入（供既有日志链路镜像调用，自动跳过未开启状态）</summary>
    public static void WriteTagged(string level, string tag, string message)
    {
        var p = Instance;
        if (p is null || !p.IsEnabled) return;
        p.Write(level, tag, message);
    }

    private void Write(string level, string tag, string message)
    {
        // 调试控制台始终输出（开发期可见），文件写入受开关与限流约束
        System.Diagnostics.Debug.WriteLine($"[{level}][{tag}] {message}");
        if (!_enabled) return;

        if (!TryTakeBudget()) return;

        var sanitized = Sanitize(message);
        if (sanitized.Length > MaxLineLength)
            sanitized = sanitized[..MaxLineLength] + "…(截断)";

        _buffer.Enqueue($"{DateTime.Now:HH:mm:ss.fff}\t[{level}][{tag}] {sanitized}");

        if (_buffer.Count >= FlushThreshold)
            _ = Task.Run(FlushInternal);
    }

    /// <summary>
    /// 取用本秒的写入配额；超额返回 false（计入丢弃数）。
    /// 跨秒时把上一秒的丢弃数汇总成一行写入，避免「日志缺失却看不出来」。
    /// </summary>
    private bool TryTakeBudget()
    {
        var sec = Environment.TickCount64 / 1000;
        if (sec != _secStamp)
        {
            if (_secDropped > 0)
            {
                _buffer.Enqueue($"{DateTime.Now:HH:mm:ss.fff}\t[W][Log] " +
                                $"上一秒限流丢弃 {_secDropped} 条高频日志（FFmpeg/解码类刷屏已压缩）");
                _secDropped = 0;
            }
            _secStamp = sec;
            _secBudget = MaxLinesPerSecond;
        }

        if (_secBudget <= 0) { _secDropped++; return false; }
        _secBudget--;
        return true;
    }

    /// <summary>把缓冲区日志批量落盘（必要时先轮转），后台线程执行</summary>
    private void FlushInternal()
    {
        if (_buffer.IsEmpty) return;
        if (!Monitor.TryEnter(_flushLock)) return;
        try
        {
            var lines = new List<string>();
            while (_buffer.TryDequeue(out var line)) lines.Add(line);
            if (lines.Count == 0) return;

            try
            {
                RotateIfNeeded();
                File.AppendAllLines(_logFilePath, lines);
            }
            catch { }
        }
        finally
        {
            Monitor.Exit(_flushLock);
        }
    }

    /// <summary>单文件超上限时转存为 debug.1.log（只保留一份备份）</summary>
    private void RotateIfNeeded()
    {
        try
        {
            var fi = new FileInfo(_logFilePath);
            if (!fi.Exists || fi.Length < MaxFileBytes) return;

            try { File.Delete(_bakFilePath); } catch { }
            File.Move(_logFilePath, _bakFilePath);
        }
        catch { }
    }

    /// <summary>
    /// 脱敏：掩码 URL 凭证、Authorization/Bearer、api key / token / secret 类字段。
    /// 诊断日志常被用户直接发出来，必须默认去掉凭据。
    /// </summary>
    private static string Sanitize(string message)
    {
        if (string.IsNullOrEmpty(message)) return message ?? "";

        message = CredentialRegex().Replace(message, "://***@");
        message = BearerRegex().Replace(message, "$1=***");
        message = ApiKeyRegex().Replace(message, "$1\"***\"");
        return message;
    }

    [GeneratedRegex(@"://[^/@:\s]+:[^/@:\s]+@", RegexOptions.Compiled)]
    private static partial Regex CredentialRegex();

    [GeneratedRegex(@"(?i)(authorization|bearer)\s*[=:]\s*\S+", RegexOptions.Compiled)]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"(?i)(""?(?:api[_-]?key|apikey|token|secret|passwd|password)""?\s*[=:]\s*)""?[^""&\s,}]+""?", RegexOptions.Compiled)]
    private static partial Regex ApiKeyRegex();
}
