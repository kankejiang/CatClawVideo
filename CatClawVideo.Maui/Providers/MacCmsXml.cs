using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using CatClawVideo.Core.Models;

namespace CatClawVideo.Core.Providers;

/// <summary>
/// 苹果 CMS 的 <b>XML</b> 接口解析（TVBox 里 <c>type == 0</c> 那一支：
/// <c>SourceViewModel.java:482-503</c> 对 type 0 发 <c>ac=videolist</c> 后走 <c>xml(...)</c> 解析，
/// type 1 才走 JSON —— 所以 type 0 不是「不支持的格式」，只是同一套查询换一种响应体）。
///
/// <para>文档形状：<c>&lt;rss&gt;&lt;class&gt;&lt;ty id="1"&gt;电影&lt;/ty&gt;&lt;/class&gt;
/// &lt;list page=… pagecount=…&gt;&lt;video id= name= pic= year= area= note=&gt;
/// &lt;dl&gt;&lt;dd flag="qq"&gt;第1集$url#第2集$url&lt;/dd&gt;&lt;/dl&gt;&lt;/video&gt;&lt;/list&gt;&lt;/rss&gt;</c>
/// —— 字段全在<b>属性</b>上，剧集在 <c>dd</c> 的<b>文本</b>里。</para>
///
/// <para><b>安全</b>：这是网络来的 XML，解析器一律禁 DTD、禁外部实体、限文档大小，
/// 否则一个带外部实体的响应就能让进程去读本地文件或被打成 XXE。</para>
/// </summary>
public static class MacCmsXml
{
    /// <summary>响应体是不是 XML（JSON 永远不以 <c>&lt;</c> 开头，够用来分流）。</summary>
    public static bool LooksLikeXml(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var i = 0;
        while (i < raw.Length && char.IsWhiteSpace(raw[i])) i++;
        if (i < raw.Length && raw[i] == '\uFEFF') i++;
        return i < raw.Length && raw[i] == '<';
    }

    /// <summary>解析文档；非法/超限 XML 返回 null（调用方按「无数据」处理，不抛）。</summary>
    public static XDocument? Parse(string raw)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,   // 禁 DOCTYPE：挡 XXE 与实体膨胀
                XmlResolver = null,
                MaxCharactersInDocument = 8_000_000,
                MaxCharactersFromEntities = 0,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                CloseInput = true,
            };
            using var reader = XmlReader.Create(new System.IO.StringReader(raw), settings);
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch
        {
            return null;
        }
    }

    static string Attr(XElement e, string name)
    {
        var v = e.Attribute(name)?.Value;
        return string.IsNullOrEmpty(v) ? "" : v.Trim();
    }

    static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    /// <summary><c>ac=list</c> 的 <c>class/ty</c> → 分类。</summary>
    public static List<VodCategory> ParseCategories(string raw)
    {
        var list = new List<VodCategory>();
        var doc = Parse(raw);
        if (doc?.Root is null) return list;
        foreach (var ty in doc.Root.Descendants("ty"))
        {
            var id = Attr(ty, "id");
            var name = (ty.Value ?? "").Trim();
            if (id.Length > 0 && name.Length > 0) list.Add(new VodCategory { Id = id, Name = name });
        }
        return list;
    }

    /// <summary><c>list/video</c> → 影片条目（字段在属性上）。</summary>
    public static List<VodItem> ParseItems(string raw, string sourceKey)
    {
        var list = new List<VodItem>();
        var doc = Parse(raw);
        if (doc?.Root is null) return list;
        foreach (var v in doc.Root.Descendants("video"))
        {
            var id = Attr(v, "id");
            if (id.Length == 0) continue;
            list.Add(new VodItem
            {
                Id = id,
                SourceKey = sourceKey,
                Title = Attr(v, "name"),
                Cover = NullIfEmpty(Attr(v, "pic")),
                Category = NullIfEmpty(Attr(v, "type")),
                Year = NullIfEmpty(Attr(v, "year")),
                Area = NullIfEmpty(Attr(v, "area")),
                Remarks = NullIfEmpty(Attr(v, "note")),
                Score = ParseScore(Attr(v, "score")),
            });
        }
        return list;
    }

    /// <summary>
    /// <c>video/dl/dd</c> → 线路与剧集。<c>dd@flag</c> 是线路名，文本按 <c>#</c> 分集、
    /// 每集再按第一个 <c>$</c> 分成「集名 / 地址」（与 JSON 路的 <c>vod_play_url</c> 同构）。
    /// </summary>
    public static List<VodPlaySource> ParsePlaySources(string raw)
    {
        var sources = new List<VodPlaySource>();
        var doc = Parse(raw);
        if (doc?.Root is null) return sources;
        var video = doc.Root.Descendants("video").FirstOrDefault();
        if (video is null) return sources;

        foreach (var dd in video.Descendants("dd"))
        {
            var flag = Attr(dd, "flag");
            var source = new VodPlaySource { Name = flag.Length > 0 ? flag : "默认线路" };
            foreach (var ep in (dd.Value ?? "").Split('#',
                         StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var sep = ep.IndexOf('$');
                if (sep <= 0) continue;
                source.Episodes.Add(new VodEpisode
                {
                    Name = ep[..sep].Trim(),
                    Url = ep[(sep + 1)..].Trim(),
                    Flag = flag,
                });
            }
            if (source.Episodes.Count > 0) sources.Add(source);
        }
        return sources;
    }

    static double ParseScore(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
