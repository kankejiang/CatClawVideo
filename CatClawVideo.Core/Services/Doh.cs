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

    // 解析结果按「配置原文」缓存：Endpoints 在每条连接的 ConnectCallback 里都会被摸一遍
    // （Selected → Endpoints），每次都 JsonDocument.Parse 一遍纯属浪费。
    private static string _parsedJson = "\0";   // 不可能等于任何真实配置的哨兵
    private static List<DohEndpoint> _parsedList = [];

    public static List<DohEndpoint> Endpoints
    {
        get
        {
            var json = ConfigJson.Length > 0 ? ConfigJson : DefaultJson;
            if (!string.Equals(_parsedJson, json, StringComparison.Ordinal))
            {
                _parsedList = Parse(json);
                _parsedJson = json;
            }
            return [.. _parsedList];   // 拷贝出去，防调用方改写污染缓存
        }
    }

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
    ///
    /// <para><b>默认请求版本设成「2.0 或更低」</b>：这样 <c>GetAsync</c>/<c>GetStringAsync</c> 这些便捷方法
    /// 才会去要 h2（<see cref="Alpn"/> 依请求版本决定 ALPN，所以拿不到 h2 时自动落回 1.1）。
    /// 注意这只覆盖便捷方法 —— <c>SendAsync(req)</c> 用的是请求自己的版本，那条路上 h2 要靠调用方显式设。
    /// 不设默认值也不会坏（1.1 一律安全），只是白丢掉多路复用。</para>
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
        // 实测只作用于 GetAsync/GetStringAsync 这类便捷方法，SendAsync(req) 不继承（见上）
        client.DefaultRequestVersion = HttpVersion.Version20;
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
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
                        ApplicationProtocols = Alpn(ctx),
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

    /// <summary>
    /// ALPN 必须跟随「这条连接要跑的 HTTP 版本」。
    /// <para>写死 <c>[h2, http/1.1]</c> 会静默打死所有 https 取配置：<c>ConnectCallback</c> 报完 ALPN 就交了流，
    /// 而 .NET 仍按请求自己的 <c>Version</c> 发报文（默认 1.1，且 <c>SendAsync(req)</c> 不继承
    /// <see cref="HttpClient.DefaultRequestVersion"/>），不会因为协商到 h2 就改口 —— 于是把 HTTP/1.1
    /// 的明文报文写进 h2 连接，服务器直接关流：
    /// <c>HttpConnection.FillAsync → The response ended prematurely</c>，
    /// 外面包成用户看到的 <c>HttpRequestException: An error occurred while sending the request.</c>。</para>
    /// <para>实测（2026-09-26，DoH 关闭以排除 DNS 变量）：ALPN 含 h2 时
    /// <c>https://dns.alidns.com/resolve?...</c> 上「未设版本的请求」和「1.1/OrLower」都 100% 失败，
    /// 只有「2.0/OrLower」拿到 <c>v2.0 200 253B</c>；改成按请求版本出 ALPN 后四种发法全 200
    /// （未设版本 → v1.1 251B、2.0/OrLower → v2.0 253B、1.1/OrLower → v1.1 251B、GetAsync → 251B/41ms）。
    /// 系统默认栈为什么不出事：它的 ALPN 本来就是按请求版本策略生成的，不会自相矛盾。</para>
    /// </summary>
    static List<System.Net.Security.SslApplicationProtocol> Alpn(SocketsHttpConnectionContext ctx) =>
        ctx.InitialRequestMessage?.Version.Major >= 2
            ? [
                System.Net.Security.SslApplicationProtocol.Http2,
                System.Net.Security.SslApplicationProtocol.Http11,
            ]
            : [System.Net.Security.SslApplicationProtocol.Http11];

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
    /// <para><c>/dns-query</c> 与 <c>/resolve</c> 两个路径都要认：实测（2026-09-26）内置三家里
    /// <c>/resolve</c> 全 200，而 TVBox 抄来的 <c>/dns-query</c> 只有 doh.pub 收 dns-json，
    /// 阿里与 360 一律 <c>HTTP 400</c> —— 不改路径就等于「选阿里/360 时 DoH 形同关闭」。</para>
    /// </summary>
    public static async Task<IPAddress[]> ResolveAsync(DohEndpoint endpoint, string host, CancellationToken ct = default)
    {
        LastError = null;
        var key = (endpoint.Url, host);

        // 缓存/熔断前置：命中正缓存直接返回；熔断冷却中跳过 DoH（调用方回落系统 DNS），
        // 别让坏端点把每个新连接都拖满超时。
        if (ResolveCache.TryGetValue(key, out var hit))
        {
            if (hit.ExpiresUtc > DateTime.UtcNow) return hit.Ips;
            ResolveCache.TryRemove(key, out _);
        }
        if (IsBroken(endpoint.Url)) return [];

        // ⚠ 中文域名必须先转 punycode 再进 DNS 查询：协议里只有 ASCII 标签。
        // 实测 alidns 对 name=www.英格里希嗷呜.top 回 Status 3（NXDOMAIN，Question 里那串是 UTF-8
        // 字节被当成标签），换成 www.xn--nsr30bl8nb2kyt7a8vs.top 就是 Status 0 带答案。
        // 少了这一步 DoH 对中文域名就是白问（不报错，因为空结果还会回落系统 DNS）。
        var query = "name=" + Uri.EscapeDataString(IdnAscii(host)) + "&type=1";
        string With(string url) => url + (url.Contains('?') ? "&" : "?") + query;

        // 路径探测记忆：此前每次解析都把 /dns-query ↔ /resolve 两个路径轮着打一遍，
        // 靠 400/404 判定哪个能用 —— 记住试通的那个之后，后续解析只打一次。
        // 记忆的路径哪天也开始回 400/404（端点行为变了）→ 清掉记忆重走双路径探测。
        string? memo = PathMemo.TryGetValue(endpoint.Url, out var m) ? m : null;
        List<string> paths = memo is not null ? [memo] : [.. Paths(endpoint.Url).Select(With)];

        for (var attempt = 0; attempt < paths.Count; attempt++)
        {
            var url = paths[attempt];
            var (ips, code, err) = await QueryOnce(url, ct).ConfigureAwait(false);
            LastError = err;
            if (ips.Length > 0)
            {
                CacheResolve(endpoint.Url, host, ips);
                NoteEndpointOk(endpoint.Url);
                if (memo is null || url != memo) PathMemo[endpoint.Url] = url;
                return ips;
            }
            if (code is 400 or 404)
            {
                // 「这个路径不认 dns-json」→ 换路径；但若走的是记忆路径，先清记忆再补一轮双路径
                if (attempt == 0 && memo is not null && paths.Count == 1)
                {
                    PathMemo.TryRemove(endpoint.Url, out _);
                    memo = null;
                    paths = [.. Paths(endpoint.Url).Select(With)];
                }
                continue;
            }
            // 其余（NXDOMAIN、超时…）换路径也没用：负缓存 30s；超时/连不上额外给端点健康度减分
            if (code is null) NoteEndpointFail(endpoint.Url);
            CacheResolve(endpoint.Url, host, []);
            return [];
        }
        return [];
    }

    /// <summary>同一个端点下 dns-json 可能落在哪个路径上（先 /resolve，再回到配置原样）。</summary>
    static IEnumerable<string> Paths(string url)
    {
        if (!url.Contains("/dns-query", StringComparison.Ordinal) && !url.Contains("/resolve", StringComparison.Ordinal))
            return [url];
        var other = url.Contains("/dns-query", StringComparison.Ordinal)
            ? url.Replace("/dns-query", "/resolve", StringComparison.Ordinal)
            : url.Replace("/resolve", "/dns-query", StringComparison.Ordinal);
        return [other, url];
    }

    // ═══════════ 解析缓存 / 端点熔断（性能：ConnectCallback 每个新 TCP 连接都进来）═══════════
    //
    // 此前每次建连都现查 DoH：连接池过期、站点多域名、订阅轮询……每个新连接都付一次
    // DoH HTTPS 往返；端点被墙/坏时更是每连接先白等满 8s 才回落系统 DNS（实测把
    // 首页首载/切站/取配置整片拖慢的公共层）。桌面端同域名隔几分钟就要重连一次，
    // 不缓存等于把解析成本重复付无限次。
    //
    // ── 正缓存：成功结果 5 分钟（A 记录短 TTL 场景下 5min 足够新鲜，TVBox 的 OkGo 侧
    //    DnsCache 也是分钟级）
    // ── 负缓存：查到了但没结果（NXDOMAIN 等）30 秒，避免对坏域名连环打 DoH
    // ── 熔断：端点「连不上/超时」连续 3 次 → 冷却 3 分钟内直接跳过 DoH 走系统 DNS
    //    （与 NXDOMAIN 区分：后者服务器是通的，不该熔断端点本身）

    private sealed record ResolveEntry(IPAddress[] Ips, DateTime ExpiresUtc);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Endpoint, string Host), ResolveEntry>
        ResolveCache = new();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Fails, DateTime BlockedUntilUtc)>
        EndpointHealth = new();

    /// <summary>同一端点下试通的查询路径记忆（/resolve vs /dns-query）：否则不认该路径的端点
    /// 每次解析都要白打一个 400/404 才换到对的那个。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> PathMemo = new();

    private static readonly TimeSpan PositiveTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BreakCooldown = TimeSpan.FromMinutes(3);
    private const int BreakThreshold = 3;

    private static void CacheResolve(string endpoint, string host, IPAddress[] ips)
    {
        var ttl = ips.Length > 0 ? PositiveTtl : NegativeTtl;
        ResolveCache[(endpoint, host)] = new ResolveEntry(ips, DateTime.UtcNow + ttl);
    }

    /// <summary>DoH 端点当前是否处于熔断冷却中（true = 跳过 DoH，让调用方直接回落系统 DNS）。</summary>
    private static bool IsBroken(string endpoint) =>
        EndpointHealth.TryGetValue(endpoint, out var h) && h.BlockedUntilUtc > DateTime.UtcNow;

    private static void NoteEndpointFail(string endpoint)
    {
        var now = DateTime.UtcNow;
        EndpointHealth.TryGetValue(endpoint, out var h);
        var fails = now > h.BlockedUntilUtc ? 1 : h.Fails + 1;   // 冷却结束后重新计数
        EndpointHealth[endpoint] = fails >= BreakThreshold ? (fails, now + BreakCooldown) : (fails, DateTime.MinValue);
    }

    private static void NoteEndpointOk(string endpoint) => EndpointHealth[endpoint] = (0, DateTime.MinValue);

    /// <summary>发一次 dns-json 查询。<c>code</c> 为 null 表示压根没拿到 HTTP 状态。</summary>
    static async Task<(IPAddress[] ips, int? code, string? err)> QueryOnce(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("application/dns-json");
            using var resp = await QueryClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return ([], (int)resp.StatusCode, $"HTTP {(int)resp.StatusCode}");
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (ParseAnswer(body, out var why), (int)resp.StatusCode, why);
        }
        catch (Exception ex)
        {
            return ([], null, ex.Message);
        }
    }

    /// <summary>
    /// 域名转 DNS 查询用的 ASCII（punycode）。已经是 ASCII 的原样返回（幂等 —— .NET 有时已经把
    /// <c>HttpRequestMessage</c> 的 host 归一化成 <c>xn--</c> 形式再交给 ConnectCallback）。
    /// 转换失败（非法字符）时返回原串，让查询自己失败并由调用方回落系统 DNS，比抛异常好。
    /// </summary>
    static string IdnAscii(string host)
    {
        if (host.Length == 0 || host.All(c => c < 128)) return host;
        try { return new System.Globalization.IdnMapping().GetAscii(host); }
        catch { return host; }
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
