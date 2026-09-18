using System.Collections.Concurrent;
using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;

namespace CatClawVideo.Core.Providers;

/// <summary>
/// 聚合路由 Provider：按站点的 CanHandle 把请求路由到具体实现
/// （MacCMS json 直连 / TVBox spider 爬虫）。HomeViewModel / WatchPage 统一注入本类。
/// </summary>
public class CompositeVodSourceProvider : IVodSourceProvider
{
    private readonly IReadOnlyList<IVodSourceProvider> _providers;

    public CompositeVodSourceProvider(IEnumerable<IVodSourceProvider> providers)
    {
        _providers = providers.ToList();
    }

    public string Id => "composite";
    public string Name => "聚合源";

    private IVodSourceProvider? Route(VodSiteInfo site) =>
        _providers.FirstOrDefault(p => p.CanHandle(site));

    public bool CanHandle(VodSiteInfo site) => Route(site) != null;

    private IVodSourceProvider Required(VodSiteInfo site) =>
        Route(site) ?? throw new NotSupportedException($"站点 {site.Name} 没有可用的源适配器（{site.StatusNote ?? "type " + site.Type}）");

    public Task<List<VodCategory>> GetCategoriesAsync(VodSiteInfo site, CancellationToken ct = default) =>
        Required(site).GetCategoriesAsync(site, ct);

    public Task<List<VodItem>> GetItemsAsync(VodSiteInfo site, VodCategory category, int page = 1, CancellationToken ct = default) =>
        Required(site).GetItemsAsync(site, category, page, ct);

    public async Task<List<VodPlaySource>> GetPlaySourcesAsync(VodSiteInfo site, VodItem item, CancellationToken ct = default)
    {
        var sources = await Required(site).GetPlaySourcesAsync(site, item, ct).ConfigureAwait(false);
        return await ExpandMagnetEpisodesAsync(sources, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 流式线路加载：先把**首个磁力**展开好再上屏（保证首播集名正确），
    /// 其余磁力后台继续展开、每完成一条刷新一次选集栏。
    ///
    /// <para>⚠ 为什么首个磁力必须同步展开：未展开时集名是站点给的打包名
    /// （如「第四季01-03-1080p.mp4」），而引擎的 preferName 按文件名匹配、
    /// 匹配不到时会退化选「最大的视频文件」——**可能播错集**。所以宁可多等这一条
    /// （实测 0.2~3.1s），也不能拿错误的集名起播。</para>
    /// </summary>
    public async IAsyncEnumerable<List<VodPlaySource>> StreamPlaySourcesAsync(
        VodSiteInfo site, VodItem item,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var sources = await Required(site).GetPlaySourcesAsync(site, item, ct).ConfigureAwait(false);

        var engine = Interfaces.MagnetEngines.Thunder;
        if (engine is null || !engine.IsReady)
        {
            yield return Clone(sources);
            yield break;
        }

        var magnetCount = sources
            .SelectMany(s => s.Episodes)
            .Count(e => e.Url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase));
        if (magnetCount == 0 || magnetCount > MaxMagnetsToExpand)
        {
            yield return Clone(sources);
            yield break;
        }

        using var gate = new SemaphoreSlim(1, 1);   // 引擎侧本就串行，这里保证顺序产出

        // ① 同步展开首个可探磁力 —— 首播集名必须准确
        await ExpandFirstMagnetAsync(sources, engine, gate, ct).ConfigureAwait(false);

        // ② 上屏：此时首集已是真实文件名（若首探失败则退回原始名，行为同改动前）
        yield return Clone(sources);

        // ③ 其余磁力继续展开：每完成一条推一次
        foreach (var src in sources)
        {
            for (int i = 0; i < src.Episodes.Count; i++)
            {
                var ep = src.Episodes[i];
                if (!ep.Url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) continue;
                if (ListedMagnets.ContainsKey(ep.Url) || TryDiskCache(ep.Url, out _)) continue;  // 已探过
                if (engine.IsBusy) continue;
                if (ct.IsCancellationRequested) yield break;

                var changed = await ExpandSingleMagnetAsync(src, i, engine, gate, ct).ConfigureAwait(false);
                if (changed) yield return Clone(sources);
            }
        }
    }

    /// <summary>展开第一条可探磁力（只做一条，保证首屏集名准确）。</summary>
    private static async Task<bool> ExpandFirstMagnetAsync(
        List<VodPlaySource> sources, IPreferredMagnetEngine engine, SemaphoreSlim gate, CancellationToken ct)
    {
        foreach (var src in sources)
            for (int i = 0; i < src.Episodes.Count; i++)
            {
                if (!src.Episodes[i].Url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) continue;
                if (engine.IsBusy) return false;

                // 缓存命中无需再探（ExpandSingleMagnetAsync 内部会走缓存并回填）
                var ok = await ExpandSingleMagnetAsync(src, i, engine, gate, ct).ConfigureAwait(false);
                if (ok || ListedMagnets.ContainsKey(src.Episodes[i].Url) || TryDiskCache(src.Episodes[i].Url, out _))
                    return true;
            }
        return false;
    }

    /// <summary>
    /// 展开单条磁力并把结果并回 <paramref name="src"/>。返回是否真的发生了替换。
    /// 内部无异常（失败静默返回 false），以便调用方安全地 yield。
    /// </summary>
    private static async Task<bool> ExpandSingleMagnetAsync(
        VodPlaySource src, int index, IPreferredMagnetEngine engine, SemaphoreSlim gate, CancellationToken ct)
    {
        if (index < 0 || index >= src.Episodes.Count) return false;
        var ep = src.Episodes[index];

        // 缓存命中：直接用（首次进入详情页时磁盘缓存即来源于此）
        List<Interfaces.MagnetFile>? files;
        if (!ListedMagnets.TryGetValue(ep.Url, out files) && !TryDiskCache(ep.Url, out files))
        {
            files = await ProbeOneAsync(engine, ep, gate, ct).ConfigureAwait(false);
        }
        if (files is not { Count: > 0 }) return false;

        var videos = files
            .Where(f => VideoExtensions.Contains(Path.GetExtension(f.Name)))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (videos.Count == 0) return false;

        // 幂等：已是展开态（首项名字与种子内首文件一致）就不重复替换
        if (src.Episodes[index].Name == videos[0].Name) return false;

        var rebuilt = new List<VodEpisode>(src.Episodes.Count + videos.Count - 1);
        for (int k = 0; k < src.Episodes.Count; k++)
        {
            if (k == index)
                foreach (var f in videos)
                    rebuilt.Add(new VodEpisode { Name = f.Name, Url = ep.Url, Flag = ep.Flag });
            else
                rebuilt.Add(src.Episodes[k]);
        }
        src.Episodes = SortExpandedEpisodes(rebuilt);
        return true;
    }

    /// <summary>深拷贝线路（避免把内部可变 List 交给界面后被后续展开改写而闪烁）。</summary>
    private static List<VodPlaySource> Clone(List<VodPlaySource> sources) =>
        sources.Select(s => new VodPlaySource
        {
            Name = s.Name,
            Episodes = s.Episodes.ToList(),
        }).ToList();

    /// <summary>
    /// 把「一条磁力 = 一集」展开成「种子里的每个视频文件 = 一集」。
    ///
    /// <para><b>为什么必须做</b>：磁力站的 vod_play_url 往往是几个**打包磁力**。例如
    /// 新6V《生逢其时》只给 3 条磁力（01-06 / 07 / 08-11），不展开就成了「只能看 1、7、8 三集」，
    /// 而 TVBox 显示 11 集 —— 因为它的 11 集正是种子文件列表（6+1+4）。</para>
    ///
    /// <para>展开靠优先引擎（迅雷）的文件列表：实测 35~567ms/磁力，详情页代价可接受；
    /// 引擎没起来或解析失败时**原样保留**，不影响任何既有行为。</para>
    ///
    /// <para>展开后集名 = 种子内文件名；播放时磁力引擎（迅雷）用它做 preferName 命中同一个文件，
    /// 因此无需额外传文件索引。</para>
    /// </summary>
    private static async Task<List<VodPlaySource>> ExpandMagnetEpisodesAsync(
        List<VodPlaySource> sources, CancellationToken ct)
    {
        var engine = Interfaces.MagnetEngines.Thunder;
        if (engine is null || !engine.IsReady) return sources;

        var magnetCount = sources
            .SelectMany(s => s.Episodes)
            .Count(e => e.Url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase));
        // 磁力太多就不展开：详情页会被串行解析拖住（宁可保持现状）
        if (magnetCount == 0 || magnetCount > MaxMagnetsToExpand) return sources;

        foreach (var src in sources)
        {
            var isMagnet = new bool[src.Episodes.Count];
            for (int i = 0; i < src.Episodes.Count; i++)
                isMagnet[i] = src.Episodes[i].Url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);

            // 探测任务化（原来是就地串行 await）：引擎侧 ListFilesAsync 内部持有 _gate
            //   （单 VM 单会话的硬约束，不能并发建任务），所以**真正的探测仍是串行**——
            //   这里任务化换来的是：缓存命中项零开销短路、以及各探测的宿主侧开销
            //  （HTTP 尝试/Bencode 解析/磁盘缓存写入）能重叠在等待间隙里。
            //   想要大幅提速只能靠缓存命中（见下方磁盘缓存）与 VM 预热。
            var probes = new Task<List<Interfaces.MagnetFile>?>[src.Episodes.Count];
            using var probeGate = new SemaphoreSlim(ProbeConcurrency);

            for (int i = 0; i < src.Episodes.Count; i++)
            {
                if (!isMagnet[i]) { probes[i] = Task.FromResult<List<Interfaces.MagnetFile>?>(null); continue; }

                var ep = src.Episodes[i];

                // 先查进程级缓存（命中零开销）：命中时**即使引擎忙也照样套用真实文件名**。
                // 播放中 IsBusy 只应阻止「再去探测新磁力」，不该连带丢弃已缓存的结果 ——
                // 否则播放时选集栏永远显示站点给的打包名（如「第四季01-03-1080p」），
                // 而不是种子内真实文件名。
                if (ListedMagnets.TryGetValue(ep.Url, out var cached))
                {
                    probes[i] = Task.FromResult(cached);
                    continue;
                }

                // 再查磁盘缓存（跨启动复用）：磁力文件列表是种子里写死的元数据，永不变更，
                // 同一个磁力没必要每次启动都重探一遍。
                if (TryDiskCache(ep.Url, out var diskCached))
                {
                    ListedMagnets[ep.Url] = diskCached;   // 回填进程级，后续零开销
                    probes[i] = Task.FromResult<List<Interfaces.MagnetFile>?>(diskCached);
                    continue;
                }

                // ★ 未缓存 + 引擎忙（播放/下载中）：探测会与播放会话抢引擎（单会话互顶），
                //   引擎下载中建新任务还会被拒（9111）——顶掉 45Mbps 下载中的播放 = 黑屏 + 弹窗。
                //   本次保持原名，下次进详情页再探。
                if (engine.IsBusy)
                {
                    probes[i] = Task.FromResult<List<Interfaces.MagnetFile>?>(null);
                    continue;
                }

                probes[i] = ProbeOneAsync(engine, ep, probeGate, ct);
            }

            var results = await Task.WhenAll(probes).ConfigureAwait(false);

            var expanded = new List<VodEpisode>(src.Episodes.Count);
            for (int i = 0; i < src.Episodes.Count; i++)
            {
                var ep = src.Episodes[i];
                if (!isMagnet[i]) { expanded.Add(ep); continue; }

                var videos = results[i]?
                    .Where(f => VideoExtensions.Contains(Path.GetExtension(f.Name)))
                    .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // 展开失败（或种子内没有视频文件）→ 保留原本那一条，行为与改动前一致
                if (videos is null || videos.Count == 0) { expanded.Add(ep); continue; }

                foreach (var f in videos)
                    expanded.Add(new VodEpisode { Name = f.Name, Url = ep.Url, Flag = ep.Flag });
            }
            src.Episodes = SortExpandedEpisodes(expanded);
        }
        return sources;
    }

    /// <summary>探测单条磁力的文件列表；成功即写进程级 + 磁盘缓存。失败返回 null（调用方保留原集名）。</summary>
    private static async Task<List<Interfaces.MagnetFile>?> ProbeOneAsync(
        IPreferredMagnetEngine engine, VodEpisode ep, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var files = await engine.ListFilesAsync(ep.Url, ep.Name, ct).ConfigureAwait(false);
            // 只缓存成功结果：失败（引擎未就绪等）下次进详情页重探
            if (files is { Count: > 0 })
            {
                ListedMagnets[ep.Url] = files;
                SaveDiskCache(ep.Url, files);
            }
            return files;
        }
        catch { return null; }
        finally { gate.Release(); }
    }

    /// <summary>探测并发上限。引擎侧 _gate 已串行化真正的探测，这里只限制排队任务数，
    /// 避免一次详情页（实测最多 8 个打包磁力）把任务/句柄堆起来。</summary>
    private const int ProbeConcurrency = 4;

    // ═══════════ 探测结果磁盘缓存（跨启动复用）═══════════
    //
    // 进程内缓存在 Application 重启后即失效，而磁力文件列表**是种子里写死的元数据**，
    // 永远不会变 —— 同一个磁力没必要每次启动都重新探测一遍（每条 0.2~3s）。
    // 落盘后「上次看过的剧」再次进入详情页可直接展开选集，零网络零等待。

    private const int MaxDiskCacheFiles = 2000;

    private static readonly string DiskCachePath =
        Path.Combine(CatClawVideo.Core.AppPaths.DataRoot, "magnet-files.json");

    private static readonly object DiskCacheSync = new();
    private static bool _diskCacheLoaded;

    /// <summary>按磁力链接建索引的磁盘缓存：<c>{ magnet: [文件…] }</c></summary>
    private static readonly Dictionary<string, List<Interfaces.MagnetFile>> DiskCache =
        new(StringComparer.Ordinal);

    /// <summary>首次访问时加载磁盘缓存（懒加载，避免启动期做 IO）。</summary>
    private static void EnsureDiskCacheLoaded()
    {
        if (_diskCacheLoaded) return;
        lock (DiskCacheSync)
        {
            if (_diskCacheLoaded) return;
            _diskCacheLoaded = true;
            try
            {
                if (!File.Exists(DiskCachePath)) return;
                var json = File.ReadAllText(DiskCachePath);
                var data = System.Text.Json.JsonSerializer
                    .Deserialize<Dictionary<string, List<Interfaces.MagnetFile>>>(json);
                if (data is null) return;
                foreach (var kv in data)
                {
                    if (kv.Value is { Count: > 0 }) DiskCache[kv.Key] = kv.Value;
                }
            }
            catch { /* 缓存损坏 → 当作空，下次探测会重建 */ }
        }
    }

    /// <summary>取磁盘缓存（不存在返回 false）。</summary>
    private static bool TryDiskCache(string magnet, out List<Interfaces.MagnetFile> files)
    {
        EnsureDiskCacheLoaded();
        lock (DiskCacheSync) return DiskCache.TryGetValue(magnet, out files!);
    }

    /// <summary>写入磁盘缓存（超出上限时丢最旧的一半；异步落盘不挡调用方）。</summary>
    private static void SaveDiskCache(string magnet, List<Interfaces.MagnetFile> files)
    {
        Dictionary<string, List<Interfaces.MagnetFile>> snapshot;
        lock (DiskCacheSync)
        {
            EnsureDiskCacheLoaded();
            DiskCache[magnet] = files;
            if (DiskCache.Count > MaxDiskCacheFiles)
            {
                // 不做 LRU（重建代价可控），超限直接裁掉前一半，避免文件无限增长
                foreach (var k in DiskCache.Keys.Take(DiskCache.Count / 2).ToList())
                    DiskCache.Remove(k);
            }
            snapshot = new Dictionary<string, List<Interfaces.MagnetFile>>(DiskCache);
        }

        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DiskCachePath)!);
                File.WriteAllText(DiskCachePath,
                    System.Text.Json.JsonSerializer.Serialize(snapshot));
            }
            catch { /* 落盘失败不影响本次展开 */ }
        });
    }

    /// <summary>
    /// 展开后按「集号」排序（2026-09-17 实测：多打包磁力 01-05-1080p / 01-05-2160p / 06-09…
    /// 展开的列表天然按磁力包顺序排列，连播到包尾会跳进下一个包的<b>第 1 集</b>——
    /// 1080p 第 5 集播完自动连播到 2160p 第 1 集，重新下载 5.7GB）。
    /// 排序键 = (是否首选分辨率, 集号, 原顺序)：首选分辨率 = 第一个展开文件的分辨率
    /// （站点通常把主打清晰度的包排前面），保证连播沿首选线 1→N 一路走完才进其他线。
    /// </summary>
    private static List<VodEpisode> SortExpandedEpisodes(List<VodEpisode> expanded)
    {
        if (expanded.Count <= 1) return expanded;
        // 先按 (集号, 分辨率) 去重：分包包与全集打包包常含同名同集文件（如 01.1080p 同时
        // 出现在 01-05-1080p 与 全集打包-1080p 两个种子里），不去重连播会在同集内容上
        // 跨种子重来。保留先展开的（站点主打包顺序）。
        var seen = new HashSet<(int, string)>();
        var deduped = new List<VodEpisode>(expanded.Count);
        foreach (var ep in expanded)
        {
            if (!seen.Add((EpisodeNumberOf(ep.Name), ResolutionOf(ep.Name)))) continue;
            deduped.Add(ep);
        }
        var preferredRes = ResolutionOf(deduped[0].Name);
        return deduped
            .Select((ep, idx) => (Ep: ep, Idx: idx))
            .OrderBy(t => ResolutionOf(t.Ep.Name) == preferredRes ? 0 : 1)
            .ThenBy(t => EpisodeNumberOf(t.Ep.Name))
            .ThenBy(t => t.Idx)
            .Select(t => t.Ep)
            .ToList();
    }

    /// <summary>文件名里的分辨率标记（"2160p"/"1080p"/"720p"，无则空串）。</summary>
    private static string ResolutionOf(string name)
    {
        foreach (var res in new[] { "2160p", "1080p", "720p" })
            if (name.Contains(res, StringComparison.OrdinalIgnoreCase))
                return res;
        return "";
    }

    /// <summary>从文件名提取集号，按常见命名依次尝试：
    /// <c>S01E07</c>（季集）→ <c>EP07</c>/<c>E07</c> → <c>第07集</c> → 开头数字 <c>07.1080p…</c>；
    /// 全部解析不出返回 9999（排最后、保持原序）。
    /// <para>⚠ 2026-09-17 压测实测 bug：此前只认「<b>开头</b>数字」，遇到
    /// <c>杀手妈咪.A.Bona.Fide.Killer.S01E01.1080p…</c> 这类「字母在前、SxxExx 在中间」的命名，
    /// 16 个文件全被判成 9999 → <see cref="SortExpandedEpisodes"/> 按 (集号,分辨率) 去重后
    /// **只剩 1 集**（详情页选集栏只显示 1 条，其余 15 集全部丢失）。</para></summary>
    private static int EpisodeNumberOf(string name)
    {
        // ① S01E07 / s1e7（取 E 后面的集号；季号不参与排序）
        var m = System.Text.RegularExpressions.Regex.Match(
            name, @"[Ss]\d{1,2}\s*[Ee](\d{1,4})(?![0-9])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n1)) return n1;

        // ② EP07 / E07 / EP.07（前面是分隔符或串首，避免误吃 S01E07 里的 E07——那里已被 ① 截获）
        m = System.Text.RegularExpressions.Regex.Match(
            name, @"(?:^|[\s._\-\[\]])(?:[Ee][Pp]|[Ee])[\s._\-]?(\d{1,4})(?![0-9])",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n2)) return n2;

        // ③ 第07集 / 第7话
        m = System.Text.RegularExpressions.Regex.Match(
            name, @"第\s*(\d{1,4})(?![0-9])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n3)) return n3;

        // ④ 开头数字（原有行为）：01.1080p… / 23-24-2160p…
        m = System.Text.RegularExpressions.Regex.Match(
            name, @"^(?:\s*第\s*)?(\d{1,4})(?![0-9])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n4) ? n4 : 9999;
    }

    /// <summary>单次详情页最多展开的磁力条数（超出则放弃展开，避免串行解析拖慢）</summary>
    private const int MaxMagnetsToExpand = 12;

    /// <summary>磁力 → 文件列表 的进程级缓存：每条磁力探测要 ~5s（引擎解析种子），5 条磁力的详情页
    /// 首次要 25s+。缓存后再次进入（含离开后回来、超引擎 10 分钟会话）直接命中，秒开。只存成功结果。</summary>
    private static readonly ConcurrentDictionary<string, List<Interfaces.MagnetFile>?> ListedMagnets =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".ts", ".m2ts", ".wmv", ".flv", ".mov",
        ".rmvb", ".rm", ".webm", ".m4v", ".mpg", ".mpeg", ".3gp", ".vob",
    };

    public Task<PlayRequest> ResolvePlayUrlAsync(VodSiteInfo site, VodEpisode episode, CancellationToken ct = default) =>
        Required(site).ResolvePlayUrlAsync(site, episode, ct);

    public Task<List<VodItem>> SearchAsync(VodSiteInfo site, string keyword, CancellationToken ct = default) =>
        Required(site).SearchAsync(site, keyword, ct);
}
