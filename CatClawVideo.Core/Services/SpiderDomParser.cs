using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

namespace CatClawVideo.Core.Services;

/// <summary>
/// TVBox JS Spider 的 DOM 规则解析宿主：逐行移植 TVBoxOSC
/// <c>crawler.js.HtmlParser</c>（jsoup 语义）到 AngleSharp。
/// <para>
/// 海阔规则语法：<c>body&&.tag&&Text</c>（&& 分段，末段为取值方式）、
/// <c>:eq(n)/:lt(n)/:gt(n)/:first/:last</c>（jsoup 扩展伪类，AngleSharp 不识别，本类剥掉后手动套用）、
/// <c>--</c>（排除子选择器）、<c>style</c> 取值自动提取 <c>url(...)</c>、
/// <c>url/src/href/-original/-play</c> 结尾自动 joinUrl。
/// </para>
/// </summary>
public static class SpiderDomParser
{
    private const int DocCacheLimit = 4;

    private static readonly Regex StyleUrlRegex =
        new("url\\((.*?)\\)", RegexOptions.Multiline | RegexOptions.Singleline);

    /// <summary>不自动补 eq 下标索引的规则片段</summary>
    private static readonly Regex NoAddIndex =
        new(":eq|:lt|:gt|:first|:last|^body$|^#", RegexOptions.Compiled);

    /// <summary>需要自动 urljoin 的属性后缀</summary>
    private static readonly Regex UrlJoinAttr =
        new("(url|src|href|-original|-src|-play|-url|style)$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>特殊协议链接，不走 urlJoin</summary>
    private static readonly Regex SpecialUrl =
        new("^(ftp|magnet|thunder|ws):", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly AngleSharp.Html.Parser.HtmlParser Parser = new();

    // 文档缓存：同一段 html 的 pdfh/pdfa 反复查询免重复 parse（对齐 Java pdfh_doc/pdfa_doc 缓存语义）
    private static readonly object DocCacheLock = new();
    private static readonly List<(string Html, IHtmlDocument Doc)> DocCache = new();

    // ═══════════════════ 对外 API ═══════════════════

    /// <summary>JS: <c>joinUrl(parent, child)</c>。</summary>
    public static string JoinUrl(string? parent, string? child)
    {
        if (string.IsNullOrEmpty(parent)) return child ?? "";
        if (string.IsNullOrEmpty(child)) return parent;
        if (child.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return child;
        try { return new Uri(new Uri(parent), child).ToString(); }
        catch { return child; }
    }

    /// <summary>JS: <c>pdfh(html, rule)</c> / <c>pd(html, rule, add_url)</c> —— 取单个值。</summary>
    public static string Pdfh(string html, string rule, string addUrl = "")
    {
        try
        {
            var doc = GetDocument(html);
            if (rule is "body&&Text" or "Text") return doc.Body?.TextContent ?? doc.TextContent;
            if (rule is "body&&Html" or "Html") return doc.DocumentElement?.InnerHtml ?? "";

            var option = "";
            if (rule.Contains("&&"))
            {
                var rs = rule.Split("&&");
                option = rs[^1];
                rule = string.Join("&&", rs[..^1]);
            }

            rule = ParseHikerToJq(rule, first: true);
            var ret = new List<IElement>();
            foreach (var nparse in rule.Split(' '))
            {
                ret = ParseOneRule(doc, ret, nparse);
                if (ret.Count == 0) return "";
            }

            if (string.IsNullOrEmpty(option)) return ConcatHtml(ret);

            if (option is "Text") return ConcatText(ret);
            if (option is "Html") return ConcatHtml(ret);

            var result = ret.Count > 0 ? ret[0].GetAttribute(option) ?? "" : "";
            if (option.Contains("style", StringComparison.OrdinalIgnoreCase) && result.Contains("url("))
            {
                var m = StyleUrlRegex.Match(result);
                if (m.Success) result = m.Groups[1].Value;
                result = Regex.Replace(result, "^['|\"](.*)['|\"]$", "$1");
            }

            if (!string.IsNullOrEmpty(result) && !string.IsNullOrEmpty(addUrl)
                && UrlJoinAttr.IsMatch(option) && !SpecialUrl.IsMatch(result))
            {
                var httpIdx = result.IndexOf("http", StringComparison.Ordinal);
                result = httpIdx > 0 ? result[httpIdx..] : JoinUrl(addUrl, result);
            }
            return result;
        }
        catch { return ""; }
    }

    /// <summary>JS: <c>pdfa(html, rule)</c> —— 取元素列表（outerHtml 数组）。</summary>
    public static List<string> Pdfa(string html, string rule)
    {
        try
        {
            var doc = GetDocument(html);
            rule = ParseHikerToJq(rule, first: false);
            var ret = new List<IElement>();
            foreach (var nparse in rule.Split(' '))
            {
                ret = ParseOneRule(doc, ret, nparse);
                if (ret.Count == 0) return new List<string>();
            }
            return ret.Select(e => e.OuterHtml).ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>
    /// JS: <c>pdfla(html, p1, list_text, list_url, add_url)</c> ——
    /// 列表页专用：先取每条记录的 outerHtml，再对每条分别套 text/url 两条规则，拼成 <c>文本$链接</c>。
    /// </summary>
    public static List<string> Pdffl(string html, string p1, string listText, string listUrl, string addUrl)
    {
        try
        {
            var doc = GetDocument(html);
            p1 = ParseHikerToJq(p1, first: false);
            var ret = new List<IElement>();
            foreach (var nparse in p1.Split(' '))
            {
                ret = ParseOneRule(doc, ret, nparse);
                if (ret.Count == 0) return new List<string>();
            }

            var result = new List<string>();
            foreach (var element in ret)
            {
                var it = element.OuterHtml;
                var text = Pdfh(it, listText, "").Trim();
                var url = Pdfh(it, listUrl, addUrl);
                result.Add(text + "$" + url);
            }
            return result;
        }
        catch { return new List<string>(); }
    }

    // ═══════════════════ 规则解析核心 ═══════════════════

    private sealed record ParseInfo(string Rule, int Index, List<string> Excludes, string? Pseudo);

    /// <summary>剥掉 jsoup 扩展伪类（:eq/:lt/:gt/:first/:last），返回可交给 AngleSharp 的 CSS 与索引信息。</summary>
    private static ParseInfo GetParseInfo(string nparse)
    {
        var rule = nparse;
        var index = 0;
        var excludes = new List<string>();
        string? pseudo = null;

        // :lt(n)/:gt(n)/:first/:last —— AngleSharp 不识别，剥掉后手动套用（:eq 在下方单独处理）
        var pm = Regex.Match(nparse, ":(lt|gt)\\((-?\\d+)\\)$|:(first|last)$");
        if (pm.Success && !nparse.Contains(":eq"))
        {
            pseudo = pm.Value[1..];
            rule = nparse[..pm.Index];
        }

        if (nparse.Contains(":eq"))
        {
            var colon = nparse.IndexOf(':');
            rule = nparse[..colon];
            var pos = nparse[(colon + 1)..];

            if (rule.Contains("--"))
            {
                var rules = rule.Split("--");
                excludes.AddRange(rules[1..]);
                rule = rules[0];
            }
            else if (pos.Contains("--"))
            {
                var rules = pos.Split("--");
                excludes.AddRange(rules[1..]);
                pos = rules[0];
            }

            try
            {
                index = int.Parse(pos.Replace("eq(", "").Replace(")", ""));
            }
            catch { index = 0; }
        }
        else if (rule.Contains("--"))
        {
            var rules = rule.Split("--");
            excludes.AddRange(rules[1..]);
            rule = rules[0];
        }

        return new ParseInfo(rule, index, excludes, pseudo);
    }

    /// <summary>海阔表达式转原生表达式：&& 分段后对**末段**自动补 :eq(0)（first=true 时全部补）。</summary>
    private static string ParseHikerToJq(string parse, bool first)
    {
        if (parse.Contains("&&"))
        {
            var parses = parse.Split("&&");
            var newParses = new List<string>();
            for (var i = 0; i < parses.Length; i++)
            {
                var pss = parses[i].Split(' ');
                var ps = pss[^1];
                if (!NoAddIndex.IsMatch(ps))
                {
                    if (!first && i >= parses.Length - 1) newParses.Add(parses[i]);
                    else newParses.Add(parses[i] + ":eq(0)");
                }
                else newParses.Add(parses[i]);
            }
            parse = string.Join(" ", newParses);
        }
        else
        {
            var pss = parse.Split(' ');
            var ps = pss[^1];
            if (!NoAddIndex.IsMatch(ps) && first) parse = parse + ":eq(0)";
        }
        return parse;
    }

    /// <summary>执行一段规则：select（或对上一段结果集继续 select 后代）→ :eq/:lt/:gt/:first/:last 索引 → 排除。</summary>
    private static List<IElement> ParseOneRule(IHtmlDocument doc, List<IElement> ret, string nparse)
    {
        var info = GetParseInfo(nparse);

        IEnumerable<IElement> selected = ret.Count == 0
            ? SelectSafe(doc, info.Rule)
            : ret.SelectMany(el => SelectSafe(el, info.Rule));
        var list = selected.ToList();

        // :eq(n) —— 负数从尾部数（对齐 jsoup Elements.eq）
        if (nparse.Contains(":eq"))
        {
            if (list.Count == 0) return list;
            var idx = info.Index < 0 ? list.Count + info.Index : info.Index;
            return idx >= 0 && idx < list.Count ? new List<IElement> { list[idx] } : new List<IElement>();
        }

        // :lt(n)/:gt(n)/:first/:last —— jsoup 原生支持，AngleSharp 需手动套用
        if (info.Pseudo is not null)
        {
            list = info.Pseudo switch
            {
                "first" => list.Count > 0 ? [list[0]] : list,
                "last" => list.Count > 0 ? [list[^1]] : list,
                "lt" => list.Take(Math.Max(0, info.Index)).ToList(),
                "gt" => list.Skip(info.Index + 1).ToList(),
                _ => list,
            };
        }

        // 排除子选择器：在**临时文档**里删（不动缓存文档）
        if (info.Excludes.Count > 0 && list.Count > 0)
        {
            var clone = new List<IElement>();
            foreach (var el in list)
            {
                var tempDoc = Parser.ParseDocument(el.OuterHtml);
                foreach (var ex in info.Excludes)
                {
                    try
                    {
                        foreach (var hit in tempDoc.QuerySelectorAll(ex)) hit.Remove();
                    }
                    catch { }
                }
                clone.Add(tempDoc.DocumentElement ?? tempDoc.Body!);
            }
            return clone;
        }

        return list;
    }

    private static IEnumerable<IElement> SelectSafe(IParentNode scope, string css)
    {
        try { return scope.QuerySelectorAll(css); }
        catch { return Array.Empty<IElement>(); } // AngleSharp 不认识的选择器 → 空结果
    }

    // ═══════════════════ 文档缓存 ═══════════════════

    private static IHtmlDocument GetDocument(string html)
    {
        lock (DocCacheLock)
        {
            for (var i = 0; i < DocCache.Count; i++)
            {
                if (DocCache[i].Html == html)
                {
                    var hit = DocCache[i];
                    DocCache.RemoveAt(i);
                    DocCache.Insert(0, hit); // LRU 提到队首
                    return hit.Doc;
                }
            }

            var doc = Parser.ParseDocument(html);
            DocCache.Insert(0, (html, doc));
            while (DocCache.Count > DocCacheLimit) DocCache.RemoveAt(DocCache.Count - 1);
            return doc;
        }
    }

    // ═══════════════════ 取值工具 ═══════════════════

    /// <summary>jsoup Elements.text()：多元素文本以空格连接。</summary>
    private static string ConcatText(List<IElement> elements) =>
        string.Join(" ", elements.Select(e => e.TextContent.Trim())
            .Where(t => t.Length > 0));

    /// <summary>jsoup Elements.html()/outerHtml()：多元素以换行连接。</summary>
    private static string ConcatHtml(List<IElement> elements) =>
        string.Join("\n", elements.Select(e => e.InnerHtml));

}
