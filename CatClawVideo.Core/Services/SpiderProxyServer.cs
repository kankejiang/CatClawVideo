using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CatClawVideo.Core.Services;

/// <summary>
/// 本地代理服务：TVBox 系爬虫（<c>ProxyOrigin.getUrl()</c> 等）会把播放地址拼成
/// <c>http://127.0.0.1:&lt;port&gt;/proxy?...</c> 交给播放器，本类就是那个 <c>/proxy</c>。
///
/// <para><b>不实现它会怎样</b>（2026-09-16 实测）：爬虫用 <c>drivePort()</c> 在
/// <b>6677–6999</b> 逐端口探测 <c>GET /proxy?do=ck</c>，谁答应就用谁；一个都不答应时端口缓存
/// <c>yq</c> 保持非法值 → 拼出 <c>http://127.0.0.1:/proxy?...</c>（端口为空）→
/// .NET 侧 <c>new Uri(...)</c> 抛 <c>Invalid URI: Invalid port specified.</c>，
/// 播放页显示「播放失败：加载失败」，而日志里只有爬虫的 <c>adjustPort: 端口检测失败</c>。
/// 端口值来自 jar 内的字符串表（用 <c>_scratch_tb/dump_strings.py</c> 还原），不是猜的。</para>
///
/// <para><b>端点契约</b>（同样由字符串表还原）：</para>
/// <list type="bullet">
///   <item><c>/proxy?do=ck</c> — 存活探测，回 200 即可。</item>
///   <item><c>/proxy?...&amp;type=302&amp;url=…</c> — 302 跳到真实地址（最简单的一类线路）。</item>
///   <item><c>/proxy?do=m3u8&amp;url=…</c> — 取 m3u8 并把里面的分片/密钥 URI 改写成再走本代理
///       （解决 CDN 防盗链：播放器直连分片会被 403）。</item>
///   <item><c>do=饭太硬</c> / <c>/proxy/</c> 前缀 — 同上，按同一套处理。</item>
/// </list>
///
/// <para>用裸 <see cref="TcpListener"/> 而不是 HttpListener：与 <see cref="QemuThunder.QemuControlServer"/>
/// 一致，避开 Windows http.sys 的 URL ACL（非管理员注册前缀会 Access Denied），
/// 且播放器发来的请求本身就是极简 HTTP。</para>
/// </summary>
public sealed class SpiderProxyServer : IDisposable
{
    /// <summary>
    /// 各代爬虫探测的端口（实测：<c>ProxyOrigin</c> 族在 <b>6677–6999</b> 逐端口探、
    /// 荐片 <c>csp_JPJGuard</c> 走 <b>9978 / 9997–9999</b>；Android 侧注释的「9978…9999 整段反代」即此）。
    /// 一个监听器只能占一个端口，所以这里把已知的候选端口**全部**监听上，
    /// 谁先探到谁用；改写后的分片地址用「请求进来的那个端口」拼，保证可达。
    /// </summary>
    public static readonly int[] CandidatePorts = [6677, 9978, 9997, 9998, 9999];

    /// <summary>默认 UA：部分 CDN 对空 UA 直接 403。</summary>
    private const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0 Safari/537.36";

    private static readonly HttpClient Http = CreateClient();

    private readonly List<TcpListener> _listeners = [];
    private CancellationTokenSource? _cts;

    /// <summary>已监听的端口（升序）。</summary>
    public IReadOnlyList<int> Ports => _listeners
        .Select(l => ((IPEndPoint)l.LocalEndpoint).Port).OrderBy(p => p).ToArray();

    /// <summary>主端口（第一个成功绑定的，0 = 未启动）——用于日志与「生成新地址」的默认值。</summary>
    public int Port { get; private set; }

    /// <summary>日志回调（可设属性，便于对象初始化器注入）。</summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// JS Spider 回环代理回调（js2Proxy 语义，<c>from=catvod</c> 或 <c>do=js</c> 的请求）。
    /// 由宿主在构造运行时后注入；返回 null = 无人处理（回 502）。
    /// </summary>
    public Func<IReadOnlyDictionary<string, string>, CancellationToken,
        Task<(int Status, string Mime, byte[]? Body)?>?>? JsProxyHandler { get; set; }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            // 分片地址常跨域跳转，交给 HttpClient 自己跟随会丢掉 Range/Referer，所以手动处理
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// 逐一把 <see cref="CandidatePorts"/> 绑上（被占的跳过，不影响其他端口）。全部失败才返回 false。
    /// </summary>
    public bool Start()
    {
        if (_listeners.Count > 0) return true;
        _cts ??= new CancellationTokenSource();

        foreach (var port in CandidatePorts)
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, port);
                l.Start();
                _listeners.Add(l);
                Port = Port == 0 ? port : Port;
                _ = Task.Run(() => AcceptLoopAsync(l, _cts.Token));
            }
            catch (SocketException)
            {
                // 该端口被占（可能有别的服务）→ 跳过，其余端口照常
            }
        }

        if (_listeners.Count == 0)
        {
            Log?.Invoke($"[proxy] {string.Join('/', CandidatePorts)} 全部占用，本地代理未启动");
            return false;
        }

        ActivePort = Port;
        Log?.Invoke($"[proxy] 本地代理就绪 {string.Join('、', Ports.Select(p => $"http://127.0.0.1:{p}/proxy"))}（爬虫探测可命中）");
        return true;
    }

    /// <summary>
    /// 最近一次成功 Start 的主端口（进程级静态）。Core 内其他服务（如 SpiderVodProvider 的
    /// 网盘配置入口容错）需要拼本地 /proxy 地址时取用；0 = 未启动。
    /// </summary>
    public static int ActivePort { get; private set; }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { continue; }
            _ = Task.Run(() => HandleAsync(client), CancellationToken.None);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                // 请求从哪个候选端口进来的：改写分片地址时要用它，否则播放器连到别的端口可能没监听
                var localPort = client.Client.LocalEndPoint is IPEndPoint ep ? ep.Port : Port;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var head = await ReadHeadAsync(stream, timeout.Token).ConfigureAwait(false);
                if (head is null) return;

                var lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0) return;
                var parts = lines[0].Split(' ');
                if (parts.Length < 2 || (parts[0] != "GET" && parts[0] != "HEAD"))
                {
                    await RespondTextAsync(stream, "405 Method Not Allowed", "bad method").ConfigureAwait(false);
                    return;
                }

                var target = parts[1];
                var q = target.IndexOf('?');
                var path = q < 0 ? target : target[..q];
                var query = q < 0 ? "" : target[(q + 1)..];

                if (!path.StartsWith("/proxy", StringComparison.Ordinal))
                {
                    await RespondTextAsync(stream, "404 Not Found", "not found").ConfigureAwait(false);
                    return;
                }

                var args = ParseQuery(query);
                Log?.Invoke($"[spider-proxy] {parts[0]} {target}（来自 {client.Client.RemoteEndPoint}）");

                // ① 存活探测：爬虫 drivePort() 就是靠它确认端口
                if (args.GetValueOrDefault("do") == "ck")
                {
                    await RespondTextAsync(stream, "200 OK", "ok", "text/plain").ConfigureAwait(false);
                    return;
                }

                // ①½ Spider proxy 回调（TVBox ApiConfig.proxyLocal 语义）：交给注入的 JsProxyHandler
                //    （MauiProgram 按 SpiderKind 分派 Jar/JS 运行时，jar 的 proxy(Map) 自答）。
                //    必须在「missing url → 400」之前——部分 proxy 请求可能没有 url 参数。
                //    ① catvod/js2Proxy：from=catvod / do=js；
                //    ② Guard 系网盘源（csp_MyDriveGuard 等）：do=config / do=danmu —— jar 的
                //      proxy(Map) 自答「云盘配置」数据（登录/启用状态 JSON），宿主只做回环转发；
                //      不转发的话 jar 拿到「missing url」文本 → Gson 解析炸 → 配置界面弹不出来
                //      （真机实测：detailContent 内 Expected BEGIN_OBJECT but was STRING）。
                var doVal = args.GetValueOrDefault("do");

                // ★ TVBox RemoteServer.normalizeDanmuParams 对等（2026-09-24）：
                //   do=danmu 时补 vodName（当前片名）/ vodIndex（当前集序号）—— 爬虫（jar）的
                //   danmaku / proxy 处理器靠这两个键判断「现在看的是哪部、第几集」；缺了会走不进
                //   对应分支。TVBox 的取值来源是 App.getVodInfo()（当前播放信息），桌面侧由
                //   WatchPage 起播时写进 PlaybackContext。
                //   规则也照抄 TVBox：只在「请求没带」且「vodIndex 不是纯数字」时才补。
                if (string.Equals(doVal, "danmu", StringComparison.OrdinalIgnoreCase))
                {
                    if (!args.ContainsKey("vodName") && PlaybackContext.VodName is { Length: > 0 } vnd)
                        args["vodName"] = vnd;
                    if (!IsNumeric(args.GetValueOrDefault("vodIndex")) && PlaybackContext.VodIndex is { Length: > 0 } vid)
                        args["vodIndex"] = vid;
                }

                if (args.GetValueOrDefault("from") == "catvod" || doVal is "js" or "config" or "danmu")
                {
                    var handler = JsProxyHandler;
                    if (handler is null)
                    {
                        await RespondTextAsync(stream, "502 Bad Gateway", "js proxy handler missing")
                            .ConfigureAwait(false);
                        return;
                    }
                    var result = await handler(args, timeout.Token).ConfigureAwait(false);
                    if (result is not { } r)
                    {
                        await RespondTextAsync(stream, "502 Bad Gateway", "proxy not handled")
                            .ConfigureAwait(false);
                        return;
                    }
                    await RespondBytesAsync(stream, r.Status >= 200 && r.Status < 600 ? $"{r.Status} OK" : "200 OK",
                        r.Body ?? Array.Empty<byte>(), r.Mime, "Connection: close\r\n").ConfigureAwait(false);
                    return;
                }

                var url = DecodeUrl(args.GetValueOrDefault("url"));
                if (string.IsNullOrEmpty(url))
                {
                    await RespondTextAsync(stream, "400 Bad Request", "missing url").ConfigureAwait(false);
                    return;
                }

                // ② 302 模式：直接把真实地址还给播放器
                if (args.GetValueOrDefault("type") == "302")
                {
                    await RespondTextAsync(stream, "302 Found", "", "text/plain",
                        extraHeaders: $"Location: {url}\r\n").ConfigureAwait(false);
                    return;
                }

                // ③ 代理取流：m3u8 改写 / 其他原样透传（含 Range）
                await ProxyAsync(stream, url, args, lines, localPort, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[proxy] 请求处理异常: {ex.Message}");
            }
        }
    }

    private async Task ProxyAsync(NetworkStream clientStream, string url, Dictionary<string, string> args,
        string[] requestHeaders, int localPort, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", args.GetValueOrDefault("ua") ?? DefaultUserAgent);
        req.Headers.TryAddWithoutValidation("Referer", ResolveReferer(url, args));
        req.Headers.TryAddWithoutValidation("Accept", "*/*");

        // Range 透传（视频分片/拖动进度必需）
        var range = HeaderValue(requestHeaders, "Range");
        if (!string.IsNullOrEmpty(range)) req.Headers.TryAddWithoutValidation("Range", range);

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? url;
        var contentType = resp.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        var body = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        // m3u8：改写内部 URI 再回给播放器（分片直连会被防盗链 403）
        if (LooksLikePlaylist(finalUrl, contentType, body))
        {
            var text = Encoding.UTF8.GetString(body);
            var rewritten = RewritePlaylist(text, finalUrl, args, localPort);
            await RespondBytesAsync(clientStream, "200 OK", Encoding.UTF8.GetBytes(rewritten),
                "application/vnd.apple.mpegurl").ConfigureAwait(false);
            return;
        }

        // 其他：原样透传状态码/长度（Range 命中时是 206 + Content-Range）
        var status = (int)resp.StatusCode is 206 ? "206 Partial Content" : "200 OK";
        var extra = new StringBuilder();
        if (resp.Content.Headers.ContentRange is { } cr) extra.Append($"Content-Range: {cr}\r\n");
        if (resp.Content.Headers.ContentLength is { } cl) extra.Append($"Content-Length: {cl}\r\n");
        extra.Append("Accept-Ranges: bytes\r\n");
        extra.Append("Connection: close\r\n");

        await RespondBytesAsync(clientStream, status, body, contentType, extra.ToString(), contentLength: false)
            .ConfigureAwait(false);
    }

    /// <summary>把播放列表里的分片/密钥地址改写成再走本代理（相对路径先补全为绝对地址）。</summary>
    private string RewritePlaylist(string text, string playlistUrl, Dictionary<string, string> args, int localPort)
    {
        var referer = ResolveReferer(playlistUrl, args);
        var sb = new StringBuilder(text.Length + 256);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) { sb.Append('\n'); continue; }

            if (line[0] == '#')
            {
                // #EXT-X-KEY / #EXT-X-MAP 的 URI="..." 同样要经过本代理
                sb.Append(line.Contains("URI=\"", StringComparison.OrdinalIgnoreCase)
                    ? RewriteUriAttributes(line, playlistUrl, referer, localPort)
                    : line);
                sb.Append('\n');
                continue;
            }

            sb.Append(ProxyUrl(Absolute(playlistUrl, line), referer, localPort)).Append('\n');
        }
        return sb.ToString();
    }

    private string RewriteUriAttributes(string line, string baseUrl, string referer, int localPort)
    {
        const string marker = "URI=\"";
        var idx = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return line;
        var start = idx + marker.Length;
        var end = line.IndexOf('"', start);
        if (end < 0) return line;
        var inner = line[start..end];
        var rewritten = ProxyUrl(Absolute(baseUrl, inner), referer, localPort);
        return line[..start] + rewritten + line[end..];
    }

    /// <summary>生成一条指向本代理的地址（url/referer 都做 URL 编码，播放器直接 GET 即可）。</summary>
    private static string ProxyUrl(string url, string? referer, int localPort)
    {
        var sb = new StringBuilder();
        sb.Append("http://127.0.0.1:").Append(localPort).Append("/proxy?url=").Append(Uri.EscapeDataString(url));
        if (!string.IsNullOrEmpty(referer))
            sb.Append("&referer=").Append(Uri.EscapeDataString(referer));
        return sb.ToString();
    }

    private static string Absolute(string baseUrl, string relative)
    {
        if (relative.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            relative.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return relative;
        try { return new Uri(new Uri(baseUrl), relative).ToString(); }
        catch { return relative; }
    }

    private static string ResolveReferer(string url, Dictionary<string, string> args)
    {
        var r = args.GetValueOrDefault("referer") ?? args.GetValueOrDefault("referrer");
        if (!string.IsNullOrEmpty(r)) return r;
        try
        {
            var u = new Uri(url);
            return $"{u.Scheme}://{u.Host}/";
        }
        catch { return ""; }
    }

    private static bool LooksLikePlaylist(string url, string contentType, byte[] body)
    {
        if (contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)) return true;
        if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)) return true;
        if (body.Length < 7) return false;
        var head = Encoding.UTF8.GetString(body, 0, Math.Min(64, body.Length));
        return head.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal);
    }

    /// <summary>爬虫把 url 参数按两种约定编码：URL-encode（多数）与 Base64（webParse/302 系）。两种都认。</summary>
    private static string DecodeUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var candidate = Uri.UnescapeDataString(raw);
        if (LooksLikeHttpUrl(candidate)) return candidate;

        // Base64（URL-safe 与标准两种，可能缺 padding）
        var s = raw.Replace('-', '+').Replace('_', '/');
        s = s.PadRight((s.Length + 3) / 4 * 4, '=');
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(s));
            if (LooksLikeHttpUrl(decoded)) return decoded;
        }
        catch { }

        return candidate;
    }

    private static bool LooksLikeHttpUrl(string s) =>
        s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    // ════════════════ 极简 HTTP 读写 ════════════════

    private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[8192];
        var len = 0;
        while (len < buf.Length)
        {
            var r = await stream.ReadAsync(buf.AsMemory(len), ct).ConfigureAwait(false);
            if (r <= 0) break;
            len += r;
            var text = Encoding.ASCII.GetString(buf, 0, len);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal)) return text;
        }
        return len == 0 ? null : Encoding.ASCII.GetString(buf, 0, len);
    }

    private static async Task RespondTextAsync(NetworkStream stream, string status, string body,
        string contentType = "text/plain", string extraHeaders = "")
    {
        await RespondBytesAsync(stream, status, Encoding.UTF8.GetBytes(body), contentType, extraHeaders)
            .ConfigureAwait(false);
    }

    private static async Task RespondBytesAsync(NetworkStream stream, string status, byte[] body,
        string contentType, string extraHeaders = "", bool contentLength = true)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status).Append("\r\n");
        sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
        if (contentLength) sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        if (!string.IsNullOrEmpty(extraHeaders)) sb.Append(extraHeaders);
        sb.Append("\r\n");

        var head = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(head).ConfigureAwait(false);
        if (body.Length > 0) await stream.WriteAsync(body).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static string? HeaderValue(string[] headerLines, string name)
    {
        foreach (var line in headerLines)
        {
            var i = line.IndexOf(':');
            if (i <= 0) continue;
            if (line[..i].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return line[(i + 1)..].Trim();
        }
        return null;
    }

    /// <summary>是否纯数字（对齐 TVBox RemoteServer.isNumeric，用于判断 vodIndex 是否已可用）。</summary>
    private static bool IsNumeric(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var c in s)
            if (c < '0' || c > '9') return false;
        return true;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = pair.IndexOf('=');
            if (i < 0) dict[Uri.UnescapeDataString(pair)] = "";
            else dict[Uri.UnescapeDataString(pair[..i])] = pair[(i + 1)..];
        }
        return dict;
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        foreach (var l in _listeners) { try { l.Stop(); } catch { } }
        _listeners.Clear();
        Port = 0;
    }
}
