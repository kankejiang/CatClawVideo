namespace CatClawVideo.Core.Services;

/// <summary>
/// 公共 tracker 列表供给（解决硬编码过期问题）：
/// 本地缓存（带更新时间戳）→ 过期时从多个镜像拉 ngosang 列表 → **BEP15 connect 探测过滤**只留可达项 → 回写缓存。
/// <para>为什么必须过滤：同一 tier 内 tracker announce 串行，任何一个 DNS 污染/不可达的 tracker
/// 其超时都会拖垮整轮 peer 获取（2026-09 实测：23 个未过滤 → 60s 零节点；精选可达 → 15s 拿到 metadata）。</para>
/// <para>为什么用 trackers_best_ip.txt：IP 形式不含域名，天然免疫 DNS 污染。</para>
/// </summary>
public sealed class BtTrackerSource
{
    /// <summary>内置保底（实测配方）——联网拉取失败时保证磁力可用</summary>
    public static readonly string[] BuiltInFallback =
    [
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://open.stealth.si:80/announce",
        "udp://explodie.org:6969/announce",
        "udp://exodus.desync.com:6969/announce",
        "https://tracker.foreverpirates.co:443/announce",
        "http://tracker.openbittorrent.com:80/announce",
    ];

    /// <summary>可用列表源（按可用性排序；ngosang 官方 raw 在国内被 DNS 污染，故走 CDN/代理多源回退）</summary>
    public static readonly string[] DefaultSources =
    [
        "https://cdn.jsdelivr.net/gh/ngosang/trackerslist@master/trackers_best_ip.txt",
        "https://fastly.jsdelivr.net/gh/ngosang/trackerslist@master/trackers_best_ip.txt",
        "https://git.yylx.win/https://raw.githubusercontent.com/ngosang/trackerslist/master/trackers_best_ip.txt",
        "https://cdn.jsdelivr.net/gh/ngosang/trackerslist@master/trackers_best.txt",
        "https://git.yylx.win/https://raw.githubusercontent.com/ngosang/trackerslist/master/trackers_best.txt",
    ];

    /// <summary>过滤后保留的 tracker 上限（太多反而拖慢 announce）</summary>
    public const int MaxTrackers = 14;
    /// <summary>缓存有效期</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    private readonly string _cachePath;
    private readonly Action<string>? _log;
    private readonly HttpClient _http;
    private string[]? _memory;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public BtTrackerSource(string cacheDir, Action<string>? log = null)
    {
        _cachePath = Path.Combine(cacheDir, "trackers.txt");
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.Add("User-Agent", "CatClawVideo/1.0");
    }

    private void Log(string m) => _log?.Invoke("[trackers] " + m);

    /// <summary>最近一次成功更新时间（UI 展示用）</summary>
    public DateTime? LastUpdatedUtc { get; private set; }

    /// <summary>当前生效列表（可直接注入磁力）</summary>
    public IReadOnlyList<string> Current => _memory ?? BuiltInFallback;

    /// <summary>
    /// 取 tracker 列表：内存 → 文件缓存（未过期）→ 远端拉取并过滤 → 失败回退保底。
    /// </summary>
    public async Task<string[]> GetAsync(bool forceRefresh = false, bool allowFetch = true, CancellationToken ct = default)
    {
        if (!forceRefresh && _memory is { Length: > 0 }) return _memory;

        await _lock.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _memory is { Length: > 0 }) return _memory;

            // ① 文件缓存
            if (TryLoadCache(out var cached, out var updatedAt))
            {
                _memory = cached;
                LastUpdatedUtc = updatedAt;
                if (!forceRefresh && DateTime.UtcNow - updatedAt < CacheTtl)
                {
                    Log($"缓存命中（{cached.Length} 个，{updatedAt:MM-dd HH:mm} 更新）");
                    return cached;
                }
                if (!allowFetch)
                {
                    Log("自动更新已关闭：沿用过期缓存");
                    return cached;
                }
            }
            else if (!allowFetch)
            {
                Log("自动更新已关闭且无缓存：使用内置保底列表");
                _memory = BuiltInFallback;
                return _memory;
            }

            // ② 远端拉取（多镜像回退）
            var raw = await FetchFromSourcesAsync(ct);
            if (raw.Count == 0)
            {
                Log("拉取失败，使用内置保底列表");
                _memory = _memory is { Length: > 0 } ? _memory : BuiltInFallback;
                return _memory;
            }

            // ③ 探测过滤：只留 BEP15 connect 有响应的（并发，3s 超时）
            var reachable = await FilterReachableAsync(raw, ct);
            var final = reachable.Count >= 3 ? reachable : raw.Take(MaxTrackers).ToList();
            if (reachable.Count < 3 && reachable.Count > 0)
                Log($"可达项仅 {reachable.Count} 个，回退用未过滤列表保底");
            if (reachable.Count == 0)
                Log("探测全失败（可能网络抖动/被限），使用未过滤列表");

            var result = final.Take(MaxTrackers).ToArray();
            if (result.Length == 0) result = BuiltInFallback;

            _memory = result;
            LastUpdatedUtc = DateTime.UtcNow;
            SaveCache(result);
            Log($"列表更新：{raw.Count} 个候选 → {reachable.Count} 个可达 → 采用 {result.Length} 个");
            return result;
        }
        catch (Exception ex)
        {
            Log($"更新异常：{ex.Message}，沿用现有列表");
            _memory ??= BuiltInFallback;
            return _memory;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ────────────────────── 拉取 ──────────────────────

    private async Task<List<string>> FetchFromSourcesAsync(CancellationToken ct)
    {
        foreach (var url in DefaultSources)
        {
            try
            {
                var text = await _http.GetStringAsync(url, ct);
                var list = Parse(text);
                if (list.Count > 0)
                {
                    Log($"{list.Count} 个候选 ← {new Uri(url).Host}");
                    return list;
                }
            }
            catch (Exception ex)
            {
                Log($"源不可用（{new Uri(url).Host}）：{ex.Message}");
            }
        }
        return [];
    }

    /// <summary>解析列表文本：每行一个 tracker，跳过注释/空行，去重</summary>
    public static List<string> Parse(string text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            var s = line.Trim();
            if (s.Length == 0 || s.StartsWith('#') || s.StartsWith("//")) continue;
            if (!s.StartsWith("udp://", StringComparison.OrdinalIgnoreCase) &&
                !s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
            set.Add(s);
        }
        return set.ToList();
    }

    // ────────────────────── 可达性探测（BEP15）──────────────────────

    private static readonly byte[] ZeroInfoHash = new byte[20];

    private async Task<List<string>> FilterReachableAsync(List<string> candidates, CancellationToken ct)
    {
        // 只看 UDP（占列表绝大多数）；HTTP tracker 一律保留（TCP 路径另有价值，失败也只占 1 个槽位）
        var udp = candidates.Where(c => c.StartsWith("udp://", StringComparison.OrdinalIgnoreCase))
                            .Take(30).ToList();
        var http = candidates.Where(c => !c.StartsWith("udp://", StringComparison.OrdinalIgnoreCase)).Take(3);

        var ok = new List<string>();
        var results = new (string Url, bool Reachable)[udp.Count];
        using (var gate = new SemaphoreSlim(12))
        {
            var tasks = udp.Select(async (u, i) =>
            {
                await gate.WaitAsync(ct);
                try { results[i] = (u, await ProbeUdpAsync(u, ct)); }
                finally { gate.Release(); }
            }).ToArray();
            await Task.WhenAll(tasks);
        }
        foreach (var (url, reachable) in results)
            if (reachable) ok.Add(url);
        ok.AddRange(http);
        return ok;
    }

    /// <summary>BEP15 connect 探测：发 connect 请求看是否有 16B 响应</summary>
    private static async Task<bool> ProbeUdpAsync(string tracker, CancellationToken ct)
    {
        try
        {
            var (host, port) = SplitEndpoint(tracker);
            if (host == null) return false;
            var ips = await System.Net.Dns.GetHostAddressesAsync(host, ct);
            var ip = ips.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            if (ip == null) return false;

            using var udp = new System.Net.Sockets.UdpClient();
            udp.Connect(ip, port);
            var packet = new byte[16];
            WriteUInt64(packet, 0, 0x41727101980UL);
            WriteUInt32(packet, 8, 0);                      // action = connect
            WriteUInt32(packet, 12, (uint)Random.Shared.Next(1, int.MaxValue));
            await udp.SendAsync(packet, packet.Length);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var recv = await udp.ReceiveAsync(cts.Token);
            return recv.Buffer.Length >= 16;
        }
        catch { return false; }
    }

    private static (string? Host, int Port) SplitEndpoint(string url)
    {
        try
        {
            var body = url[(url.IndexOf("://", StringComparison.Ordinal) + 3)..];
            var host = body.Split('/')[0];
            var idx = host.LastIndexOf(':');
            if (idx <= 0) return (host, 80);
            return (host[..idx], int.Parse(host[(idx + 1)..]));
        }
        catch { return (null, 0); }
    }

    private static void WriteUInt64(byte[] b, int off, ulong v) { for (int i = 7; i >= 0; i--) { b[off + i] = (byte)(v & 0xFF); v >>= 8; } }
    private static void WriteUInt32(byte[] b, int off, uint v) { for (int i = 3; i >= 0; i--) { b[off + i] = (byte)(v & 0xFF); v >>= 8; } }

    // ────────────────────── 缓存 ──────────────────────

    private bool TryLoadCache(out string[] list, out DateTime updatedAt)
    {
        list = [];
        updatedAt = default;
        try
        {
            if (!File.Exists(_cachePath)) return false;
            var lines = File.ReadAllLines(_cachePath);
            if (lines.Length == 0 || !lines[0].StartsWith("# updated=")) return false;
            updatedAt = DateTimeOffset.FromUnixTimeSeconds(long.Parse(lines[0]["# updated=".Length..].Trim())).UtcDateTime;
            list = lines.Skip(1).Where(l => l.Trim().Length > 0).ToArray();
            return list.Length > 0;
        }
        catch { return false; }
    }

    private void SaveCache(string[] list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var lines = new[] { "# updated=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() }.Concat(list);
            File.WriteAllLines(_cachePath, lines);
        }
        catch { }
    }
}
