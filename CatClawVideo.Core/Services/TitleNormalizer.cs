using System.Text.RegularExpressions;

namespace CatClawVideo.Core.Services;

/// <summary>
/// 片名规范化：站点给的标题带大量**非片名**的附加信息，直接拿来做联想/索引会出错。
///
/// <para>两类处理，目的不同，别混用：</para>
/// <list type="bullet">
/// <item><see cref="Clean"/> —— 去掉纯状态标注（<c>[全集]</c>、<c>(2024)</c>、<c>更新至120集</c>、
/// <c>1080P</c>）。这些进首字母串会算出一堆废字母（<c>黑白清道夫[全集]</c> → <c>HBQDFQJ</c>），
/// 用户按片名输 <c>hbqdf</c> 反而匹配不上。</item>
/// <item><see cref="StripSeason"/> —— 去掉**季标识**，得到系列基名（<c>凡人修仙传第六季</c> →
/// <c>凡人修仙传</c>）。用于「按系列聚合」：用户输字母想看的是**系列**，
/// 而不是某一季；确定系列后再去挑季。</item>
/// </list>
/// </summary>
public static class TitleNormalizer
{
    /// <summary>去成对括号（含内容）：站点用 [] 【】 () 装集数/清晰度/年份标注。</summary>
    private static readonly Regex Brackets = new(@"\[[^\]]*\]|【[^】]*】|\([^)]*\)|（[^）]*）", RegexOptions.Compiled);

    /// <summary>
    /// 纯状态尾缀：更新至N集 / 全N集 / N集全 / 全集 / 第N集 / HD / 4K / 1080P 等。
    /// ⚠ 刻意**不含**「第N季」——那是片名的一部分，删掉会把不同季混成一部。
    /// 「第N集」可以安全去掉：它是集标记，不是片名（「1985故事集」结尾是「故事集」，不受影响）。
    /// </summary>
    private static readonly Regex StatusSuffix = new(
        @"\s*((更新至|全|更新)\s*\d*\s*集?|全集|\d+\s*集全|第\s*\d+\s*集|HD|BD|4K|8K|1080P|720P|2160P)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>播放历史里的「片名 · 集名」分隔（如「流人 第六季 · 第02集」）。</summary>
    private static readonly Regex EpisodeTail = new(@"\s*·\s*[^·]*$", RegexOptions.Compiled);

    /// <summary>季标识（中文数字 / 阿拉伯数字；也兼容 Season N、S3）。</summary>
    private static readonly Regex SeasonSuffix = new(
        @"\s*(第\s*[0-9一二三四五六七八九十百]+\s*[季部]|Season\s*\d+|S\d{1,2})\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>中国数字 → 阿拉伯数字（仅支持 1~99，片名季数不会更大）。</summary>
    private static readonly Dictionary<char, int> CnDigits = new()
    {
        ['零'] = 0, ['一'] = 1, ['二'] = 2, ['两'] = 2, ['三'] = 3, ['四'] = 4,
        ['五'] = 5, ['六'] = 6, ['七'] = 7, ['八'] = 8, ['九'] = 9, ['十'] = 10,
    };

    /// <summary>连续空白（含全角空格、NBSP、换行、制表）→ 单个半角空格。</summary>
    private static readonly Regex Spaces = new(@"[\s\u00a0\u3000]+", RegexOptions.Compiled);

    // ═══════════════ 非影视内容过滤 ═══════════════
    //
    // 站点里混着大量**不是影视**的条目：UP 主视频、MV、解说、游戏皮肤爆料、新闻。
    // 实测（2026-09-19，应用真实库 984 条）它们约占 10%，且标题是整句描述：
    //   [MTV] 英雄回家！第十三批在韩中国人民志愿军烈士遗骸今日回国，11:30在沈阳…
    //   [MTV] 王者全新皮肤首曝-神仙团队、食宿全包、法宝任挑
    // 这些进了片名索引就会污染首字母搜索（输 lr 蹦出足球新闻）。
    //
    // ⚠ 判据只使用「长度 + 明确描述词 + 话题标签」，**刻意不用标点**：
    //   中文逗号、感叹号、问号在**真片名**里很常见，用它们过滤会误伤 ——
    //   实测误伤「风，带有香气」「好，我们离婚吧」「天才，女友」「年会不能停！」「八仙！」
    //   「剑仙归来：谁让这剑仙逆世重修的？第六季」。以上规则经 984 条真实数据回归，
    //   丢弃 95 条（全部确为非影视），真片名误伤 0 条。

    /// <summary>明确的**非影视**描述词（UP主视频/MV/解说/游戏内容 的典型特征）。</summary>
    private static readonly Regex NonVideoMarkers = new(
        @"(一口气看完|合集|完整版|精简版|沙雕动画|动态漫|片头曲|主题曲|片尾曲|特辑|" +
        @"解说|盘点|预告|花絮|MV|BGM|皮肤|爆料|建模|高燃|名场面|#)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 标题是否**像影视片名**（不似则不应进片名索引）。
    ///
    /// <para>真片名实测最长 19 字（「怪奇物语：1985故事集 第二季」），
    /// 故以 28 字为硬上限 —— 超过必是整句描述。</para>
    /// </summary>
    public static bool LooksLikeVideoTitle(string? title)
    {
        var t = (title ?? string.Empty).Trim();
        if (t.Length == 0) return false;
        if (t.Length > 28) return false;
        if (NonVideoMarkers.IsMatch(t)) return false;
        return true;
    }

    /// <summary>清洗片名：归一空白 + 去括号标注 + 去状态尾缀 + 去「 · 集名」尾巴。</summary>
    public static string Clean(string? title)
    {
        var t = (title ?? string.Empty).Trim();
        if (t.Length == 0) return t;

        // 先归空白：站点标题可能夹 \n / NBSP / 全角空格，直接上屏会渲染出多余空行
        t = Spaces.Replace(t, " ").Trim();
        t = Brackets.Replace(t, "");
        t = StatusSuffix.Replace(t, "");
        t = EpisodeTail.Replace(t, "");   // 播放历史的「片名 · 第02集」
        return t.Trim();
    }

    /// <summary>
    /// 取**系列基名**：去掉末尾的季标识。「凡人修仙传第六季」→「凡人修仙传」、
    /// 「流人 第六季」→「流人」、「万物生灵 第七季」→「万物生灵」。
    /// 无季标识时原样返回（已先 <see cref="Clean"/> 过）。
    /// </summary>
    public static string StripSeason(string? title)
    {
        var t = Clean(title);
        if (t.Length == 0) return t;

        var stripped = SeasonSuffix.Replace(t, "").Trim();

        // 全被剥光的病态情况（标题就是「第二季」）：保留原文，否则基名变空串
        return stripped.Length > 0 ? stripped : t;
    }

    /// <summary>片名是否带季标识（决定「按系列聚合」还是「单片直接播」）。</summary>
    public static bool HasSeason(string? title) =>
        SeasonSuffix.IsMatch(Clean(title));

    /// <summary>
    /// 解析季号（用于排序：第一季在前）。无季标识返回 <see cref="int.MaxValue"/>
    /// —— 不带季的（如剧场版/单季）排在带季的之后。
    /// </summary>
    public static int SeasonNumber(string? title)
    {
        var t = Clean(title);
        var m = SeasonSuffix.Match(t);
        if (!m.Success) return int.MaxValue;

        var raw = m.Groups[1].Value;

        // Season 3 / S3
        var digits = Regex.Match(raw, @"\d+");
        if (digits.Success && int.TryParse(digits.Value, out var n)) return n;

        // 第三季 / 第十二季 / 第二十季
        var cn = Regex.Replace(raw, @"[^零一二两三四五六七八九十]", "");
        if (cn.Length == 0) return int.MaxValue;
        return ParseCnNumber(cn);
    }

    /// <summary>中文数字解析（一到九十九）。</summary>
    private static int ParseCnNumber(string s)
    {
        // 十 / 十二 / 二十 / 二十三 / 九十九
        var idx = s.IndexOf('十');
        if (idx < 0)
            return s.Length == 1 && CnDigits.TryGetValue(s[0], out var d) ? d : int.MaxValue;

        var tens = idx == 0 ? 1 : (CnDigits.TryGetValue(s[0], out var t) ? t : 1);
        var ones = idx == s.Length - 1 ? 0 : (CnDigits.TryGetValue(s[^1], out var o) ? o : 0);
        return tens * 10 + ones;
    }
}
