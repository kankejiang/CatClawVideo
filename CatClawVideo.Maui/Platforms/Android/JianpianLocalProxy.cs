using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CatClawVideo.Maui.Platforms.Android;

/// <summary>
/// 荐片本地反代：在 <c>127.0.0.1:9978…9999</c>（spider 扫描的那段）上监听，
/// 把请求原样转发给真正的 P2P httpd（<c>libp2p.so</c> 的 <c>doxstarthttpd</c>，实测落在 8087+）。
///
/// <para><b>为什么需要</b>：荐片 spider 的 <c>adjustPort</c> 只扫 9978…9999，
/// 而 P2P 引擎自身的 httpd 端口不在这段里，于是 spider 判定「端口检测失败」并拼出
/// 端口位为空的地址（→ 播放器 <c>Source error</c>）。TVBox 侧能成立是因为它另有
/// NanoHTTPD 的 <c>RemoteServer</c> 常驻 9978，恰好被 spider 探到。</para>
///
/// <para>本类同时充当**探针**：把 spider 发来的每条请求记进 bt.log，
/// 便于在协议不明时直接观察它的真实需求。</para>
/// </summary>
internal sealed class JianpianLocalProxy
{
    private readonly int _upstreamPort;
    private readonly Action<string>? _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly List<TcpListener> _listeners = [];
    private CancellationTokenSource? _cts;

    public JianpianLocalProxy(int upstreamPort, Action<string>? log = null)
    {
        _upstreamPort = upstreamPort;
        _log = log;
    }

    /// <summary>监听端口（0 = 未启动）</summary>
    public int Port { get; private set; }

    /// <summary>
    /// 把 <b>9978…9999 整段</b>都监听上。
    /// <para>只占一个端口不够：spider 的 adjustPort 会从 9978 一路扫到 9999，
    /// 只要中途某个端口被拒它就会把这次失败当作致命错误抛出
    /// （真机实测：9978 探成功后继续扫 9979 被拒 → ConnectException 直接冒到 playerContent）。
    /// 整段接管后扫描必然全通过。</para>
    /// </summary>
    public bool Start()
    {
        if (Port > 0) return true;
        var first = 0;
        for (var p = 9978; p <= 9999; p++)
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, p);
                l.Start();
                _listeners.Add(l);
                if (first == 0) first = p;
            }
            catch
            {
                // 该端口被占：跳过即可，spider 扫到它会失败（通常不是我们占的）
            }
        }
        if (_listeners.Count == 0)
        {
            _log?.Invoke("[荐片] 反代启动失败：9978…9999 全部被占用");
            return false;
        }

        Port = first;
        _cts = new CancellationTokenSource();
        foreach (var l in _listeners)
        {
            var listener = l;
            _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
        }
        _log?.Invoke($"[荐片] 本地反代已监听 127.0.0.1:9978…9999（{_listeners.Count} 个端口）→ P2P httpd :{_upstreamPort}");
        return true;
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        foreach (var l in _listeners)
        {
            try { l.Stop(); } catch { }
        }
        _listeners.Clear();
        Port = 0;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = Task.Run(() => HandleAsync(client), CancellationToken.None);
        }
    }

    /// <summary>/proxy 回调：交给爬虫自身的 proxy(Map) 生成响应（由 MauiProgram 绑定到 DexSpiderRuntime）</summary>
    public Func<IDictionary<string, string>, CancellationToken, Task<(int Status, string Mime, byte[]? Body)?>>? ProxyHandler { get; set; }

    private static Dictionary<string, string> ParseQuery(string target)
    {
        var r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var q = target.IndexOf('?');
        if (q < 0 || q == target.Length - 1) return r;
        foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) r[Uri.UnescapeDataString(pair)] = "";
            else r[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return r;
    }

    private static async Task WriteAsync(NetworkStream ns, int status, string mime, byte[]? body)
    {
        var bytes = body ?? [];
        var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "ERR")}\r\n"
                 + $"Content-Type: {mime}\r\n"
                 + $"Content-Length: {bytes.Length}\r\n"
                 + "Connection: close\r\n\r\n";
        await ns.WriteAsync(Encoding.ASCII.GetBytes(head));
        if (bytes.Length > 0) await ns.WriteAsync(bytes);
    }

    /// <summary>极简 HTTP/1.1：只解析请求头，整包转发给上游，再把响应原样写回（含 Range 与 206）</summary>
    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            client.NoDelay = true;
            await using var ns = client.GetStream();

            // ① 读请求头（直到空行）
            var head = new StringBuilder();
            var buf = new byte[4096];
            var delim = -1;
            while (delim < 0 && head.Length < 64 * 1024)
            {
                var n = await ns.ReadAsync(buf);
                if (n <= 0) return;
                head.Append(Encoding.ASCII.GetString(buf, 0, n));
                delim = head.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
            }
            if (delim < 0) return;

            var raw = head.ToString();
            var lines = raw[..delim].Split("\r\n");
            if (lines.Length == 0) return;

            var parts = lines[0].Split(' ');
            if (parts.Length < 2) return;
            var method = parts[0];
            var target = parts[1];

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < lines.Length; i++)
            {
                var c = lines[i].IndexOf(':');
                if (c > 0) headers[lines[i][..c].Trim()] = lines[i][(c + 1)..].Trim();
            }

            // spider 的真实需求全在这里 —— 这是协议不明时最有价值的一行
            _log?.Invoke($"[荐片·反代] {method} {target}"
                + (headers.TryGetValue("Range", out var rg) ? $"  Range={rg}" : "")
                + (headers.TryGetValue("User-Agent", out var ua) ? $"  UA={ua}" : ""));

            // ── /proxy 是**荐片协议自己的端点**（`do=` 参数族），不是 P2P 引擎的文件接口。
            // TVBox 的做法：本地 HTTP 服务器收到 /proxy?do=… 后回调 **spider 自己的 proxy()**
            // 方法（ApiConfig.proxyLocal → spider.proxy(param)），响应体由爬虫生成。
            // 真机实测：宿主直接回 200 空体能让 adjustPort 走成功分支，但握手数据不对，
            // playerContent 最终仍拼出空端口地址 → 播放器 invalid port: -1。
            if (target.StartsWith("/proxy", StringComparison.OrdinalIgnoreCase))
            {
                var query = ParseQuery(target);
                _log?.Invoke($"[荐片·反代] /proxy 回调爬虫 proxy()：{target}");
                if (ProxyHandler is not null)
                {
                    var rs = await ProxyHandler(query, CancellationToken.None).ConfigureAwait(false);
                    if (rs is { } v)
                    {
                        await WriteAsync(ns, v.Status, v.Mime, v.Body).ConfigureAwait(false);
                        _log?.Invoke($"[荐片·反代] {method} {target} → {v.Status}（爬虫），{v.Body?.Length ?? 0}B");
                        return;
                    }
                }
                _log?.Invoke("[荐片·反代] 无 proxy 处理器，回 200 空体");
                await WriteAsync(ns, 200, "text/plain", null).ConfigureAwait(false);
                return;
            }

            // ② 转发到上游 P2P httpd
            var upUrl = $"http://127.0.0.1:{_upstreamPort}{target}";
            using var req = new HttpRequestMessage(new HttpMethod(method), upUrl);
            if (headers.TryGetValue("Range", out var range)) req.Headers.TryAddWithoutValidation("Range", range);
            if (headers.TryGetValue("User-Agent", out var agent)) req.Headers.TryAddWithoutValidation("User-Agent", agent);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            await using var body = await resp.Content.ReadAsStreamAsync();

            // ③ 写回响应
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append((int)resp.StatusCode).Append(' ').Append(resp.ReasonPhrase).Append("\r\n");
            var hasLength = false;
            foreach (var h in resp.Headers)
            {
                foreach (var v in h.Value)
                {
                    sb.Append(h.Key).Append(": ").Append(v).Append("\r\n");
                }
            }
            foreach (var h in resp.Content.Headers)
            {
                if (h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) hasLength = true;
                foreach (var v in h.Value)
                {
                    sb.Append(h.Key).Append(": ").Append(v).Append("\r\n");
                }
            }
            if (!hasLength)
                sb.Append("Transfer-Encoding: chunked\r\n");
            sb.Append("Connection: close\r\n\r\n");

            var headBytes = Encoding.ASCII.GetBytes(sb.ToString());
            await ns.WriteAsync(headBytes);

            var copy = new byte[64 * 1024];
            int read;
            while ((read = await body.ReadAsync(copy)) > 0)
            {
                if (hasLength)
                {
                    await ns.WriteAsync(copy.AsMemory(0, read));
                }
                else
                {
                    await ns.WriteAsync(Encoding.ASCII.GetBytes($"{read:X}\r\n"));
                    await ns.WriteAsync(copy.AsMemory(0, read));
                    await ns.WriteAsync(Encoding.ASCII.GetBytes("\r\n"));
                }
            }
            if (!hasLength) await ns.WriteAsync(Encoding.ASCII.GetBytes("0\r\n\r\n"));
            _log?.Invoke($"[荐片·反代] {method} {target} → {(int)resp.StatusCode} 完成");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[荐片·反代] 处理失败：{ex.GetType().Name}: {ex.Message}");
        }
    }
}
