using System.Globalization;
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
                // ⚠️ 单条坏数据不能让整页归零：TVBox 各爬虫的字段类型不一致，
                // 实测某 Guard 站点把 vod_score 返回成字符串 "0.0"，
                // 而 JsonElement.TryGetDouble() 遇到非 Number **会抛异常**（不是返回 false），
                // 一旦抛出就被外层 catch 吞掉 → 整页 0 条（症状是「有数据但暂无影片」）。
                try
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
                        Action = GetAction(v),
                        Tag = NullToEmpty(GetStr(v, "vod_tag")),
                        Score = GetDouble(v, "vod_score"),
                    });
                }
                catch
                {
                    // 跳过这一条，继续后面的
                }
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
                var flagName = i < flags.Length ? flags[i].Trim() : $"线路{i + 1}";
                foreach (var ep in eps) ep.Flag = flagName;   // flag 真传：spider playerContent 按线路分支
                result.Add(new VodPlaySource
                {
                    Name = flagName,
                    Episodes = eps,
                });
            }
        }
        catch { }
        return result;
    }

    /// <summary>playerContent → 播放请求。parse=0 直链；parse=1 返回网页地址待嗅探/JSON 解析。</summary>
    public static PlayRequest ParsePlayRequest(string json, string title)
    {
        if (string.IsNullOrWhiteSpace(json)) return new PlayRequest { Title = title };
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var url = GetStr(root, "url");
            var playUrl = GetStr(root, "playUrl");
            if (playUrl.Length > 0 && url.Length > 0)
                url = playUrl + url;   // TVBox 协议：playUrl 前缀拼接
            var play = new PlayRequest { Title = title, Url = url };
            // Guard 系网盘源的宿主钩子：danmaku 字段指向本地 proxy（do=danmu&url=<vod_id>），
            // GET 该 URL 回调 jar 的 proxy(Map) 触发网盘配置对话框（TVBox 弹幕加载语义）
            play.DanmakuUrl = GetStr(root, "danmaku");
            // 失败原因：网盘源 url 为空时靠这两个字段说明为什么（盘满 / 转存失败 / 无权限…）
            play.Message = NullToEmpty(GetStr(root, "errMsg").Length > 0 ? GetStr(root, "errMsg") : GetStr(root, "msg"));
            // TVBox PlayFragment L1198：parse 缺省按 "1"；但直链源（m3u8/mp4）不嗅探直接播，
            // 故缺省语义收敛为「非视频格式 URL 即走解析」。显式 parse=0/jx=0 必须直连。
            var explicitParse = (bool?)null;
            if (root.TryGetProperty("parse", out var p))
            {
                if (p.ValueKind == JsonValueKind.Number) explicitParse = p.TryGetInt32(out var pi) && pi != 0;
                else if (p.ValueKind == JsonValueKind.True) explicitParse = true;
                else if (p.ValueKind == JsonValueKind.False) explicitParse = false;
            }
            var explicitJx = false;
            if (root.TryGetProperty("jx", out var jx))
            {
                if (jx.ValueKind == JsonValueKind.Number) explicitJx = jx.TryGetInt32(out var ji) && ji != 0;
                else if (jx.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    explicitJx = jx.ValueKind == JsonValueKind.True;
            }
            play.NeedsSniff = explicitParse == true || explicitJx ||
                (explicitParse is null && !explicitJx && url.Length > 0 && !TvBoxParseEngine.IsVideoFormat(url));
            if (root.TryGetProperty("header", out var h) && h.ValueKind == JsonValueKind.Object)
            {
                play.Headers = new Dictionary<string, string>();
                foreach (var kv in h.EnumerateObject())
                {
                    if (kv.Value.ValueKind != JsonValueKind.String) continue;
                    play.Headers[kv.Name] = kv.Value.GetString() ?? "";
                    if (kv.Name.Equals("Referer", StringComparison.OrdinalIgnoreCase)) play.Referer = play.Headers[kv.Name];
                    else if (kv.Name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) play.UserAgent = play.Headers[kv.Name];
                }
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

    /// <summary>取 <c>action</c>：TVBox 里它是**对象**（<c>{"do":2,"key":"..."}</c>），
    /// 而爬虫的 <c>action(String)</c> 要的是 JSON 文本，所以按原样取 raw text 而非字符串值。</summary>
    private static string GetAction(JsonElement e) =>
        e.TryGetProperty("action", out var v) switch
        {
            false => "",
            _ when v.ValueKind == JsonValueKind.Object || v.ValueKind == JsonValueKind.Array => v.GetRawText(),
            _ when v.ValueKind == JsonValueKind.String => v.GetString() ?? "",
            _ => "",
        };

    /// <summary>
    /// 宽松取数：数字直接用；字符串尝试解析。
    /// <para>⚠️ 不能直接调 <c>TryGetDouble</c> —— 它在元素不是 Number 时**抛 InvalidOperationException**
    /// （不是返回 false），实测有站点把 <c>vod_score</c> 返回成 <c>"0.0"</c> 字符串。</para>
    /// </summary>
    public static double GetDouble(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.TryGetDouble(out var d) ? d : 0;
        if (v.ValueKind == JsonValueKind.String)
            return double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 0;
        return 0;
    }

    private static string NullToEmpty(string? s) => s ?? "";
}
