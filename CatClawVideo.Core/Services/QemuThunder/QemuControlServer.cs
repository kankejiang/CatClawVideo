using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CatClawVideo.Core.Services.QemuThunder;

/// <summary>guest 上报的一条事件（对应 /report?ev=…）。</summary>
public sealed record QemuReport(string Ev, long Id, int St, int Err, long Done, long Total, string Msg);

/// <summary>
/// 宿主控制端：guest（VM 内 harness）每秒 <c>GET /task</c> 取命令，用 <c>GET /report</c> 回传状态。
///
/// <para>用裸 <see cref="TcpListener"/> 实现最小 HTTP/1.0 服务而不是 HttpListener：
/// guest 侧是手写的 C HTTP 客户端（<c>GET … HTTP/1.0</c> + 读到 EOF），协议极简；
/// 同时避开 Windows http.sys 的 URL ACL（非管理员账户注册前缀会 Access Denied）。
/// 协议与实验装置 <c>build/ctrlserver2.py</c> 完全一致（已在全链路验证）。</para>
/// </summary>
public sealed class QemuControlServer : IDisposable
{
    /// <summary>收到 guest 上报（任何 ev）。</summary>
    public event Action<QemuReport>? ReportReceived;

    /// <summary>日志回调（含每笔命令下发/上报摘要）。</summary>
    public event Action<string>? Log;

    private readonly int _port;
    private readonly object _sync = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private string _pending = "NONE";
    private volatile TaskCompletionSource _firstPoll = NewTcs();

    public QemuControlServer(int port) => _port = port;

    /// <summary>guest 首次来取任务 = VM 环境已就绪的信号（每次 VM 启动前调 <see cref="ResetFirstPoll"/>）。</summary>
    public async Task<bool> WaitFirstPollAsync(TimeSpan timeout)
    {
        try { await _firstPoll.Task.WaitAsync(timeout).ConfigureAwait(false); return true; }
        catch (TimeoutException) { return false; }
    }

    public void ResetFirstPoll() => _firstPoll = NewTcs();

    /// <summary>设置下一条待取命令（"NONE" = 无任务；取走即复位，与 ctrlserver.py 语义一致）。</summary>
    public void SetCommand(string command)
    {
        lock (_sync) _pending = string.IsNullOrWhiteSpace(command) ? "NONE" : command;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        Log?.Invoke($"[qemu] 控制端就绪 http://127.0.0.1:{_port}");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { continue; }
            _ = Task.Run(() => HandleAsync(client), CancellationToken.None);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            try
            {
                var stream = client.GetStream();

                // 读请求头（到 \r\n\r\n 为止；guest 的请求只有两行）
                var buf = new byte[8192];
                var len = 0;
                while (len < buf.Length)
                {
                    var r = await stream.ReadAsync(buf.AsMemory(len), timeout.Token).ConfigureAwait(false);
                    if (r <= 0) break;
                    len += r;
                    if (IndexOfHeaderEnd(buf, len) >= 0) break;
                }
                if (len == 0) return;

                var head = Encoding.ASCII.GetString(buf, 0, len);
                var lineEnd = head.IndexOf('\n');
                if (lineEnd < 0) return;
                var requestLine = head[..lineEnd].Trim();
                // "GET /path?query HTTP/1.0"
                var parts = requestLine.Split(' ');
                if (parts.Length < 2 || parts[0] != "GET") { await RespondAsync(stream, "404 Not Found", "bad request").ConfigureAwait(false); return; }
                var target = parts[1];

                var q = target.IndexOf('?');
                var path = q < 0 ? target : target[..q];
                var query = q < 0 ? "" : target[(q + 1)..];

                switch (path)
                {
                    case "/task":
                    {
                        string cmd;
                        lock (_sync) { cmd = _pending; _pending = "NONE"; }
                        if (cmd != "NONE") Log?.Invoke($"[宿主] ↓ 下发: {cmd}");
                        _firstPoll.TrySetResult();
                        await RespondAsync(stream, "200 OK", cmd).ConfigureAwait(false);
                        return;
                    }
                    case "/report":
                    {
                        var args = ParseQuery(query);
                        var report = new QemuReport(
                            Get(args, "ev"), ParseLong(Get(args, "id")), ParseInt(Get(args, "st")),
                            ParseInt(Get(args, "err")), ParseLong(Get(args, "done")),
                            ParseLong(Get(args, "total")), Get(args, "msg"));
                        if (report.Ev != "status" || ShouldLogStatus(report))
                            Log?.Invoke($"[宿主] ↑ {report.Ev,-8} id={report.Id} st={report.St} err={report.Err} {report.Done}/{report.Total} {report.Msg}");
                        ReportReceived?.Invoke(report);
                        await RespondAsync(stream, "200 OK", "ok").ConfigureAwait(false);
                        return;
                    }
                    default:
                        await RespondAsync(stream, "404 Not Found", "?").ConfigureAwait(false);
                        return;
                }
            }
            catch { /* 单个连接异常不影响服务 */ }
        }
    }

    private TimeSpan _lastStatusLog = TimeSpan.MinValue;
    private long _lastStatusDone = -1;
    private bool ShouldLogStatus(QemuReport r)
    {
        var now = DateTime.UtcNow.TimeOfDay;
        if (r.Done != _lastStatusDone && (r.Done - _lastStatusDone) >= 8 * 1024 * 1024)
        {
            _lastStatusDone = r.Done; _lastStatusLog = now; return true;
        }
        return false;
    }

    private static async Task RespondAsync(NetworkStream stream, string status, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.0 {status}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
        await stream.WriteAsync(bytes).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static int IndexOfHeaderEnd(byte[] buf, int len)
    {
        for (var i = 0; i + 3 < len; i++)
            if (buf[i] == 13 && buf[i + 1] == 10 && buf[i + 2] == 13 && buf[i + 3] == 10) return i;
        return -1;
    }

    /// <summary>解析查询串（guest 用 url_encode 编码：空格为 '+'，其余标准百分号编码）。</summary>
    private static Dictionary<string, string> ParseQuery(string query)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = kv.IndexOf('=');
            var k = i < 0 ? kv : kv[..i];
            var v = i < 0 ? "" : kv[(i + 1)..];
            d[Uri.UnescapeDataString(k.Replace('+', ' '))] = Uri.UnescapeDataString(v.Replace('+', ' '));
        }
        return d;
    }

    private static string Get(Dictionary<string, string> d, string key) => d.TryGetValue(key, out var v) ? v : "";
    private static long ParseLong(string s) => long.TryParse(s, out var v) ? v : 0;
    private static int ParseInt(string s) => int.TryParse(s, out var v) ? v : 0;

    private static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _cts?.Dispose();
        _listener = null;
    }
}
