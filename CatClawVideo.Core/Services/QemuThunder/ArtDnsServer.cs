using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CatClawVideo.Core.Services.QemuThunder;

/// <summary>
/// 给 ART guest 用的最小 DNS 转发（宿主侧）。
///
/// <para><b>为什么需要它</b>（2026-09-25 实测，四步定位）：
/// ① Android 9 的 bionic 不读 <c>/etc/resolv.conf</c>，也不读 <c>net.dns1</c> 属性 —— 它只认 netd
/// （<c>/dev/socket/fwmarkd</c>）给的域名服务器，本 guest 没有 netd ⇒ 任何 Java 层解析都是
/// <c>Unable to resolve host</c>；② slirp 自带的 <c>10.0.2.3:53</c> 实测回 ICMP port-unreachable
/// （errno=ECONNREFUSED 直接冒到 <c>android_getaddrinfo failed</c>）；
/// ③ 而 <b>guest→宿主的 TCP 是通的</b>（桥的连接、jar 的取回都走它）。</para>
///
/// <para>所以：域名解析交给宿主。guest 里 <c>proppreload.so</c> 的 dnsshim 接管
/// <c>android_getaddrinfofornet</c>（libjavacore 唯一的解析入口），真实现失败时连这里问一句。
/// 协议两行文本就够（两端都是自己人）：<c>"Q &lt;host&gt;\n" → "A &lt;ip&gt; [&lt;ip&gt;…]\n" / "NX\n"。</para>
///
/// <para>端口经 kernel cmdline 的 <c>ctrl=</c> 告诉 guest（<c>QemuHostRuntime</c> 现成的那条），
/// guest 的 <c>/init</c> 把它导成 <code>CATCLAW_DNS=10.0.2.2:&lt;port&gt;</code>。</para>
/// </summary>
public sealed class ArtDnsServer : IDisposable
{
    private readonly TcpListener _l;
    private readonly Action<string>? _log;
    private readonly Task _loop;

    public int Port { get; }

    public ArtDnsServer(Action<string>? log = null)
    {
        _log = log;
        _l = new TcpListener(IPAddress.Loopback, 0);
        _l.Start();
        Port = ((IPEndPoint)_l.LocalEndpoint).Port;
        _loop = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        while (true)
        {
            TcpClient c;
            try { c = await _l.AcceptTcpClientAsync().ConfigureAwait(false); }
            catch { return; }
            _ = Task.Run(() => ServeAsync(c));
        }
    }

    private async Task ServeAsync(TcpClient c)
    {
        using (c)
        {
            try {
                var s = c.GetStream();
                var buf = new byte[256];
                int n = await s.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false);
                var line = Encoding.ASCII.GetString(buf, 0, Math.Max(0, n)).Trim();
                if (!line.StartsWith("Q ")) return;
                var host = line[2..].Split(' ', '\t')[0];
                byte[] answer;
                try {
                    var ips = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork).ConfigureAwait(false);
                    answer = Encoding.ASCII.GetBytes(ips.Length > 0
                        ? "A " + string.Join(" ", ips.Select(x => x.ToString())) + "\n" : "NX\n");
                } catch {
                    answer = Encoding.ASCII.GetBytes("NX\n");
                }
                await s.WriteAsync(answer, 0, answer.Length).ConfigureAwait(false);
                await s.FlushAsync().ConfigureAwait(false);
            } catch { /* guest 断开 */ }
        }
    }

    public void Dispose()
    {
        try { _l.Stop(); } catch { }
        _ = _loop;
    }
}
