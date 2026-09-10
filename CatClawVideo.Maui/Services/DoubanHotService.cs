using System.Text.Json;

namespace CatClawVideo.Maui.Services;

/// <summary>
/// 豆瓣热门片单（TVBox 同源思路）：/j/search_subjects 拉热门电影+剧集，标题作搜索页热搜词。
/// 会话级缓存；任一分类失败跳过，全部失败返回空列表（调用方降级隐藏热搜区，不放假数据）。
/// </summary>
public static class DoubanHotService
{
    private static readonly HttpClient Http = CreateHttp();
    private static List<string>? _cache;

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/124.0");
        client.DefaultRequestHeaders.Referrer = new Uri("https://movie.douban.com/");
        return client;
    }

    /// <summary>热搜词：热门电影 + 热门剧集标题合并去重，最多 10 个</summary>
    public static async Task<List<string>> GetHotWordsAsync()
    {
        if (_cache is { Count: > 0 }) return _cache;

        var words = new List<string>();
        foreach (var type in new[] { "movie", "tv" })
        {
            try
            {
                var url = "https://movie.douban.com/j/search_subjects" +
                          $"?type={type}&tag=%E7%83%AD%E9%97%A8&sort=recommend&page_limit=12&page=1";
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
                if (doc.RootElement.TryGetProperty("subjects", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var s in arr.EnumerateArray())
                        if (s.TryGetProperty("title", out var t) && t.GetString() is { Length: > 0 } title)
                            words.Add(title);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Douban] {type} 热门拉取失败: {ex.Message}");
            }
        }

        if (words.Count > 0)
            _cache = words.Distinct().Take(10).ToList();
        return _cache ?? [];
    }
}
