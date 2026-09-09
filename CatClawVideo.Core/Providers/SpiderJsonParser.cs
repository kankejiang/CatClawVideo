using System.Text.Json;
using CatClawVideo.Core.Models;

namespace CatClawVideo.Core.Providers;

/// <summary>
/// TVBox Spider 协议 JSON → 领域模型解析器。
/// 覆盖 homeContent（class）、categoryContent/searchContent（list）、
/// detailContent（vod_play_from/vod_play_url 线路拆分，与 MacCMS 同构）、playerContent。
/// </summary>
public static class SpiderJsonParser
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>homeContent → 分类列表（class[].type_id/type_name）</summary>
    public static List<VodCategory> ParseCategories(string json)
    {
        var result = new List<VodCategory>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("class", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return result;
            foreach (var c in arr.EnumerateArray())
            {
                var id = c.TryGetProperty("type_id", out var tid) ? tid.ToString() : "";
                var name = c.TryGetProperty("type_name", out var tn) ? tn.GetString() ?? "" : "";
                if (id.Length == 0 || name.Length == 0) continue;
                result.Add(new VodCategory { Id = id, Name = name });
            }
        }
        catch { }
        return result;
    }

    /// <summary>categoryContent / searchContent → 影片列表（list[].vod_*）</summary>
    public static List<VodItem> ParseItems(string json, string sourceKey)
    {
        var result = new List<VodItem>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("list", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return result;
            foreach (var v in arr.EnumerateArray())
            {
                var id = GetStr(v, "vod_id");
                var title = GetStr(v, "vod_name");
                if (id.Length == 0 || title.Length == 0) continue;
                result.Add(new VodItem
                {
                    Id = id,
                    SourceKey = sourceKey,
                    Title = title,
                    Cover = NullToEmpty(GetStr(v, "vod_pic")),
                    Remarks = NullToEmpty(GetStr(v, "vod_remarks")),
                    Category = NullToEmpty(GetStr(v, "type_name")),
                    Year = NullToEmpty(GetStr(v, "vod_year")),
                    Area = NullToEmpty(GetStr(v, "vod_area")),
                    Actors = NullToEmpty(GetStr(v, "vod_actor")),
                    Director = NullToEmpty(GetStr(v, "vod_director")),
                    Description = NullToEmpty(GetStr(v, "vod_content")),
                    Score = v.TryGetProperty("vod_score", out var sc) && sc.TryGetDouble(out var d) ? d : 0,
                });
            }
        }
        catch { }
        return result;
    }

    /// <summary>
    /// detailContent → 播放线路与剧集。
    /// vod_play_from = "线路1$$$线路2"；vod_play_url = "集1$u1#集2$u2$$$集1$u1"（与 MacCMS 同构）。
    /// </summary>
    public static List<VodPlaySource> ParsePlaySources(string json)
    {
        var result = new List<VodPlaySource>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("list", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
                return result;
            var vod = arr[0];
            var from = GetStr(vod, "vod_play_from");
            var urls = GetStr(vod, "vod_play_url");
            if (from.Length == 0 || urls.Length == 0) return result;

            var flags = from.Split("$$$");
            var groups = urls.Split("$$$");
            for (int i = 0; i < groups.Length; i++)
            {
                var eps = new List<VodEpisode>();
                foreach (var seg in groups[i].Split('#', StringSplitOptions.RemoveEmptyEntries))
                {
                    var idx = seg.IndexOf('$');
                    if (idx <= 0) continue;
                    var name = seg[..idx].Trim();
                    var url = seg[(idx + 1)..].Trim();
                    if (url.Length == 0) continue;
                    eps.Add(new VodEpisode { Name = name, Url = url });
                }
                if (eps.Count == 0) continue;
                result.Add(new VodPlaySource
                {
                    Name = i < flags.Length ? flags[i].Trim() : $"线路{i + 1}",
                    Episodes = eps,
                });
            }
        }
        catch { }
        return result;
    }

    /// <summary>playerContent → 播放请求。parse=0 直链；parse=1 返回网页地址待嗅探。</summary>
    public static PlayRequest ParsePlayRequest(string json, string title)
    {
        if (string.IsNullOrWhiteSpace(json)) return new PlayRequest { Title = title };
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var url = GetStr(root, "url");
            var play = new PlayRequest { Title = title, Url = url };
            if (root.TryGetProperty("header", out var h) && h.ValueKind == JsonValueKind.Object)
            {
                if (h.TryGetProperty("Referer", out var r)) play.Referer = r.GetString();
                if (h.TryGetProperty("User-Agent", out var ua)) play.UserAgent = ua.GetString();
            }
            return play;
        }
        catch
        {
            return new PlayRequest { Title = title };
        }
    }

    private static string GetStr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? ""
        : e.TryGetProperty(name, out var v2) && v2.ValueKind == JsonValueKind.Number ? v2.GetRawText()
        : "";

    private static string NullToEmpty(string? s) => s ?? "";
}
