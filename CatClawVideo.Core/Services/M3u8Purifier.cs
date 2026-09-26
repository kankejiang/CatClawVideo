using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CatClawVideo.Core.Services;

/// <summary>
/// m3u8 广告段清洗 —— 移植 TVBox <c>util/M3u8.java</c> 的 <c>purify()</c> 链（958 行，作者 asdfgh/FongMi）。
///
/// <para><b>为什么需要它</b>：这类站点的正片和广告切在同一个播放列表里，靠 <c>#EXT-X-DISCONTINUITY</c>
/// 分段。播放器直连时会把广告一起播完，表现为「进度条莫名变长 / 中间插一段不相干画面」。
/// TVBox 的处理是在播放前把播放列表文本洗一遍，我们的接线点在
/// <see cref="SpiderProxyServer"/> 取到播放列表之后、回给播放器之前。</para>
///
/// <para><b>开关语义照抄 TVBox</b>：<c>HawkConfig.M3U8_PURIFY</c> 默认 <b>false</b>，
/// 由设置页「m3u8 去广告」控制（<see cref="Enabled"/>）。默认关是有意为之——清洗是启发式的，
/// 宁可少删也不能删掉正片；每级都有回退保护（见下）。</para>
///
/// <para><b>五级流程</b>（与 TVBox 逐级对齐）：
/// ① <c>removeMinorityUrl</c> 按「切片路径前缀」或「域名」统计主导者，删少数派；
/// ② <c>clean</c> 用订阅 <c>rules[].regex</c> 里带 DISCONTINUITY/EXTINF 的规则整段删；
/// ③ <c>cleanCommonAdMarkers</c> 删 SCTE-35 / CUE-OUT~CUE-IN / DATERANGE 广告块与 URL 特征段；
/// ④ <c>cleanDecimalPrecisionGroups</c> 与 ⑤ <c>cleanFrameRateGroups</c> 按 EXTINF 小数精度/帧率特征
/// 删短不连续块（广告素材常由不同帧率素材拼接）；最后 <c>cleanDiscontinuityGroups</c> 删非主导短组。</para>
///
/// <para><b>回退保护</b>（三道，任一触发就放弃本次清洗、回原文并把计数归零）：
/// 单级删除超过总段数 30% → 该级作废；累计删除超过总段数 50% → 整体回退；
/// 洗完不满足 <c>isPlayableMediaPlaylist</c>（须有段、不得连续两个 EXTINF、不得 EXTINF 紧跟 ENDLIST）→ 整体回退。</para>
/// </summary>
public sealed class M3u8Purifier
{
    /// <summary>总开关（对位 TVBox <c>HawkConfig.M3U8_PURIFY</c>，默认关）。</summary>
    public static bool Enabled { get; set; }

    /// <summary>过程留痕（对位 TVBox 的 <c>LOG.i("echo-fixAdM3u8 …")</c>），未设则不打。</summary>
    public Action<string>? Log { get; set; }

    /// <summary>本轮清洗掉的段数（对位 <c>M3u8.currentAdCount</c>）。TVBox 用它决定「是否回原文」和 Toast 文案。</summary>
    public int AdCount { get; private set; }

    /// <summary>按播放地址取该 host 的正则规则表（对位 <c>VideoParseRuler.getHostsRegex()</c>）。</summary>
    private readonly Func<string, IReadOnlyList<string>?>? _hostRegex;

    public M3u8Purifier(Func<string, IReadOnlyList<string>?>? hostRegex = null) => _hostRegex = hostRegex;

    // ───── 标签常量（与 TVBox 同名同值）─────
    const string TagDiscontinuity = "#EXT-X-DISCONTINUITY";
    const string TagMediaDuration = "#EXTINF";
    const string TagEndList = "#EXT-X-ENDLIST";
    const string TagKey = "#EXT-X-KEY";
    const string TagMap = "#EXT-X-MAP";
    const string TagCueOut = "#EXT-X-CUE-OUT";
    const string TagCueIn = "#EXT-X-CUE-IN";
    const string TagDateRange = "#EXT-X-DATERANGE";

    /// <summary>正则缓存（对位 <c>RegexUtils.getPattern</c>）。必须声明在下面这几个 static readonly 之前——
    /// C# 静态字段按声明顺序初始化，晚声明会让 <see cref="Cached"/> 在 cctor 里撞到 null。</summary>
    static readonly ConcurrentDictionary<string, Regex> PatternCache = new();

    static readonly Regex RegexXDiscontinuity = Cached(@"#EXT-X-DISCONTINUITY[\s\S]*?(?=#EXT-X-DISCONTINUITY|$)");
    static readonly Regex RegexMediaDuration = Cached(@"#EXTINF:([\d\.]+)\b");
    static readonly Regex RegexUri = Cached("URI=\"(.+?)\"");

    /// <summary>广告切片 URL 特征（TVBox 增强项）。</summary>
    static readonly Regex RegexAdSegmentUri = Cached(
        @"(^|[/?&=_.-])(ads?|adv|advert(ise(ment)?)?|commercial|preroll|pre-roll|midroll|mid-roll|postroll|post-roll|sponsor|scte|vast|vmap|interstitial|bumper)([/?&=_.-]|$)",
        RegexOptions.IgnoreCase);

    /// <summary>常见广告 CDN 域名特征（M3u8.java:41-44 原表，10 项）。</summary>
    static readonly string[] AdDomainKeywords =
    [
        "adservice", "adserver", "adsystem", "doubleclick", "googlesyndication",
        "advertising", "2mdn.net", "moatads", "scorecardresearch", "quantserve",
    ];

    const int MaxFrameRateAdBlockSize = 12;
    const int TimesNoAd = 15;

    /// <summary>帧率小数特征表（对位 <c>FRAME_RATE_FEATURES</c>）：30/24 含 NTSC 变体，25 不含。</summary>
    static readonly Dictionary<int, HashSet<decimal>> FrameRateFeatures = PrepareFrameRateFeatures();

    /// <summary>订阅 <c>rules[].regex</c> 是否属于「广告段规则」（对位 <c>M3u8.isAd</c>，ApiConfig 收集规则时用）。</summary>
    public static bool IsAd(string regex) =>
        regex.Contains(TagDiscontinuity, StringComparison.Ordinal) ||
        regex.Contains(TagMediaDuration, StringComparison.Ordinal) ||
        regex.Contains(TagEndList, StringComparison.Ordinal) ||
        regex.Contains(TagKey, StringComparison.Ordinal) ||
        regex.Contains(TagCueOut, StringComparison.Ordinal) ||
        regex.Contains(TagCueIn, StringComparison.Ordinal) ||
        regex.Contains(TagDateRange, StringComparison.Ordinal) ||
        IsDouble(regex);

    /// <summary>
    /// 主入口（对位 <c>M3u8.purify(tsUrlPre, m3u8content)</c>）。
    /// </summary>
    /// <param name="tsUrlPre">播放列表自身的绝对地址，用来把相对切片补全。</param>
    /// <param name="content">播放列表文本。</param>
    /// <returns>清洗后的文本；<b>null = 不适用</b>（空/无 BOM 外非 #EXTM3U 开头），调用方直接播原文。
    /// 即使非 null，<see cref="AdCount"/> 为 0 时 TVBox 语义也是「播原文」。</returns>
    public string? Purify(string tsUrlPre, string? content)
    {
        var start = Environment.TickCount64;
        AdCount = 0;
        if (string.IsNullOrEmpty(content)) return null;
        if (content.StartsWith('\uFEFF')) content = content[1..];
        if (!content.StartsWith("#EXTM3U", StringComparison.Ordinal)) return null;

        var totalSegments = CountSegments(content);

        var result = RemoveMinorityUrl(tsUrlPre, content);
        result = result is not null && AdCount > 0 ? Get(tsUrlPre, result) : Get(tsUrlPre, content);
        result = KeepVodEndList(content, result);

        if (totalSegments > 0 && AdCount > totalSegments * 0.5)
        {
            Log?.Invoke($"[m3u8] 去广告删太多 {AdCount}/{totalSegments}，回退原文");
            AdCount = 0;
            result = content;
        }
        if (AdCount > 0 && !IsPlayableMediaPlaylist(result))
        {
            Log?.Invoke("[m3u8] 去广告后播放列表不可播，回退原文");
            AdCount = 0;
            result = content;
        }

        Log?.Invoke($"[m3u8] 去广告耗时 {Environment.TickCount64 - start}ms，删除 {AdCount}/{totalSegments} 段");
        return result;
    }

    static int CountSegments(string content)
    {
        var n = 0;
        foreach (var line in content.Split(content.Contains("\r\n", StringComparison.Ordinal) ? ["\r\n"] : ["\n"],
                     StringSplitOptions.None))
            if (line.Length > 0 && line[0] != '#') n++;
        return n;
    }

    // ═══════════════ ① 少数派路径/域名剔除 ═══════════════

    string? RemoveMinorityUrl(string tsUrlPre, string content)
    {
        var split = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = content.Split([split], StringSplitOptions.None);
        var totalSegments = CountSegments(content);

        // 第一遍：按「去掉末段扩展名后的路径前缀」计数
        var preUrlMap = new Dictionary<string, int>();
        foreach (var line in lines)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var absolute = ToAbsoluteUrl(tsUrlPre, line);
            var ilast = absolute.LastIndexOf('.');
            if (ilast <= 4) continue;
            var pre = absolute[..(ilast - 4)];
            preUrlMap[pre] = preUrlMap.GetValueOrDefault(pre) + 1;
        }
        if (preUrlMap.Count <= 1) return null;

        var domainFiltering = false;
        if (MaxPercent(preUrlMap) < 0.8)
        {
            // 退到「域名」统计
            preUrlMap.Clear();
            foreach (var line in lines)
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var absolute = ToAbsoluteUrl(tsUrlPre, line);
                if (!absolute.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !absolute.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    return null;
                var ifirst = IndexAfterScheme(absolute);
                if (ifirst <= 0) continue;
                var pre = absolute[..ifirst];
                preUrlMap[pre] = preUrlMap.GetValueOrDefault(pre) + 1;
            }
            if (preUrlMap.Count <= 1) return null;
            if (MaxPercent(preUrlMap) < 0.8) return null;
            // 所有域名都超过阈值 → 说明不是「正片 + 零星广告」的形状，不动
            if (preUrlMap.Values.All(c => c > TimesNoAd)) return null;
            domainFiltering = true;
        }

        var maxTimesPreUrl = "";
        var maxTimes = 0;
        foreach (var (k, v) in preUrlMap)
            if (v > maxTimes)
            {
                maxTimesPreUrl = k;
                maxTimes = v;
            }
        if (maxTimes == 0) return null;

        var filtered = new StringBuilder();
        var pending = new List<string>();
        foreach (var line in lines)
        {
            var item = line.Trim();
            if (item.Length == 0)
            {
                if (pending.Count == 0) AppendLine(filtered, line, split);
                else pending.Add(line);
                continue;
            }
            if (item[0] == '#')
            {
                var output = HasUriAttribute(item) ? ResolveUriLine(tsUrlPre, line) : line;
                if (IsSegmentTag(item)) pending.Add(output);
                else
                {
                    Flush(filtered, pending, split);
                    AppendLine(filtered, output, split);
                }
                continue;
            }

            var absolute = ToAbsoluteUrl(tsUrlPre, line);
            if (ShouldKeepMediaUrl(absolute, domainFiltering, maxTimesPreUrl, preUrlMap))
            {
                Flush(filtered, pending, split);
                AppendLine(filtered, absolute, split);
            }
            else
            {
                pending.Clear();
                AdCount++;
            }
        }

        if (totalSegments > 0 && AdCount > totalSegments * 0.3)
        {
            Log?.Invoke($"[m3u8] 路径法可疑（{AdCount}/{totalSegments}），跳过");
            AdCount = 0;
            return null;
        }
        return NormalizeMediaPlaylist(filtered.ToString());
    }

    static double MaxPercent(Dictionary<string, int> map)
    {
        var max = 0;
        long total = 0;
        foreach (var v in map.Values)
        {
            if (v > max) max = v;
            total += v;
        }
        return total == 0 ? 0 : max / (double)total;
    }

    /// <summary>http(s):// 之后第一个 '/' 的下标（对位 <c>indexOf('/', 9)</c>）。</summary>
    static int IndexAfterScheme(string url) => url.IndexOf('/', 9);

    static bool ShouldKeepMediaUrl(string absolute, bool domainFiltering, string dominant,
        Dictionary<string, int> map)
    {
        if (!domainFiltering) return absolute.StartsWith(dominant, StringComparison.Ordinal);
        var ifirst = IndexAfterScheme(absolute);
        var domain = ifirst > 0 ? absolute[..ifirst] : absolute;
        return domain == dominant || map.GetValueOrDefault(domain) > TimesNoAd;
    }

    // ═══════════════ ②~⑤ 规则清洗 + 标记清洗 + 精度/帧率/分组 ═══════════════

    string Get(string tsUrlPre, string content)
    {
        var line = ResolveContent(tsUrlPre, content);
        var ads = GetRegex(tsUrlPre);
        if (ads is { Count: > 0 }) line = Clean(line, ads);
        line = CleanCommonAdMarkers(line);
        if (HasEndList(line) && line.Contains(TagDiscontinuity, StringComparison.Ordinal))
        {
            line = CleanDecimalPrecisionGroups(line);
            line = CleanFrameRateGroups(line);
        }
        return CleanDiscontinuityGroups(line);
    }

    IReadOnlyList<string>? GetRegex(string tsUrlPre)
    {
        var hosts = _hostRegex?.Invoke(tsUrlPre);
        return hosts is { Count: > 0 } ? hosts : null;
    }

    string Clean(string line, IReadOnlyList<string> ads)
    {
        var scan = false;
        foreach (var ad in ads)
        {
            if (ad.Contains(TagDiscontinuity, StringComparison.Ordinal) ||
                ad.Contains(TagMediaDuration, StringComparison.Ordinal))
                line = ScanAd(line, ad);
            else if (IsDouble(ad)) scan = true;
        }
        return scan ? Scan(line, ads) : line;
    }

    /// <summary>整段删除：规则本身是正则，命中即删掉那段 DISCONTINUITY 区间，并按 EXTINF 个数计数。</summary>
    string ScanAd(string line, string tagAd)
    {
        var needRemove = new List<string>();
        foreach (Match m1 in Cached(tagAd).Matches(line))
        {
            var group = m1.Value.Replace(TagEndList, "", StringComparison.Ordinal);
            AdCount += RegexMediaDuration.Matches(group).Count;
            needRemove.Add(group);
        }
        foreach (var rem in needRemove) line = line.Replace(rem, "", StringComparison.Ordinal);
        return line;
    }

    /// <summary>按「首段/末段/总时长」的字符串前缀比对删整段（规则串以 '-' 开头 = 比末段）。</summary>
    string Scan(string line, IReadOnlyList<string> ads)
    {
        var needRemove = new List<string>();
        foreach (Match m1 in RegexXDiscontinuity.Matches(line))
        {
            var group = m1.Value;
            var groupCleaned = group.Replace(TagEndList, "", StringComparison.Ordinal);
            decimal ft = 0, lt = 0, t = 0;
            var tCount = 0;
            foreach (Match m2 in RegexMediaDuration.Matches(group))
            {
                var v = ParseDec(m2.Groups[1].Value);
                if (ft == 0) ft = v;
                lt = v;
                t += v;
                tCount++;
            }
            var ftStr = ft.ToString(CultureInfo.InvariantCulture);
            var ltStr = lt.ToString(CultureInfo.InvariantCulture);
            var tStr = t.ToString(CultureInfo.InvariantCulture);
            foreach (var ad in ads)
            {
                var hit = ad.StartsWith('-')
                    ? ltStr.StartsWith(ad[1..], StringComparison.Ordinal)
                    : ftStr.StartsWith(ad, StringComparison.Ordinal) ||
                      tStr.StartsWith(ad, StringComparison.Ordinal);
                if (!hit) continue;
                needRemove.Add(groupCleaned);
                AdCount += tCount;
                break;
            }
        }
        foreach (var rem in needRemove) line = line.Replace(rem, "", StringComparison.Ordinal);
        return line;
    }

    string CleanCommonAdMarkers(string line)
    {
        var removed = 0;
        var sb = new StringBuilder();
        var pending = new List<string>();
        var inAdBreak = false;
        var changed = false;

        foreach (var raw in line.Split('\n'))
        {
            var item = raw.Trim();
            if (item.Length == 0)
            {
                if (pending.Count == 0) sb.Append(raw).Append('\n');
                else pending.Add(raw);
                continue;
            }
            if (item.StartsWith('#'))
            {
                if (item.StartsWith(TagCueIn, StringComparison.Ordinal) && (inAdBreak || HasAdSignal(pending)))
                {
                    inAdBreak = false;
                    pending.Clear();
                    changed = true;
                    continue;
                }
                if (IsAdBreakStart(item))
                {
                    Flush(sb, pending);
                    inAdBreak = true;
                    pending.Add(raw);
                    changed = true;
                    continue;
                }
                if (inAdBreak)
                {
                    pending.Add(raw);
                    changed = true;
                    continue;
                }
                if (IsStandaloneAdTag(item))
                {
                    Flush(sb, pending);
                    removed++;
                    changed = true;
                    continue;
                }
                if (IsSegmentTag(item) || IsAdSignalTag(item)) pending.Add(raw);
                else
                {
                    Flush(sb, pending);
                    sb.Append(raw).Append('\n');
                }
                continue;
            }

            if (inAdBreak || HasAdSignal(pending) || IsAdSegmentUri(item) || HasAdDomain(item))
            {
                pending.Clear();
                removed++;
                changed = true;
                continue;
            }
            Flush(sb, pending);
            sb.Append(raw).Append('\n');
        }

        if (!inAdBreak) Flush(sb, pending);
        AdCount += removed;
        return changed ? sb.ToString() : line;
    }

    static void Flush(StringBuilder sb, List<string> pending)
    {
        foreach (var l in pending) sb.Append(l).Append('\n');
        pending.Clear();
    }

    static void Flush(StringBuilder sb, List<string> pending, string split)
    {
        foreach (var l in pending) AppendLine(sb, l, split);
        pending.Clear();
    }

    static void AppendLine(StringBuilder sb, string line, string split) => sb.Append(line).Append(split);

    static bool HasAdSignal(List<string> pending) =>
        pending.Any(l => IsAdBreakStart(l.Trim()) || IsAdSignalTag(l.Trim()));

    static bool IsAdBreakStart(string line) => line.StartsWith(TagCueOut, StringComparison.Ordinal);

    static bool IsAdSignalTag(string line)
    {
        if (line.StartsWith("#EXT-X-DISCONTINUITY-SEQUENCE", StringComparison.Ordinal)) return false;
        return line.StartsWith("#EXT-OATCLS-SCTE35", StringComparison.Ordinal) ||
               line.StartsWith("#EXT-X-SCTE35", StringComparison.Ordinal) ||
               line.StartsWith("#EXT-X-SPLICEPOINT-SCTE35", StringComparison.Ordinal) ||
               line.StartsWith("#EXT-X-CUE", StringComparison.Ordinal) ||
               line.StartsWith("#EXT-X-ASSET", StringComparison.Ordinal) ||
               line.StartsWith("#EXT-X-VMAP-AD-BREAK", StringComparison.Ordinal) ||
               line.StartsWith("#EXT-X-AD", StringComparison.Ordinal);
    }

    static bool IsSegmentTag(string line)
    {
        if (line.StartsWith("#EXT-X-DISCONTINUITY-SEQUENCE", StringComparison.Ordinal)) return false;
        return line.StartsWith(TagMediaDuration, StringComparison.Ordinal) ||
               line.StartsWith("#EXT-X-BYTERANGE", StringComparison.Ordinal) ||
               line.StartsWith("#EXT-X-PROGRAM-DATE-TIME", StringComparison.Ordinal) ||
               line.StartsWith(TagDiscontinuity, StringComparison.Ordinal) ||
               line.StartsWith("#EXT-X-PART", StringComparison.Ordinal) ||
               line.StartsWith("#EXT-X-PRELOAD-HINT", StringComparison.Ordinal);
    }

    static bool IsStandaloneAdTag(string line)
    {
        if (!line.StartsWith(TagDateRange, StringComparison.Ordinal)) return false;
        return IsAdLikeText(line) || line.Contains("X-ASSET-URI", StringComparison.Ordinal) ||
               line.Contains("X-ASSET-LIST", StringComparison.Ordinal);
    }

    static bool IsAdLikeText(string line)
    {
        var lower = line.ToLowerInvariant();
        return lower.Contains("scte") || lower.Contains("cue") || lower.Contains("interstitial") ||
               lower.Contains("vmap") || lower.Contains("vast") || lower.Contains("advert") ||
               lower.Contains("commercial") || lower.Contains("ad-") || lower.Contains("ad_") ||
               lower.Contains("ad.") || lower.Contains("preroll") || lower.Contains("midroll") ||
               lower.Contains("postroll") || lower.Contains("bumper");
    }

    static bool IsAdSegmentUri(string url) => RegexAdSegmentUri.IsMatch(url);

    static bool HasAdDomain(string url)
    {
        var lower = url.ToLowerInvariant();
        return AdDomainKeywords.Any(lower.Contains);
    }

    string CleanDecimalPrecisionGroups(string content)
    {
        var groups = BuildDiscontinuityGroups(content.Split('\n'));
        if (groups.Count < 2) return content;

        var precisionCounts = new Dictionary<int, int>();
        var totalSegments = 0;
        foreach (var g in groups)
            foreach (var raw in g.Lines)
            {
                var precision = GetDecimalPrecision(raw);
                if (precision < 0) continue;
                totalSegments++;
                precisionCounts[precision] = precisionCounts.GetValueOrDefault(precision) + 1;
            }
        if (totalSegments < 8 || precisionCounts.Count < 2) return content;

        var majorPrecision = -1;
        var majorCount = 0;
        foreach (var (k, v) in precisionCounts)
            if (v > majorCount)
            {
                majorPrecision = k;
                majorCount = v;
            }
        if (majorPrecision < 0 || majorCount / (double)totalSegments < 0.7) return content;

        var remove = new bool[groups.Count];
        var removable = 0;
        for (var i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            if (i == groups.Count - 1 || g.SegmentCount == 0 || g.SegmentCount > MaxFrameRateAdBlockSize) continue;
            var (total, mismatched) = g.DecimalPrecisionStats(majorPrecision);
            if (total > 0 && mismatched == total)
            {
                remove[i] = true;
                removable += g.SegmentCount;
            }
        }
        if (removable == 0 || removable > GetAdSegmentLimit(content) || removable > totalSegments * 0.3) return content;

        var (sb, blocks) = DropGroups(groups, remove);
        Log?.Invoke($"[m3u8] 精度法：主导={majorPrecision} 位，删 {blocks} 块 / {removable} 段");
        AdCount += removable;
        return NormalizeMediaPlaylist(sb.ToString());
    }

    string CleanFrameRateGroups(string content)
    {
        var groups = BuildDiscontinuityGroups(content.Split('\n'));
        if (groups.Count < 2) return content;

        var master = FindDominantFrameRate(groups);
        if (master == 0) return content;

        var remove = new bool[groups.Count];
        var removable = 0;
        for (var i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            if (i == groups.Count - 1 || g.SegmentCount == 0 || g.SegmentCount > MaxFrameRateAdBlockSize) continue;
            var (matched, mismatched) = g.FrameRateStats(master);
            if (mismatched > 0 && mismatched >= matched)
            {
                remove[i] = true;
                removable += g.SegmentCount;
            }
        }
        var limit = GetAdSegmentLimit(content);
        if (removable == 0 || removable > limit) return content;

        var (sb, blocks) = DropGroups(groups, remove);
        Log?.Invoke($"[m3u8] 帧率法：主导={master}fps，删 {blocks} 块 / {removable} 段");
        AdCount += removable;
        return NormalizeMediaPlaylist(sb.ToString());
    }

    (StringBuilder Sb, int Blocks) DropGroups(List<Group> groups, bool[] remove)
    {
        var sb = new StringBuilder();
        var blocks = 0;
        for (var i = 0; i < groups.Count; i++)
        {
            if (remove[i]) blocks++;
            else groups[i].AppendTo(sb);
        }
        return (sb, blocks);
    }

    static int FindDominantFrameRate(List<Group> groups)
    {
        var c30 = 0;
        var c25 = 0;
        var c24 = 0;
        foreach (var g in groups)
            foreach (var raw in g.Lines)
            {
                if (GetExtInfValueStart(raw) < 0) continue;
                switch (GetExclusiveFrameRate(ParseExtInfDuration(raw)))
                {
                    case 30: c30++; break;
                    case 25: c25++; break;
                    case 24: c24++; break;
                }
            }
        var max = Math.Max(c30, Math.Max(c25, c24));
        if (max < 2) return 0;
        var winners = (c30 == max ? 1 : 0) + (c25 == max ? 1 : 0) + (c24 == max ? 1 : 0);
        if (winners != 1) return 0;
        return c30 == max ? 30 : (c25 == max ? 25 : 24);
    }

    static int GetExclusiveFrameRate(decimal duration)
    {
        var is30 = IsFrameAligned(duration, 30);
        var is25 = IsFrameAligned(duration, 25);
        var is24 = IsFrameAligned(duration, 24);
        if (is30 && !is25 && !is24) return 30;
        if (is25 && !is30 && !is24) return 25;
        if (is24 && !is30 && !is25) return 24;
        return 0;
    }

    static bool IsFrameAligned(decimal duration, int frameRate) =>
        FrameRateFeatures.TryGetValue(frameRate, out var set) &&
        set.Contains(Math.Abs(duration % 1m));

    static Dictionary<int, HashSet<decimal>> PrepareFrameRateFeatures() => new()
    {
        [30] = CreateFrameRateFeatures(30, true),
        [25] = CreateFrameRateFeatures(25, false),
        [24] = CreateFrameRateFeatures(24, true),
    };

    static HashSet<decimal> CreateFrameRateFeatures(int frameRate, bool includeNtsc)
    {
        var set = new HashSet<decimal>();
        AddFrameRateFeatures(set, frameRate, frameRate);
        if (includeNtsc) AddFrameRateFeatures(set, frameRate / 1.001m, frameRate * 10);
        return set;
    }

    static void AddFrameRateFeatures(HashSet<decimal> set, decimal rate, int maxFrames)
    {
        for (var frame = 1; frame <= maxFrames; frame++)
        {
            var fraction = Math.Round(frame / rate, 10, MidpointRounding.AwayFromZero) % 1m;
            for (var scale = 3; scale <= 6; scale++)
            {
                var value = Math.Round(fraction, scale, MidpointRounding.AwayFromZero);
                if (value != 0) set.Add(value);
            }
        }
    }

    /// <summary>可删段数上限：按时长分档（≤30min→18，≤60→24，≤90→30，else 36）。</summary>
    static int GetAdSegmentLimit(string content)
    {
        decimal total = 0;
        foreach (var raw in content.Split('\n')) total += ParseExtInfDuration(raw);
        var minutes = (double)total / 60;
        if (minutes <= 30) return 18;
        if (minutes <= 60) return 24;
        if (minutes <= 90) return 30;
        return 36;
    }

    string CleanDiscontinuityGroups(string content)
    {
        var groups = BuildDiscontinuityGroups(content.Split('\n'));
        if (groups.Count < 3) return content;
        var main = FindMainGroup(groups);
        if (main is null || main.SegmentCount < 3) return content;

        var sb = new StringBuilder();
        var changed = false;
        foreach (var g in groups)
        {
            if (ShouldDropGroup(g, main))
            {
                AdCount += g.SegmentCount;
                changed = true;
                continue;
            }
            g.AppendTo(sb);
        }
        return changed ? sb.ToString() : content;
    }

    static List<Group> BuildDiscontinuityGroups(string[] lines)
    {
        var groups = new List<Group>();
        var group = new Group();
        foreach (var raw in lines)
        {
            if (raw.Trim().StartsWith(TagDiscontinuity, StringComparison.Ordinal) && group.HasMedia())
            {
                groups.Add(group);
                group = new Group();
            }
            group.Add(raw);
        }
        if (group.HasMedia() || group.Lines.Count > 0) groups.Add(group);
        return groups;
    }

    static Group? FindMainGroup(List<Group> groups)
    {
        Group? main = null;
        foreach (var g in groups)
        {
            if (g.SegmentCount == 0) continue;
            if (main is null || g.Score() > main.Score()) main = g;
        }
        return main;
    }

    static bool ShouldDropGroup(Group group, Group main)
    {
        if (ReferenceEquals(group, main) || group.SegmentCount == 0) return false;

        var shortGroup = group.SegmentCount <= 2 ||
                         (main.TotalDuration > 0 && group.TotalDuration > 0 &&
                          group.TotalDuration < main.TotalDuration * 0.18);
        var differentHost = main.Host.Length > 0 && group.Host.Length > 0 && main.Host != group.Host;
        var differentPath = main.PathPrefix.Length > 0 && group.PathPrefix.Length > 0 &&
                            main.PathPrefix != group.PathPrefix;
        var hasAdFeature = group.AdLikeCount > 0 || HasAdDomain(group.Host) || IsAdSegmentUri(group.PathPrefix);
        var adLike = hasAdFeature || differentHost || (group.SegmentCount <= 2 && differentPath);

        return shortGroup && adLike;
    }

    // ═══════════════ 通用工具 ═══════════════

    sealed class Group
    {
        public List<string> Lines { get; } = [];
        public int SegmentCount { get; private set; }
        public int AdLikeCount { get; private set; }
        public double TotalDuration { get; private set; }
        public string Host { get; private set; } = "";
        public string PathPrefix { get; private set; } = "";

        public void Add(string raw)
        {
            Lines.Add(raw);
            var line = raw.Trim();
            var start = GetExtInfValueStart(line);
            if (start >= 0)
            {
                var end = GetExtInfValueEnd(line, start);
                if (double.TryParse(line[start..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    TotalDuration += d;
            }
            if (line.Length == 0 || line.StartsWith('#'))
            {
                if (IsAdSignalTag(line) || IsStandaloneAdTag(line)) AdLikeCount++;
                return;
            }
            SegmentCount++;
            if (IsAdSegmentUri(line) || HasAdDomain(line)) AdLikeCount++;
            if (Host.Length == 0) Host = HostOf(line);
            if (PathPrefix.Length == 0) PathPrefix = PathPrefixOf(line);
        }

        public bool HasMedia() => SegmentCount > 0;
        public double Score() => TotalDuration > 0 ? TotalDuration : SegmentCount;

        public void AppendTo(StringBuilder sb)
        {
            foreach (var l in Lines) sb.Append(l).Append('\n');
        }

        public (int Total, int Mismatched) DecimalPrecisionStats(int majorPrecision)
        {
            var total = 0;
            var mismatched = 0;
            foreach (var raw in Lines)
            {
                var p = GetDecimalPrecision(raw);
                if (p < 0) continue;
                total++;
                if (p != majorPrecision) mismatched++;
            }
            return (total, mismatched);
        }

        public (int Matched, int Mismatched) FrameRateStats(int masterFrameRate)
        {
            var matched = 0;
            var mismatched = 0;
            foreach (var raw in Lines)
            {
                if (GetExtInfValueStart(raw) < 0) continue;
                var rate = GetExclusiveFrameRate(ParseExtInfDuration(raw));
                if (rate == masterFrameRate) matched++;
                else if (rate != 0) mismatched++;
            }
            return (matched, mismatched);
        }
    }

    static string HostOf(string url)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "";
        var start = url.IndexOf("://", StringComparison.Ordinal) + 3;
        var end = url.IndexOf('/', start);
        return end > start ? url[start..end] : url[start..];
    }

    static string PathPrefixOf(string url)
    {
        var clean = url;
        var query = clean.IndexOf('?');
        if (query >= 0) clean = clean[..query];
        var slash = clean.LastIndexOf('/');
        return slash > 0 ? clean[..(slash + 1)] : "";
    }

    static decimal ParseExtInfDuration(string line)
    {
        var start = GetExtInfValueStart(line);
        if (start < 0) return 0;
        var end = GetExtInfValueEnd(line, start);
        return ParseDec(line[start..end]);
    }

    static decimal ParseDec(string s) =>
        decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    static int GetExtInfValueStart(string line)
    {
        if (line.Length == 0) return -1;
        var i = 0;
        while (i < line.Length && line[i] <= ' ') i++;
        if (!line.StartsWith(TagMediaDuration, StringComparison.Ordinal)) return -1;
        i += TagMediaDuration.Length;
        if (i >= line.Length || line[i] != ':') return -1;
        i++;
        while (i < line.Length && line[i] <= ' ') i++;
        return i < line.Length ? i : -1;
    }

    static int GetExtInfValueEnd(string line, int start)
    {
        var end = line.IndexOf(',', start);
        if (end < 0) end = line.Length;
        while (end > start && line[end - 1] <= ' ') end--;
        return end;
    }

    static int GetDecimalPrecision(string line)
    {
        var start = GetExtInfValueStart(line);
        if (start < 0) return -1;
        var end = GetExtInfValueEnd(line, start);
        var dot = line.IndexOf('.', start);
        return dot < 0 || dot >= end ? 0 : end - dot - 1;
    }

    static bool IsDouble(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v != 0;

    static string ResolveContent(string tsUrlPre, string content)
    {
        var sb = new StringBuilder();
        foreach (var line in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            sb.Append(ShouldResolve(line) ? Resolve(tsUrlPre, line.Trim()) : line).Append('\n');
        return sb.ToString();
    }

    static bool ShouldResolve(string line)
    {
        var item = line.Trim();
        if (item.Length == 0) return false;
        return (!item.StartsWith('#') && !item.StartsWith("http", StringComparison.OrdinalIgnoreCase)) ||
               HasUriAttribute(item);
    }

    static string Resolve(string base64, string line) =>
        HasUriAttribute(line) ? ResolveUriLine(base64, line) : ToAbsoluteUrl(base64, line);

    static bool HasUriAttribute(string line) =>
        line.StartsWith(TagKey, StringComparison.Ordinal) || line.StartsWith(TagMap, StringComparison.Ordinal);

    /// <summary>把 <c>#EXT-X-KEY</c> / <c>#EXT-X-MAP</c> 行里的 URI 补成绝对地址（首个匹配值、全量替换）。</summary>
    static string ResolveUriLine(string baseUrl, string line)
    {
        var m = RegexUri.Match(line);
        if (!m.Success) return line;
        var value = m.Groups[1].Value;
        return line.Replace(value, ToAbsoluteUrl(baseUrl, value), StringComparison.Ordinal);
    }

    /// <summary>相对地址补全（对位 ExoPlayer <c>UriUtil.resolve</c>）。</summary>
    static string ToAbsoluteUrl(string baseUrl, string url)
    {
        var line = url.Trim();
        if (line.Length == 0 ||
            line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return line;
        try
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var b)) return line;
            return new Uri(b, line).ToString();
        }
        catch { return line; }
    }

    /// <summary>删完段之后收拾残局：合并多余的 DISCONTINUITY、去掉尾部空行。</summary>
    static string NormalizeMediaPlaylist(string content)
    {
        var sb = new StringBuilder();
        var seenMedia = false;
        var hasPending = false;
        var pending = "";
        foreach (var raw in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var item = raw.Trim();
            if (IsDiscontinuityTag(item))
            {
                if (seenMedia && !hasPending)
                {
                    pending = raw;
                    hasPending = true;
                }
                continue;
            }
            if (hasPending)
            {
                if (item.Length == 0) continue;
                if (!item.StartsWith(TagEndList, StringComparison.Ordinal)) sb.Append(pending).Append('\n');
                hasPending = false;
            }
            if (item.Length == 0 && sb.Length == 0) continue;
            sb.Append(raw).Append('\n');
            if (IsMediaUriLine(item)) seenMedia = true;
        }
        return sb.ToString();
    }

    static bool IsPlayableMediaPlaylist(string? content)
    {
        if (string.IsNullOrEmpty(content) || !content.StartsWith("#EXTM3U", StringComparison.Ordinal)) return false;
        var mediaCount = 0;
        var pendingExtInf = false;
        foreach (var raw in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith(TagMediaDuration, StringComparison.Ordinal))
            {
                if (pendingExtInf) return false;
                pendingExtInf = true;
            }
            else if (IsMediaUriLine(line))
            {
                mediaCount++;
                pendingExtInf = false;
            }
            else if (line.StartsWith(TagEndList, StringComparison.Ordinal) && pendingExtInf) return false;
        }
        return mediaCount > 0 && !pendingExtInf;
    }

    static string? KeepVodEndList(string original, string? result)
    {
        if (result is null) return null;
        if (!HasEndList(original) || HasEndList(result)) return result;
        return result + (result.EndsWith('\n') ? "" : "\n") + TagEndList + "\n";
    }

    static bool HasEndList(string? content) =>
        content != null &&
        content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Any(raw => raw.Trim().StartsWith(TagEndList, StringComparison.Ordinal));

    static bool IsMediaUriLine(string line) => line.Length > 0 && !line.StartsWith('#');

    static bool IsDiscontinuityTag(string line) =>
        line.StartsWith(TagDiscontinuity, StringComparison.Ordinal) &&
        !line.StartsWith("#EXT-X-DISCONTINUITY-SEQUENCE", StringComparison.Ordinal);

    /// <summary>订阅下发的规则是任意用户串，必须带匹配超时，否则一个坏正则就能把代理线程挂死。</summary>
    static Regex Cached(string pattern, RegexOptions? options = null) => PatternCache.GetOrAdd(
        pattern, p => new Regex(p, options ?? RegexOptions.None, TimeSpan.FromSeconds(2)));
}
