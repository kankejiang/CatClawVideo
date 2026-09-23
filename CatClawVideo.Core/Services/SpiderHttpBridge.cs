using System.Net;
using System.Text;
using System.Text.Json;

namespace CatClawVideo.Core.Services;

/// <summary>TVBox JS Spider 的请求选项模型（对齐 TVBoxOSC <c>crawler.js.Req</c>）。</summary>
public sealed class SpiderReqOptions
{
    /// <summary>返回体形态：0=文本 1=字节数组 2=Base64</summary>
    public int Buffer { get; set; }
    /// <summary>是否跟随重定向：1=是 0=否</summary>
    public int Redirect { get; set; } = 1;
    /// <summary>超时毫秒（默认 10s，对齐 Req.getTimeout）</summary>
    public int Timeout { get; set; } = 10000;
    /// <summary>data 的发送形态：json / form / form-data</summary>
    public string PostType { get; set; } = "json";
    /// <summary>HTTP 方法（大小写不敏感）</summary>
    public string Method { get; set; } = "get";
    /// <summary>原始请求体（与 data 二选一）</summary>
    public string? Body { get; set; }
    /// <summary>结构化数据：string → 按原始文本发送；object → 按 PostType 序列化</summary>
    public JsonElement? Data { get; set; }
    /// <summary>请求头（JS 对象 / 字符串均可，调用方已转字典）</summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>独立 Cookie 项（部分源以 cookie 键传，合并进 headers）</summary>
    public string? Cookie { get; set; }

    public string Charset
    {
        get
        {
            var ct = Headers.GetValueOrDefault("Content-Type") ?? Headers.GetValueOrDefault("content-type");
            if (ct is null) return "UTF-8";
            foreach (var part in ct.Split(';'))
                if (part.Contains("charset=", StringComparison.OrdinalIgnoreCase))
                    return part.Split('=')[^1].Trim().ToUpperInvariant();
            return "UTF-8";
        }
    }
}

/// <summary>TVBox JS Spider 的响应模型（对齐 <c>Connect.success</c>：{headers, content}）。</summary>
public sealed class SpiderHttpResponse
{
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Buffer=0 → 文本；1 → 内容为 Jint 字节数组；2 → Base64 字符串</summary>
    public int Buffer { get; set; }
    public string Content { get; set; } = "";
    public byte[]? ContentBytes { get; set; }
    public int Status { get; set; }
    public bool Ok { get; set; }
}

/// <summary>
/// TVBox JS Spider 的 HTTP 桥：把 JS 侧 <c>req(url, opts)</c> 翻译成 .NET 请求。
/// <para>⚠ 必须用托管 <see cref="SocketsHttpHandler"/>：Android 默认的 AndroidMessageHandler
/// 对非 2xx 状态码直接抛异常，挑战页/错误页类源会拿不到响应体（猫爪音乐同款教训）。</para>
/// <para>同步 API：JS 调用线程内阻塞完成（引擎单线程串行模型，天然安全）。</para>
/// </summary>
public static class SpiderHttpBridge
{
    /// <summary>浏览器 UA（对齐 TVBox 请求特征）。</summary>
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Linux; Android 12) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Mobile Safari/537.36";

    /// <summary>解析 JS 传入的 options 对象（宿主已把 JsValue 转成 JSON 字符串）。</summary>
    public static SpiderReqOptions ParseOptions(string? optionsJson)
    {
        var opt = new SpiderReqOptions();
        if (string.IsNullOrWhiteSpace(optionsJson)) return opt;
        try
        {
            using var doc = JsonDocument.Parse(optionsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return opt;

            if (root.TryGetProperty("buffer", out var b) && b.TryGetInt32(out var bi)) opt.Buffer = bi;
            if (root.TryGetProperty("redirect", out var r) && r.TryGetInt32(out var ri)) opt.Redirect = ri;
            if (root.TryGetProperty("timeout", out var t) && t.TryGetInt32(out var ti) && ti > 0) opt.Timeout = ti;
            if (root.TryGetProperty("postType", out var pt) && pt.ValueKind == JsonValueKind.String)
                opt.PostType = pt.GetString() ?? "json";
            if (root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String)
                opt.Method = m.GetString() ?? "get";
            if (root.TryGetProperty("body", out var bd) && bd.ValueKind == JsonValueKind.String)
                opt.Body = bd.GetString();
            if (root.TryGetProperty("cookie", out var ck) && ck.ValueKind == JsonValueKind.String)
                opt.Cookie = ck.GetString();
            if (root.TryGetProperty("data", out var data) && data.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.String)
                opt.Data = data.Clone();
            if (root.TryGetProperty("headers", out var hs))
                opt.Headers = ToDictionary(hs);
        }
        catch { }
        return opt;
    }

    /// <summary>JS 侧 headers 兼容三种形态：对象 / JSON 字符串 / key=value 串。</summary>
    public static Dictionary<string, string> ToDictionary(JsonElement element)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in element.EnumerateObject())
                    dict[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString();
                break;
            case JsonValueKind.String:
                foreach (var line in (element.GetString() ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var i = line.IndexOf(':');
                    if (i > 0) dict[line[..i].Trim()] = line[(i + 1)..].Trim();
                }
                break;
        }
        return dict;
    }

    /// <summary>执行请求（同步阻塞）。异常时返回 Ok=false 的空响应（对齐 Connect.error）。</summary>
    public static SpiderHttpResponse Request(string url, SpiderReqOptions opt)
    {
        var result = new SpiderHttpResponse { Buffer = opt.Buffer };
        try
        {
            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = opt.Redirect == 1,
                AutomaticDecompression = DecompressionMethods.All,
                UseCookies = false, // Cookie 由源自己管理（headers/cookie 项）
                ConnectTimeout = TimeSpan.FromSeconds(Math.Min(20, Math.Max(3, opt.Timeout / 1000))),
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(Math.Max(2000, opt.Timeout)) };

            using var req = new HttpRequestMessage(new HttpMethod(opt.Method.ToUpperInvariant()), url);
            req.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
            req.Headers.TryAddWithoutValidation("Accept", "*/*");
            foreach (var (k, v) in opt.Headers)
                req.Headers.TryAddWithoutValidation(k, v);
            if (!string.IsNullOrEmpty(opt.Cookie))
                req.Headers.TryAddWithoutValidation("Cookie", opt.Cookie);

            var method = opt.Method.ToUpperInvariant();
            if (method is "POST" or "PUT" or "PATCH")
                req.Content = BuildContent(opt);

            using var resp = client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            result.Status = (int)resp.StatusCode;
            result.Ok = resp.IsSuccessStatusCode;
            foreach (var h in resp.Headers)
                result.Headers[h.Key] = string.Join(", ", h.Value);
            foreach (var h in resp.Content.Headers)
                result.Headers.TryAdd(h.Key, string.Join(", ", h.Value));

            var bytes = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            result.ContentBytes = bytes;
            result.Buffer = opt.Buffer;
            result.Content = opt.Buffer switch
            {
                2 => Convert.ToBase64String(bytes),
                _ => Decode(bytes, opt.Charset),
            };
            return result;
        }
        catch (Exception)
        {
            return result; // Ok=false, 空 content —— 对齐 Connect.error
        }
    }

    private static string Decode(byte[] bytes, string charset)
    {
        try
        {
            var enc = charset.Equals("UTF-8", StringComparison.OrdinalIgnoreCase)
                ? Encoding.UTF8
                : Encoding.GetEncoding(charset);
            return enc.GetString(bytes);
        }
        catch
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }

    private static HttpContent BuildContent(SpiderReqOptions opt)
    {
        // data 优先：按 postType 序列化；否则用原始 body
        if (opt.Data is { } data)
        {
            switch (opt.PostType.ToLowerInvariant())
            {
                case "form":
                {
                    var pairs = new List<string>();
                    if (data.ValueKind == JsonValueKind.Object)
                        foreach (var p in data.EnumerateObject())
                            pairs.Add($"{Uri.EscapeDataString(p.Name)}={Uri.EscapeDataString(p.Value.ToString())}");
                    return new StringContent(string.Join("&", pairs), Encoding.UTF8,
                        "application/x-www-form-urlencoded");
                }
                case "form-data":
                {
                    var form = new MultipartFormDataContent();
                    if (data.ValueKind == JsonValueKind.Object)
                        foreach (var p in data.EnumerateObject())
                            form.Add(new StringContent(p.Value.ToString()), p.Name);
                    return form;
                }
                default: // json
                {
                    var text = data.ValueKind == JsonValueKind.String ? data.GetString() ?? "" : data.ToString();
                    return new StringContent(text, Encoding.UTF8, "application/json");
                }
            }
        }

        if (!string.IsNullOrEmpty(opt.Body))
            return new StringContent(opt.Body, Encoding.UTF8,
                opt.Headers.TryGetValue("Content-Type", out var ct) ? ct : "application/octet-stream");

        return new StringContent("", Encoding.UTF8);
    }
}
