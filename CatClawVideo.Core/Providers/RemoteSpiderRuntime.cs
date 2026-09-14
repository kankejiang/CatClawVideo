using System.Net;
using System.Text.Json;
using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;

namespace CatClawVideo.Core.Providers;

/// <summary>
/// 远端解析节点（手机）配置。
///
/// <para><b>为什么需要它</b>：Guard 加固 jar 的解密器是 ARM Android native
/// （<c>JNI_OnLoad</c> + <c>RegisterNatives</c> 动态注册），Windows 的 x64 JVM 没有执行路径；
/// 而加固框架又把解密后的 dex 写成 <c>code_cache/sharedb/config.db</c> 后**立即删除**（反 dump），
/// 所以「导出明文 dex 给 PC」这条路走不通。</para>
///
/// <para>于是改让**手机当解析节点**：手机本来就能跑 Guard（原生环境），把 spider 的调用
/// 转成 HTTP 给 PC 用。<b>代价很低</b> —— 元数据解析只是取页 + 解析 JSON/HTML（几毫秒 CPU），
/// 真正会让手机发烫的是「搬媒体流」，而本方案里播放地址由 PC 直接拉取
/// （绝大多数 Guard 站点是网页抓取，返回的是远程 m3u8/mp4，不是 127.0.0.1）。</para>
/// </summary>
public static class RemoteSpiderNode
{
    private static string? _baseUrl;
    private static bool _loaded;

    /// <summary>
    /// 宿主注入的读取器（如 MAUI Preferences）。**延迟到首次使用时才调** ——
    /// MauiProgram 早期平台尚未初始化，直接读 Preferences/FileSystem 会炸。
    /// </summary>
    public static Func<string?>? Loader { get; set; }

    /// <summary>宿主注入的写入器（(baseUrl, token) => 持久化）。Core 不依赖 MAUI Preferences。</summary>
    public static Action<string?, string?>? Saver { get; set; }

    /// <summary>设置并持久化节点地址/口令（设置页与扫码配对都走这里）</summary>
    public static void Set(string? baseUrl, string? token = null)
    {
        BaseUrl = baseUrl;
        Token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        try { Saver?.Invoke(BaseUrl, Token); } catch { }
    }

    /// <summary>清除节点配置（回落到纯本地）</summary>
    public static void Clear() => Set(null, null);

    /// <summary>节点基地址，形如 <c>http://192.168.1.5:8899</c>；空 = 未配置（全部走本地）。</summary>
    public static string? BaseUrl
    {
        get
        {
            if (!_loaded)
            {
                _loaded = true;
                try { _baseUrl = Loader?.Invoke()?.Trim(); } catch { }
            }
            return string.IsNullOrWhiteSpace(_baseUrl) ? null : _baseUrl;
        }
        set
        {
            _loaded = true;
            _baseUrl = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    /// <summary>可选共享口令（手机端启动时打印）。</summary>
    public static string? Token
    {
        get
        {
            if (!_tokenLoaded)
            {
                _tokenLoaded = true;
                try { _token = TokenLoader?.Invoke()?.Trim(); } catch { }
            }
            return string.IsNullOrWhiteSpace(_token) ? null : _token;
        }
        set
        {
            _tokenLoaded = true;
            _token = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    private static string? _token;
    private static bool _tokenLoaded;

    /// <summary>口令牌的宿主读取器（与 <see cref="Loader"/> 同理，延迟读取）。</summary>
    public static Func<string?>? TokenLoader { get; set; }

    /// <summary>本机是否是「节点提供方」（手机端）。PC 端不需要。</summary>
    public static bool IsNodeHost { get; set; }

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);

    /// <summary>该站点是否属于「本地跑不了、必须借手机」的类型</summary>
    public static bool NeedsRemote(VodSiteInfo site) =>
        IsConfigured
        && site.SpiderKind == VodSpiderKind.Jar
        // 订阅里 Guard 站点的 api 一律是 csp_XxxGuard —— 不用下载 jar 就能判，且极准
        && site.Api.EndsWith("Guard", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 把 <see cref="ISpiderRuntime"/> 的调用转发到手机端 HTTP 服务。
/// 协议：<c>GET {base}/spider?site=&amp;method=home|category|detail|search|player&amp;…</c>，
/// 响应统一包 <c>{"ok":bool,"result":"&lt;TVBox 协议 JSON 字符串&gt;"}</c>。
/// </summary>
public sealed class RemoteSpiderRuntime : ISpiderRuntime
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(90) };
    private readonly Action<string>? _log;

    public RemoteSpiderRuntime(Action<string>? log = null) => _log = log;

    public string Id => "remote-node";
    public string Name => "手机解析节点";

    /// <summary>恒为 true —— 可达性在调用时判定，失败由上层回退到本地</summary>
    public bool IsSupported => true;

    public Task<string> HomeContentAsync(VodSiteInfo site, CancellationToken ct = default)
        => CallAsync(site, "home", [], ct);

    public Task<string> CategoryContentAsync(VodSiteInfo site, string tid, string pg, CancellationToken ct = default)
        => CallAsync(site, "category", new() { ["tid"] = tid, ["pg"] = pg }, ct);

    public Task<string> DetailContentAsync(VodSiteInfo site, string id, CancellationToken ct = default)
        => CallAsync(site, "detail", new() { ["id"] = id }, ct);

    public Task<string> SearchContentAsync(VodSiteInfo site, string keyword, string pg, CancellationToken ct = default)
        => CallAsync(site, "search", new() { ["wd"] = keyword, ["pg"] = pg }, ct);

    public Task<string> PlayerContentAsync(VodSiteInfo site, string flag, string id, CancellationToken ct = default)
        => CallAsync(site, "player", new() { ["flag"] = flag, ["id"] = id }, ct);

    private async Task<string> CallAsync(VodSiteInfo site, string method, Dictionary<string, string?> args, CancellationToken ct)
    {
        var baseUrl = RemoteSpiderNode.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("未配置手机解析节点");

        var url = $"{baseUrl.TrimEnd('/')}/spider?site={Uri.EscapeDataString(site.Key)}&method={method}";
        foreach (var kv in args)
            if (kv.Value is not null)
                url += $"&{kv.Key}={Uri.EscapeDataString(kv.Value)}";
        if (!string.IsNullOrEmpty(RemoteSpiderNode.Token))
            url += $"&token={Uri.EscapeDataString(RemoteSpiderNode.Token)}";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        sw.Stop();

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"手机节点 HTTP {(int)resp.StatusCode}：{Clip(body)}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
        {
            var err = root.TryGetProperty("error", out var e) ? e.GetString() : body;
            throw new InvalidOperationException($"手机解析失败：{Clip(err ?? "")}");
        }

        var result = root.TryGetProperty("result", out var r) ? r.GetString() ?? "{}" : "{}";
        _log?.Invoke($"[远程] {site.Key}.{method} ← {result.Length}B / {sw.ElapsedMilliseconds}ms");
        return result;
    }

    private static string Clip(string s) => s.Length <= 300 ? s : s[..300] + "…";
}

/// <summary>
/// 主备运行时：先试 <paramref name="primary"/>，抛异常就退到 <paramref name="secondary"/>。
/// 用于「Guard 站点走手机、手机不在就退本地（含非 Guard 替代 jar）」。
/// </summary>
public sealed class FallbackSpiderRuntime : ISpiderRuntime
{
    private readonly ISpiderRuntime _primary;
    private readonly ISpiderRuntime _secondary;
    private readonly Action<string>? _log;

    public FallbackSpiderRuntime(ISpiderRuntime primary, ISpiderRuntime secondary, Action<string>? log = null)
    {
        _primary = primary;
        _secondary = secondary;
        _log = log;
    }

    public string Id => $"{_primary.Id}+{_secondary.Id}";
    public bool IsSupported => _primary.IsSupported || _secondary.IsSupported;

    private async Task<string> RunAsync(Func<ISpiderRuntime, Task<string>> call, string what)
    {
        try
        {
            return await call(_primary).ConfigureAwait(false);
        }
        catch (System.Exception ex)
        {
            _log?.Invoke($"[远程] {what} 失败，回退本地：{ex.Message}");
            return await call(_secondary).ConfigureAwait(false);
        }
    }

    public Task<string> HomeContentAsync(VodSiteInfo site, CancellationToken ct = default)
        => RunAsync(rt => rt.HomeContentAsync(site, ct), "homeContent");

    public Task<string> CategoryContentAsync(VodSiteInfo site, string tid, string pg, CancellationToken ct = default)
        => RunAsync(rt => rt.CategoryContentAsync(site, tid, pg, ct), "categoryContent");

    public Task<string> DetailContentAsync(VodSiteInfo site, string id, CancellationToken ct = default)
        => RunAsync(rt => rt.DetailContentAsync(site, id, ct), "detailContent");

    public Task<string> SearchContentAsync(VodSiteInfo site, string keyword, string pg, CancellationToken ct = default)
        => RunAsync(rt => rt.SearchContentAsync(site, keyword, pg, ct), "searchContent");

    public Task<string> PlayerContentAsync(VodSiteInfo site, string flag, string id, CancellationToken ct = default)
        => RunAsync(rt => rt.PlayerContentAsync(site, flag, id, ct), "playerContent");
}
