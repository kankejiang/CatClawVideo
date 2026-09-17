using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using CatClawVideo.Core.Interfaces;

namespace CatClawVideo.Core.Services.QemuThunder;

/// <summary>
/// Windows 磁力优先引擎：在 QEMU 里跑 ARM64 Android 迅雷下载引擎（P2SP 私有网络）。
///
/// <para><b>为什么</b>：新6V 这类站的磁力公共 BT swarm 极薄甚至已死，而迅雷 P2SP 能秒出文件列表并满速。
/// 迅雷只有 ARM64 Android 的 .so，因此 Windows 上靠 QEMU 承载（见 <c>JavaBridge/qemu-src/README.md</c>）。</para>
///
/// <para><b>编排</b>（= 实验装置 <c>build/ctrlserver2.py</c> 的 C# 移植，同一协议同一状态机）：
/// <c>TASK MAGNET</c>（把磁力解析成 .torrent）→ 宿主经媒体口拉回 .torrent 展开文件列表 →
/// <c>DL</c> 选片建 BT 任务 → guest 给播放地址 → 宿主拉前若干字节验证 → 返回可播 URL。</para>
///
/// <para><b>回落契约</b>：任何一步失败返回 null（调用方回落内置 BT），绝不因本引擎坏掉让磁力整体不可用。
/// VM 懒启动、常驻复用、空闲自停（见 <c>IdleCheck</c>）。</para>
///
/// <para>⚠ 单 VM 单会话：新任务会把 guest 代理重新武装到新引擎端口，正在播放的旧流会断——与 Android 迅雷一致。</para>
/// </summary>
public sealed class QemuThunderEngine : IPreferredMagnetEngine, IDisposable
{
    /// <summary>控制口：烧死在 initrd 里（guest 每秒回连宿主 10.0.2.2:18080），改不了。</summary>
    /// <summary>默认控制口（多实例时各用各的，见构造参数）</summary>
    public const int CtrlPort = 18080;

    /// <summary>媒体口首选值（实际会在被占时向后探测空闲端口）。</summary>
    public const int PreferredMediaPort = 20092;

    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".ts", ".m2ts", ".wmv", ".flv", ".mov",
        ".rmvb", ".rm", ".webm", ".m4v", ".mpg", ".mpeg", ".3gp", ".vob", ".iso",
    };

    private static readonly Regex BtihHex = new(@"xt=urn:btih:([0-9a-fA-F]{40})", RegexOptions.Compiled);

    private readonly string _runtimeDir;
    private readonly Action<string>? _log;
    /// <summary>本实例的控制口（多实例场景：下载引擎用 18081，配套 pkg_initrd_dl.gz）</summary>
    private readonly int _ctrlPort;
    /// <summary>本实例的 initrd 文件名与控制台日志标签（多实例隔离）</summary>
    private readonly string _initrdName, _consoleTag;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly Timer _idleTimer;
    private readonly Timer _watchdog;   // 播放中巡检上游断粮（guest 对「速度=0 但任务未死」不上报，宿主必须自己盯）

    private QemuHostRuntime? _runtime;
    private QemuControlServer? _server;
    private int _mediaPort;
    private Session? _session;
    private QemuStreamProxy? _streamProxy;
    private DateTime _lastActiveUtc = DateTime.UtcNow;
    private long _watchLastUpstream = -1;   // 看门狗上次采样的上游字节数
    private int _watchFrozenTicks;          // 连续冻结采样次数（×15s）
    private bool _watchKicked;              // 本轮断粮已 KICK 过（数据推进时复位）
    private bool _disposed;

    /// <summary>磁力 → 已展开会话 的进程级历史。同一磁力重复 TASK MAGNET 会被 guest 引擎以
    /// 9128（任务已存在）拒绝，导致 TryOpen 判失败、整条引擎链静默回落内置 BT——然后引擎与内置 BT
    /// 同时下载同一部种子抢带宽（实测引发卡顿掉帧）。命中历史直接复用，彻底绕开重复建任务。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Session> _history = new(StringComparer.Ordinal);

    /// <summary>引擎实例：同 runtime 可跑多个（播放/下载各一），靠控制口/initrd/媒体口/日志名隔离。
    /// 各实例的 VM 懒启动互不干扰 → 「边下边播」天然成立（下载 VM 与播放 VM 并行）。</summary>
    public QemuThunderEngine(string runtimeDir, Action<string>? log = null,
        int ctrlPort = 18080, int mediaPortBase = PreferredMediaPort,
        string initrdName = "pkg_initrd.gz", string consoleTag = "")
    {
        _runtimeDir = runtimeDir;
        _log = log;
        _ctrlPort = ctrlPort;
        _mediaPort = mediaPortBase;
        _initrdName = initrdName;
        _consoleTag = consoleTag;
        _idleTimer = new Timer(_ => IdleCheck(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        _watchdog = new Timer(_ => WatchdogTick(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
    }

    public string Name => "迅雷(QEMU)";

    /// <summary>运行时文件已部署即算「就绪」；VM 懒启动发生在首次任务。</summary>
    public bool IsReady => QemuHostRuntime.IsPresent(_runtimeDir, _initrdName);

    // ═══════════ IPreferredMagnetEngine ═══════════

    public async Task<bool> EnsureReadyAsync()
    {
        if (!IsReady) return false;
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return await EnsureStartedLockedAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { Log($"启动异常：{ex.GetType().Name}: {ex.Message}"); return false; }
        finally { _gate.Release(); }
    }

    public async Task<List<MagnetFile>?> ListFilesAsync(string magnet, string? preferName = null, CancellationToken ct = default)
    {
        if (!IsReady) return null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await EnsureStartedLockedAsync(ct).ConfigureAwait(false)) return null;
            var s = await PrepareSessionLockedAsync(magnet, preferName, ct).ConfigureAwait(false);
            return s?.Files.ToList();
        }
        catch (Exception ex) { Log($"ListFiles 异常：{ex.GetType().Name}: {ex.Message}"); return null; }
        finally { _gate.Release(); }
    }

    public async Task<MagnetPlayback?> TryOpenAsync(string magnet, string? preferName = null, CancellationToken ct = default)
    {
        if (!IsReady) return null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await EnsureStartedLockedAsync(ct).ConfigureAwait(false)) return null;
            var s = await PrepareSessionLockedAsync(magnet, preferName, ct).ConfigureAwait(false);
            if (s is null) return null;

            var pick = SelectFile(s.Files, preferName);
            var hash = ResolveInfoHashHex(magnet);

            // 会话已在播同一种子：换集时**不重建任务**（同 btih 重建会被引擎以 9128 拒绝）。
            // 引擎任务按全选下载整个种子（excludeCsv 留空），换集 = 直接把媒体口切到新文件路径，
            // 等待引擎顺序下载覆盖到该文件（下载 3-4MB/s，未到目标集时验证循环内等待）。
            if (s.DlSent && s.Played && s.PlayUrlPath.Length > 0)
            {
                if (s.PickIndex == pick.Index && _streamProxy is not null)
                {
                    Log($"复用已在播会话（走缓存代理）：#{pick.Index} {pick.Name}");
                    _lastActiveUtc = DateTime.UtcNow;
                    return new MagnetPlayback(hash, pick.Index, pick.Size, Path.GetFileName(pick.Name), _streamProxy.Url);
                }

                // 换片（同种子）：合成新文件路径并验证引擎能否供数
                var newPath = SynthesizeUrlPath(s.Dir + "/" + pick.Name);
                var gotNew = await VerifyBytesAsync(newPath, ct).ConfigureAwait(false);
                if (gotNew > 0)
                {
                    Log($"同任务换片：#{pick.Index} {pick.Name}（引擎已可供数）");
                    s.PickIndex = pick.Index; s.PickName = pick.Name; s.PickSize = pick.Size;
                    s.PlayUrlPath = newPath; s.Played = true;
                    _lastActiveUtc = DateTime.UtcNow;
                    LastDirectMediaUrl = MediaUrl(newPath);
                    StartStreamProxy(s);
                    return new MagnetPlayback(hash, pick.Index, pick.Size, Path.GetFileName(pick.Name),
                        _streamProxy?.Url ?? MediaUrl(newPath));
                }
                if (!s.Cancelled) Log($"换片 #{pick.Index} 引擎尚无数据（等待顺序下载中），重新下发 DL 兜底");
                s.PickIndex = pick.Index; s.PickName = pick.Name; s.PickSize = pick.Size;
                s.DlSent = true; s.Played = false; s.PlayUrlPath = ""; s.LastError = null;
                LastDirectMediaUrl = null;
                // 落到下方常规 DL 流程（全选重建；若引擎因 9128 拒绝则走回落）
            }
            else if (s.Played && s.PlayUrlPath.Length > 0 && s.PickIndex == pick.Index)
            {
                if (_streamProxy is not null)
                {
                    Log($"复用已在播会话（走缓存代理）：#{pick.Index} {pick.Name}");
                    _lastActiveUtc = DateTime.UtcNow;
                    return new MagnetPlayback(hash, pick.Index, pick.Size, Path.GetFileName(pick.Name), _streamProxy.Url);
                }
                var reuse = await VerifyOnceAsync(s.PlayUrlPath, ct).ConfigureAwait(false);
                if (reuse > 0)
                {
                    Log($"复用已在播会话：#{pick.Index} {pick.Name}");
                    _lastActiveUtc = DateTime.UtcNow;
                    StartStreamProxy(s);
                    return new MagnetPlayback(hash, pick.Index, pick.Size, Path.GetFileName(pick.Name),
                        _streamProxy?.Url ?? MediaUrl(s.PlayUrlPath));
                }
                Log("旧播放地址已失效，重新下发 DL");
            }

            s.PickIndex = pick.Index; s.PickName = pick.Name; s.PickSize = pick.Size;
            s.DlSent = true; s.Played = false; s.PlayUrlPath = ""; s.LastError = null;
            LastDirectMediaUrl = null;

            // ★ excludeCsv 传空 = 全选下载整个种子：换集时同 btih 重建任务会被 9128 拒绝，
            //   全选让引擎顺序下载所有集，换集只需把媒体口切到新文件路径（见上方换片分支）。
            //   tmpfs 3500m 装得下整部剧；下载顺序从种子头部开始，首集起播不受影响。
            s.Others = "";
            // ★ 必须显式携带种子路径：guest 的 "-" 回退会取「引擎当前上下文」的种子（最后一次
            //   TASK MAGNET），而详情页探测/会话复用会把上下文停在别的磁力上 —— 任务会建到
            //   错误的种子上（内容与目录错位），播放路径在引擎里 404 死循环（实测）。
            s.DLTorrentPath = s.TorrentRawPath.Length > 0 ? s.TorrentRawPath
                : Uri.UnescapeDataString(Uri.UnescapeDataString(s.TorrentUrlPath));
            _server!.SetCommand($"DL {s.DLTorrentPath}|{s.Dir}|{pick.Name}|{pick.Index}|");
            _lastActiveUtc = DateTime.UtcNow;

            // 等阶段二 ev=play（视频地址）
            await PollUntilAsync(() => s.Cancelled || s.Played || s.LastError is not null, TimeSpan.FromSeconds(240), ct).ConfigureAwait(false);
            if (!s.Played)
            {
                if (!s.Cancelled)
                {
                    Log(s.LastError ?? "等播放地址超时（240s）");
                    // DL 失败（如 VM 重启后种子已不在磁盘）：剔除历史，下次重新走 TASK MAGNET 展开
                    _history.TryRemove(magnet, out _);
                }
                return null;
            }
            Log($"播放地址已就绪：{s.PlayUrlPath}");

            // 验证确实出数据（P2SP 起量通常几秒；0 字节判失败→回落）
            var got = await VerifyBytesAsync(s.PlayUrlPath, ct).ConfigureAwait(false);
            if (got == 0)
            {
                Log("拉流验证 60s 内 0 字节 —— 判失败，回落后续引擎");
                return null;
            }

            _lastActiveUtc = DateTime.UtcNow;
            LastDirectMediaUrl = MediaUrl(s.PlayUrlPath);
            StartStreamProxy(s);
            return new MagnetPlayback(hash, pick.Index, pick.Size, Path.GetFileName(pick.Name),
                _streamProxy?.Url ?? MediaUrl(s.PlayUrlPath));
        }
        catch (Exception ex) { Log($"TryOpen 异常：{ex.GetType().Name}: {ex.Message}"); return null; }
        finally { _gate.Release(); }
    }

    /// <summary>最近一次会话的**直连媒体口** URL（调试/基准用；播放器走的是缓存代理地址）。</summary>
    public string? LastDirectMediaUrl { get; private set; }

    /// <summary>
    /// 磁力下载：选中文件（<paramref name="preferName"/> 匹配，否则最大视频文件）由引擎独占下载，
    /// 完成后经媒体口拉回宿主磁盘（写 <c>destPath+".part"</c>，支持断点续传）。
    /// </summary>
    /// <param name="destPathFor">由引擎解析出的真实文件名（含扩展名）→ 返回宿主保存全路径（调用方负责改名/去重/更新任务字段）。</param>
    /// <param name="progress">(已下载字节, 总字节)——引擎下载阶段与导出阶段共用，约 1s/次回调。</param>
    /// <returns>false = 失败/被取消（原因见日志）。</returns>
    /// <remarks>
    /// ⚠ 单 VM 单会话：本方法**不持锁等待**（setup 持 <see cref="_gate"/>，下载轮询在锁外），
    /// 下载期间发起播放会重建任务（9128 自愈）把下载会话顶掉——此时本方法快速失败返回 false，
    /// 以播放优先；调用方可择机重试（.part 已拉部分可续传）。
    /// 引擎 tmpfs 是易失存储：导出完成前 VM 退出会丢数据，.part 之外的进度不作数。
    /// </remarks>
    public async Task<bool> DownloadToFileAsync(string magnet, string preferName,
        Func<string, string> destPathFor, Action<long, long>? progress, CancellationToken ct)
    {
        if (!IsReady) { Log("磁力下载不可用：迅雷引擎运行时缺失"); return false; }

        Session? s;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await EnsureStartedLockedAsync(ct).ConfigureAwait(false)) return false;
            s = await PrepareSessionLockedAsync(magnet, preferName, ct).ConfigureAwait(false);
            if (s is null) return false;

            var pick = SelectFile(s.Files, preferName);
            var destPath = destPathFor(pick.Name);
            Log($"磁力下载：选中 #{pick.Index} {pick.Name}（{pick.Size / 1048576.0:F1}MB）→ {destPath}");

            // 独占下载：反选其余文件（引擎任务总量收敛为所选文件，进度即文件进度）。
            // guest 反选参数最多 64 项（idxs[64]），超出时退化全选（st=2 = 整种子完成）。
            var exclude = s.Files.Count <= 65
                ? string.Join(",", s.Files.Where(f => f.Index != pick.Index).Select(f => f.Index))
                : "";
            s.PickIndex = pick.Index; s.PickName = pick.Name; s.PickSize = pick.Size;
            s.DlSent = true; s.Played = false; s.PlayUrlPath = ""; s.LastError = null;
            s.Others = exclude;
            s.DLTorrentPath = s.TorrentRawPath.Length > 0 ? s.TorrentRawPath
                : Uri.UnescapeDataString(Uri.UnescapeDataString(s.TorrentUrlPath));
            _server!.SetCommand($"DL {s.DLTorrentPath}|{s.Dir}|{pick.Name}|{pick.Index}|{exclude}");
            _lastActiveUtc = DateTime.UtcNow;
            LastDirectMediaUrl = MediaUrl(SynthesizeUrlPath(s.Dir + "/" + pick.Name));   // 下载文件的媒体口地址（调试/seektest 用）
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(180);
            while (!ct.IsCancellationRequested && !s.Cancelled && _session == s && s.LastSt != 2)
            {
                if (s.LastSt == 3 || s.LastError is not null)
                {
                    Log($"磁力下载失败：{s.LastError ?? $"引擎状态 st={s.LastSt}"}");
                    return false;
                }
                progress?.Invoke(s.LastDone, s.LastTotal > 0 ? s.LastTotal : pick.Size);
                if (DateTime.UtcNow > deadline) { Log("磁力下载超时（180 分钟）"); return false; }
                try { await Task.Delay(1000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return false; }
            }
            if (s.Cancelled || _session != s)
            {
                Log("磁力下载中断：会话被播放/其他任务替换（以播放优先），稍后重试可续传");
                return false;
            }
            Log("引擎侧下载完成（st=2），开始导出到本机");

            var encoded = SynthesizeUrlPath(s.Dir + "/" + pick.Name);
            return await PullToFileAsync(encoded, pick.Size, destPath, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { Log($"磁力下载异常：{ex.GetType().Name}: {ex.Message}"); return false; }
        finally { _gate.Release(); }
    }

    /// <summary>经媒体口把引擎已就绪的文件拉回宿主磁盘（写 <c>destPath+".part"</c>，Range 断点续传，
    /// 连接中断自动重试最多 5 次）。totalSize 为引擎侧文件大小。</summary>
    private async Task<bool> PullToFileAsync(string encodedPath, long totalSize, string destPath,
        Action<long, long>? progress, CancellationToken ct)
    {
        var partPath = destPath + ".part";
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            long start = 0;
            try { if (File.Exists(partPath)) start = new FileInfo(partPath).Length; } catch { start = 0; }
            if (totalSize > 0 && start >= totalSize) { Log("导出完成（.part 已齐）"); return true; }

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, MediaUrl(encodedPath));
                if (start > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, null);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                var code = (int)resp.StatusCode;
                long total;
                if (code == 206)
                {
                    total = resp.Content.Headers.ContentRange?.Length ?? totalSize;   // ContentRange.Length = 文件全长
                }
                else if (code == 200)
                {
                    start = 0;   // 引擎未按 Range 应答：从头拉
                    total = resp.Content.Headers.ContentLength ?? totalSize;
                }
                else
                {
                    Log($"导出失败：HTTP {code}（第 {attempt}/5 次，2s 后重试）");
                    await Task.Delay(2000, ct).ConfigureAwait(false);
                    continue;
                }

                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = new FileStream(partPath, start > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write);
                var buf = new byte[262144];
                long read = start;
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
                    read += n;
                    progress?.Invoke(read, total);
                }
                if (total <= 0 || read >= total) { Log($"导出完成：{read / 1048576.0:F1}MB"); return true; }
                Log($"导出中断于 {read}/{total}（第 {attempt}/5 次，续传重试）");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log($"导出异常：{ex.GetType().Name}: {ex.Message}（第 {attempt}/5 次，续传重试）");
            }
            try { await Task.Delay(2000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
        }
        return false;
    }

    /// <summary>停止当前任务（保留 VM；空闲计时器过会儿会收掉它）。</summary>
    public void Stop()
    {
        var s = _session;
        _session = null;
        if (s is not null) s.Cancelled = true;   // 打断进行中的等待（否则会挂满超时）
        var p = _streamProxy;
        _streamProxy = null;
        p?.Dispose();                            // 停止播放即关闭缓存代理（播放器地址随之失效）
    }

    /// <summary>为当前会话（重新）建立读前缓存代理；播放器地址从媒体口换成代理口。
    /// 代理起不来时静默回落直连媒体口（功能不受影响，只是回到旧行为）。</summary>
    private void StartStreamProxy(Session s)
    {
        var old = _streamProxy;
        _streamProxy = null;
        old?.Dispose();
        try
        {
            var proxy = new QemuStreamProxy(_mediaPort, s.PlayUrlPath, s.PickSize,
                QemuStreamProxy.ContentTypeFor(s.PickName), Log);
            // seek 重定位 → KICK 引擎进入预取模式：让引擎优先下载 seek 目标区间
            //（「seek 到哪下到哪」，不重定位也发无害——引擎已按读位置供数）
            proxy.OnRelocate = () =>
            {
                Log("[引擎] seek 重定位 → KICK PREFETCH（区间优先下载）");
                _server?.SetCommand("KICK PREFETCH");
            };
            proxy.Start();
            _streamProxy = proxy;
            Log($"播放器地址改走缓存代理：{proxy.Url}");
        }
        catch (Exception ex)
        {
            Log($"缓存代理启动失败（回落直连媒体口）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ═══════════ 编排（逐行对齐 ctrlserver2.py） ═══════════

    private async Task<bool> EnsureStartedLockedAsync(CancellationToken ct)
    {
        if (_server is null)
        {
            try
            {
                _server = new QemuControlServer(_ctrlPort);
                _server.ReportReceived += OnReport;
                _server.Log += Log;
                _server.Start();
            }
            catch (Exception ex)
            {
                _server = null;
                Log($"控制端启动失败（端口 {CtrlPort} 被占？）：{ex.Message}");
                return false;
            }
        }

        if (_runtime is null || !_runtime.IsRunning)
        {
            _runtime?.Dispose();
            _mediaPort = PickFreePort(_mediaPort);
            _runtime = new QemuHostRuntime(_runtimeDir, _mediaPort, Log, _initrdName, _consoleTag);
            _server.ResetFirstPoll();
            if (!await _runtime.StartAsync(ct).ConfigureAwait(false)) return false;

            var ready = await _server.WaitFirstPollAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(false);
            if (!ready)
            {
                Log("VM 已启动但 90s 内 guest 未连上控制端");
                return false;
            }
            Log($"VM 就绪（媒体口 {_mediaPort}）");
        }

        _lastActiveUtc = DateTime.UtcNow;
        return true;
    }

    /// <summary>Phase 1：下发磁力并把 .torrent 拉回来展开文件列表（成功即缓存会话）。</summary>
    private async Task<Session?> PrepareSessionLockedAsync(string magnet, string? preferName, CancellationToken ct)
    {
        // 历史命中：同一磁力直接复用（含文件列表/种子路径），不再发 TASK MAGNET（9128 重复任务）
        if (_history.TryGetValue(magnet, out var hist) && hist.Files.Count > 0)
        {
            _lastActiveUtc = DateTime.UtcNow;
            _session = hist;
            return hist;
        }

        // 换磁力 = 引擎会被重武装到别的文件，旧缓存代理必须立即停
        //（否则它会把「新文件」的字节当作旧文件送给播放器——数据就错了）
        var staleProxy = _streamProxy;
        _streamProxy = null;
        staleProxy?.Dispose();

        var s = new Session { Magnet = magnet, Name = BuildTaskName(preferName, magnet) };
        s.Dir = "/thunder-data/" + StripExtension(s.Name);
        _session = s;
        _lastActiveUtc = DateTime.UtcNow;

        Log($"下发磁力任务：{s.Name}");
        _server!.SetCommand($"TASK MAGNET {magnet} {s.Name}");

        // ① 等种子落盘：ev=torrent（原路径）与阶段一 ev=play（引擎 URL 路径）先到哪个算哪个
        await PollUntilAsync(
            () => s.Cancelled || s.TorrentRawPath.Length > 0 || s.TorrentUrlPath.Length > 0 || s.LastError is not null,
            TimeSpan.FromSeconds(120), ct).ConfigureAwait(false);
        if (s.Cancelled) return null;
        if (s.LastError is not null) { Log(s.LastError); _session = null; return null; }
        if (s.TorrentRawPath.Length == 0 && s.TorrentUrlPath.Length == 0)
        {
            Log("等种子落盘超时（120s）");
            _session = null;
            return null;
        }

        // ② 取 .torrent：优先用阶段一 play 的 URL 路径（mag26/mag28 验证过的格式），
        //    拿不到时用 torrent 原路径合成「双重 URL 编码」（引擎的编码格式："/" + 两次 escape 的绝对路径）
        var fetchPath = s.TorrentUrlPath.Length > 0
            ? s.TorrentUrlPath
            : SynthesizeUrlPath(s.TorrentRawPath);

        var data = await FetchTorrentAsync(fetchPath, ct).ConfigureAwait(false);
        if (data is null && s.TorrentUrlPath.Length == 0)
        {
            // 合成路径失败 → 再等 30s 让 play 事件到，用官方路径重试
            await PollUntilAsync(() => s.TorrentUrlPath.Length > 0, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            if (s.TorrentUrlPath.Length > 0)
                data = await FetchTorrentAsync(s.TorrentUrlPath, ct).ConfigureAwait(false);
        }
        if (data is null)
        {
            Log("展开种子失败（两种路径都取不到 bencode）");
            _session = null;
            return null;
        }

        try
        {
            var (entries, tname) = Bencode.ParseTorrent(data);
            if (entries.Count == 0)
            {
                Log("种子文件列表为空");
                _session = null;
                return null;
            }
            s.Files = entries.Select(e => new MagnetFile(e.Index, e.Size, e.Rel)).ToList();
            s.AtUtc = DateTime.UtcNow;
            _history[magnet] = s;   // 进程级历史：重复点播/换集不再重复建任务（9128）
            Log($"文件列表 {entries.Count} 项（种子名 {tname}）");
            foreach (var f in s.Files.Take(12)) Log($"  #{f.Index,3}  {f.Size / 1048576.0,8:F1}MB  {f.Name}");
            if (s.Files.Count > 12) Log($"  … 共 {s.Files.Count} 项");
            return s;
        }
        catch (Exception ex)
        {
            Log($"种子解析失败：{ex.Message}");
            _session = null;
            return null;
        }
    }

    private async Task<byte[]?> FetchTorrentAsync(string path, CancellationToken ct)
    {
        var (code, body) = await FetchAsync(path, 0, ct).ConfigureAwait(false);
        if (body.Length >= 100 && body[0] == (byte)'d') return body;
        Log($"种子内容异常（HTTP={code} len={body.Length} path={path}）");
        return null;
    }

    // ═══════════ 事件与工具 ═══════════

    private void OnReport(QemuReport r)
    {
        _lastActiveUtc = DateTime.UtcNow;
        var s = _session;
        if (s is null) return;
        switch (r.Ev)
        {
            case "torrent":
                if (s.TorrentRawPath.Length == 0) s.TorrentRawPath = r.Msg;
                break;
            case "play":
                if (r.Msg.Length == 0) break;
                if (!s.DlSent)
                {
                    if (s.TorrentUrlPath.Length == 0) s.TorrentUrlPath = r.Msg;
                }
                else if (!s.Played && r.Msg != s.TorrentUrlPath)
                {
                    s.Played = true;
                    s.PlayUrlPath = r.Msg;
                }
                break;
            case "status":
                // 任务死亡自动恢复：guest 引擎偶发中途死亡（实测 err=114010，1.57GB/1.9GB 处，
                // 之后 st=3/速度 0 永不复生），不恢复的话播放器读到未下载区间就永远缓冲。
                // 注意 guest 只在状态变化时上报，死亡状态只到此一报——重试循环在恢复方法内部。
                s.LastSt = r.St;
                s.LastDone = r.Done;
                s.LastTotal = r.Total;
                if (r.St == 3 && r.Err != 0 && s.DlSent && !s.Cancelled && s.Played)
                {
                    s.FailedErr = r.Err;
                    _ = RecoverTaskAsync(s);
                }
                break;
            case "error":
                s.LastError = $"guest 报错：{r.Msg}（st={r.St} err={r.Err}）";
                break;
        }
    }

    /// <summary>单次播放会话内任务死亡的最大自动恢复次数（约 60s 的重试窗口；用尽后由用户换源）。</summary>
    private const int MaxTaskRetries = 5;

    /// <summary>任务死亡自动恢复：重新下发 DL，每个尝试观察最多 15s，未复活则打断缓存代理上游
    /// 连接强制重连后再次重试；复活即返回并清零计数。guest 侧 start_dl 遇 9128（同种子任务句柄
    /// 还挂在引擎里——死亡任务重建的必经之路）会自动 stopTask 清理后重试 createBtTask，已下载数据
    /// 在 tmpfs 里由引擎续传。guest 只在状态变化时上报 status，死亡状态只报一次——所以重试必须在
    /// 此循环内完成，不能依赖后续 status 事件再次触发。CAS 闸保证同一会话只有一个恢复循环在跑。</summary>
    private async Task RecoverTaskAsync(Session s)
    {
        if (Interlocked.CompareExchange(ref s.Recovering, 1, 0) != 0) return;
        try
        {
            while (!s.Cancelled && s.RetryCount < MaxTaskRetries)
            {
                s.RetryCount++;
                var cause = s.FailedErr != 0 ? $"err={s.FailedErr}" : "上游持续断粮";
                Log($"任务死亡（{cause}），自动恢复 {s.RetryCount}/{MaxTaskRetries}：重新下发 DL" +
                    "（guest 侧 9128=同种子任务已存在时会自动 stopTask 清理后重建，已有数据续传）");
                _server?.SetCommand($"DL {s.DLTorrentPath}|{s.Dir}|{s.PickName}|{s.PickIndex}|{s.Others}");

                var revived = false;
                for (var i = 0; i < 15 && !s.Cancelled; i++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    if (s.LastSt is 1 or 2) { revived = true; break; }   // 新任务已建并运行
                }
                // 无条件踢一次上游：旧连接可能绑在死任务的流状态上，断开重连后
                // guest 重新武装 getLocalUrl，新连接从（可能已复活的）任务续流
                _streamProxy?.KickUpstream();
                if (revived)
                {
                    Log("任务已复活（恢复成功，续传中）");
                    s.RetryCount = 0;
                    break;
                }
            }
            if (!s.Cancelled && s.RetryCount >= MaxTaskRetries && s.LastSt == 3)
                Log("任务自动恢复次数用尽，放弃（请换源或重新点播）");
        }
        finally { Interlocked.Exchange(ref s.Recovering, 0); }
    }

    /// <summary>经媒体口取字节（<paramref name="maxBytes"/>=0 表示不限，上限 4MB 防呆）。</summary>
    private async Task<(int Code, byte[] Body)> FetchAsync(string path, int maxBytes, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, MediaUrl(path));
            if (maxBytes > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, maxBytes - 1);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var cap = maxBytes > 0 ? maxBytes : 4 * 1024 * 1024;
            var buf = new byte[cap];
            var off = 0;
            int n;
            while (off < cap && (n = await stream.ReadAsync(buf.AsMemory(off, cap - off), ct).ConfigureAwait(false)) > 0)
                off += n;
            var body = new byte[off];
            Buffer.BlockCopy(buf, 0, body, 0, off);
            return ((int)resp.StatusCode, body);
        }
        catch (Exception ex)
        {
            Log($"媒体口取字节失败：{ex.GetType().Name}: {ex.Message}");
            return (0, []);
        }
    }

    /// <summary>轮询拉流验证：最多 60s，目标 64KB（返回实际拿到的最大字节数）。</summary>
    private async Task<long> VerifyBytesAsync(string path, CancellationToken ct)
    {
        const int target = 64 * 1024;
        long got = 0;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var (code, data) = await FetchAsync(path, target, ct).ConfigureAwait(false);
            got = Math.Max(got, data.Length);
            if (got >= target && code is 200 or 206)
            {
                var head = Convert.ToHexString(data.AsSpan(0, Math.Min(16, data.Length))).ToLowerInvariant();
                Log($"拉流验证通过：HTTP={code} 字节={data.Length} 头={head}");
                return got;
            }
            if (data.Length > 0 && data.Length < target)
                Log($"拉流进行中：HTTP={code} 字节={data.Length}（继续等）");
            try { await Task.Delay(2000, ct).ConfigureAwait(false); } catch { break; }
        }
        return got;
    }

    /// <summary>单次取字节（会话复用前的活性探测）。</summary>
    private async Task<long> VerifyOnceAsync(string path, CancellationToken ct)
    {
        var (code, data) = await FetchAsync(path, 64 * 1024, ct).ConfigureAwait(false);
        return code is 200 or 206 ? data.Length : 0;
    }

    private static async Task PollUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            try { await Task.Delay(200, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        }
    }

    private string MediaUrl(string path) => $"http://127.0.0.1:{_mediaPort}{path}";

    /// <summary>引擎媒体代理接受的路径格式：" /" + 对绝对路径做两次 URL 编码（mag26 实测样例：/%252Fthunder-data%252Fa.mp4）。</summary>
    private static string SynthesizeUrlPath(string absPath) => "/" + Uri.EscapeDataString(Uri.EscapeDataString(absPath));

    private static int PickFreePort(int start)
    {
        for (var p = start; p < start + 20; p++)
        {
            try
            {
                var probe = new TcpListener(IPAddress.Loopback, p);
                probe.Start();
                probe.Stop();
                return p;
            }
            catch { }
        }
        return start;
    }

    private static MagnetFile SelectFile(List<MagnetFile> files, string? preferName)
    {
        if (!string.IsNullOrWhiteSpace(preferName))
        {
            var want = Path.GetFileNameWithoutExtension(preferName.Trim());
            if (want.Length > 0)
            {
                var hit = files.FirstOrDefault(f =>
                    Path.GetFileNameWithoutExtension(f.Name).Contains(want, StringComparison.OrdinalIgnoreCase));
                if (hit is not null) return hit;
            }
        }
        var vids = files.Where(f => VideoExt.Contains(Path.GetExtension(f.Name))).ToList();
        return (vids.Count > 0 ? vids : files).OrderByDescending(f => f.Size).First();
    }

    /// <summary>任务名：无空格、无路径分隔符、UTF-8 ≤90 字节；扩展名保留（取不到则 .mp4）。</summary>
    internal static string BuildTaskName(string? preferName, string magnet)
    {
        var basis = preferName;
        if (string.IsNullOrWhiteSpace(basis)) basis = ExtractDn(magnet);
        if (string.IsNullOrWhiteSpace(basis)) basis = ResolveInfoHashHex(magnet);
        if (string.IsNullOrWhiteSpace(basis)) basis = "download";

        var name = Path.GetFileName(basis.Replace('\\', '/').Trim());
        var ext = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        stem = new string(stem.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        if (stem.Length == 0) stem = "download";
        while (Encoding.UTF8.GetByteCount(stem) > 90 && stem.Length > 1) stem = stem[..^1];

        if (ext.Length is < 2 or > 8 || !ext.Skip(1).All(char.IsLetterOrDigit)) ext = ".mp4";
        return stem + ext;
    }

    private static string StripExtension(string name)
    {
        var i = name.LastIndexOf('.');
        return i > 0 ? name[..i] : name;
    }

    private static string? ExtractDn(string magnet)
    {
        var i = magnet.IndexOf("dn=", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var rest = magnet[(i + 3)..];
        var end = rest.IndexOf('&');
        var raw = end < 0 ? rest : rest[..end];
        try { return Uri.UnescapeDataString(raw.Replace('+', ' ')); }
        catch { return null; }
    }

    internal static string ResolveInfoHashHex(string magnet)
    {
        var m = BtihHex.Match(magnet);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : "";
    }

    // ═══════════ 空闲自停 / 断粮看门狗 / 释放 ═══════════

    /// <summary>播放中断粮看门狗：guest 只在「状态或进度变化」时上报——任务活着但源断光
    /// （速度=0、进度冻结）时宿主完全失明，实测这是 err=114010 自杀的前奏。看门狗盯缓存代理
    /// 的上游字节计数：读者还在读而上游 30s 零字节 → KICK BOTH（XLRequeryIndex 重查 hub 资源 +
    /// XLEnterPrefetchMode 预取模式）自救；90s 仍断粮 → 按任务死亡走 RecoverTaskAsync 重建任务续传。</summary>
    private void WatchdogTick()
    {
        try
        {
            if (_disposed) return;
            var s = _session;
            var p = _streamProxy;
            if (s is null || p is null || !s.Played || s.Cancelled || s.Recovering != 0) { ResetWatch(); return; }
            if (!p.ReaderStarving) { ResetWatch(); return; }   // 读者没在等前沿数据（暂停/顺畅播放）：不判
            if (p.UpstreamEof) { ResetWatch(); return; }       // 已拉到文件尾：断粮属正常

            var cur = p.UpstreamTotal;
            if (cur != _watchLastUpstream)
            {
                _watchLastUpstream = cur;
                _watchFrozenTicks = 0;
                _watchKicked = false;
                return;
            }
            _watchFrozenTicks++;
            if (!_watchKicked && _watchFrozenTicks >= 2)   // ~30s 上游零字节
            {
                _watchKicked = true;
                Log("[引擎] 上游 30s 断粮（播放器仍在读），KICK BOTH：requery 重查资源 + prefetch 预取模式");
                _server?.SetCommand("KICK BOTH");
            }
            else if (_watchFrozenTicks >= 6)               // ~90s 仍断粮：按任务死亡处理
            {
                Log("[引擎] 上游 90s 断粮未复活，按任务死亡处理（重建任务续传）");
                ResetWatch();
                s.FailedErr = 0;
                _ = RecoverTaskAsync(s);
            }
        }
        catch { }
    }

    private void ResetWatch()
    {
        _watchLastUpstream = -1;
        _watchFrozenTicks = 0;
        _watchKicked = false;
    }

    private void IdleCheck()
    {
        try
        {
            if (_disposed || _runtime is not { IsRunning: true }) return;
            if (DateTime.UtcNow - _lastActiveUtc < TimeSpan.FromMinutes(15)) return;
            // 有「已交播放地址」的会话时不停（播放器可能随时来取流，宿主侧不可见）
            if (_session is { Played: true }) return;

            Log("空闲 15 分钟，停掉 QEMU 释放内存");
            _session = null;
            _runtime.Stop();
        }
        catch { }
    }

    private void Log(string message) => _log?.Invoke(message);

    private sealed class Session
    {
        public string Magnet = "";
        public string Name = "";
        public string Dir = "";
        public string TorrentRawPath = "";
        public string TorrentUrlPath = "";
        public List<MagnetFile> Files = [];
        public int PickIndex = -1;
        public string PickName = "";
        public long PickSize;
        public string Others = "";          // DL 下发时的其余文件索引串（任务死亡恢复时重放）
        public string DLTorrentPath = "";   // DL 显式携带的种子路径（恢复时重放）
        public bool DlSent;
        public bool Played;
        public string PlayUrlPath = "";
        public string? LastError;
        public volatile bool Cancelled;
        public DateTime AtUtc = DateTime.UtcNow;
        public int RetryCount;              // 任务死亡后的自动恢复次数（任务复活时清零）
        public int Recovering;              // 恢复单飞闸（0=空闲 1=恢复中，Interlocked CAS）
        public int FailedErr;               // 最近一次任务死亡的错误码（日志用）
        public int LastSt;                  // 最近一次 status 的任务状态（1=运行 2=完成 3=失败）
        public long LastDone;               // 最近一次 status 的已下载字节数
        public long LastTotal;              // 最近一次 status 的任务总字节数
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _idleTimer.Dispose(); } catch { }
        try { _watchdog.Dispose(); } catch { }
        try { _streamProxy?.Dispose(); } catch { }
        try { _runtime?.Dispose(); } catch { }
        try { _server?.Dispose(); } catch { }
        try { _http.Dispose(); } catch { }
    }
}
