using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using CatClawVideo.Core.Providers;

namespace CatClawVideo.Core.Services;

/// <summary>一台 MediaRenderer（对位 TVBox <c>dlna/CastDevice</c>）。</summary>
public sealed class DlnaDevice
{
    public required string Udn { get; init; }
    public required string Name { get; init; }
    /// <summary>AVTransport:1 的 controlURL，已按 location 解析成绝对地址。</summary>
    public required string ControlUrl { get; init; }
    public string? RenderingControlUrl { get; init; }
    public string? Location { get; init; }
}

/// <summary>一次投屏请求（对位 <c>dlna/CastVideo</c>）。</summary>
public sealed record DlnaCastTarget(string Url, string Name, IReadOnlyDictionary<string, string>? Headers, long PositionMs);

/// <summary>
/// DLNA / UPnP 投屏（移植 TVBox <c>osc/dlna</c> 的 815 行，但那套整个架在 Cling
/// <c>org.fourthline.cling</c> 上 —— Cling 没有 .NET 实现，所以 SSDP + 设备描述 + SOAP 三段都得自己写）。
/// <para>只覆盖 TVBox 实际用到的 <c>AVTransport:1</c> 三动作：
/// <c>SetAVTransportURI</c> → <c>Play</c> →（有续播位置时）<c>Seek REL_TIME</c>。
/// Cling 的 Gena 事件订阅、RenderingControl 音量、设备存活租约刷新全部不做：
/// TVBox 也没用（它只在 <c>registry</c> 里查一次性列表）。</para>
/// <para>纯逻辑（报文构造 / 应答解析 / DIDL / SOAP 外壳）与网络分离，
/// 所以 parity 台架可以逐字节断言发出去的东西，不需要真路由器。</para>
/// </summary>
public static class Dlna
{
    public const string SsdpAddress = "239.255.255.250";
    public const int SsdpPort = 1900;
    public const string RendererTarget = "urn:schemas-upnp-org:device:MediaRenderer:1";
    public const string AvTransportType = "urn:schemas-upnp-org:service:AVTransport:1";
    public const string RenderingControlType = "urn:schemas-upnp-org:service:RenderingControl:1";
    private const string SoapNs = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string AvTransportNs = "urn:schemas-upnp-org:service:AVTransport:1";

    // ───────────────────────── SSDP 发现 ─────────────────────────

    /// <summary>M-SEARCH 请求体（对位 Cling 的 <c>STAllHeader</c> 定向搜索）。</summary>
    public static string BuildSearchRequest(int mxSeconds = 2) =>
        "M-SEARCH * HTTP/1.1\r\n" +
        $"HOST: {SsdpAddress}:{SsdpPort}\r\n" +
        "MAN: \"ssdp:discover\"\r\n" +
        $"ST: {RendererTarget}\r\n" +
        $"MX: {mxSeconds}\r\n" +
        "\r\n";

    /// <summary>
    /// 解析一条 SSDP 应答。只认 200 且带 LOCATION 的；NOTIFY 广播（<c>NTS: ssdp:alive</c>）也吃，
    /// 因为很多电视是主动 announce 而不回 M-SEARCH。
    /// </summary>
    public static (string Usn, string Location, string St)? TryParseResponse(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        string? location = null, usn = null, st = null;
        var lines = message.Replace("\r\n", "\n").Split('\n');
        // 首行必须是状态行（HTTP/1.1 200 OK）或 NOTIFY —— M-SEARCH 是我们自己发出去的，别把自己的包读回来
        if (lines.Length == 0 ||
            (!lines[0].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase) &&
             !lines[0].StartsWith("NOTIFY", StringComparison.OrdinalIgnoreCase)))
            return null;
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim().ToUpperInvariant();
            var val = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "LOCATION": location = val; break;
                case "USN": usn = val; break;
                case "ST": st = val; break;
                case "NT": st ??= val; break;
            }
        }
        if (string.IsNullOrEmpty(location)) return null;
        return (usn ?? location!, location, st ?? RendererTarget);
    }

    /// <summary>
    /// 本机局域网 IPv4。投屏必须给接收端一个<b>它那边可达</b>的地址，127.0.0.1 与
    /// <c>0.0.0.0</c> 都不行，所以要挑真实网卡地址（第一个非回环 IPv4，与 TVBox 取 wifi ip 同策略）。
    /// </summary>
    public static string? LocalLanIp()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                        return a.Address.ToString();
            }
        }
        catch (Exception ex)
        {
            CatClawLog.Write($"[投屏] 本机局域网地址取不到: {ex.Message}");
        }
        return null;
    }

    /// <summary>向组播组发 M-SEARCH 并收集应答的 LOCATION。真机路径，台架不覆盖。</summary>
    public static async Task<List<string>> SearchLansAsync(CancellationToken ct = default, int? port = null)
    {
        var found = new List<string>();
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, port ?? 0));
            udp.Ttl = 4;                                  // DLNA 惯例：不让发现包跑过本网段
            var payload = Encoding.UTF8.GetBytes(BuildSearchRequest());
            var group = new IPEndPoint(IPAddress.Parse(SsdpAddress), SsdpPort);
            for (int i = 0; i < 3; i++) await udp.SendAsync(payload, group, ct).ConfigureAwait(false);

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMilliseconds(400));
                UdpReceiveResult got;
                try { got = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { continue; }
                var parsed = TryParseResponse(Encoding.UTF8.GetString(got.Buffer));
                if (parsed is not null && !found.Contains(parsed.Value.Location)) found.Add(parsed.Value.Location);
            }
        }
        catch (Exception ex)
        {
            CatClawLog.Write($"[投屏] SSDP 发现失败: {ex.Message}");
        }
        return found;
    }

    // ───────────────────────── 设备描述 ─────────────────────────

    /// <summary>
    /// 从 <c>location</c> 抓并解析设备描述 XML，取 MediaRenderer 的 AVTransport controlURL。
    /// </summary>
    public static async Task<DlnaDevice?> FetchDeviceAsync(string location, HttpClient http, CancellationToken ct = default)
    {
        try
        {
            var bytes = await http.GetByteArrayAsync(location, ct).ConfigureAwait(false);
            var xml = Encoding.UTF8.GetString(bytes);
            return ParseDevice(xml, location);
        }
        catch (Exception ex)
        {
            CatClawLog.Write($"[投屏] 设备描述拉取失败 {location}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 解析设备描述。controlURL 常见三种写法：绝对 http、<c>/upnp/control/…</c>、裸 <c>upnp/control/…</c>，
    /// 全部要按 location 的基地址补全 —— Cling 内部做的这件事，这里必须自己做。
    /// </summary>
    public static DlnaDevice? ParseDevice(string xml, string location)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            var doc = XDocument.Load(reader);
            var root = doc.Root;
            if (root is null) return null;

            string? udn = null, name = null, avt = null, rc = null;
            foreach (var el in doc.Descendants())
            {
                switch (el.Name.LocalName)
                {
                    case "UDN" when udn is null: udn = el.Value.Trim(); break;
                    case "friendlyName" when name is null: name = el.Value.Trim(); break;
                    case "service":
                        var type = Local(el, "serviceType");
                        var ctrl = Local(el, "controlURL");
                        if (type is null || ctrl is null) break;
                        if (avt is null && type.Equals(AvTransportType, StringComparison.OrdinalIgnoreCase)) avt = ctrl;
                        if (rc is null && type.Equals(RenderingControlType, StringComparison.OrdinalIgnoreCase)) rc = ctrl;
                        break;
                }
            }
            if (avt is null) return null;   // 没有 AVTransport 的设备 TVBox 同样不列出来
            return new DlnaDevice
            {
                Udn = udn ?? location,
                Name = string.IsNullOrEmpty(name) ? "未命名设备" : name,
                ControlUrl = Combine(location, avt),
                RenderingControlUrl = rc is null ? null : Combine(location, rc),
                Location = location,
            };
        }
        catch (Exception ex)
        {
            CatClawLog.Write($"[投屏] 设备描述解析失败: {ex.Message}");
            return null;
        }
    }

    static string? Local(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim();

    /// <summary>location + 相对 controlURL → 绝对地址（裸相对段要挂到根，不是挂到同级目录）。</summary>
    public static string Combine(string location, string controlUrl)
    {
        if (Uri.TryCreate(controlUrl, UriKind.Absolute, out var abs) &&
            (abs.Scheme == "http" || abs.Scheme == "https")) return controlUrl;
        if (!Uri.TryCreate(location, UriKind.Absolute, out var loc)) return controlUrl;
        // UPnP 的 controlURL 一律相对**设备根**，不是相对 descriptor 所在目录。
        // ⚠ GetLeftPart(Authority) 本身已含 scheme，别再拼一次（台架 DL6 抓到过 http://http://…）
        var root = loc.GetLeftPart(UriPartial.Authority);
        var tail = controlUrl.StartsWith('/') ? controlUrl : "/" + controlUrl;
        return root.TrimEnd('/') + tail;
    }

    // ───────────────────────── DIDL-Lite ─────────────────────────

    /// <summary>
    /// 构造 <c>CurrentURIMetaData</c>（对位 <c>DLNACastManager.buildMetaData</c>）。
    /// <para>TVBox 把防盗链头序列化成 JSON 塞进 <c>dc:description</c> —— 少数增强型接收端会读它。</para>
    /// </summary>
    public static string BuildMetaData(DlnaCastTarget target)
    {
        var sb = new StringBuilder();
        sb.Append("<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" ")
          .Append("xmlns:dc=\"http://purl.org/dc/elements/1.1/\" ")
          .Append("xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\">")
          .Append("<item id=\"0\" parentID=\"-1\" restricted=\"0\">")
          .Append("<dc:title>").Append(EscapeXml(target.Name)).Append("</dc:title>")
          .Append("<dc:creator></dc:creator>")
          .Append("<upnp:class>object.item.videoItem</upnp:class>");
        if (target.Headers is { Count: > 0 })
            sb.Append("<dc:description>").Append(EscapeXml(HeadersJson(target.Headers))).Append("</dc:description>");
        sb.Append("<res protocolInfo=\"http-get:*:video/*:*\">").Append(EscapeXml(target.Url)).Append("</res>")
          .Append("</item></DIDL-Lite>");
        return sb.ToString();
    }

    static string HeadersJson(IReadOnlyDictionary<string, string> headers)
    {
        var parts = headers.Select(h =>
            $"{System.Text.Json.JsonSerializer.Serialize(h.Key)}:{System.Text.Json.JsonSerializer.Serialize(h.Value)}");
        return "{" + string.Join(",", parts) + "}";
    }

    /// <summary>与 TVBox <c>escapeXml</c> 同覆盖面：5 个预定义实体 + 引号。</summary>
    public static string EscapeXml(string? s) => s is null || s.Length == 0 ? "" :
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
         .Replace("\"", "&quot;").Replace("'", "&apos;");

    /// <summary>毫秒 → UPnP 的 <c>H:MM:SS.mmm</c>（Seek REL_TIME 用）。</summary>
    public static string FormatMs(long ms)
    {
        if (ms < 0) ms = 0;
        var ts = TimeSpan.FromMilliseconds(ms);
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}";
    }

    // ───────────────────────── SOAP ─────────────────────────

    /// <summary>
    /// <c>SetAVTransportURI</c> 的请求体。
    /// <para>DIDL 必须<b>再整体转义一层</b>（它是字符串实参，不是内联 XML）—— 漏这层是最常见的
    /// 「投屏没反应」根因，所以台架专门断言它。</para>
    /// </summary>
    public static string BuildSetUriBody(string uri, string didl) =>
        SoapBody("SetAVTransportURI",
            "<InstanceID>0</InstanceID>" +
            $"<CurrentURI>{EscapeXml(uri)}</CurrentURI>" +
            $"<CurrentURIMetaData>{EscapeXml(didl)}</CurrentURIMetaData>");

    public static string BuildPlayBody() =>
        SoapBody("Play", "<InstanceID>0</InstanceID><Speed>1</Speed>");

    public static string BuildStopBody() =>
        SoapBody("Stop", "<InstanceID>0</InstanceID>");

    public static string BuildSeekBody(string relTime) =>
        SoapBody("Seek", $"<InstanceID>0</InstanceID><Unit>REL_TIME</Unit><Target>{relTime}</Target>");

    static string SoapBody(string action, string args) =>
        $"<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        $"<s:Envelope xmlns:s=\"{SoapNs}\" s:encodingStyle=\"{SoapNs}-encoding\">" +
        $"<s:Body><u:{action} xmlns:u=\"{AvTransportNs}\">{args}</u:{action}></s:Body></s:Envelope>";

    /// <summary>SOAP 1.1 要求 SOAPAction 是<b>带引号</b>的 # 形式动作名。</summary>
    public static string SoapActionHeader(string action) => $"\"{AvTransportNs}#{action}\"";

    // ───────────────────────── 投屏动作链 ─────────────────────────

    /// <summary>
    /// 完整动作链（对位 <c>cast()</c>）：SetAVTransportURI 成功才 Play，Play 成功后有位置才 Seek，
    /// Seek 失败只记日志、不影响「投屏成功」的判定。
    /// </summary>
    public static async Task<(bool Ok, string Error)> CastAsync(
        DlnaDevice device, DlnaCastTarget target, HttpClient http, CancellationToken ct = default)
    {
        var uri = BuildSetUriBody(target.Url, BuildMetaData(target));
        var (ok, err) = await PostAsync(http, device.ControlUrl, "SetAVTransportURI", uri, ct).ConfigureAwait(false);
        if (!ok) return (false, $"设备拒绝了播放地址：{err}");

        (ok, err) = await PostAsync(http, device.ControlUrl, "Play", BuildPlayBody(), ct).ConfigureAwait(false);
        if (!ok) return (false, $"已送达但起播失败：{err}");

        if (target.PositionMs > 0)
        {
            var (seekOk, seekErr) = await PostAsync(http, device.ControlUrl, "Seek",
                BuildSeekBody(FormatMs(target.PositionMs)), ct).ConfigureAwait(false);
            if (!seekOk) CatClawLog.Write($"[投屏] Seek 被忽略（继续按当前进度播）: {seekErr}");
        }
        return (true, "");
    }

    static async Task<(bool, string)> PostAsync(
        HttpClient http, string controlUrl, string action, string body, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, controlUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/xml"),
            };
            req.Headers.TryAddWithoutValidation("SOAPACTION", SoapActionHeader(action));
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return (false, $"HTTP {(int)resp.StatusCode}");
            // UPnP 出错也用 200 回一个 Fault 信封，必须看 body
            if (text.Contains("FaultCode", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("<errorDescription>", StringComparison.OrdinalIgnoreCase))
            {
                var desc = Between(text, "<errorDescription>", "</errorDescription>");
                return (false, string.IsNullOrEmpty(desc) ? "设备返回 UPnP Fault" : desc);
            }
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    static string Between(string s, string open, string close)
    {
        int a = s.IndexOf(open, StringComparison.Ordinal);
        if (a < 0) return "";
        a += open.Length;
        int b = s.IndexOf(close, a, StringComparison.Ordinal);
        return b < 0 ? "" : s[a..b];
    }

    // ───────────────────────── 投屏地址改写 ─────────────────────────

    /// <summary>
    /// 把当前播放地址改写成「接收端真的能拉到」的地址（对位 <c>getCastUrl</c> + 那个 246 行的
    /// <c>SocketHttpStreamServer</c>）。
    /// <para>TVBox 要自己起一个带请求头的中转 http server，本仓不用：内置本地代理
    /// <c>/proxy?go=live&amp;type=media&amp;url=…&amp;ua=…&amp;referer=…</c> 本来就会带 UA/Referer/Cookie
    /// 回源，把流挂到它上面即可 —— 于是「补一个转发服务」塌缩成「改一次 URL」。
    /// 注意这需要接收端与本机同网段可达（走局域网 IP，不能是 127.0.0.1）。</para>
    /// </summary>
    public static string BuildCastUrl(
        string playUrl, IReadOnlyDictionary<string, string>? headers, string localBase, string? proxyNamespace = "proxy")
    {
        if (string.IsNullOrEmpty(playUrl) || string.IsNullOrEmpty(localBase)) return playUrl;
        var ua = Header(headers, "User-Agent");
        var referer = Header(headers, "Referer");
        var cookie = Header(headers, "Cookie");
        var range = Header(headers, "Range");
        var origin = Header(headers, "Origin");
        var sb = new StringBuilder($"{localBase.TrimEnd('/')}/{proxyNamespace}?go=live&type=media&url={EscapeData(playUrl)}");
        if (ua is not null) sb.Append("&ua=").Append(EscapeData(ua));
        if (referer is not null) sb.Append("&referer=").Append(EscapeData(referer));
        if (origin is not null) sb.Append("&origin=").Append(EscapeData(origin));
        if (cookie is not null) sb.Append("&cookie=").Append(EscapeData(cookie));
        if (range is not null) sb.Append("&range=").Append(EscapeData(range));
        return sb.ToString();
    }

    static string? Header(IReadOnlyDictionary<string, string>? headers, string key)
    {
        if (headers is null) return null;
        foreach (var h in headers)
            if (string.Equals(h.Key, key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(h.Value))
                return h.Value;
        return null;
    }

    static string EscapeData(string s) => Uri.EscapeDataString(s);
}
