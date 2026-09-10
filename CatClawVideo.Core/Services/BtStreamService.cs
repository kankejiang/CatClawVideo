using System.Collections.Concurrent;
using System.Net;
using MonoTorrent;
using MonoTorrent.Client;

namespace CatClawVideo.Core.Services;

/// <summary>
/// BT 流式引擎封装（MonoTorrent，Android/Windows 同一套实现）：
/// 磁力 → metadata（公共 tracker + DHT 注入）→ 选视频文件（其余不下载）→ 可 seek 的流式 Stream。
/// 本地 HTTP 代理（<see cref="BtHttpProxy"/>）按 127.0.0.1 暴露给播放器，Range 拖动映射到 piece 优先级重排。
/// <para>策略（docs/magnet-streaming-cache-plan.md）：</para>
/// <para>· 同一 infoHash 复用 manager：剧集包多集复用已下 piece，切集不重新起播；</para>
/// <para>· MonoTorrent 单活跃 Stream 约束：切集/流被空闲回收后先 Dispose 再重建；</para>
/// <para>· 播放缓冲在磁盘（预取窗口落盘），内存 DiskCacheBytes 只做写合并；</para>
/// <para>· 空闲 3 分钟关流、10 分钟暂停 manager（缓存与 fast-resume 保留，二次打开秒续）。</para>
/// </summary>
public sealed class BtStreamService : IAsyncDisposable
{
    /// <summary>会话描述（代理与调用方衔接用）</summary>
    public sealed record BtSession(string InfoHashHex, int FileIndex, long FileLength, string FileName, string Url);

    private sealed class TorrentSession
    {
        public required TorrentManager Manager;
        public ITorrentManagerFile File = null!;
        public int FileIndex;
        public Stream? Stream;
        public readonly SemaphoreSlim ReadLock = new(1, 1);
        public DateTime LastUsedUtc = DateTime.UtcNow;
    }

    private readonly string _cacheRoot;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConcurrentDictionary<string, TorrentSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private ClientEngine? _engine;
    private BtHttpProxy? _proxy;
    private Timer? _idleTimer;

    // ─── 可调参数（两端注册时按平台覆盖）───
    /// <summary>内存写合并缓存（平滑乱序块落盘，非播放缓冲）</summary>
    public int MemoryCacheBytes { get; init; } = 48 * 1024 * 1024;
    /// <summary>单 torrent 连接数上限</summary>
    public int MaxConnections { get; init; } = 150;
    /// <summary>上传限速（B/s）。BT 是对等互惠协议：上传能力过低会被 peer 降权（choke），
    /// 下载速度随之受限；1MB/s 是下载场景的平衡值</summary>
    public int MaxUploadRate { get; init; } = 1024 * 1024;
    /// <summary>引擎级半开连接上限（默认 8 太低——建立连接慢、影响 peer 爬升速度）</summary>
    public int MaxHalfOpenConnections { get; init; } = 60;
    /// <summary>metadata 等待超时（节点探测 + DHT bootstrap 并行）</summary>
    public TimeSpan MetadataTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>公共 tracker 注入（xb6v 等站磁力自带 tracker 少；流式播放与下载管理器共用）。
    /// <para>⚠ 列表纪律（2026-09 实测教训）：同一 tier 内 announce 串行，任何一个 DNS 污染/不可达的
    /// tracker 其超时会拖垮整轮 peer 获取（实测 23 个未过滤 → 60s 零节点；精选可达 → 15s 拿到 metadata）。
    /// 现由 <see cref="BtTrackerSource"/> 动态供给（拉 ngosang 列表 + BEP15 探测过滤 + 每日更新），
    /// 此静态数组仅作为**联网拉取失败时的保底**。</para>
    /// </summary>
    public static readonly string[] PublicTrackers = BtTrackerSource.BuiltInFallback;

    /// <summary>tracker 列表供给（可空：空则只用保底列表）</summary>
    private readonly BtTrackerSource? _trackerSource;

    /// <summary>BT/下载设置（可空：空则用内置默认值）</summary>
    private readonly BtSettings? _settings;

    /// <summary>当前生效的 tracker 列表（动态列表未就绪时回退保底）</summary>
    public IReadOnlyList<string> TrackerList =>
        _trackerSource?.Current is { Count: > 0 } list ? list : PublicTrackers;

    /// <summary>预热 tracker 列表（App 启动后调用，避免首次磁力现场等待拉取+探测）。
    /// 关闭"每天自动更新"时只读本地缓存、不联网。</summary>
    public async Task WarmUpTrackersAsync(CancellationToken ct = default)
    {
        if (_trackerSource == null) return;
        await _trackerSource.GetAsync(forceRefresh: false, allowFetch: _settings?.AutoUpdateTrackers ?? true, ct);
    }

    /// <summary>强制更新 tracker 列表（设置页"立即更新"按钮）</summary>
    public Task<string[]> RefreshTrackersAsync(CancellationToken ct = default) =>
        _trackerSource?.GetAsync(forceRefresh: true, allowFetch: true, ct) ?? Task.FromResult(PublicTrackers);

    /// <summary>按当前设置重建引擎（设置页保存后调用；进行中的 BT 任务会被停止，缓存与 fast-resume 保留）</summary>
    public async Task RecreateEngineAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_engine == null) return;
            Log("按新设置重建 BT 引擎…");
            foreach (var (_, s) in _sessions)
            {
                try { await s.Manager.StopAsync(TimeSpan.FromSeconds(2)); } catch { }
            }
            _sessions.Clear();
            try { _engine.Dispose(); } catch { }
            _engine = null;
            Log("引擎已重建，下次磁力操作生效");
        }
        finally { _lock.Release(); }
    }

    /// <summary>URI 字符串层追加公共 tracker（已存在则跳过；MagnetLink.AnnounceUrls 只读）</summary>
    public static string InjectPublicTrackers(string magnetUri)
    {
        var sb = new System.Text.StringBuilder(magnetUri);
        foreach (var tracker in PublicTrackers)
        {
            if (magnetUri.Contains(tracker, StringComparison.OrdinalIgnoreCase)) continue;
            sb.Append("&tr=").Append(Uri.EscapeDataString(tracker));
        }
        return sb.ToString();
    }

    /// <summary>URI 字符串层追加当前 tracker 列表（已存在则跳过；MagnetLink.AnnounceUrls 只读）</summary>
    public string InjectTrackers(string magnetUri) => InjectTrackersImpl(magnetUri, TrackerList);

    /// <summary>URI 字符串层追加指定列表（静态工具：下载服务等在拿到动态列表后调用）</summary>
    public static string InjectTrackersImpl(string magnetUri, IReadOnlyList<string> trackers)
    {
        var sb = new System.Text.StringBuilder(magnetUri);
        foreach (var tracker in trackers)
        {
            if (magnetUri.Contains(tracker, StringComparison.OrdinalIgnoreCase)) continue;
            if (magnetUri.Contains(Uri.EscapeDataString(tracker), StringComparison.OrdinalIgnoreCase)) continue;
            sb.Append("&tr=").Append(Uri.EscapeDataString(tracker));
        }
        return sb.ToString();
    }

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".ts", ".mov", ".wmv", ".flv", ".m2ts", ".webm",
        ".mpg", ".mpeg", ".m4v", ".vob", ".rmvb", ".rm",
    };

    public BtStreamService(string cacheRoot, Action<string>? log = null,
        BtTrackerSource? trackerSource = null, BtSettings? settings = null)
    {
        _cacheRoot = cacheRoot;
        _log = log;
        _trackerSource = trackerSource;
        _settings = settings;
    }

    private void Log(string m) => _log?.Invoke("[bt] " + m);

    /// <summary>本地流地址前缀（如 http://127.0.0.1:45117）</summary>
    public string ProxyPrefix => _proxy?.Prefix ?? "";

    /// <summary>
    /// 获取（懒启动的）共享 BT 引擎：下载管理器复用同一引擎，
    /// 共享 DHT 路由表与连接基础设施——播放会话焐热的 DHT 表下载任务直接受益。
    /// </summary>
    public async Task<ClientEngine> GetEngineAsync()
    {
        await _lock.WaitAsync();
        try
        {
            await EnsureEngineAndProxyAsync();
            return _engine!;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 打开磁力：解析 → metadata → 选文件 → 建流式 Stream。
    /// 同 infoHash + 同文件且流存活时直接复用（播放器续连/多连接不重建）。
    /// </summary>
    public async Task<BtSession> OpenAsync(string magnetUri, string? preferredName, CancellationToken ct)
    {
        var magnet = MagnetLink.Parse(InjectTrackers(magnetUri));
        var infoHex = ResolveInfoHashHex(magnet)
            ?? throw new NotSupportedException("磁力链接缺少 info-hash（仅支持 btih v1 磁力）");

        await _lock.WaitAsync(ct);
        try
        {
            await EnsureEngineAndProxyAsync();
            var manager = await EnsureManagerAsync(magnet, infoHex, ct);
            var file = SelectFile(manager, preferredName);
            var fileIndex = manager.Files.IndexOf(file);

            if (!_sessions.TryGetValue(infoHex, out var s))
            {
                s = new TorrentSession { Manager = manager };
                _sessions[infoHex] = s;
            }

            // 同文件且流存活 → 复用（不重建，避免打断下载窗口）
            if (s.Stream != null && s.FileIndex == fileIndex)
            {
                s.LastUsedUtc = DateTime.UtcNow;
                return new BtSession(infoHex, s.FileIndex, s.File.Length, s.File.FullPath, SessionUrl(infoHex));
            }

            await CloseStreamAsync(s); // 单活跃流约束：先关旧流（换集/重建）
            await ApplyPrioritiesAsync(manager, file);
            s.File = file;
            s.FileIndex = fileIndex;
            s.Stream = await manager.StreamProvider.CreateStreamAsync(file, prebuffer: true, ct);
            s.LastUsedUtc = DateTime.UtcNow;
            Log($"会话 {infoHex[..12]}… → 第 {fileIndex} 个文件 {file.Length / 1048576.0:F1}MB");
            return new BtSession(infoHex, s.FileIndex, file.Length, file.FullPath, SessionUrl(infoHex));
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>定位会话（代理请求头用：长度/文件名）</summary>
    public BtSession? FindSession(string infoHex) =>
        _sessions.TryGetValue(infoHex, out var s) && s.FileIndex >= 0
            ? new BtSession(infoHex, s.FileIndex, s.File.Length, s.File.FullPath, SessionUrl(infoHex))
            : null;

    /// <summary>BT 实时下载速率（B/s；会话不存在/非 BT 返回 0）</summary>
    public long GetDownloadSpeed(string infoHex) =>
        _sessions.TryGetValue(infoHex, out var s)
            ? (long?)s.Manager.Monitor.DownloadSpeed ?? 0
            : 0;

    /// <summary>从本地流地址提取 infoHash（非 BT 地址返回 null）</summary>
    public static string? ExtractInfoHash(string? localUrl)
    {
        if (string.IsNullOrEmpty(localUrl)) return null;
        const string marker = "/stream/";
        var idx = localUrl.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        var hash = localUrl[(idx + marker.Length)..];
        var end = hash.IndexOf('/');
        return end > 0 ? hash[..end] : hash;
    }

    /// <summary>
    /// 代理读取：把 (offset, count) 映射到流式 Stream（内部阻塞至 piece 到货）。
    /// 流被空闲回收时按需重建；会话级锁串行化多 HTTP 连接（单活跃流）。
    /// </summary>
    public async Task<int> ReadAsync(string infoHex, long offset, byte[] buffer, int count, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(infoHex, out var s))
            throw new InvalidOperationException("BT 会话不存在");
        s.LastUsedUtc = DateTime.UtcNow;

        await s.ReadLock.WaitAsync(ct);
        try
        {
            if (s.Stream == null)
            {
                Log("流已被空闲回收，按需重建…");
                s.Stream = await s.Manager.StreamProvider.CreateStreamAsync(s.File, prebuffer: true, ct);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            long pos = offset;
            int total = 0;
            while (total < count)
            {
                s.Stream.Seek(pos, SeekOrigin.Begin);
                var n = await s.Stream.ReadAsync(buffer.AsMemory(total, count - total), timeout.Token);
                if (n <= 0) break; // EOF
                total += n;
                pos += n;
            }
            return total;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException("BT 读取超时（120s）：节点速度不足或资源已无做种");
        }
        finally
        {
            s.ReadLock.Release();
        }
    }

    // ────────────────────────── 内部 ──────────────────────────

    /// <summary>懒启动引擎 + 代理（首次磁力播放才初始化，不拖慢启动首帧）</summary>
    private async Task EnsureEngineAndProxyAsync()
    {
        if (_engine != null) return;

        var btPort = _settings?.BtListenPort ?? 0;      // 0 = 随机端口
        var dhtPort = _settings?.DhtListenPort ?? 0;
        var upnp = _settings?.UpnpNatPmp ?? false;
        var maxConn = _settings?.MaxConnPerServer ?? MaxConnections;
        var uploadLimit = _settings?.UploadLimitBytesPerSec ?? MaxUploadRate;   // 0 = 不限
        var downloadLimit = _settings?.DownloadLimitBytesPerSec ?? 0;           // 0 = 不限
        var saveMetadata = _settings?.SaveMagnetMetadata ?? true;

        var settings = new EngineSettingsBuilder
        {
            CacheDirectory = Path.Combine(_cacheRoot, "engine"), // fast-resume + DHT 缓存
            DiskCacheBytes = MemoryCacheBytes,
            AutoSaveLoadFastResume = true,
            AutoSaveLoadDhtCache = true,
            AutoSaveLoadMagnetLinkMetadata = saveMetadata,
            UsePartialFiles = true,
            AllowPortForwarding = upnp,
            AllowLocalPeerDiscovery = true,
            MaximumConnections = Math.Max(maxConn, 60),
            MaximumHalfOpenConnections = MaxHalfOpenConnections,
            MaximumUploadRate = uploadLimit,
            MaximumDownloadRate = downloadLimit,
            DhtEndPoint = new IPEndPoint(IPAddress.Any, dhtPort),   // null 会禁用 DHT
            ListenEndPoints = new Dictionary<string, IPEndPoint>
            {
                ["ipv4"] = new(IPAddress.Any, btPort),
            },
        }.ToSettings();
        _engine = new ClientEngine(settings);
        _proxy = new BtHttpProxy(this, Log);
        _proxy.Start();
        _idleTimer = new Timer(async _ => await ShutdownIdleSafeAsync(), null,
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        Log($"引擎就绪（缓存 {_cacheRoot}，代理端口 {_proxy.Port}）");
        await Task.CompletedTask;
    }

    /// <summary>获取/复用 manager：新会话注入公共 tracker、启动、等 metadata</summary>
    private async Task<TorrentManager> EnsureManagerAsync(MagnetLink magnet, string infoHex, CancellationToken ct)
    {
        if (_sessions.TryGetValue(infoHex, out var existing))
        {
            existing.LastUsedUtc = DateTime.UtcNow;
            if (existing.Manager.State is TorrentState.Stopped or TorrentState.Paused)
                await existing.Manager.StartAsync();
            if (existing.Manager.HasMetadata) return existing.Manager;
        }
        else
        {
            // 公共 tracker 已在 URI 字符串层注入（MagnetLink.AnnounceUrls 只读，无法运行时追加）

            var savePath = Path.Combine(_cacheRoot, infoHex);
            Directory.CreateDirectory(savePath);
            var settings = new TorrentSettingsBuilder
            {
                MaximumConnections = Math.Min(_settings?.MaxConnPerServer ?? MaxConnections, 200),
                UploadSlots = 6,
                AllowDht = true,
                AllowPeerExchange = true,
            }.ToSettings();
            var manager = await _engine!.AddStreamingAsync(magnet, savePath, settings);
            await manager.StartAsync();
            try { await manager.DhtAnnounceAsync(); } catch { }
            _sessions[infoHex] = new TorrentSession { Manager = manager };
            Log($"新会话 {infoHex[..12]}…（{Path.GetFileName(savePath)}）");
        }

        // 等 metadata（DHT bootstrap 与其并行，已由引擎内部处理）
        var current = _sessions[infoHex].Manager;
        if (!current.HasMetadata)
        {
            Log("等待 metadata…");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(MetadataTimeout);
            try
            {
                await current.WaitForMetadataAsync(cts.Token);
                Log($"metadata 就绪：{current.Files.Count} 个文件");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new NotSupportedException("获取资源信息超时：该磁力当前无有效节点/做种，请换线路或稍后再试");
            }
        }
        return current;
    }

    /// <summary>选目标文件：视频扩展名优先 + 集名匹配 + 最大体积兜底</summary>
    private static ITorrentManagerFile SelectFile(TorrentManager manager, string? preferredName)
    {
        var videos = manager.Files
            .Where(f => VideoExtensions.Contains(Path.GetExtension(f.FullPath)))
            .OrderByDescending(f => f.Length)
            .ToList();
        var pool = videos.Count > 0 ? videos : manager.Files.OrderByDescending(f => f.Length).ToList();
        if (pool.Count == 0)
            throw new InvalidOperationException("种子内没有可用文件");

        if (!string.IsNullOrWhiteSpace(preferredName))
        {
            var hit = pool.FirstOrDefault(f => f.FullPath.Contains(preferredName, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return pool[0]; // 最大视频文件（合集包里正片通常是最大的）
    }

    /// <summary>目标文件高优先，其余一律不下载（最重要的缓存节约策略）</summary>
    private async Task ApplyPrioritiesAsync(TorrentManager manager, ITorrentManagerFile target)
    {
        foreach (var f in manager.Files)
        {
            var p = ReferenceEquals(f, target) ? Priority.High : Priority.DoNotDownload;
            if (f.Priority != p)
            {
                try { await manager.SetFilePriorityAsync(f, p); }
                catch (Exception ex) { Log($"设置文件优先级失败: {ex.Message}"); }
            }
        }
    }

    /// <summary>关闭活跃流（保留缓存与 fast-resume）</summary>
    private async Task CloseStreamAsync(TorrentSession s)
    {
        if (s.Stream == null) return;
        try { await s.Stream.DisposeAsync(); }
        catch { }
        s.Stream = null;
    }

    /// <summary>空闲回收：3 分钟关流、10 分钟暂停 manager（缓存保留）</summary>
    private async Task ShutdownIdleSafeAsync()
    {
        try
        {
            await _lock.WaitAsync();
            try
            {
                foreach (var (key, s) in _sessions)
                {
                    var idle = DateTime.UtcNow - s.LastUsedUtc;
                    if (idle > TimeSpan.FromMinutes(3) && s.Stream != null)
                    {
                        Log($"会话 {key[..12]}… 空闲 3 分钟，关闭流");
                        await CloseStreamAsync(s);
                    }
                    if (idle > TimeSpan.FromMinutes(10) &&
                        s.Manager.State is TorrentState.Downloading or TorrentState.Metadata)
                    {
                        Log($"会话 {key[..12]}… 空闲 10 分钟，暂停 manager");
                        try { await s.Manager.PauseAsync(); } catch { }
                    }
                }
            }
            finally { _lock.Release(); }
        }
        catch { }
    }

    /// <summary>
    /// 下载管理器要整包下载同 infoHash 磁力时调用：共享引擎里同一磁力只能注册一个 manager
    /// （"A manager for this torrent has already been registered"），
    /// 若流式会话正占用该磁力则先关闭并从引擎移除，让整包下载接管。
    /// 播放器正在读的流会随之结束（用户选择下载即放弃流式）。
    /// </summary>
    public async Task CloseStreamingSessionAsync(string infoHashHex)
    {
        TorrentSession? session;
        await _lock.WaitAsync();
        try
        {
            if (!_sessions.TryRemove(infoHashHex, out session)) return;
        }
        finally
        {
            _lock.Release();
        }

        Log($"流式会话被整包下载接管 {infoHashHex[..12]}…，关闭流式会话");
        try { await session.Manager.StopAsync(TimeSpan.FromSeconds(3)); } catch { }
        try { if (_engine != null) await _engine.RemoveAsync(session.Manager); } catch { }
    }

    /// <summary>MagnetLink → btih hex（下载服务复用）</summary>
    public static string? ResolveInfoHashHex(MagnetLink magnet)
    {
        try { return magnet.InfoHashes.V1?.ToHex(); }
        catch { return null; }
    }

    private string SessionUrl(string infoHex) => $"{_proxy!.Prefix}/stream/{infoHex}";

    public ValueTask DisposeAsync()
    {
        _idleTimer?.Dispose();
        _engine?.Dispose();
        return ValueTask.CompletedTask;
    }
}
