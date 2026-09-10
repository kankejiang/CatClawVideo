using MonoTorrent;
using MonoTorrent.Client;

namespace CatClawVideo.Maui.Services;

/// <summary>
/// 磁力下载服务：基于 MonoTorrent 3.x 引擎的 BT 整包下载（支持 magnet: 链接、DHT、做种者发现）。
/// 任务与 HTTP 下载共用 DownloadManager 的任务模型（DownloadTaskItem, Kind="magnet"），
/// 由 DownloadManager 委托本服务执行与回调进度。
/// <para>
/// 引擎复用（速度关键）：不自建独立引擎，而是借用 <see cref="Core.Services.BtStreamService"/>
/// 的 ClientEngine——共享 DHT 路由表：流式播放会话焐热的 DHT 表，下载任务启动即受益，
/// 避免第二个引擎 DHT 从零 bootstrap 导致的"半天只有两个节点"。
/// </para>
/// </summary>
public class BitTorrentDownloadService : IDisposable
{
    private readonly Core.Services.BtStreamService _bt;
    private readonly Action<string>? _log;
    private ClientEngine? _engine;
    private readonly Dictionary<string, TorrentManager> _managers = new();

    /// <summary>btih hex → 任务 ID（共享引擎同磁力唯一注册的防重地图）</summary>
    private readonly Dictionary<string, string> _infoHashToTask = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private bool _disposed;

    public BitTorrentDownloadService(Core.Services.BtStreamService bt, Action<string>? log = null)
    {
        _bt = bt;
        _log = log;
    }

    private void Log(string m) => _log?.Invoke("[dl] " + m);

    /// <summary>任务 ID → TorrentManager</summary>
    private TorrentManager? GetManager(string taskId)
    {
        lock (_lock) return _managers.TryGetValue(taskId, out var m) ? m : null;
    }

    /// <summary>开始磁力下载：解析 magnet（注入公共 tracker）→ 获取元数据 → 下载到 saveDir。
    /// 回调：onState（状态文本）、onProgress（0~100）、onStats（周期统计快照）、onComplete。
    /// 返回 null=成功启动；否则返回错误信息。</summary>
    public async Task<string?> StartAsync(string taskId, string magnet, string saveDir,
        Action<string> onState, Action<double> onProgress, Action<Core.Services.BtTaskStats> onStats, Action onComplete)
    {
        try
        {
            // tracker 注入（xb6v 等站磁力自带 tracker 少，纯 DHT 冷启动基本无速度）
            // 用 BtStreamService 的**动态列表**（ngosang 拉取 + 可达性过滤 + 每日更新），而非静态硬编码
            magnet = _bt.InjectTrackers(magnet.Trim());

            if (!magnet.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                return "不是有效的磁力链接（需以 magnet: 开头）";

            // 必须用 MagnetLink.Parse + AddAsync(MagnetLink)：AddAsync(string) 会把
            // magnet 字符串当 torrent 文件路径尝试读取，抛 IOException
            if (!MagnetLink.TryParse(magnet, out var link))
            {
                Log("磁力解析失败（MagnetLink.TryParse 返回 false）");
                return "磁力链接解析失败（btih 信息不完整）";
            }

            var infoHex = Core.Services.BtStreamService.ResolveInfoHashHex(link);
            if (string.IsNullOrEmpty(infoHex))
                return "无法识别磁力的 btih 标识";

            Log($"下载启动 {infoHex[..12]}… 任务 {taskId} saveDir={saveDir}");

            // 共享引擎里同一磁力只能注册一个 manager：
            // 1) 流式播放会话占用中 → 先关闭并移除（整包下载接管）
            // 2) 其他下载任务占用中 → 拒绝重复入队
            await _bt.CloseStreamingSessionAsync(infoHex);
            lock (_lock)
            {
                if (_infoHashToTask.TryGetValue(infoHex, out var existing) &&
                    existing != taskId && _managers.ContainsKey(existing))
                {
                    Log($"拒绝重复入队：任务 {existing} 已占用该磁力");
                    return "同一磁力已有下载任务（请先在列表中删除旧任务）";
                }
            }

            _engine = await _bt.GetEngineAsync();
            lock (_lock) Log($"引擎就绪（本服务已托管 {_managers.Count} 个任务）");

            // ⚠⚠ 必须显式传 TorrentSettings，不能用 AddAsync(magnet, saveDir) 的**默认**值。
            // 实测（2026-09-10，MonoTorrent 3.0.1）：默认 TorrentSettings 下 manager 完全下不动 ——
            // 连接数恒为 1~4、候选 peer 从 104 一路衰减到 0、已下始终 0 字节，但 state 仍显示
            // Downloading（极具迷惑性，容易误判成"网络问题"）；同一时刻换成这里的 settings 后
            // 立刻跑满带宽（13~17 连接 / 峰值 15.7MB/s）。流式路径一直显式传 settings，
            // 所以只有整包下载会中招。
            var torrentSettings = new TorrentSettingsBuilder
            {
                MaximumConnections = Math.Min(_bt.MaxConnections, 200),
                UploadSlots = 6,
                AllowDht = true,
                AllowPeerExchange = true,
            }.ToSettings();

            TorrentManager manager;
            try
            {
                manager = await _engine.AddAsync(link, saveDir, torrentSettings);
            }
            catch (Exception ex) when (ex.Message.Contains("already been registered", StringComparison.OrdinalIgnoreCase))
            {
                // 共享引擎里同一磁力只能注册一个 manager。冲突来源有两条：
                //   a) 播放器的流式会话 —— CloseStreamingSessionAsync 能清掉；
                //   b) 本服务此前留下的下载 manager —— 例如用户在列表里删了任务，
                //      RemoveAsync 内部 engine.RemoveAsync 静默失败时 manager 仍挂在引擎上，
                //      而 _managers 已经清空、检测不出占用，于是 AddAsync 必然失败且永远无法恢复
                //      （实测日志：连续两次「AddAsync 注册冲突」→「already been registered」失败）。
                // 所以这里做「按 infoHash 彻底清扫」，而不是只关流式会话。
                Log("AddAsync 注册冲突，按 infoHash 清扫引擎中的残留 manager 后重试");
                await ForceRemoveStaleManagersAsync(infoHex);
                await Task.Delay(500);
                manager = await _engine.AddAsync(link, saveDir, torrentSettings);
            }
            catch (Exception ex)
            {
                Log($"AddAsync 失败：{ex}");
                throw;
            }
            lock (_lock)
            {
                _managers[taskId] = manager;
                _infoHashToTask[infoHex] = taskId;
            }

            // AddAsync 后状态为 Stopped，需显式 StartAsync 才开始 DHT/tracker 查找做种者
            await manager.StartAsync();
            try { await manager.DhtAnnounceAsync(); } catch { }
            Log("manager 已启动，等待 metadata/节点");

            // 诊断：确认 tracker 是否真的进了这个 manager（排障"候选 peer 恒为 0"用）
            try
            {
                var tiers = manager.TrackerManager.Tiers.ToList();
                int trTotal = 0;
                foreach (var tier in tiers)
                    if (tier.GetType().GetProperty("Trackers")?.GetValue(tier) is System.Collections.IEnumerable list)
                        foreach (var _ in list) trTotal++;
                Log($"[诊断] 注入后 magnet 长度={magnet.Length}；manager 里 tracker tiers={tiers.Count} / tracker={trTotal}；" +
                    $"AllowDht={manager.Settings.AllowDht} MaxConn={manager.Settings.MaximumConnections}");
            }
            catch (Exception ex) { Log($"[诊断] 读取 tracker 失败：{ex.Message}"); }

            manager.TorrentStateChanged += (_, e) =>
            {
                onState(StateText(e.NewState));
                if (e.NewState == TorrentState.Seeding || e.NewState == TorrentState.Stopped)
                    onComplete();
            };

            // 周期进度上报（每 500ms）：进度用 manager.Progress（0~100），字节数/速度/连接数取 Monitor
            _ = Task.Run(async () =>
            {
                var tick = 0;
                var metaLogged = false;
                while (!_disposed)
                {
                    var m = GetManager(taskId);
                    if (m == null) return;
                    if (m.State is TorrentState.Seeding or TorrentState.Stopped or TorrentState.Error)
                    {
                        Log($"任务 {taskId} 结束：state={m.State}");
                        if (m.State == TorrentState.Seeding) onComplete();
                        return;
                    }
                    if (!metaLogged && m.HasMetadata)
                    {
                        metaLogged = true;
                        Log($"metadata 就绪：{m.Files?.Count ?? 0} 个文件，总量 {m.Torrent?.Size ?? 0} 字节");
                    }
                    onProgress(m.Progress);
                    var downloaded = m.Monitor.DataBytesDownloaded;
                    var total = m.HasMetadata ? m.Torrent!.Size : 0L;
                    onStats(new Core.Services.BtTaskStats(
                        downloaded, total,
                        m.Monitor.DownloadRate, m.Monitor.UploadRate, m.Monitor.DataBytesUploaded,
                        m.OpenConnections, m.Peers.Seeds, m.Peers.Leechs));
                    // 每 5 秒落一行现场证据（速度/节点/状态），排障"下载不动"
                    // 注意：DownloadSpeed 已过时且 :B0 是**二进制**格式（会把 927B/s 打成 1110011111B/s，
                    // 排障时极易误判），统一用 DownloadRate + 默认十进制。
                    if (++tick % 10 == 0)
                    {
                        Log($"任务 {taskId} state={m.State} 已下={downloaded} 总量={total} 速度={m.Monitor.DownloadRate}B/s 实连={m.OpenConnections} 候选={m.Peers.Available}");
                        Log(TrackerDiagnostics(m));
                    }
                    await Task.Delay(500);
                }
            });
            return null;
        }
        catch (Exception ex)
        {
            Log($"启动磁力下载失败：{ex}");
            return ex.Message;
        }
    }

    /// <summary>
    /// 任务运行快照（详情页/卡片统计）。manager 不存在或未拿到 metadata 时仍返回基础信息。
    /// </summary>
    public Core.Services.BtTorrentStats? GetStats(string taskId)
    {
        var m = GetManager(taskId);
        if (m == null) return null;
        try
        {
            var bitfield = m.Bitfield;
            var pieces = new bool[bitfield.Length];
            for (int i = 0; i < pieces.Length; i++)
                pieces[i] = bitfield[i];

            // 文件列表（进度按文件覆盖的分片区间在 bitfield 上的命中率计算）
            var files = new List<Core.Services.BtFileInfo>();
            if (m.HasMetadata)
            {
                for (int i = 0; i < m.Files.Count; i++)
                {
                    var f = m.Files[i];
                    int done = 0;
                    var end = Math.Min(f.EndPieceIndex, bitfield.Length - 1);
                    for (int p = f.StartPieceIndex; p <= end; p++)
                        if (pieces[p]) done++;
                    double pct = f.PieceCount > 0 ? done * 100.0 / f.PieceCount : 0;
                    files.Add(new Core.Services.BtFileInfo(
                        i,
                        Path.GetFileName(f.FullPath),
                        Path.GetExtension(f.FullPath),
                        f.FullPath,
                        f.Length,
                        (long)(f.Length * pct / 100.0),
                        pct,
                        f.Priority != MonoTorrent.Priority.DoNotDownload));
                }
            }

            // Tracker 列表（ITrackerManager 无 Trackers 属性 → 遍历 Tiers → TrackerTier.Trackers）
            var trackers = new List<Core.Services.BtTrackerInfo>();
            try
            {
                foreach (var tier in m.TrackerManager.Tiers)
                {
                    if (tier.GetType().GetProperty("Trackers")?.GetValue(tier) is not System.Collections.IEnumerable list)
                        continue;
                    foreach (var tr in list)
                    {
                        var t = tr.GetType();
                        trackers.Add(new Core.Services.BtTrackerInfo(
                            t.GetProperty("Uri")?.GetValue(tr)?.ToString() ?? "",
                            t.GetProperty("Status")?.GetValue(tr)?.ToString() ?? "",
                            t.GetProperty("FailureMessage")?.GetValue(tr)?.ToString() ?? ""));
                    }
                }
            }
            catch { }

            return new Core.Services.BtTorrentStats(
                m.InfoHashes.V1?.ToHex() ?? "",
                StateText(m.State),
                m.HasMetadata,
                m.SavePath,
                m.Name,
                m.Progress,
                m.HasMetadata ? m.Torrent!.Size : 0L,
                m.Monitor.DataBytesDownloaded,
                m.Monitor.DataBytesUploaded,
                m.Monitor.DownloadRate,
                m.Monitor.UploadRate,
                m.Peers.Seeds,
                m.Peers.Leechs,
                m.OpenConnections,
                m.HasMetadata ? m.Torrent!.PieceLength : 0L,
                pieces,
                trackers,
                files);
        }
        catch { return null; }
    }

    /// <summary>勾选/取消勾选文件（切换下载优先级：Normal ↔ DoNotDownload）</summary>
    public async Task<bool> SetFileSelectionAsync(string taskId, int fileIndex, bool selected)
    {
        var m = GetManager(taskId);
        if (m == null || !m.HasMetadata || fileIndex < 0 || fileIndex >= m.Files.Count) return false;
        try
        {
            await m.SetFilePriorityAsync(m.Files[fileIndex],
                selected ? MonoTorrent.Priority.Normal : MonoTorrent.Priority.DoNotDownload);
            return true;
        }
        catch (Exception ex)
        {
            Log($"切换文件优先级失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>暂停任务</summary>
    public async Task PauseAsync(string taskId)
    {
        var m = GetManager(taskId);
        if (m != null) await m.PauseAsync();
    }

    /// <summary>继续任务</summary>
    public async Task ResumeAsync(string taskId)
    {
        var m = GetManager(taskId);
        if (m != null) await m.StartAsync();
    }

    /// <summary>移除任务（停止下载并清理会话，已下载文件保留）</summary>
    public async Task RemoveAsync(string taskId)
    {
        var m = GetManager(taskId);
        if (m == null || _engine == null) return;
        lock (_lock)
        {
            _managers.Remove(taskId);
            var kv = _infoHashToTask.FirstOrDefault(p => p.Value == taskId);
            if (!string.IsNullOrEmpty(kv.Key)) _infoHashToTask.Remove(kv.Key);
        }
        try { await _engine.RemoveAsync(m); }
        catch (Exception ex) { Log($"从引擎移除 manager 失败（残留会导致同磁力无法重新入队）：{ex.Message}"); }
    }

    /// <summary>
    /// 按 infoHash 彻底清除引擎中该磁力的 manager（流式会话 + 残留下载会话），让 AddAsync 能重新注册。
    /// 共享引擎同一磁力只允许一个 manager，残留会让重试永久失败
    /// （"A manager for this torrent has already been registered"）。
    /// </summary>
    private async Task ForceRemoveStaleManagersAsync(string infoHex)
    {
        // 1) 流式会话（播放器正占用）
        try { await _bt.CloseStreamingSessionAsync(infoHex); } catch { }

        // 2) 引擎里仍在注册的同磁力 manager（含此前没清干净的下载任务）
        if (_engine == null) return;
        foreach (var m in _engine.Torrents.ToList())
        {
            string? hex = null;
            try { hex = m.InfoHashes.V1?.ToHex(); } catch { }
            if (!string.Equals(hex, infoHex, StringComparison.OrdinalIgnoreCase)) continue;

            Log($"清扫残留 manager {hex![..12]}…（state={m.State}）");
            try { await m.StopAsync(TimeSpan.FromSeconds(2)); } catch { }
            try { await _engine.RemoveAsync(m); }
            catch (Exception ex) { Log($"移除残留 manager 失败：{ex.Message}"); }
        }

        // 3) 同步本地映射，避免与实际引擎状态脱节
        lock (_lock)
        {
            if (_infoHashToTask.TryGetValue(infoHex, out var staleTask))
            {
                _managers.Remove(staleTask);
                _infoHashToTask.Remove(infoHex);
            }
        }
    }

    /// <summary>
    /// 诊断：打印 tracker 分层与每个 tracker 的 announce 状态。
    /// 排障「候选 peer 恒为 0」用 —— 多个 tracker 挤在同一 tier 时 announce 是**串行**的，
    /// 前面的超时会拖垮整轮（docs 第 6 节的教训）；Status / FailureMessage 直接给出失败原因。
    /// </summary>
    private static string TrackerDiagnostics(TorrentManager m)
    {
        try
        {
            var tiers = m.TrackerManager.Tiers;
            var sb = new System.Text.StringBuilder($"[诊断] tracker tiers={tiers.Count}");
            int idx = 0;
            foreach (var tier in tiers)
            {
                if (tier.GetType().GetProperty("Trackers")?.GetValue(tier) is not System.Collections.IEnumerable list)
                    continue;
                foreach (var tr in list)
                {
                    if (idx++ >= 6) { sb.Append(" …"); break; }
                    var t = tr.GetType();
                    var uri = t.GetProperty("Uri")?.GetValue(tr)?.ToString() ?? "?";
                    var st = t.GetProperty("Status")?.GetValue(tr)?.ToString() ?? "?";
                    var fm = t.GetProperty("FailureMessage")?.GetValue(tr)?.ToString();
                    sb.Append($" | {uri} [{st}{(string.IsNullOrEmpty(fm) ? "" : ": " + fm)}]");
                }
                if (idx > 6) break;
            }
            return sb.ToString();
        }
        catch (Exception ex) { return "[诊断] tracker 读取失败：" + ex.Message; }
    }

    /// <summary>状态文本映射</summary>
    private static string StateText(TorrentState state) => state switch
    {
        TorrentState.Metadata => "获取种子信息...",
        TorrentState.Hashing => "校验数据...",
        TorrentState.Downloading => "下载中",
        TorrentState.Seeding => "已完成",
        TorrentState.Paused => "已暂停",
        TorrentState.Stopped => "已停止",
        TorrentState.Error => "出错",
        _ => state.ToString()
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // 注意：引擎归 BtStreamService 所有（共享），这里绝不 Dispose 引擎本体
    }
}
