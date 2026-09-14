using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CatClawVideo.Core.Services;

/// <summary>
/// 「猫爪互联」本机服务：让局域网内的手机与本机配对 / 之后做遥控与记录同步。
///
/// <para><b>用途</b>：PC 端跑不了 Guard 加固源（解密器是安卓 ARM 原生库），
/// 需要把手机当解析节点。配对就是把手机节点的地址 + 口令登记到本机：
/// PC 显示二维码（内含本机地址）→ 手机扫码 → 手机 POST <c>/pair</c> 过来 → 写入配置。</para>
///
/// <para><b>为什么放 Core</b>：只用 <see cref="TcpListener"/>，不依赖任何平台 API，
/// 桌面与 Android 都能跑（Android 侧已用同一套写法实现过两个本地服务）。</para>
///
/// <para>协议（均为 JSON）：
/// <list type="bullet">
///   <item><c>GET /ping</c> → <c>{"ok":true,"app":"catclaw","name":"&lt;设备名&gt;"}</c></item>
///   <item><c>POST /pair</c>，体 <c>{"node":"http://ip:port","token":"…","name":"…"}</c>
///        → 登记为解析节点，返回 <c>{"ok":true}</c></item>
///   <item><c>GET /state</c> → 当前节点配置，便于手机端回显</item>
/// </list></para>
/// </summary>
public sealed class LinkServer
{
    public const int DefaultPort = 8900;

    private readonly int _port;
    private readonly Action<string>? _log;
    private readonly Func<string>? _deviceName;
    /// <summary>配对成功后回调（宿主用来刷新源列表 / 弹提示）</summary>
    private readonly Action<string, string?>? _onPaired;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public LinkServer(int port = DefaultPort, Action<string>? log = null,
        Func<string>? deviceName = null, Action<string, string?>? onPaired = null)
    {
        _port = port;
        _log = log;
        _deviceName = deviceName;
        _onPaired = onPaired;
    }

    public bool IsRunning => _listener is not null;
    public int Port => _port;

    public bool Start()
    {
        if (_listener is not null) return true;
        try
        {
            var l = new TcpListener(IPAddress.Any, _port);
            l.Start();
            _listener = l;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[互联] 启动失败（端口 {_port}）：{ex.Message}");
            return false;
        }

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _log?.Invoke($"[互联] ✅ 已就绪：http://{LanInfo.PrimaryIPv4()}:{_port}（设备名 {_deviceName?.Invoke() ?? "?"}）");
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

            // 读头部
            var head = new StringBuilder();
            var buf = new byte[8192];
            var headerEnd = -1;
            while (headerEnd < 0 && head.Length < 64 * 1024)
            {
                var n = await ns.ReadAsync(buf);
                if (n <= 0) return;
                head.Append(Encoding.ASCII.GetString(buf, 0, n));
                headerEnd = head.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
            }
            if (headerEnd < 0) return;

            var raw = head.ToString();
            var lines = raw[..headerEnd].Split("\r\n");
            var parts = lines[0].Split(' ');
            if (parts.Length < 2) return;
            var method = parts[0].ToUpperInvariant();
            var target = parts[1];
            var path = target;
            var q = target.IndexOf('?');
            if (q >= 0) path = target[..q];

            // Content-Length（本服务只有 /pair 带体）
            var contentLength = 0;
            foreach (var line in lines)
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(line[15..].Trim(), out var cl))
                    contentLength = cl;
            }

            var body = "";
            if (contentLength > 0)
            {
                var already = Encoding.UTF8.GetByteCount(raw[(headerEnd + 4)..]);
                var bodyBuf = new byte[contentLength];
                var got = 0;
                if (already > 0)
                {
                    var tail = Encoding.UTF8.GetBytes(raw[(headerEnd + 4)..]);
                    got = Math.Min(tail.Length, contentLength);
                    Array.Copy(tail, bodyBuf, got);
                }
                while (got < contentLength)
                {
                    var n = await ns.ReadAsync(bodyBuf.AsMemory(got, contentLength - got));
                    if (n <= 0) break;
                    got += n;
                }
                body = Encoding.UTF8.GetString(bodyBuf, 0, got);
            }

            var name = SafeDeviceName();

            switch (path)
            {
                case "/ping":
                    await WriteAsync(ns, 200, Json(new JsonObject
                    {
                        ["ok"] = true,
                        ["app"] = "catclaw",
                        ["name"] = name,
                        ["port"] = _port,
                    }));
                    return;

                case "/state":
                    await WriteAsync(ns, 200, Json(new JsonObject
                    {
                        ["ok"] = true,
                        ["node"] = Providers.RemoteSpiderNode.BaseUrl ?? "",
                        ["name"] = name,
                    }));
                    return;

                case "/pair":
                    {
                        if (method != "POST")
                        {
                            await WriteAsync(ns, 405, """{"ok":false,"error":"请用 POST"}""");
                            return;
                        }
                        var (node, token) = ParsePair(body);
                        if (string.IsNullOrWhiteSpace(node))
                        {
                            await WriteAsync(ns, 400, """{"ok":false,"error":"缺少 node 字段"}""");
                            return;
                        }
                        Providers.RemoteSpiderNode.Set(node, token);
                        _log?.Invoke($"[互联] ✅ 已配对：手机节点 {node}（口令{(string.IsNullOrEmpty(token) ? "无" : "有")}）");
                        try { _onPaired?.Invoke(node!, token); } catch { }
                        await WriteAsync(ns, 200, Json(new JsonObject { ["ok"] = true, ["node"] = node }));
                        return;
                    }

                default:
                    await WriteAsync(ns, 404, """{"ok":false,"error":"not found"}""");
                    return;
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[互联] 处理连接异常：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>兼容 {"node":…,"token":…} 与 {"url":…,"token":…} 两种键名</summary>
    private static (string? Node, string? Token) ParsePair(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            string? Get(params string[] keys)
            {
                foreach (var k in keys)
                    if (r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                        return v.GetString();
                return null;
            }
            return (Get("node", "url", "baseUrl")?.TrimEnd('/'), Get("token"));
        }
        catch
        {
            return (null, null);
        }
    }

    private string SafeDeviceName()
    {
        try { return _deviceName?.Invoke() ?? "catclaw"; }
        catch { return "catclaw"; }
    }

    private static string Json(JsonObject o) => o.ToJsonString();

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
}

/// <summary>
/// 局域网地址探测（展示配对二维码 / 提示用）。
///
/// <para>⚠️ 不能简单取「第一个 Up 的非回环 IPv4」——本机装了 WSL2 / Hyper-V 后，
/// 枚举常常先撞上 <c>vEthernet (WSL)</c> 这类虚拟网卡（172.16-31.x），
/// 手机根本连不上。这里做加权排序：<b>有默认网关</b>的最优先（虚拟网卡通常没有），
/// 再按网段与网卡名做加减。</para>
/// </summary>
public static class LanInfo
{
    private static readonly string[] VirtualMarkers =
    [
        "vethernet", "wsl", "hyper-v", "docker", "virtualbox", "vmware", "loopback",
        "bluetooth", "tap", "tun", "vpn", "radmin", "zerotier",
    ];

    /// <summary>按「像真实局域网」排序后的候选地址</summary>
    public static List<string> AllIPv4()
    {
        var scored = new List<(string Ip, int Score)>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                var props = ni.GetIPProperties();
                var hasGateway = props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));

                var name = (ni.Name + " " + ni.Description).ToLowerInvariant();
                var isVirtual = VirtualMarkers.Any(m => name.Contains(m));

                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = ua.Address.ToString();
                    if (ip.StartsWith("169.254.")) continue;   // APIPA
                    if (ip.StartsWith("127.")) continue;

                    var score = 0;
                    if (hasGateway) score += 100;
                    if (isVirtual) score -= 60;

                    if (ip.StartsWith("192.168.")) score += 20;
                    else if (ip.StartsWith("10.")) score += 10;
                    else if (IsHyperVOrWslRange(ip)) score -= 40;

                    scored.Add((ip, score));
                }
            }
        }
        catch { }

        return scored.OrderByDescending(x => x.Score).Select(x => x.Ip).Distinct().ToList();
    }

    /// <summary>本机首选局域网 IPv4；取不到返回 "127.0.0.1"</summary>
    public static string PrimaryIPv4()
    {
        var all = AllIPv4();
        return all.Count > 0 ? all[0] : "127.0.0.1";
    }

    /// <summary>172.16.0.0/12 —— WSL2 / Hyper-V 默认网段</summary>
    private static bool IsHyperVOrWslRange(string ip)
    {
        var parts = ip.Split('.');
        return parts.Length == 4 && parts[0] == "172"
               && int.TryParse(parts[1], out var b) && b >= 16 && b <= 31;
    }
}
