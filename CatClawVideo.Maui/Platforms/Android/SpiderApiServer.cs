using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CatClawVideo.Maui.Platforms.Android;

/// <summary>
/// 手机端「解析节点」HTTP 服务：把 spider 调用经局域网暴露给 PC。
///
/// <para><b>为什么是手机当节点</b>：Guard 加固 jar 的解密器是 ARM Android native，PC 跑不了；
/// 而加固又把解密后的 dex 写完即删（反 dump），没法导出给 PC。
/// 手机本来就能跑 Guard，把它的解析能力借给 PC 即可。</para>
///
/// <para><b>不烫</b>：只转发元数据（列表/详情/搜索），是取页 + 解析 JSON/HTML 的轻活；
/// 播放地址由 PC 直接拉取（多数 Guard 站点返回的是远程 m3u8/mp4）。
/// 真正会让手机发烫的媒体流中继，本服务不做。</para>
///
/// <para>协议：<c>GET /ping</c>、<c>GET /spider?site=&amp;method=home|category|detail|search|player&amp;…</c>；
/// 响应统一 <c>{"ok":bool,"result":"&lt;TVBox 协议 JSON&gt;"}</c>。
/// 用裸 TcpListener 而非 HttpListener —— 与荐片反代同一套已验证的写法，避免平台差异。</para>
/// </summary>
internal sealed class SpiderApiServer
{
    /// <summary>返回 TVBox 协议 JSON 字符串；抛异常会被转成 {"ok":false,"error":…}</summary>
    public delegate Task<string> Handler(string siteKey, string method, IReadOnlyDictionary<string, string> args);

    private readonly int _port;
    private readonly Handler _handler;
    private readonly Action<string>? _log;
    private readonly string? _token;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public SpiderApiServer(int port, Handler handler, string? token = null, Action<string>? log = null)
    {
        _port = port;
        _handler = handler;
        _token = token;
        _log = log;
    }

    public bool IsRunning => _listener is not null;

    /// <summary>绑定 0.0.0.0:_port 并接受局域网连接</summary>
    public bool Start()
    {
        if (_listener is not null) return true;
        try
        {
            var l = new TcpListener(IPAddress.Any, _port);
            l.Start();
            _listener = l;
        }
        catch (System.Exception ex)
        {
            _log?.Invoke($"[节点] 启动失败（端口 {_port}）：{ex.Message}");
            return false;
        }

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));

        var ips = LocalIPv4();
        var shown = ips.Count > 0 ? string.Join(" / ", ips.Select(ip => $"http://{ip}:{_port}")) : $"(局域网 IP 未取到，端口 {_port})";
        _log?.Invoke($"[节点] ✅ 解析节点已就绪：{shown}");
        return true;
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = Task.Run(() => HandleAsync(client), CancellationToken.None);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            client.NoDelay = true;
            await using var ns = client.GetStream();

            // 只读请求头（本服务只有 GET）
            var sb = new StringBuilder();
            var buf = new byte[8192];
            var end = -1;
            while (end < 0 && sb.Length < 32 * 1024)
            {
                var n = await ns.ReadAsync(buf);
                if (n <= 0) return;
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                end = sb.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
            }
            if (end < 0) return;

            var firstLine = sb.ToString()[..end].Split("\r\n")[0];
            var parts = firstLine.Split(' ');
            if (parts.Length < 2) return;

            var target = parts[1];
            var path = target;
            var query = "";
            var q = target.IndexOf('?');
            if (q >= 0) { path = target[..q]; query = target[(q + 1)..]; }
            var args = ParseQuery(query);

            // 鉴权（配置了 token 才校验；/ping 放行，方便 PC 侧探测节点是否在线）
            if (!string.IsNullOrEmpty(_token) && path != "/ping"
                && (!args.TryGetValue("token", out var t) || t != _token))
            {
                await WriteAsync(ns, 403, """{"ok":false,"error":"token 无效"}""");
                return;
            }

            if (path is "/ping" or "/")
            {
                await WriteAsync(ns, 200, $"{{\"ok\":true,\"result\":\"catclaw-node\",\"token\":{(string.IsNullOrEmpty(_token) ? "false" : "true")}}}");
                return;
            }

            if (path != "/spider")
            {
                await WriteAsync(ns, 404, """{"ok":false,"error":"not found"}""");
                return;
            }

            var site = args.TryGetValue("site", out var s) ? s : "";
            var method = args.TryGetValue("method", out var m) ? m : "";
            if (string.IsNullOrEmpty(site) || string.IsNullOrEmpty(method))
            {
                await WriteAsync(ns, 400, """{"ok":false,"error":"缺少 site 或 method"}""");
                return;
            }

            try
            {
                var result = await _handler(site, method, args);
                await WriteAsync(ns, 200, Envelope(true, result, null));
            }
            catch (System.Exception ex)
            {
                var msg = ex.GetType().Name + ": " + ex.Message;
                _log?.Invoke($"[节点] {site}.{method} 失败：{msg}");
                await WriteAsync(ns, 200, Envelope(false, null, msg));
            }
        }
        catch (System.Exception ex)
        {
            _log?.Invoke($"[节点] 处理连接异常：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return r;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) r[Uri.UnescapeDataString(pair)] = "";
            else r[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return r;
    }

    private static string Envelope(bool ok, string? result, string? error)
    {
        var payload = ok
            ? $"{{\"ok\":true,\"result\":{System.Text.Json.JsonSerializer.Serialize(result ?? "{}")}}}"
            : $"{{\"ok\":false,\"error\":{System.Text.Json.JsonSerializer.Serialize(error ?? "")}}}";
        return payload;
    }

    private static async Task WriteAsync(NetworkStream ns, int status, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "ERR")}\r\n"
                 + "Content-Type: application/json; charset=utf-8\r\n"
                 + $"Content-Length: {body.Length}\r\n"
                 + "Connection: close\r\n\r\n";
        await ns.WriteAsync(Encoding.ASCII.GetBytes(head));
        await ns.WriteAsync(body);
    }

    /// <summary>本机局域网 IPv4（排除回环）</summary>
    private static List<string> LocalIPv4()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in global::System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != global::System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == global::System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = ua.Address.ToString();
                    if (ip.StartsWith("169.254.")) continue;   // link-local
                    list.Add(ip);
                }
            }
        }
        catch { }
        return list;
    }
}
