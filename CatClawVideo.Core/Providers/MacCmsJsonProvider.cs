using System.Text.Json;
using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;

namespace CatClawVideo.Core.Providers;

/// <summary>
/// MacCMS JSON 资源站适配器（苹果 CMS V10 标准 json 接口）：
/// 分类 ac=list / 列表·详情·搜索 ac=videolist（t=分类 ids=指定 wd=关键词 pg=页）。
/// 播放地址：vod_play_from 与 vod_play_url 按 $$$ 对齐拆线路，线路内 集$直链 以 # 分隔。
/// 直链多为 m3u8/mp4（可带 302 跳转），html 页面类直链交给播放器直试（嗅探后续版本）。
/// </summary>
public class MacCmsJsonProvider : IVodSourceProvider
{
    private static readonly HttpClient Http = CreateHttp();

    /// <summary>带浏览器 UA + 20s 超时（量子源偶发慢响应，15s 会误超时）</summary>
    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/124.0");
        return client;
    }
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public string Id => "maccms-json";
    public string Name => "MacCMS JSON 源";

    public bool CanHandle(VodSiteInfo site) =>
        site.Type == 1 || site.Api.Contains("api.php/provide/vod", StringComparison.OrdinalIgnoreCase);

    private static string BuildUrl(string api, string query) =>
        api.TrimEnd('/') + (api.Contains('?') ? "&" : "?") + query;

    public async Task<List<VodCategory>> GetCategoriesAsync(VodSiteInfo site, CancellationToken ct = default)
    {
        try
        {
            var raw = await GetJsonAsync(BuildUrl(site.Api, "ac=list"), ct);
            if (string.IsNullOrWhiteSpace(raw)) return [];
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

    public async Task<List<VodItem>> GetItemsAsync(VodSiteInfo site, VodCategory category, int page = 1, CancellationToken ct = default)
    {
        var query = category.Id.Length > 0
            ? $"ac=videolist&t={Uri.EscapeDataString(category.Id)}&pg={page}"
            : $"ac=videolist&pg={page}";
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
                    Title = it.TryGetProperty("vod_name", out var n) ? n.GetString() ?? "" : "",
                    Cover = it.TryGetProperty("vod_pic", out var p) ? p.GetString() : null,
                    Category = it.TryGetProperty("type_name", out var c) ? c.GetString() : null,
                    Year = it.TryGetProperty("vod_year", out var y) ? y.GetString() : null,
                    Area = it.TryGetProperty("vod_area", out var a) ? a.GetString() : null,
                    Remarks = it.TryGetProperty("vod_remarks", out var r) ? r.GetString() : null,
                    Score = it.TryGetProperty("vod_score", out var s) && s.TryGetDouble(out var sv) ? sv : 0,
                });
            }
            Log($"[items] return {items.Count} 条");
            return items;
        }
        catch { return []; }
    }

    public async Task<List<VodPlaySource>> GetPlaySourcesAsync(VodSiteInfo site, VodItem item, CancellationToken ct = default)
    {
        try
        {
            var raw = await GetJsonAsync(BuildUrl(site.Api, $"ac=videolist&ids={Uri.EscapeDataString(item.Id)}"), ct);
            if (string.IsNullOrWhiteSpace(raw)) return [];
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

    public Task<PlayRequest> ResolvePlayUrlAsync(VodSiteInfo site, VodEpisode episode, CancellationToken ct = default)
    {
        // MacCMS 直链源：集地址即播放地址（m3u8/mp4 或 302 跳转直链），页面嗅探随后续版本
        return Task.FromResult(new PlayRequest
        {
            Title = episode.Name,
            Url = episode.Url,
            Referer = site.Api,
        });
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
