using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CatClawVideo.Core.Services.QemuThunder;

/// <summary>
/// 给 ART guest 供 jar 的最小 HTTP 文件服务（回环监听，guest 经 slirp 用 <c>10.0.2.2:&lt;port&gt;</c> 取）。
///
/// <para><b>为什么需要它</b>：guest 里读不到宿主的盘，而订阅里的 jar 是开放集合 ——
/// 不能像迅雷/Guard 那样把文件烧进 initrd。所以约定：桥的 <c>rawJar</c> 字段在 ART 模式下传 URL，
/// guest 侧 <c>bridge.Art.materialize()</c> 取回归档到 <c>&lt;data.dir&gt;/art/inbox/</c> 再装载。</para>
///
/// <para>用裸 <see cref="TcpListener"/> 而不是 <c>HttpListener</c>：免 urlacl/权限问题，
/// 而且客户端是我们自己（<c>java.net.URL.openStream</c>），协议面只需「GET → 一段 200 响应」。</para>
/// </summary>
public sealed class ArtJarServer : IDisposable
{
    private readonly TcpListener _l;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> jars = new();

    /// <summary>回环监听端口（guest 侧连 <c>10.0.2.2:Port</c>）。</summary>
    public int Port { get; }

    public ArtJarServer(Action<string>? log = null)
    {
        _l = new TcpListener(IPAddress.Loopback, 0);
        _l.Start();
        Port = ((IPEndPoint)_l.LocalEndpoint).Port;
        _ = Task.Run(() => LoopAsync(log));
    }

    /// <summary>登记一个 jar 并给出 guest 可取的 URL（key 通常是 jar 地址的 SHA256 前 24 位）。</summary>
    public string UrlFor(string key, string localPath)
    {
        jars[key] = localPath;
        return $"http://10.0.2.2:{Port}/jar/{key}";
    }

    private async Task LoopAsync(Action<string>? log)
    {
        while (true)
        {
            TcpClient c;
            try { c = await _l.AcceptTcpClientAsync().ConfigureAwait(false); }
            catch { return; }                       // Dispose/停机
            _ = Task.Run(async () =>
            {
                try
                {
                    using (c)
                    {
                        var s = c.GetStream();
                        var buf = new byte[1024];
                        int n = await s.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false);
                        string head = Encoding.ASCII.GetString(buf, 0, Math.Max(0, n));
                        string? path = null;
                        var parts = head.Split(' ');
                        if (parts.Length >= 2 && parts[0] == "GET") path = parts[1];
                        var key = path?.StartsWith("/jar/") == true ? path[5..] : null;
                        var file = key is not null ? jars[key] : null;
                        if (file is null || !File.Exists(file))
                        {
                            var body404 = Encoding.UTF8.GetBytes("no such jar");
                            await WriteAsync(s, 404, "text/plain", body404).ConfigureAwait(false);
                            log?.Invoke($"[art-jar] 404 {path}");
                            return;
                        }
                        var data = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
                        await WriteAsync(s, 200, "application/octet-stream", data).ConfigureAwait(false);
                        log?.Invoke($"[art-jar] 供给 {key} {data.Length}B");
                    }
                }
                catch { /* guest 断开/半关闭，忽略 */ }
            });
        }
    }

    private static async Task WriteAsync(NetworkStream s, int status, string ctype, byte[] body)
    {
        // 状态行必须带数字码 + 原因短语：Android 侧 Art.materialize 用 java.net.URL，
        // 它走 com.android.okhttp 的严格解析器，"HTTP/1.1 OK" 会被判
        // ProtocolException: Unexpected status line（2026-09-25 实测）。
        var reason = status == 200 ? "OK" : "Not Found";
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: {ctype}\r\n"
            + $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await s.WriteAsync(head, 0, head.Length).ConfigureAwait(false);
        await s.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
        await s.FlushAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        try { _l.Stop(); } catch { }
    }
}
