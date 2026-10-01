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

        var year = DateTime.Now.Year;
        // TVBox 同参数：sort=U 综合排序、range 0-10 全分段、playable=1 只要有片源的
        var url = "https://movie.douban.com/j/new_search_subjects?sort=U&range=0,10&tags=&playable=1" +
                  $"&start=0&year_range={year},{year}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // 豆瓣对无 UA/无 Referer 的裸请求返回 418；带齐浏览器头可直连
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
            req.Headers.TryAddWithoutValidation("Referer", "https://movie.douban.com/");
            req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);

            var list = new List<DoubanEntry>();
            if (doc.RootElement.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
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
            }

            Cache[key] = (DateTime.UtcNow, list);
            return list;
        }
        catch (Exception)
        {
            // 失败也缓存（空表走 FailTtl）：豆瓣不可达时避免每次切回主页都等 8s 超时
            Cache[key] = (DateTime.UtcNow, new List<DoubanEntry>());
            return new List<DoubanEntry>();
        }
    }
}
