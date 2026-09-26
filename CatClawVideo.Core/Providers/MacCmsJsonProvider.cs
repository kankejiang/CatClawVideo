using System.Text.Json;
using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;

namespace CatClawVideo.Core.Providers;

/// <summary>
/// MacCMS JSON 资源站适配器（苹果 CMS V10 标准 json 接口）：
/// 分类 ac=list / 列表·详情·搜索 ac=videolist（t=分类 ids=指定 wd=关键词 pg=页）。
/// 播放地址：vod_play_from 与 vod_play_url 按 $$$ 对齐拆线路，线路内 集$直链 以 # 分隔。
/// 直链多为 m3u8/mp4（可带 302 跳转）；明显是网页的地址（VIP 站 / .html / /play/）走嗅探链。
/// <b>type 0 与 type 1 同一套查询</b>，只是响应体是 XML —— 由 <see cref="MacCmsXml"/> 分流处理。
/// </summary>
public class MacCmsJsonProvider : IVodSourceProvider
{
    /// <summary>嗅探器（可选）：网页型直链靠它拿直链；null 时这类地址原样交给播放器。</summary>
    private readonly IWebSniffer? _sniffer;

    public MacCmsJsonProvider(IWebSniffer? sniffer = null) => _sniffer = sniffer;

    private static readonly HttpClient Http = CreateHttp();

    /// <summary>带浏览器 UA + 20s 超时（量子源偶发慢响应，15s 会误超时）</summary>
    private static HttpClient CreateHttp()
    {
        // 站点 api 取列表这一跳也走 DoH（TVBox 的 OkGo 是全局挂的）；UA 照旧
        return Services.Doh.NewClient(20, "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/124.0");
    }
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public string Id => "maccms-json";
    public string Name => "MacCMS JSON 源";

    public bool CanHandle(VodSiteInfo site) =>
        (site.Type == 0 || site.Type == 1) ||
        site.Api.Contains("api.php/provide/vod", StringComparison.OrdinalIgnoreCase);

    private static string BuildUrl(string api, string query) =>
        api.TrimEnd('/') + (api.Contains('?') ? "&" : "?") + query;

    public async Task<List<VodCategory>> GetCategoriesAsync(VodSiteInfo site, CancellationToken ct = default)
    {
        try
        {
            var raw = await GetJsonAsync(BuildUrl(site.Api, "ac=list"), ct);
            if (string.IsNullOrWhiteSpace(raw)) return [];
            if (MacCmsXml.LooksLikeXml(raw)) return MacCmsXml.ParseCategories(raw);
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("class", out var cats) || cats.ValueKind != JsonValueKind.Array)
                return [];
            var list = new List<VodCategory>();
            foreach (var c in cats.EnumerateArray())
            {
                var id = c.TryGetProperty("type_id", out var idEl) ? idEl.ToString() : "";
                var name = c.TryGetProperty("type_name", out var n) ? n.GetString() ?? "" : "";
                if (id.Length > 0 && name.Length > 0)
                    list.Add(new VodCategory { Id = id, Name = name });
            }
            return list;
        }
        catch { return []; }
    }

    public async Task<List<VodItem>> GetItemsAsync(VodSiteInfo site, VodCategory category, int page = 1,
        IReadOnlyDictionary<string, string>? filter = null, CancellationToken ct = default)
    {
        var query = category.Id.Length > 0
            ? $"ac=videolist&t={Uri.EscapeDataString(category.Id)}&pg={page}"
            : $"ac=videolist&pg={page}";
        // MacCMS 的筛选就是普通查询参数（area/year/lang/vodclass…），直接拼上去；
        // 站点没声明 filters 时这里恒空，行为与改动前一致。
        if (filter is { Count: > 0 })
        {
            foreach (var (k, v) in filter)
                if (k.Length > 0 && v.Length > 0) query += $"&{Uri.EscapeDataString(k)}={Uri.EscapeDataString(v)}";
            // TVBox 同时再传一个 f=<筛选JSON>（SourceViewModel:487-489），不少 CMS 变体只认这个
            query += "&f=" + Uri.EscapeDataString(JsonSerializer.Serialize(new Dictionary<string, string>(filter)));
        }
        return await FetchItemsAsync(site, query, ct);
    }

    public async Task<List<VodItem>> SearchAsync(VodSiteInfo site, string keyword, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return [];
        return await FetchItemsAsync(site, $"ac=videolist&wd={Uri.EscapeDataString(keyword.Trim())}", ct);
    }

    private async Task<List<VodItem>> FetchItemsAsync(VodSiteInfo site, string query, CancellationToken ct)
    {
        try
        {
            var raw = await GetJsonAsync(BuildUrl(site.Api, query), ct);
            if (string.IsNullOrWhiteSpace(raw)) return [];
            if (MacCmsXml.LooksLikeXml(raw)) return MacCmsXml.ParseItems(raw, site.Key);
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                Log($"[items] 响应无 list 字段，前120字: {(raw.Length > 120 ? raw[..120] : raw)}");
                return [];
            }
            Log($"[items] 解析到 {list.GetArrayLength()} 条");
            var items = new List<VodItem>();
            foreach (var it in list.EnumerateArray())
            {
                if (!it.TryGetProperty("vod_id", out var idEl) || idEl.ValueKind == JsonValueKind.Null) continue;
                items.Add(new VodItem
                {
                    Id = idEl.ToString(),
                    SourceKey = site.Key,
                    Title = Str(it, "vod_name"),
                    Cover = NullIfEmpty(Str(it, "vod_pic")),
                    Category = NullIfEmpty(Str(it, "type_name")),
                    Year = NullIfEmpty(Str(it, "vod_year")),
                    Area = NullIfEmpty(Str(it, "vod_area")),
                    Remarks = NullIfEmpty(Str(it, "vod_remarks")),
                    // ⚠️ 不能直接用 TryGetDouble：元素非 Number 时它**抛异常**（不是返回 false），
                    // 而这里外层是 catch → return []，会整页归零。实测有站点返回 vod_score = "0.0"。
                    Score = SpiderJsonParser.GetDouble(it, "vod_score"),
                });
            }
            Log($"[items] return {items.Count} 条");
            return items;
        }
        catch { return []; }
    }

    /// <summary>宽松取字符串：字符串直接用，数字转文本；缺失/其他类型 → ""。
    /// <para>直接用 <c>GetString()</c> 遇到 Number 会抛 InvalidOperationException。</para></summary>
    private static string Str(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            _ => "",
        };
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    public async Task<List<VodPlaySource>> GetPlaySourcesAsync(VodSiteInfo site, VodItem item, CancellationToken ct = default)
    {
        try
        {
            var raw = await GetJsonAsync(BuildUrl(site.Api, $"ac=videolist&ids={Uri.EscapeDataString(item.Id)}"), ct);
            if (string.IsNullOrWhiteSpace(raw)) return [];
            if (MacCmsXml.LooksLikeXml(raw)) return MacCmsXml.ParsePlaySources(raw);
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array ||
                list.GetArrayLength() == 0)
                return [];

            var it = list[0];
            var from = it.TryGetProperty("vod_play_from", out var f) ? f.GetString() ?? "" : "";
            var playUrl = it.TryGetProperty("vod_play_url", out var u) ? u.GetString() ?? "" : "";

            var sourceNames = from.Split("$$$", StringSplitOptions.TrimEntries);
            var sourceBlocks = playUrl.Split("$$$", StringSplitOptions.TrimEntries);

            var sources = new List<VodPlaySource>();
            for (int i = 0; i < sourceBlocks.Length && i < sourceNames.Length; i++)
            {
                var source = new VodPlaySource { Name = sourceNames[i] };
                foreach (var ep in sourceBlocks[i].Split('#', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var sep = ep.IndexOf('$');
                    if (sep <= 0) continue;
                    source.Episodes.Add(new VodEpisode
                    {
                        Name = ep[..sep].Trim(),
                        Url = ep[(sep + 1)..].Trim(),
                    });
                }
                if (source.Episodes.Count > 0)
                    sources.Add(source);
            }
            return sources;
        }
        catch { return []; }
    }

    public async Task<PlayRequest> ResolvePlayUrlAsync(VodSiteInfo site, VodEpisode episode, CancellationToken ct = default)
    {
        var url = episode.Url ?? "";

        // 磁力 API 站的 vod_play_url 直接是 magnet:：必须转本机 BT 流式代理地址，
        // 否则播放器拿到 magnet: 会以 "unknown protocol: magnet" 报 Source error
        // （与 SpiderVodProvider 同一处理，2026-09-14 真机实测）。
        if (url.StartsWith("ed2k://", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("该集为电驴(ed2k)下载链接，暂不支持在线播放；可复制链接到下载工具");
        if (url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            // 磁力只走迅雷引擎（内置 BT 已移除）
            var engine = Interfaces.MagnetEngines.Thunder;
            if (engine is null || !engine.IsReady)
                throw new NotSupportedException("磁力播放需要迅雷引擎，当前不可用；请确认迅雷运行时已就绪");
            var opened = await engine.TryOpenAsync(url, episode.Name, ct)
                ?? throw new NotSupportedException("迅雷无法解析该磁力链接（无可用资源）");

            // 展示名用种子内真实文件名（而非磁力 dn 打包名），见 SpiderVodProvider 同类注释
            var display = string.IsNullOrWhiteSpace(opened.FileName) ? episode.Name : opened.FileName;
            return new PlayRequest { Title = display, Url = opened.Url };
        }

        // MacCMS 直链源：集地址即播放地址（m3u8/mp4 或 302 跳转直链）。
        var play = new PlayRequest
        {
            Title = episode.Name,
            Url = url,
            Referer = site.Api,
        };

        // 只有「明显是网页」的直链才改道嗅探：CMS 系的 302 直链常常没有扩展名，一律嗅探会把
        // 本来能播的源弄挂；TVBox 对 type=1 源也是直链直播，嗅探只由 parse/jx 触发。
        var looksLikePage = !TvBoxParseEngine.IsVideoFormat(url) &&
                            (TvBoxParseEngine.IsVipUrl(url) ||
                             url.Contains(".html", StringComparison.OrdinalIgnoreCase) ||
                             url.Contains("/play/", StringComparison.OrdinalIgnoreCase));
        if (!looksLikePage || _sniffer is null) return play;

        play.NeedsSniff = true;
        return await TvBoxPlayPipeline.ResolveAsync(site, play, episode.Flag ?? "", _sniffer, ct);
    }

    private static void Log(string msg)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "catclawvideo_http.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
        }
        catch { }
    }

    private static async Task<string?> GetJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "catclawvideo_http.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] {url} => {json.Length} chars{Environment.NewLine}");
            }
            catch { }
            return json;
        }
        catch (Exception ex)
        {
            var errText = ex.Message;
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "catclawvideo_http.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] {url} => ERROR {errText}{Environment.NewLine}");
            }
            catch { }
            return null;
        }
    }
}
