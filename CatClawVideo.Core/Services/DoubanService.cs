using System.Text.Json;

namespace CatClawVideo.Core.Services;

/// <summary>豆瓣推荐条目（标题 + 评分 + 封面）。</summary>
public class DoubanEntry
{
    public string Title { get; set; } = string.Empty;
    public string Rate { get; set; } = string.Empty;
    public string Cover { get; set; } = string.Empty;
}

/// <summary>
/// 豆瓣热门推荐（对位 TVBox 首页「豆瓣热播」行——壳自己拉豆瓣，不依赖任何源）。
///
/// <para>接口与 TVBox <c>UserFragment.setDouBanData</c> 同款：<c>/j/new_search_subjects</c>
///（按当前年份过滤、playable=1），返回 <c>data[]</c> 的 title/rate/cover。
/// 带浏览器 UA 可直连；结果缓存 30 分钟，失败结果缓存 2 分钟（豆瓣被墙/限流时
/// 不要每次切回主页都重试超时）。</para>
/// </summary>
public static class DoubanService
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(6),
    });

    private static readonly Dictionary<string, (DateTime At, List<DoubanEntry> Items)> Cache = new(StringComparer.Ordinal);

    private static readonly TimeSpan OkTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FailTtl = TimeSpan.FromMinutes(2);

    /// <summary>取豆瓣热播列表（正热映/热播，当前年份）；失败返回空表（调用方回退，不抛）。</summary>
    public static async Task<List<DoubanEntry>> GetHotAsync(int limit, CancellationToken ct = default)
    {
        var key = $"hot|{limit}";
        if (Cache.TryGetValue(key, out var hit))
        {
            var ttl = hit.Items.Count > 0 ? OkTtl : FailTtl;
            if (DateTime.UtcNow - hit.At < ttl) return hit.Items;
            Cache.Remove(key);
        }

        var list = await FetchNewSearchAsync(limit, ct).ConfigureAwait(false);

        // 回退：new_search_subjects 拉不到（网络/风控差异）→ 老接口 search_subjects
        // 电影+剧集各取一半混合（与「豆瓣热播」行同形态）。都空才认失败。
        if (list.Count == 0)
        {
            var half = Math.Max(1, limit / 2);
            var movies = await FetchSearchSubjectsAsync("movie", "热门", half, ct).ConfigureAwait(false);
            var tvs = await FetchSearchSubjectsAsync("tv", "热门", half, ct).ConfigureAwait(false);
            list = movies.Concat(tvs).Take(limit).ToList();
        }

        Cache[key] = (DateTime.UtcNow, list);   // 失败（空表）也缓存，走 FailTtl 防止反复超时
        return list;
    }

    /// <summary>TVBox 同款新接口（当前年份、playable=1），返回 data[]。</summary>
    private static async Task<List<DoubanEntry>> FetchNewSearchAsync(int limit, CancellationToken ct)
    {
        var year = DateTime.Now.Year;
        var url = "https://movie.douban.com/j/new_search_subjects?sort=U&range=0,10&tags=&playable=1" +
                  $"&start=0&year_range={year},{year}";
        var json = await FetchJsonAsync(url, ct).ConfigureAwait(false);
        if (json is null) return [];
        return ParseArray(json.RootElement, "data", limit);
    }

    /// <summary>老接口 search_subjects（兼容回退），返回 subjects[]。</summary>
    private static async Task<List<DoubanEntry>> FetchSearchSubjectsAsync(string type, string tag, int limit, CancellationToken ct)
    {
        var url = $"https://movie.douban.com/j/search_subjects?type={Uri.EscapeDataString(type)}" +
                  $"&tag={Uri.EscapeDataString(tag)}&sort=recommend&page_limit={limit}&page_start=0";
        var json = await FetchJsonAsync(url, ct).ConfigureAwait(false);
        if (json is null) return [];
        return ParseArray(json.RootElement, "subjects", limit);
    }

    /// <summary>带浏览器头拉 JSON（豆瓣对裸请求返回 418）；失败返回 null。</summary>
    private static async Task<JsonDocument?> FetchJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
            req.Headers.TryAddWithoutValidation("Referer", "https://movie.douban.com/");
            req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>从 data[]/subjects[] 提取 title/rate/cover。</summary>
    private static List<DoubanEntry> ParseArray(JsonElement root, string field, int limit)
    {
        var list = new List<DoubanEntry>();
        if (!root.TryGetProperty(field, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var s in arr.EnumerateArray())
        {
            var title = s.TryGetProperty("title", out var t) ? t.GetString() : null;
            var cover = s.TryGetProperty("cover", out var c) ? c.GetString() : null;
            if (string.IsNullOrWhiteSpace(title)) continue;
            list.Add(new DoubanEntry
            {
                Title = title,
                Rate = s.TryGetProperty("rate", out var r) ? (r.GetString() ?? "") : "",
                Cover = cover ?? "",
            });
            if (list.Count >= limit) break;
        }
        return list;
    }
}
