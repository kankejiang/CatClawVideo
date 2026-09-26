using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;

namespace CatClawVideo.Core.Services;

/// <summary>一条 DoH 配置（对位 <c>OkGoHelper.dnsConfigJson</c> 与订阅里下发的 <c>doh</c> 数组项）。</summary>
public sealed record DohEndpoint(string Name, string Url, string[] BootstrapIps);

/// <summary>
/// DNS over HTTPS（对位 TVBox <c>OkGoHelper.initDnsOverHttps</c> + <c>HawkConfig.DOH_URL/DOH_JSON</c>）。
///
/// <para>语义逐条照抄：选择项 <b>0 = 关闭</b>，非 0 表示用列表里第 <c>selector-1</c> 项；
/// 订阅可以下发 <c>doh</c> 覆盖内置三家，且<b>配置变了就把选择退回「关闭」</b>
/// （否则用户上次选的「阿里」在新列表里可能已经是别家 —— TVBox 也是这么重置的）。</para>
///
/// <para>只给「配置/站点/直播/EPG」这类取配置的请求用（对位 OkGo 的作用域）。
/// <b>播放流不走它</b>：TVBox 特意把 IJK 的 DNS 指回本机，避免分片解析被外置 DNS 拖慢，
/// 这里的 <c>GoLiveProxy</c>/<c>SpiderProxyServer</c> 同理保持系统解析。</para>
/// </summary>
public static class Doh
{
    /// <summary>TVBox 内置的三家（订阅没带 doh 时的默认列表，值照抄）。</summary>
    public const string DefaultJson =
        "[{\"name\":\"腾讯\",\"url\":\"https://doh.pub/dns-query\"},"
        + "{\"name\":\"阿里\",\"url\":\"https://dns.alidns.com/dns-query\"},"
        + "{\"name\":\"360\",\"url\":\"https://doh.360.cn/dns-query\"}]";

    private static int _selector;
    private static string _configJson = "";

    /// <summary>0 = 关闭；n&gt;0 = 用 <see cref="Endpoints"/> 的第 n-1 项。</summary>
    public static int Selector
    {
        get => _selector;
        set
        {
            if (_selector == value) return;
            _selector = value;
            Changed?.Invoke();
        }
    }

    /// <summary>订阅下发的配置原文（空表示用 <see cref="DefaultJson"/>）。</summary>
    public static string ConfigJson
    {
        get => _configJson;
        set
        {
            var v = value ?? "";
            if (string.Equals(_configJson, v, StringComparison.Ordinal)) return;
            _configJson = v;
            Changed?.Invoke();
        }
    }

    /// <summary>选择或列表变了（宿主据此把选择持久化回去）。</summary>
    public static Action? Changed;

    /// <summary>诊断/台架用：最后一次解析的失败原因。</summary>
    public static string? LastError { get; private set; }

    /// <summary>最后一次 <see cref="Parse"/> 失败的原因（null = 成功）。配置来自用户/订阅，必须能被看见。</summary>
    public static string? ParseError { get; private set; }

    public static List<DohEndpoint> Endpoints => Parse(ConfigJson.Length > 0 ? ConfigJson : DefaultJson);

    /// <summary>当前生效的端点；关闭、越界或配置坏了都是 null（走系统 DNS）。</summary>
    public static DohEndpoint? Selected
    {
        get
        {
            if (Selector <= 0) return null;
            var list = Endpoints;
            var idx = Selector - 1;
            return idx < list.Count ? list[idx] : null;
        }
    }

    public static List<DohEndpoint> Parse(string json)
    {
        var list = new List<DohEndpoint>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var url = Str(e, "url");
                if (url.Length == 0) continue;
                var name = Str(e, "name");
                var ips = new List<string>();
                if (e.TryGetProperty("ips", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var ip in arr.EnumerateArray())
                        if (ip.ValueKind == JsonValueKind.String && ip.GetString() is { Length: > 0 } t) ips.Add(t);
                list.Add(new DohEndpoint(name.Length > 0 ? name : url, url, [.. ips]));
            }
        }
        catch (Exception ex)
        {
            // 配置不可信就退回内置列表，但**原因要留痕** —— 之前这里静默吞掉，
            // 台架里「Parse 出来是空表」查了才知道是 json 形态问题
            ParseError = ex.Message;
        }
        return list;
    }

    static string Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>
    /// 造一个按当前 DoH 选择解析域名的 HttpClient（关了就等价于系统 DNS）。
    /// <para>解析器每次查询都现读 <see cref="Selected"/>，所以设置里改了选择**不必重建 client**
    /// —— 各调用点的 Http 是 static readonly，重建不了。</para>
    /// </summary>
    public static HttpClient NewClient(double timeoutSeconds, string? userAgent = null)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            UseCookies = false,
        };
        handler.ConnectCallback = ConnectAsync;
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        if (!string.IsNullOrEmpty(userAgent)) client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return client;
    }

    /// <summary>建连接：DoH 解析（失败则回落系统 DNS）→ 按顺序逐个地址试连 → https 时套 SslStream。</summary>
    static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var host = ctx.DnsEndPoint.Host;
        var port = ctx.DnsEndPoint.Port;
        var selected = Selected;
        var isIp = IPAddress.TryParse(host, out var literal);

        var addresses = isIp ? [literal!] : Array.Empty<IPAddress>();
        if (!isIp && host.Length > 0)
        {
            // 单标签名/局域网名不外查；其余先问 DoH，空结果再回落系统解析（别把一次取配置打死）
            if (selected is not null && host.Contains('.'))
                addresses = await ResolveAsync(selected, host, ct).ConfigureAwait(false);
            if (addresses.Length == 0)
                addresses = await System.Net.Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        }
        if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);

        Exception? last = null;
        foreach (var ip in addresses)
        {
            var sock = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await sock.ConnectAsync(new IPEndPoint(ip, port), ct).ConfigureAwait(false);
                Stream stream = new NetworkStream(sock, ownsSocket: true);
                if (!string.Equals(ctx.InitialRequestMessage?.RequestUri?.Scheme, Uri.UriSchemeHttps,
                        StringComparison.OrdinalIgnoreCase))
                    return stream;

                var ssl = new System.Net.Security.SslStream(stream, leaveInnerStreamOpen: false);
                try
                {
                    await ssl.AuthenticateAsClientAsync(new System.Net.Security.SslClientAuthenticationOptions
                    {
                        TargetHost = host,          // SNI 与证书校验都用原始域名，不是刚连上的 IP
                        ApplicationProtocols =
                        [
                            System.Net.Security.SslApplicationProtocol.Http2,
                            System.Net.Security.SslApplicationProtocol.Http11,
                        ],
                    }, ct).ConfigureAwait(false);
                }
                catch
                {
                    ssl.Dispose();
                    throw;
                }
                return ssl;
            }
            catch (Exception ex)
            {
                last = ex;
                sock.Dispose();
            }
        }
        throw last ?? new SocketException((int)SocketError.HostUnreachable);
    }

    /// <summary>DoH 查询用的独立 client：它自己**必须**走系统 DNS，否则先有鸡还是有蛋。</summary>
    static readonly HttpClient QueryClient = new(new SocketsHttpHandler
    {
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
    })
    { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>
    /// 向某个端点查一个域名的 A 记录（Google/DoH 的 <c>application/dns-json</c> 口径）。
    /// <para>返回空表而不是抛异常：解析失败该由调用方回落系统 DNS，不该把一次取配置打死。</para>
    /// </summary>
    public static async Task<IPAddress[]> ResolveAsync(DohEndpoint endpoint, string host, CancellationToken ct = default)
    {
        LastError = null;
        try
        {
            var url = endpoint.Url + (endpoint.Url.Contains('?') ? '&' : '?')
                + "name=" + Uri.EscapeDataString(host) + "&type=1";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("application/dns-json");
            using var resp = await QueryClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"HTTP {(int)resp.StatusCode}";
                return [];
            }
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var got = ParseAnswer(body, out var why);
            LastError = why;
            return got;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return [];
        }
    }

    /// <summary>解析 DoH JSON 应答。<c>Status != 0</c>（NXDOMAIN 等）或没有 A 记录时返回空表。</summary>
    public static IPAddress[] ParseAnswer(string body, out string? error)
    {
        error = null;
        var result = new List<IPAddress>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("Status", out var st) && st.ValueKind == JsonValueKind.Number
                && st.GetInt32() != 0)
            {
                error = "DNS Status=" + st.GetInt32();
                return [];
            }
            if (!doc.RootElement.TryGetProperty("Answer", out var ans) || ans.ValueKind != JsonValueKind.Array)
            {
                error = "无 Answer 段";
                return [];
            }
            // type=1 才是 A 记录（IPv4）；顺手接受 data 里带尾点的写法
            foreach (var a in ans.EnumerateArray())
            {
                if (a.TryGetProperty("type", out var tp) && tp.ValueKind == JsonValueKind.Number && tp.GetInt32() != 1)
                    continue;
                var data = a.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String
                    ? d.GetString() ?? "" : "";
                if (IPAddress.TryParse(data.TrimEnd('.'), out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                    result.Add(ip);
            }
            if (result.Count == 0) error = "Answer 里没有 A 记录";
        }
        catch (Exception ex)
        {
            error = "应答不是合法 json：" + ex.Message;
        }
        return [.. result];
    }
}

// ——— 连接建立 ———
// .NET 11 已经没有 SocketsHttpHandler.NameResolver：老的「可派生 DnsResolver」在 .NET 10 被换成
// sealed 的 System.Net.DnsResolver，而 SocketsHttpHandler 只剩无参构造 + ConnectCallback。
// 所以 DoH 只能自己建连接：解析出 IP → 连上 → https 再套 SslStream，SNI/证书校验一律用**原始域名**。
//
// 代价（写在这是为了下次别踩）：走这条路就没有系统的 Happy Eyeballs 与代理链 —— 本应用这几个
// client 都是直连、不经系统代理，且下面按解析顺序逐个试地址，把多地址回退补回来。
