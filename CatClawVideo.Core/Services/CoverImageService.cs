using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CatClawVideo.Core.Services;

/// <summary>
/// 封面获取与兜底服务：把「源封面 URL」变成「本地可用的图片文件路径」。
///
/// <para>
/// 存在的理由：源站点封面常常取不到（防盗链、CDN 失效、JS 盾、站点自身资源损坏），
/// 而播放器用的平台图片加载器既不校验内容、也不能按源补请求头，失败就是一片空白。
/// 本服务把封面获取收归一处，按固定兜底链解析：
/// </para>
/// <list type="number">
/// <item>源封面 URL（带同源 Referer / 浏览器 UA）</item>
/// <item>豆瓣按片名找海报（<c>subject_suggest</c>，图片必须带 Referer 才放行）</item>
/// <item>返回 null —— 调用方显示本地占位海报（绝不空白）</item>
/// </list>
///
/// <para>
/// 慢站/坏站防护（毒舌电影实测：18 条封面 = 18 次注定失败的请求，每次吃一个 6.5KB 挑战页）：
/// 并发上限、单请求超时、失败负缓存、**主机级熔断**（同主机连续失败即整体跳过一段冷却期）。
/// </para>
///
/// <para>
/// ⚠️ 校验必须看**魔数与长度**：豆瓣防盗链会回 <c>418</c> 且 <c>Content-Type: image/jpeg</c>
/// 却只有 13 字节；只看状态码/Content-Type 会把假图当成功缓存下来。
/// </para>
/// </summary>
public sealed class CoverImageService
{
    // ═══════════ 可调参数 ═══════════

    /// <summary>并发上限（慢站别拖垮列表）</summary>
    private const int MaxConcurrency = 4;

    /// <summary>单张封面请求超时</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    /// <summary>豆瓣检索超时（要快，失败就走占位）</summary>
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(6);

    /// <summary>失败负缓存时长（同 URL 在此期间不再重试）</summary>
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(10);

    /// <summary>同主机连续失败多少次后熔断</summary>
    private const int HostFailThreshold = 2;

    /// <summary>熔断冷却时长</summary>
    private static readonly TimeSpan HostCooldown = TimeSpan.FromMinutes(15);

    /// <summary>缓存体积上限 / 文件数上限（超出按最后写入时间淘汰）</summary>
    private const long MaxCacheBytes = 256L * 1024 * 1024;
    private const int MaxCacheFiles = 3000;

    /// <summary>小于此字节数的一律不算图片（防 13 字节假图）</summary>
    private const int MinImageBytes = 1024;

    /// <summary>已知图片扩展名（按内容魔数决定；查找缓存时逐个探测）</summary>
    private static readonly string[] KnownExts = [".jpg", ".png", ".webp", ".gif", ".bmp"];

    private const string BrowserUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    private const string DoubanReferer = "https://movie.douban.com/";

    /// <summary>rexxar 移动端接口的 Referer（通道②）</summary>
    private const string RexxarReferer = "https://m.douban.com/";

    private const string MobileUa =
        "Mozilla/5.0 (Linux; Android 13; Pixel 7) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/124.0.0.0 Mobile Safari/537.36";

    // ═══════════ 状态 ═══════════

    private readonly string _dir;
    private readonly Action<string>? _log;
    private readonly HttpClient _http;
    private readonly HttpClient _httpMobile;
    private readonly SemaphoreSlim _gate = new(MaxConcurrency, MaxConcurrency);

    /// <summary>缓存键 → 本地文件路径；值为 null 表示「已判定不可用」（命中即免重试）</summary>
    private readonly ConcurrentDictionary<string, string?> _resolved = new();

    /// <summary>缓存键 → 负缓存解禁时刻</summary>
    private readonly ConcurrentDictionary<string, DateTime> _negative = new();

    /// <summary>主机 → 熔断状态</summary>
    private readonly ConcurrentDictionary<string, HostState> _hosts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 片名 → 海报地址（跨源命中与豆瓣命中共用；null = 查过没有）。
    /// 只有 URL 有意义，来源通道不影响复用。
    /// </summary>
    private readonly ConcurrentDictionary<string, string?> _posterByTitle = new();

    /// <summary>片名 → 豆瓣「未命中」的重试时刻。
    /// ⚠️ 豆瓣 subject_suggest 会限流返回空数组，命中结果可永久缓存，
    /// 但**未命中必须带 TTL**，否则一次限流会让该片名整个会话都拿不到封面。</summary>
    private readonly ConcurrentDictionary<string, DateTime> _doubanMissUntil = new();

    /// <summary>片名 → 豆瓣未命中的重试间隔</summary>
    private static readonly TimeSpan DoubanMissTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 跨源封面通道：由上层注入（用**用户自己订阅的可搜索源**按片名找同名片封面）。
    /// Core 不依赖具体 provider —— 只认这个委托，返回海报 URL 或 null。
    /// 这条通道优先于豆瓣：打的是用户自己的源站（实测一次搜索即可拿到同名片封面），
    /// 没有第三方限流，且内容口径一致。
    /// </summary>
    private readonly Func<string, CancellationToken, Task<string?>>? _crossSourceCover;

    /// <summary>跨源检索串行 + 最小间隔（打的是用户自己的源站，克制一点）</summary>
    private readonly SemaphoreSlim _crossGate = new(1, 1);
    private static readonly TimeSpan CrossMinInterval = TimeSpan.FromMilliseconds(1200);
    private DateTime _lastCrossCall = DateTime.MinValue;

    /// <summary>片名 → 跨源未命中的重试时刻（未命中也带 TTL，站点补片后能自愈）</summary>
    private readonly ConcurrentDictionary<string, DateTime> _crossMissUntil = new();
    private static readonly TimeSpan CrossMissTtl = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 豆瓣检索串行化 + 最小间隔。suggest 对突发并发很敏感（实测并发 4 次就开始回空数组），
    /// 而**命中结果会持久化**，所以这一次性成本换来的是后续启动零请求。
    /// </summary>
    private readonly SemaphoreSlim _doubanGate = new(1, 1);
    private static readonly TimeSpan DoubanMinInterval = TimeSpan.FromMilliseconds(800);
    private DateTime _lastDoubanCall = DateTime.MinValue;

    /// <summary>
    /// 豆瓣限流识别与退避。
    /// ⚠️ 实测关键事实：豆瓣限流表现为 **HTTP 200 + 空数组**（不是 4xx），
    /// 与「确实没有这个片名」无法从单次响应区分。如果照单全收地缓存成「未命中」，
    /// 会把本来有海报的片名永久打成无图（实测：同一片名前一次命中、下一次就"未命中"）。
    /// 因此：连续多次空结果即判定为限流 → 暂停检索一段冷却期，并撤销这批疑似误判的未命中。
    /// </summary>
    private int _doubanEmptyStreak;
    private DateTime _doubanBlockedUntil = DateTime.MinValue;
    private readonly List<string> _doubanSuspectMisses = [];

    /// <summary>连续多少次空结果判定为限流</summary>
    private const int DoubanEmptyStreakThreshold = 4;

    /// <summary>限流冷却时长</summary>
    private static readonly TimeSpan DoubanCooldown = TimeSpan.FromMinutes(5);

    /// <summary>片名 → 海报映射的持久化文件（跨会话复用，避免重启后重新检索）</summary>
    private readonly string _doubanMapFile;
    private readonly object _mapLock = new();

    private sealed class DoubanMapDto
    {
        public Dictionary<string, string> Hits { get; set; } = new();
        public Dictionary<string, DateTime> Misses { get; set; } = new();
        public Dictionary<string, DateTime>? CrossMisses { get; set; }
    }

    private readonly object _sweepLock = new();
    private int _writesSinceSweep;

    private sealed class HostState
    {
        public int ConsecutiveFailures;
        public DateTime BlockedUntil;
    }

    public CoverImageService(
        string cacheDir,
        Action<string>? log = null,
        Func<string, CancellationToken, Task<string?>>? crossSourceCover = null)
    {
        _dir = Path.Combine(cacheDir, "covers");
        _log = log;
        _crossSourceCover = crossSourceCover;
        try { Directory.CreateDirectory(_dir); } catch { }

        _doubanMapFile = Path.Combine(_dir, "douban-posters.json");
        LoadDoubanMap();

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            // 站点证书偶有问题时不因证书中断（封面不值得为它整块空白）
            AllowAutoRedirect = true,
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUa);
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept", "image/avif,image/webp,image/apng,image/*,*/*;q=0.8");

        // 移动端 UA 的独立客户端：rexxar 接口与它返回的签名图 CDN 认移动端身份
        _httpMobile = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _httpMobile.DefaultRequestHeaders.UserAgent.ParseAdd(MobileUa);
    }

    /// <summary>缓存目录（诊断用）</summary>
    public string CacheDirectory => _dir;

    // ═══════════════════ 对外主入口 ═══════════════════

    /// <summary>
    /// 解析封面为本地文件路径。
    /// 返回 null 表示「无可展示封面」——调用方应显示占位海报，不要留白。
    /// </summary>
    public async Task<string?> GetCoverAsync(string? coverUrl, string? title, CancellationToken ct = default)
    {
        var key = BuildKey(coverUrl, title);
        if (key == null) return null;

        if (TryMemory(key, out var cached)) return cached;

        try
        {
            // ① 源封面：只让这一步占用并发闸门。
            // ⚠️ 不要把豆瓣兜底也放进闸门里——豆瓣有最小间隔（可能等数秒），
            // 占着闸门会让**其它条目的正常源封面**（奥特/6V 的快路径）被拖在后面。
            string? path = null;
            if (!string.IsNullOrWhiteSpace(coverUrl))
            {
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (TryMemory(key, out cached)) return cached;
                    path = await TrySourceAsync(coverUrl, ct).ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }
            }

            // ② 跨源检索：用用户自己订阅的可搜索源按片名找同名片封面（无第三方限流，优先）
            if (path == null && !string.IsNullOrWhiteSpace(title))
                path = await TryCrossSourceAsync(title!, ct).ConfigureAwait(false);

            // ③ 豆瓣兜底（会限流，作为补充通道）
            if (path == null && !string.IsNullOrWhiteSpace(title))
                path = await TryDoubanAsync(title!, ct).ConfigureAwait(false);

            Remember(key, path);
            return path;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log($"封面解析异常 {coverUrl}: {ex.Message}");
            Remember(key, null);
            return null;
        }
    }

    private bool TryMemory(string key, out string? path)
    {
        if (_resolved.TryGetValue(key, out path)) return true;

        // 负缓存期内直接判定不可用（不再发请求）
        if (_negative.TryGetValue(key, out var until) && until > DateTime.Now)
        {
            path = null;
            return true;
        }
        return false;
    }

    private void Remember(string key, string? path)
    {
        _resolved[key] = path;
        if (path == null) _negative[key] = DateTime.Now + NegativeTtl;
        else _negative.TryRemove(key, out _);
    }

    // ═══════════════════ ① 源封面 ═══════════════════

    private async Task<string?> TrySourceAsync(string? coverUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(coverUrl)) return null;
        if (!coverUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return null;

        // 缓存命中（跨进程：文件已在盘上）
        var key = "s:" + coverUrl;
        var hit = FindCachedFile(key);
        if (hit != null) return hit;

        if (IsHostBlocked(coverUrl)) return null;

        var (bytes, err) = await FetchAsync(coverUrl, SameOriginReferer(coverUrl), RequestTimeout, ct).ConfigureAwait(false);
        if (bytes == null)
        {
            MarkHostFailure(coverUrl, err);
            return null;
        }

        MarkHostSuccess(coverUrl);
        return Save(key, bytes);
    }

    // ═══════════════════ ② 跨源兜底（用户自己的可搜索源）═══════════════════

    /// <summary>
    /// 用上层注入的跨源检索拿同名片的封面。
    /// 命中/未命中都按片名缓存（命中永久、未命中 30 分钟 TTL），因此每个片名最多只检索一次。
    /// </summary>
    private async Task<string?> TryCrossSourceAsync(string title, CancellationToken ct)
    {
        if (_crossSourceCover == null) return null;

        if (_posterByTitle.TryGetValue(title, out var known) && known != null)
            return await TryPosterUrlAsync(known, title, ct).ConfigureAwait(false);

        if (_crossMissUntil.TryGetValue(title, out var until) && until > DateTime.Now) return null;

        await _crossGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_posterByTitle.TryGetValue(title, out known) && known != null)
                return await TryPosterUrlAsync(known, title, ct).ConfigureAwait(false);

            var waited = DateTime.Now - _lastCrossCall;
            if (waited < CrossMinInterval)
                await Task.Delay(CrossMinInterval - waited, ct).ConfigureAwait(false);

            _lastCrossCall = DateTime.Now;
            var url = await _crossSourceCover(title, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(url))
            {
                _crossMissUntil[title] = DateTime.Now + CrossMissTtl;
                SaveDoubanMap();
                return null;
            }

            _posterByTitle[title] = url;
            _crossMissUntil.TryRemove(title, out _);
            Log($"跨源命中封面「{title}」← {Shorten(url)}");
            SaveDoubanMap();
            return await TryPosterUrlAsync(url!, title, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _crossGate.Release();
        }
    }

    // ═══════════════════ ③ 豆瓣兜底 ═══════════════════

    /// <summary>
    /// 把海报地址取回并缓存为本地文件。
    /// Referer 按站点区分：豆瓣图必须带豆瓣 Referer（否则 418 假图），
    /// 其它站点（跨源借来的封面）用同源 Referer。
    /// </summary>
    private async Task<string?> TryPosterUrlAsync(string posterUrl, string title, CancellationToken ct)
    {
        var key = "s:" + posterUrl;
        var hit = FindCachedFile(key);
        if (hit != null) return hit;

        var isDouban = posterUrl.Contains("doubanio.com", StringComparison.OrdinalIgnoreCase);
        var referer = isDouban ? DoubanReferer : SameOriginReferer(posterUrl);

        var (bytes, err) = await FetchAsync(posterUrl, referer, RequestTimeout, ct).ConfigureAwait(false);
        if (bytes == null && isDouban)
            (bytes, err) = await FetchAsync(posterUrl, referer, RequestTimeout, ct, _httpMobile).ConfigureAwait(false);

        if (bytes == null)
        {
            // ⚠️ 豆瓣 rexxar 的 cover_url 是**带签名的临时地址**，会过期；
            // 过期后若不作废映射，该片名将永久无图 → 作废后下次重新检索。
            if (_posterByTitle.TryRemove(title, out _))
            {
                _doubanMissUntil.TryRemove(title, out _);
                _crossMissUntil.TryRemove(title, out _);
                Log($"海报地址失效，作废映射「{title}」（{err}），下次重新检索");
                SaveDoubanMap();
            }
            else
            {
                Log($"海报拉取失败「{title}」: {err}");
            }
            return null;
        }
        return Save(key, bytes);
    }

    private async Task<string?> TryDoubanAsync(string title, CancellationToken ct)
    {
        var poster = await FindDoubanPosterAsync(title, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(poster)) return null;
        return await TryPosterUrlAsync(poster, title, ct).ConfigureAwait(false);
    }

    private async Task<string?> FindDoubanPosterAsync(string title, CancellationToken ct)
    {
        if (_posterByTitle.TryGetValue(title, out var cached))
        {
            // 命中结果永久有效；未命中只在 TTL 内跳过（留出限流恢复的余地）
            if (cached != null) return cached;
            if (_doubanMissUntil.TryGetValue(title, out var until) && until > DateTime.Now) return null;
        }

        // 限流冷却期内直接返回 null 且**不缓存**（冷却结束后可重试）
        if (_doubanBlockedUntil > DateTime.Now)
        {
            Log($"豆瓣限流冷却中（至 {_doubanBlockedUntil:HH:mm:ss}），「{title}」本次跳过");
            return null;
        }

        // 串行化 + 最小间隔：suggest 对并发敏感，串行可显著降低被限流的概率
        await _doubanGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_posterByTitle.TryGetValue(title, out cached) && cached != null) return cached;

            var waited = DateTime.Now - _lastDoubanCall;
            if (waited < DoubanMinInterval)
                await Task.Delay(DoubanMinInterval - waited, ct).ConfigureAwait(false);

            // 两条独立通道，限流互相独立、互为备份：
            //   ① 网页版 subject_suggest —— 覆盖好，但限流极其敏感（实测连续几次后整片回空数组）
            //   ② 移动端 rexxar API     —— 网页版被打限流时实测仍有响应（且海报可直取）
            var poster = await LookupBySuggestAsync(title, ct).ConfigureAwait(false)
                      ?? await LookupByRexxarAsync(title, ct).ConfigureAwait(false);

            if (poster != null)
            {
                _posterByTitle[title] = poster;
                _doubanMissUntil.TryRemove(title, out _);
                // 命中即说明没被限流：清空疑似误判，让它们有机会重新解析
                if (_doubanSuspectMisses.Count > 0)
                {
                    foreach (var t in _doubanSuspectMisses)
                    {
                        _doubanMissUntil.TryRemove(t, out _);
                        _posterByTitle.TryRemove(t, out _);
                    }
                    Log($"豆瓣恢复响应，撤销 {_doubanSuspectMisses.Count} 条疑似限流误判");
                    _doubanSuspectMisses.Clear();
                }
                _doubanEmptyStreak = 0;
                Log($"豆瓣命中封面「{title}」");
                SaveDoubanMap();
                return poster;
            }

            // 两通道都没给海报：可能是「确实没有」，也可能是「整体限流」。
            _doubanEmptyStreak++;
            if (_doubanEmptyStreak >= DoubanEmptyStreakThreshold)
            {
                // 判定为限流：进入冷却，并撤销这批疑似误判的未命中（其余下轮再试）
                _doubanBlockedUntil = DateTime.Now + DoubanCooldown;
                foreach (var t in _doubanSuspectMisses)
                {
                    _doubanMissUntil.TryRemove(t, out _);
                    _posterByTitle.TryRemove(t, out _);
                }
                Log($"豆瓣疑似限流（连续 {_doubanEmptyStreak} 次两通道皆空），" +
                    $"暂停 {DoubanCooldown.TotalMinutes:F0} 分钟并撤销 {_doubanSuspectMisses.Count} 条误判");
                _doubanSuspectMisses.Clear();
                _doubanEmptyStreak = 0;
                SaveDoubanMap();
                return null;   // 本次不缓存为未命中
            }

            _posterByTitle[title] = null;
            _doubanMissUntil[title] = DateTime.Now + DoubanMissTtl;
            _doubanSuspectMisses.Add(title);
            Log($"豆瓣未命中封面「{title}」（{DoubanMissTtl.TotalMinutes:F0} 分钟后可重试）");
            SaveDoubanMap();
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _doubanGate.Release();
        }
    }

    /// <summary>通道①：网页版 subject_suggest</summary>
    private async Task<string?> LookupBySuggestAsync(string title, CancellationToken ct)
    {
        foreach (var q in QueryVariants(title))
        {
            var url = "https://movie.douban.com/j/subject_suggest?q=" + Uri.EscapeDataString(q);
            await ThrottleDoubanAsync(ct).ConfigureAwait(false);
            var (text, ok) = await FetchStringAsync(url, DoubanReferer, LookupTimeout, ct).ConfigureAwait(false);
            if (!ok) continue;

            var poster = PickPoster(text!, q);
            if (poster != null)
            {
                Log($"[suggest] 命中「{title}」← 检索词「{q}」");
                return poster;
            }
        }
        return null;
    }

    /// <summary>通道②：移动端 rexxar 搜索（结构 subjects.items[].target.cover_url）</summary>
    private async Task<string?> LookupByRexxarAsync(string title, CancellationToken ct)
    {
        foreach (var q in QueryVariants(title))
        {
            var url = "https://m.douban.com/rexxar/api/v2/search?q=" +
                      Uri.EscapeDataString(q) + "&count=3&start=0";
            await ThrottleDoubanAsync(ct).ConfigureAwait(false);
            var (text, ok) = await FetchStringAsync(url, RexxarReferer, LookupTimeout, ct, _httpMobile)
                .ConfigureAwait(false);
            if (!ok) continue;

            var poster = PickRexxarPoster(text!, q);
            if (poster != null)
            {
                Log($"[rexxar] 命中「{title}」← 检索词「{q}」");
                return poster;
            }
        }
        return null;
    }

    /// <summary>豆瓣检索最小间隔（两条通道共用，避免叠加成突发）</summary>
    private async Task ThrottleDoubanAsync(CancellationToken ct)
    {
        var waited = DateTime.Now - _lastDoubanCall;
        if (waited < DoubanMinInterval)
            await Task.Delay(DoubanMinInterval - waited, ct).ConfigureAwait(false);
        _lastDoubanCall = DateTime.Now;
    }

    /// <summary>从 rexxar 响应挑海报：优先标题完全一致的条目，否则第一条有图的</summary>
    private static string? PickRexxarPoster(string json, string query)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("subjects", out var subjects)) return null;
            if (!subjects.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return null;

            string? first = null;
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("target", out var target)) continue;
                if (!target.TryGetProperty("cover_url", out var cu)) continue;
                var url = cu.GetString();
                if (string.IsNullOrWhiteSpace(url)) continue;
                first ??= url;

                if (target.TryGetProperty("title", out var t) &&
                    string.Equals(t.GetString()?.Trim(), query, StringComparison.Ordinal))
                    return url;
            }
            return first;
        }
        catch
        {
            return null;
        }
    }

    // ═══════════════════ 豆瓣映射持久化 ═══════════════════

    private void LoadDoubanMap()
    {
        try
        {
            if (!File.Exists(_doubanMapFile)) return;
            var dto = JsonSerializer.Deserialize<DoubanMapDto>(File.ReadAllText(_doubanMapFile));
            if (dto == null) return;

            foreach (var kv in dto.Hits) _posterByTitle[kv.Key] = kv.Value;
            foreach (var kv in dto.Misses)
                if (kv.Value > DateTime.Now) _doubanMissUntil[kv.Key] = kv.Value;
            if (dto.CrossMisses != null)
                foreach (var kv in dto.CrossMisses)
                    if (kv.Value > DateTime.Now) _crossMissUntil[kv.Key] = kv.Value;
            Log($"海报映射载入 {dto.Hits.Count} 条命中 / {dto.Misses.Count} 条豆瓣未命中 / " +
                $"{dto.CrossMisses?.Count ?? 0} 条跨源未命中");
        }
        catch (Exception ex)
        {
            Log($"豆瓣映射载入失败（忽略）: {ex.Message}");
        }
    }

    /// <summary>
    /// 落盘（每次映射变更即写）。
    /// ⚠️ 不要做"防抖 + 只保存一次"的优化：进程可能在防抖窗口内退出，
    /// 而**映射丢失会让已缓存的图片变成找不到的死文件**（重启后必须重新打豆瓣）。
    /// 检索本身已串行限速（400ms/次），几次 KB 级写入可以忽略。
    /// 写入用「临时文件 + 替换」避免中途崩溃留下半个 JSON。
    /// </summary>
    private void SaveDoubanMap()
    {
        lock (_mapLock)
        {
            try
            {
                var dto = new DoubanMapDto
                {
                    Hits = _posterByTitle.Where(kv => kv.Value != null)
                                        .ToDictionary(kv => kv.Key, kv => kv.Value!),
                    Misses = _doubanMissUntil.Where(kv => kv.Value > DateTime.Now)
                                             .ToDictionary(kv => kv.Key, kv => kv.Value),
                    CrossMisses = _crossMissUntil.Where(kv => kv.Value > DateTime.Now)
                                                 .ToDictionary(kv => kv.Key, kv => kv.Value),
                };
                var tmp = _doubanMapFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(dto));
                File.Move(tmp, _doubanMapFile, overwrite: true);
            }
            catch (Exception ex)
            {
                Log($"豆瓣映射落盘失败: {ex.Message}");
            }
        }
    }

    /// <summary>检索词变体：原名 → 清洗名（去季/年番/年份/方括号标注）</summary>
    private static IEnumerable<string> QueryVariants(string title)
    {
        yield return title.Trim();

        var cleaned = CleanTitle(title);
        if (cleaned.Length > 0 && !string.Equals(cleaned, title.Trim(), StringComparison.Ordinal))
            yield return cleaned;
    }

    /// <summary>
    /// 片名清洗：去掉 []/【】/()（）标注、季数/年番后缀、末尾年份。
    /// 例：「逆天邪神[第二季]」→「逆天邪神」；「凡人修仙传 年番4」→「凡人修仙传」。
    /// </summary>
    public static string CleanTitle(string title)
    {
        var s = title;
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[\[【（(][^\]】）)]*[\]】）)]", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"第\s*[0-9一二三四五六七八九十]+\s*[季部]", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"年番\s*\d+", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*(19|20)\d{2}\s*$", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[\s·\-—_]+", " ");
        return s.Trim();
    }

    /// <summary>
    /// 跨源同名片匹配用的标题归一：清洗（去季/年份/标注）后再去空白与常见标点，
    /// 让「斗罗大陆2：绝世唐门」与「斗罗大陆2绝世唐门」这类写法能对上。
    /// </summary>
    public static string NormalizeTitle(string title)
    {
        var s = CleanTitle(title);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[\s\u3000:：·、,，.。!！?？\-—_/\\|（）()\[\]【】""'']+", "");
        return s.ToLowerInvariant();
    }

    /// <summary>
    /// 从 subject_suggest 结果里挑海报：优先标题与检索词完全一致的条目，否则取第一条有图的。
    /// </summary>
    private static string? PickPoster(string json, string query)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            string? firstImg = null;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("img", out var imgProp)) continue;
                var img = imgProp.GetString();
                if (string.IsNullOrWhiteSpace(img)) continue;
                firstImg ??= img;

                if (item.TryGetProperty("title", out var t) &&
                    string.Equals(t.GetString()?.Trim(), query, StringComparison.Ordinal))
                    return img;
            }
            return firstImg;
        }
        catch
        {
            return null;
        }
    }

    // ═══════════════════ 网络 ═══════════════════

    /// <summary>取字节；失败返回 (null, 原因)。非真实图片同样视为失败。</summary>
    private async Task<(byte[]? Bytes, string? Error)> FetchAsync(
        string url, string? referer, TimeSpan timeout, CancellationToken ct, HttpClient? client = null)
    {
        var http = client ?? _http;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (referer != null) req.Headers.Referrer = new Uri(referer);

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token)
                .ConfigureAwait(false);
            var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return (null, $"HTTP {(int)resp.StatusCode}");

            if (!IsRealImage(bytes))
                return (null, $"非图片内容（{bytes.Length}B，盾页/防盗链假图）");

            return (bytes, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, "超时");
        }
        catch (Exception ex)
        {
            return (null, ex.GetType().Name);
        }
    }

    /// <summary>取文本；返回 (内容, 是否拿到成功响应)。用 ok 区分「服务器说没有」与「根本没问到」。</summary>
    private async Task<(string? Text, bool Ok)> FetchStringAsync(
        string url, string? referer, TimeSpan timeout, CancellationToken ct, HttpClient? client = null)
    {
        var http = client ?? _http;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (referer != null) req.Headers.Referrer = new Uri(referer);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return (null, false);
            return (await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false), true);
        }
        catch
        {
            return (null, false);
        }
    }

    /// <summary>同源 Referer（防盗链站点常见要求）</summary>
    private static string? SameOriginReferer(string url)
    {
        try
        {
            var u = new Uri(url);
            return $"{u.Scheme}://{u.Authority}/";
        }
        catch
        {
            return null;
        }
    }

    // ═══════════════════ 主机熔断 ═══════════════════

    private bool IsHostBlocked(string url)
    {
        var host = HostOf(url);
        if (host == null) return false;
        if (!_hosts.TryGetValue(host, out var st)) return false;

        lock (st)
        {
            if (st.BlockedUntil > DateTime.Now) return true;
            if (st.BlockedUntil != default)
            {
                // 冷却结束：复位，允许再试
                st.BlockedUntil = default;
                st.ConsecutiveFailures = 0;
            }
            return false;
        }
    }

    private void MarkHostFailure(string url, string? reason)
    {
        var host = HostOf(url);
        if (host == null) return;
        var st = _hosts.GetOrAdd(host, _ => new HostState());
        lock (st)
        {
            st.ConsecutiveFailures++;
            if (st.ConsecutiveFailures >= HostFailThreshold && st.BlockedUntil == default)
            {
                st.BlockedUntil = DateTime.Now + HostCooldown;
                Log($"封面主机熔断 {host}（连续 {st.ConsecutiveFailures} 次失败：{reason}），" +
                    $"冷却 {HostCooldown.TotalMinutes:F0} 分钟");
            }
        }
    }

    private void MarkHostSuccess(string url)
    {
        var host = HostOf(url);
        if (host == null) return;
        if (_hosts.TryGetValue(host, out var st))
            lock (st) { st.ConsecutiveFailures = 0; st.BlockedUntil = default; }
    }

    private static string? HostOf(string url)
    {
        try { return new Uri(url).Host; }
        catch { return null; }
    }

    // ═══════════════════ 校验与落盘 ═══════════════════

    /// <summary>
    /// 是否为真实图片：长度达标 + 魔数匹配。
    /// 必须做——豆瓣防盗链的 418 响应带 <c>Content-Type: image/jpeg</c> 却只有十几字节。
    /// </summary>
    public static bool IsRealImage(byte[]? data)
    {
        if (data == null || data.Length < MinImageBytes) return false;
        return DetectExt(data) != null;
    }

    /// <summary>按魔数判定扩展名；不认识返回 null</summary>
    private static string? DetectExt(byte[] b)
    {
        if (b.Length < 12) return null;
        if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return ".jpg";
        if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return ".png";
        if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return ".gif";
        if (b[0] == 0x42 && b[1] == 0x4D) return ".bmp";
        if (b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 &&
            b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return ".webp";
        return null;
    }

    /// <summary>写入缓存文件；返回本地路径（失败返回 null）</summary>
    private string? Save(string key, byte[] bytes)
    {
        var ext = DetectExt(bytes);
        if (ext == null) return null;

        var path = Path.Combine(_dir, Hash(key) + ext);
        try
        {
            File.WriteAllBytes(path, bytes);
            MaybeSweep();
            return path;
        }
        catch (Exception ex)
        {
            Log($"封面写盘失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>按已知扩展名探测已缓存文件（无需索引文件）</summary>
    private string? FindCachedFile(string key)
    {
        var hash = Hash(key);
        foreach (var ext in KnownExts)
        {
            var p = Path.Combine(_dir, hash + ext);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..24].ToLowerInvariant();

    private static string? BuildKey(string? coverUrl, string? title)
    {
        if (!string.IsNullOrWhiteSpace(coverUrl)) return "u:" + coverUrl;
        if (!string.IsNullOrWhiteSpace(title)) return "t:" + title;
        return null;
    }

    // ═══════════════════ 缓存淘汰 ═══════════════════

    private void MaybeSweep()
    {
        if (Interlocked.Increment(ref _writesSinceSweep) < 20) return;
        Interlocked.Exchange(ref _writesSinceSweep, 0);

        lock (_sweepLock)
        {
            try
            {
                // 只统计图片文件：douban-posters.json（映射）绝不能进淘汰名单，
                // 否则映射一丢，盘上已缓存的图片就变成找不到的死文件
                var files = new DirectoryInfo(_dir).GetFiles()
                    .Where(f => KnownExts.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                long total = 0;
                foreach (var f in files) total += f.Length;
                if (files.Count <= MaxCacheFiles && total <= MaxCacheBytes) return;

                var byAge = files.OrderBy(f => f.LastWriteTimeUtc).ToList();
                var idx = 0;
                while (idx < byAge.Count &&
                       (byAge.Count - idx > MaxCacheFiles || total > MaxCacheBytes))
                {
                    total -= byAge[idx].Length;
                    try { byAge[idx].Delete(); } catch { }
                    idx++;
                }
                Log($"封面缓存淘汰 {idx} 个文件，剩余 {byAge.Count - idx} 个 / {total / 1024 / 1024}MB");
            }
            catch { }
        }
    }

    private void Log(string msg) => _log?.Invoke("[cover] " + msg);

    /// <summary>日志里缩短长 URL</summary>
    private static string Shorten(string url) => url.Length <= 72 ? url : url[..72] + "…";
}
