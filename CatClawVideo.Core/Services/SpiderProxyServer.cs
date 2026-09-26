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
    /// 各代爬虫探测的端口（实测：荐片 <c>csp_JPJGuard</c> 走 <b>9978 / 9997–9999</b>；
    /// Android 侧注释的「9978…9999 整段反代」即此）。一个监听器只能占一个端口，
    /// 这里把已知候选端口**全部**监听上，谁先探到谁用；改写后的分片地址用「请求进来的那个端口」拼，保证可达。
    /// <para><b>6677–6999 必须让给爬虫自己 —— 那是 Guard 网盘 jar 自己起的 drive 服务。</b>
    /// 真机 2026-09-26 实证：<c>ss -ltnp</c> 显示 <c>*:6677</c> 由 jar 侧监听（我们的代理只绑
    /// <c>127.0.0.1</c> 的 9978/9997/9998/9999）。以前把 6677 放进候选端口，jar 的 drive 服务绑不上，
    /// 而它 <c>playerContent</c> 返回的 <c>http://127.0.0.1:6677/proxy/play/&lt;盘&gt;/&lt;片&gt;/&lt;集&gt;</c>
    /// 就落到我们这里，被「无 url 参数」分支回成 <c>400 missing url</c> → 播放器报
    /// <c>Source error / InvalidResponseCodeException: 400</c>。让出该端口后 jar 自己应答，
    /// 《名侦探柯南》夸父盘源即正常出画（<c>c2.qti.avc.decoder Render:119 Drop:0</c>）。
    /// TVBox 侧同理不服务这条路径（<c>RemoteServer.isProxyRequest</c> 要求路径正好是 <c>/proxy</c>
    /// 且 query 带 <c>do</c>/<c>go</c>），所以这不是我们少实现了什么，而是**别抢别人的端口**。</para>
    /// </summary>
    public static readonly int[] CandidatePorts = [9978, 9997, 9998, 9999];

    /// <summary>
    /// 交给爬虫 <c>proxy(Map)</c> 的 do 值。TVBox <c>ApiConfig.proxyLocal</c> 的语义是「除宿主自答
    /// （心跳 / 取流）外一律转爬虫」，这里先显式列出网盘/弹幕/解析族：<c>m3u8 / wasm / pic</c>
    /// 仍走宿主取流，免得改道后没人做分片改写。
    /// <para>词表来源：逆向 jar 的 <c>ProxyOrigin.proxy</c>（18 路 switch，字符串运行时解密，
    /// 解法见 <c>JavaBridge/tools/DecodeDo.java</c>）。注意 <c>config</c> 不在其中——它是宿主
    /// 合成配置页 URL 时自造的占位值，jar 认的是 <c>input</c>。</para>
    /// </summary>
    static readonly HashSet<string> SpiderProxyDo = new(StringComparer.OrdinalIgnoreCase)
    {
        "js", "config", "danmu", "drive", "live", "play", "input", "quark", "ali", "UC",
        "YCyz", "musicLrc", "yinHe", "prPic", "hmys", "bili", "dnsPic", "MixDemo", "MixWeb",
    };

    /// <summary>默认 UA：部分 CDN 对空 UA 直接 403。</summary>
    public const string DefaultUserAgent =
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

    /// <summary>
    /// <c>/cache?do=get|set|del</c> 端点背后的 KV（对位 TVBox 用 Hawk 存 <c>cache_&lt;rule&gt;_&lt;key&gt;</c>）。
    /// 未注入时 get 回空串、set 静默丢弃——jar 侧表现为「登录态存了但重启就没了」，所以宿主必须接。
    /// </summary>
    public SpiderLocalStore? CacheStore { get; set; }

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
    /// 覆盖监听端口（null = 用 <see cref="CandidatePorts"/>）。
    /// <para>存在的理由不是测试：本机已有一个实例在听这 5 个端口时，第二个实例会一个都绑不上，
    /// 于是爬虫探不到 <c>do=ck</c> → 拼出空端口地址 → 播放失败。给它一条可指定的出路。</para>
    /// </summary>
    public int[]? BindPorts { get; set; }

    /// <summary>
    /// 逐一把 <see cref="CandidatePorts"/>（或 <see cref="BindPorts"/>）绑上（被占的跳过，不影响其他端口）。全部失败才返回 false。
    /// </summary>
    public bool Start()
    {
        if (_listeners.Count > 0) return true;
        _cts ??= new CancellationTokenSource();

        foreach (var port in BindPorts ?? CandidatePorts)
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

                // /rc = 局域网遥控（对位 TVBox RemoteServer + res/raw 前端）。
                // 与 TVBox 的关键差别：那边全程无鉴权，这里除 ping 外一律要 token，
                // 并且不开文件浏览 / 上传 / 改配置这三类端点。
                if (path.StartsWith("/rc", StringComparison.Ordinal))
                {
                    var rcArgs = ParseQuery(query);
                    var reply = RemoteControlHub.Handle(path, rcArgs, timeout.Token);
                    await RespondTextAsync(stream, RcStatusLine(reply.Status), reply.Body, reply.Mime)
                        .ConfigureAwait(false);
                    return;
                }

                // /proxy = 播放与爬虫回环；/cache = 爬虫的跨启动 KV（TVBox CacheRequestProcess）
                if (!path.StartsWith("/proxy", StringComparison.Ordinal) &&
                    !path.StartsWith("/cache", StringComparison.Ordinal))
                {
                    await RespondTextAsync(stream, "404 Not Found", "not found").ConfigureAwait(false);
                    return;
                }

                var args = ParseQuery(query);
                Log?.Invoke($"[spider-proxy] {parts[0]} {target}（来自 {client.Client.RemoteEndPoint}）");

                // ⓪ /cache?do=get|set|del&rule=&key= —— 先于 /proxy 各分支：它按路径前缀命中，与 do/go 无关
                if (path.StartsWith("/cache", StringComparison.Ordinal))
                {
                    await HandleCacheAsync(stream, args).ConfigureAwait(false);
                    return;
                }

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

                if (args.GetValueOrDefault("from") == "catvod" || (doVal is not null && SpiderProxyDo.Contains(doVal)))
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

                // ①¾ go= 内置命名空间（对位 TVBox <c>Proxy.proxy</c>；路由优先级是 do 优先、其次 go）。
                //     直播源的分片/密钥要一路带 ua/referer/origin/cookie，这条链此前整体缺失：
                //     表现为 itv 类直播「第一帧能出、几秒后黑屏」——首个 m3u8 是宿主直取到的，
                //     而列表里的 .ts 直连 CDN 就 403。
                var goVal = args.GetValueOrDefault("go");
                if (!string.IsNullOrEmpty(goVal))
                {
                    var g = await GoLiveProxy.HandleAsync(args, localPort, lines, timeout.Token)
                        .ConfigureAwait(false);
                    if (g is null)
                    {
                        await RespondTextAsync(stream, "502 Bad Gateway", $"go={goVal} 未处理").ConfigureAwait(false);
                        return;
                    }
                    // Content-Length 一律按实际写出的 body 计（上游那个可能与转码后的长度不一致，
                    // 或根本没有——分块传输时），其余响应头原样回传。
                    var extra = new StringBuilder("Connection: close\r\n");
                    if (g.Headers is not null)
                        foreach (var (k, v) in g.Headers)
                            if (!k.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                                extra.Append(k).Append(": ").Append(v).Append("\r\n");
                    await RespondBytesAsync(stream, StatusText(g.Status), g.Body, g.Mime, extra.ToString())
                        .ConfigureAwait(false);
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

    /// <summary>
    /// 爬虫 KV 端点（移植 TVBox <c>server/CacheRequestProcess</c>）。
    /// <para>键格式照抄 <c>getKey</c>：<c>"cache_" + (rule 空 ? "" : rule + "_") + key</c>。
    /// 语义要点：一律回 200（连 del/set 也是）、<c>key</c> 空回空串、get 未命中回空串、
    /// set 的 value 缺省按空串存。jar 靠 get 的返回是否为空判断「有没有登录过」，
    /// 所以这里<b>不能</b>用 404/400 表达未命中。</para>
    /// </summary>
    private async Task HandleCacheAsync(NetworkStream stream, Dictionary<string, string> args)
    {
        // NanoHTTPD 的 getParms() 是解码过的，我们的 ParseQuery 存的是原始值 —— 这里补上，
        // 否则爬虫存的 cookie（大量 %3D/%26）取回来是编码态，Gson 一解析就错。
        string Un(string k) => Uri.UnescapeDataString(args.GetValueOrDefault(k) ?? "");

        var action = Un("do");
        var key = Un("key");
        if (key.Length == 0)
        {
            await RespondTextAsync(stream, "200 OK", "", "text/plain").ConfigureAwait(false);
            return;
        }

        var cacheKey = "cache_" + (Un("rule") is { Length: > 0 } rule ? rule + "_" : "") + key;
        var store = CacheStore;
        if (store is null)
        {
            Log?.Invoke($"[cache] 未注入 KV，{action} {cacheKey} 落空");
            await RespondTextAsync(stream, "200 OK", action == "get" ? "" : "OK", "text/plain").ConfigureAwait(false);
            return;
        }

        switch (action)
        {
            case "get":
                await RespondTextAsync(stream, "200 OK", store.CacheGet(cacheKey), "text/plain")
                    .ConfigureAwait(false);
                break;
            case "set":
                store.CacheSet(cacheKey, Un("value"));
                await RespondTextAsync(stream, "200 OK", "OK", "text/plain").ConfigureAwait(false);
                break;
            case "del":
                store.CacheDelete(cacheKey);
                await RespondTextAsync(stream, "200 OK", "OK", "text/plain").ConfigureAwait(false);
                break;
            default:
                await RespondTextAsync(stream, "200 OK", "", "text/plain").ConfigureAwait(false);
                break;
        }
    }

    static string StatusText(int code) => code switch
    {
        200 => "200 OK",
        206 => "206 Partial Content",
        301 => "301 Moved Permanently",
        302 => "302 Found",
        403 => "403 Forbidden",
        404 => "404 Not Found",
        500 => "500 Internal Server Error",
        _ => code + " OK",
    };

    private async Task ProxyAsync(NetworkStream clientStream, string url, Dictionary<string, string> args,
        string[] requestHeaders, int localPort, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", args.GetValueOrDefault("ua") ?? DefaultUserAgent);
        req.Headers.TryAddWithoutValidation("Referer", ResolveReferer(url, args));
        req.Headers.TryAddWithoutValidation("Accept", "*/*");

        // Range 透传（视频分片/拖动进度必需）
        var range = Header(requestHeaders, "Range");
        if (!string.IsNullOrEmpty(range)) req.Headers.TryAddWithoutValidation("Range", range);

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? url;
        var contentType = resp.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        var body = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        // m3u8：改写内部 URI 再回给播放器（分片直连会被防盗链 403）
        if (LooksLikePlaylist(finalUrl, contentType, body))
        {
            var text = Encoding.UTF8.GetString(body);

            // 先去广告再改写：清洗要在「绝对地址」形态上做（订阅 rules 的正则按 host 匹配），
            // 且洗完 AdCount==0 时按 TVBox 语义播原文。开关默认关，见 M3u8Purifier.Enabled。
            if (M3u8Purifier.Enabled)
            {
                var purifier = new M3u8Purifier(Providers.TvBoxConfigStore.AdRegexForUrl) { Log = Log };
                var purified = purifier.Purify(finalUrl, text);
                if (purified is not null && purifier.AdCount > 0)
                {
                    Log?.Invoke($"[proxy] m3u8 去广告：移除 {purifier.AdCount} 段（{finalUrl}）");
                    text = purified;
                }
            }

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

    /// <summary>遥控端点的状态行（401/404 这些要有名字，浏览器与脚本都会看这个）。</summary>
    static string RcStatusLine(int status) => status switch
    {
        200 => "200 OK",
        400 => "400 Bad Request",
        401 => "401 Unauthorized",
        404 => "404 Not Found",
        499 => "499 Client Closed Request",
        501 => "501 Not Implemented",
        _ => status + " Error",
    };

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

    /// <summary>取原始请求头（供 <see cref="GoLiveProxy"/> 复用：它要把播放器的 Range 带下去）。</summary>
    public static string? Header(string[] headerLines, string name)
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
