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
public sealed class QemuThunderEngine : IPreferredMagnetEngine, IPlaybackSessionLease, IDisposable
{
    /// <summary>控制口：烧死在 initrd 里（guest 每秒回连宿主 10.0.2.2:18080），改不了。</summary>
    /// <summary>默认控制口（多实例时各用各的，见构造参数）</summary>
    public const int CtrlPort = 18080;

    /// <summary>媒体口首选值（实际会在被占时向后探测空闲端口）。</summary>
    public const int PreferredMediaPort = 20092;

    /// <summary>monitor 口首选值（实际会在被占时向后探测空闲端口；仅回环）。</summary>
    public const int PreferredMonitorPort = 18090;

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
    private int _monitorPort;
    private Session? _session;
    private QemuStreamProxy? _streamProxy;
    private DateTime _lastActiveUtc = DateTime.UtcNow;
    /// <summary>VM 是否处于「退出播放页冻结」状态（true 时任何新会话前必须先唤醒）</summary>
    private volatile bool _vmPaused;
    /// <summary>播放会话「代理无读者」的起始时刻（null = 有读者/无播放会话）；
    /// 连续无读者够久即判定播放已结束（页面退出钩子没走到时的兜底）</summary>
    private DateTime? _playbackIdleSinceUtc;
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
        string initrdName = "pkg_initrd.gz", string consoleTag = "",
        int monitorPortBase = PreferredMonitorPort)
    {
        _runtimeDir = runtimeDir;
        _log = log;
        _ctrlPort = ctrlPort;
        _mediaPort = mediaPortBase;
        _monitorPort = monitorPortBase;
        _initrdName = initrdName;
        _consoleTag = consoleTag;
        _idleTimer = new Timer(_ => IdleCheck(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        _watchdog = new Timer(_ => WatchdogTick(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
    }

    public string Name => "迅雷(QEMU)";

    /// <summary>运行时文件已部署即算「就绪」；VM 懒启动发生在首次任务。</summary>
    public bool IsReady => QemuHostRuntime.IsPresent(_runtimeDir, _initrdName);

    /// <summary>引擎是否有活跃的播放/下载会话（已下发 DL 且未被替换）。探测类操作
    /// （详情页磁力展开）必须让位：PrepareSession 会替换 _session 并 Dispose 缓存代理，
    /// 顶掉 45Mbps 下载中的播放会话 = 黑屏 + 引擎拒建新任务（9111）+「无法解析磁力」弹窗
    /// （2026-09-17 师兄太稳健 实测）。</summary>
    public bool IsBusy
    {
        get { var s = _session; return s is not null && (s.DlSent || _streamProxy is not null); }
    }

    /// <summary>磁力点播磁盘缓存根目录（null = 关闭，默认关）。由宿主注入（AppPaths.Sub("btcache")）：
    /// 播放数据 4MB 分块落盘，重看/换集回看直接磁盘秒供，不再依赖引擎 tmpfs（VM 重启即空）。</summary>
    public string? StreamCacheRoot { get; set; }

    /// <summary>
    /// 数据面块设备镜像目录（null = 关闭，默认关）。由宿主注入（AppPaths.Sub("btcache/hub")）。
    ///
    /// <para>启用后 QEMU 会多挂一块 virtio-blk，guest 侧 harness 把引擎吐出的字节按文件偏移
    /// 直接写进去，宿主供数时**直读同一物理文件**（实测 2454~2926 MB/s），
    /// 绕开 SLIRP（40 MB/s）与 harness 转发（18.9 MB/s）。</para>
    ///
    /// <para>为 null 时整个特性关闭，行为与改动前完全一致 —— 这是刻意的：
    /// 新通道是**增益**，任何环境异常都能安全退化到纯 HTTP。</para>
    /// </summary>
    public string? BlockDeviceRoot { get; set; }

    /// <summary>每个会话的块设备镜像容量（默认 16GB，够放一部 4K 片；稀疏文件实际只占写入量）。</summary>
    public long BlockDeviceCapacityBytes { get; set; } = 16L * 1024 * 1024 * 1024;

    private long? _streamCacheCapOverride;

    /// <summary>
    /// 磁盘缓存容量上限（LRU 超限自动删最旧分块）。
    /// <para>默认取用户设置 <see cref="StreamCachePrefs.CapBytes"/>（默认 20GB，设置页可调 5/10/20/30/50），
    /// **实时生效**：设置页改完，下一次超限清理即按新值判，无需重启。
    /// 显式赋值可覆盖（测试/单次用途）。</para>
    /// </summary>
    public long StreamCacheCapBytes
    {
        get => _streamCacheCapOverride ?? StreamCachePrefs.CapBytes;
        set => _streamCacheCapOverride = value > 0 ? value : null;
    }

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
            // ★ 探测让位：引擎有活跃播放/下载会话时**不得**进入 PrepareSession——它会替换
            //   _session 并 Dispose 播放中的缓存代理（黑屏），且引擎下载中建新任务被拒（9111），
            //   两头全输（2026-09-17 实测）。跳过本次探测；未缓存的磁力下次进详情页重探。
            if (_session is not null && (_session.DlSent || _streamProxy is not null))
            {
                Log($"磁力探测让位：引擎正忙（活跃会话 #{_session.PickIndex} {_session.Name}），跳过 {preferName}");
                return null;
            }
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

                // 换片（同种子）：**绝不重建任务** —— 2026-09-17 实测血的教训：
                //   第 2 集尚未下载到时 VerifyBytes 探测失败 → 旧逻辑「重新下发 DL 兜底」→
                //   guest 9128 自愈 stopTask 把**正在 6MB/s 供数的老任务停掉**（st=1→st=4 速度=0），
                //   而引擎句柄不释放 → 4 次退避重试全部 9128 → 播放彻底死锁。
                //   老任务本就全选下载整个种子，媒体口**按路径**供数；未下载到的区间靠顺序下载补上，
                //   只需把播放路径切到新文件、起代理等待即可（代理上游循环持续读，数据一到即播）。
                var newPath = SynthesizeUrlPath(s.Dir + "/" + pick.Name);
                var gotNew = await VerifyOnceAsync(newPath, ct).ConfigureAwait(false);   // 快探一次（不再 60s 轮询）
                Log(gotNew > 0
                    ? $"同任务换片：#{pick.Index} {pick.Name}（引擎已可供数）"
                    : $"同任务换片：#{pick.Index} {pick.Name}（引擎暂无数据，切路径等待顺序下载补上）");
                s.PickIndex = pick.Index; s.PickName = pick.Name; s.PickSize = pick.Size;
                s.PlayUrlPath = newPath; s.Played = true;
                _lastActiveUtc = DateTime.UtcNow;
                LastDirectMediaUrl = MediaUrl(newPath);
                StartStreamProxy(s);
                return new MagnetPlayback(hash, pick.Index, pick.Size, Path.GetFileName(pick.Name),
                    _streamProxy?.Url ?? MediaUrl(newPath));
            }
            else if (s.Played && s.PlayUrlPath.Length > 0 && s.PickIndex == pick.Index)
            {
                if (_streamProxy is not null)
                {
                    Log($"复用已在播会话（走缓存代理）：#{pick.Index} {pick.Name}");
                    _lastActiveUtc = DateTime.UtcNow;
                    return new MagnetPlayback(hash, pick.Index, pick.Size, Path.GetFileName(pick.Name), _streamProxy.Url);
                }
                // 同一集重开：**不重建任务**（同上，重建必 9128 且自杀）。直接切回原路径起代理，
                // 引擎续供；原任务已死时数据供不上，由看门狗 RecoverTask 兜底（那是唯一该重建的时机）。
                Log($"复用会话路径重开：#{pick.Index} {pick.Name}（不重建任务）");
                _lastActiveUtc = DateTime.UtcNow;
                StartStreamProxy(s);
                return new MagnetPlayback(hash, pick.Index, pick.Size, Path.GetFileName(pick.Name),
                    _streamProxy?.Url ?? MediaUrl(s.PlayUrlPath));
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
        var r = await DownloadToFileExAsync(magnet, preferName, destPathFor, progress, ct).ConfigureAwait(false);
        return r.Ok;
    }

    /// <summary>
    /// <see cref="DownloadToFileAsync"/> 的「带原因」版本。
    ///
    /// <para><b>为什么需要它</b>：原方法只返回 <c>bool</c>，而 false 是**至少五种结局**的合集
    /// （用户取消 / 会话被别的任务顶替 / 引擎建任务失败 / 种子里没有视频 / 导出超时）。
    /// 调用方只能写一句模糊文案，实测把「引擎报 9128 任务已存在」显示成
    /// 「引擎被播放占用、超时或无源」—— 把排查方向直接带偏（2026-09-20 用户实测）。</para>
    /// </summary>
    public async Task<(bool Ok, string Reason)> DownloadToFileExAsync(string magnet, string preferName,
        Func<string, string> destPathFor, Action<long, long>? progress, CancellationToken ct)
    {
        if (!IsReady) { Log("磁力下载不可用：迅雷引擎运行时缺失"); return (false, "迅雷引擎运行时缺失（ThunderRuntime 未部署）"); }

        Session? s = null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await EnsureStartedLockedAsync(ct).ConfigureAwait(false))
                return (false, "迅雷引擎（QEMU VM）启动失败或 90s 内未就绪");
            s = await PrepareSessionLockedAsync(magnet, preferName, ct).ConfigureAwait(false);
            if (s is null)
                return (false, "磁力解析失败：45s 内未拿到种子（疑似死链/无资源），或引擎侧上报了错误");

            var pick = SelectFile(s.Files, preferName);
            var destPath = destPathFor(pick.Name);
            Log($"磁力下载：选中 #{pick.Index} {pick.Name}（{pick.Size / 1048576.0:F1}MB）→ {destPath}");

            // 独占下载：反选其余文件（引擎任务总量收敛为所选文件，进度即文件进度）。
            // guest 反选参数最多 64 项（idxs[64]），超出时退化全选（st=2 = 整种子完成）。
            var exclude = s.Files.Count <= 65
                ? string.Join(",", s.Files.Where(f => f.Index != pick.Index).Select(f => f.Index))
                : "";
            // ★ 种子路径必须用「文件系统路径」，不能拿引擎的 URL 路径顶替（见 TorrentPathFor）。
            s.PickIndex = pick.Index; s.PickName = pick.Name; s.PickSize = pick.Size;
            s.DlSent = true; s.Played = false; s.PlayUrlPath = ""; s.LastError = null;
            s.Others = exclude;
            s.DLTorrentPath = TorrentPathFor(s);

            // ── 下发 DL，并按需做「同种子残留任务」的 VM 级恢复 ──
            //
            // guest 侧对 9128（XL_TASK_ALREADY_EXIST）有 stopTask 自愈，但实测**不可靠**：
            //   引擎停任务后清理句柄是异步的，且同一 btih 的旧句柄经常清不掉 ——
            //   日志实测 stopTask 返回 9000 后，四次退避重建（0.5/1.5/3/3s）**全部仍撞 9128**。
            // 此时唯一可靠的办法是把整个 guest 重来：VM 一重启，引擎的任务表就空了。
            // 代价是丢 tmpfs 里未导出的进度，但**已经落进宿主磁盘缓存（StreamCache）的块不受影响**，
            // 重下时会直线命中，所以这个代价可以接受（远好于永久卡死）。
            var dlAttempt = await SendDlWithRecoveryAsync(s, pick, exclude, ct).ConfigureAwait(false);
            if (!dlAttempt.Ok)
            {
                var why = dlAttempt.Reason;
                Log($"磁力下载失败：{why}");
                return (false, why);
            }
            _lastActiveUtc = DateTime.UtcNow;

            // ⚠ 恢复流程（9128 重启 guest）会**换掉 session 对象**，所以此后一律以 _session 为准。
            var live = _session!;
            var livePickName = live.PickName;
            LastDirectMediaUrl = MediaUrl(SynthesizeUrlPath(live.Dir + "/" + livePickName));   // 下载文件的媒体口地址（调试/seektest 用）
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(180);
            // 「给恢复让路」的等待总次数上限：恢复用尽后 st 可能停在 4，没有这道闸会空转到超时
            var recoverWaits = 0;
            while (!ct.IsCancellationRequested && !live.Cancelled && _session == live && live.LastSt != 2)
            {
                if (live.LastSt is 3 or 4 || live.LastError is not null)
                {
                    // ⚠ 任务死亡（如 err=114010）会触发 RecoverTaskAsync 自动恢复（最多 5 轮 × 15s），
                    //   而恢复期间 st 会在 3/4/1 之间跳（stopTask → 重建 → st=1）。
                    //   原代码「一看到 st=3 就判失败」会把**本可续传**的死亡直接判死 ——
                    //   2026-09-20 hosttest download 实测：跑到 45% 任务死亡，恢复日志与失败日志
                    //   出现在**同一秒**，恢复循环根本没机会跑。这里给恢复让路。
                    if (live.Recovering != 0 && live.RetryCount < MaxTaskRetries
                        && recoverWaits++ < MaxTaskRetries * 20)
                    {
                        try { await Task.Delay(1000, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return (false, "已取消"); }
                        continue;
                    }
                    var why = live.LastError ?? $"引擎状态 st={live.LastSt}";
                    Log($"磁力下载失败：{why}");
                    return (false, "引擎建下载任务失败：" + why);
                }
                progress?.Invoke(live.LastDone, live.LastTotal > 0 ? live.LastTotal : live.PickSize);
                if (DateTime.UtcNow > deadline) { Log("磁力下载超时（180 分钟）"); return (false, "下载超时（180 分钟）"); }
                try { await Task.Delay(1000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return (false, "已取消"); }
            }
            if (live.Cancelled || _session != live)
            {
                Log("磁力下载中断：会话被播放/其他任务替换（以播放优先），稍后重试可续传");
                return (false, "引擎会话被其他任务（播放或另一个磁力下载）顶替 —— 迅雷引擎同一时刻只能跑一个任务");
            }
            Log("引擎侧下载完成（st=2），开始导出到本机");

            var encoded = SynthesizeUrlPath(live.Dir + "/" + livePickName);
            var pulled = await PullToFileAsync(encoded, live.PickSize, destPath, progress, ct).ConfigureAwait(false);
            return pulled ? (true, "") : (false, "引擎侧已完成，但导出到本机失败（媒体口中断，可重试续传）");
        }
        catch (OperationCanceledException)
        {
            // 用户在下载页点了取消：**必须主动收掉任务** —— guest 侧迅雷不会因为宿主不再轮询
            // 就停下载（宿主也看不到它的状态），不主动收就是「点了取消，流量继续跑」。
            // 仅在当前会话仍是本任务时收（同引擎单会话，取消 A 不该把正在播的 B 一起收掉）。
            if (ReferenceEquals(_session, s)) Teardown("磁力下载被取消");
            return (false, "已取消");
        }
        catch (Exception ex)
        {
            Log($"磁力下载异常：{ex.GetType().Name}: {ex.Message}");
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    /// <summary>下发 DL 并等待任务真正起来；撞上 <c>9128</c>（同种子任务已存在且清理不掉）时重启 guest 重来一次。</summary>
    private async Task<(bool Ok, string Reason)> SendDlWithRecoveryAsync(
        Session s, MagnetFile pick, string exclude, CancellationToken ct)
    {
        // 首次下发 + 等 20s（正常路径 1~2s 就有 st=1，给 20s 足够区分「起来了」与「卡在 9128」）
        _server!.SetCommand($"DL {s.DLTorrentPath}|{s.Dir}|{pick.Name}|{pick.Index}|{exclude}");
        if (await WaitDlStartedAsync(s, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false))
            return (true, "");

        // 只在「明确是 9128」时才重启 VM —— 其余失败（无源/参数错误）重启也救不回来，
        // 白丢一次 VM 冷启动（约 11s）与已下进度。
        if (!IsTaskAlreadyExists(s))
            return (false, s.LastError ?? $"引擎未能在 20s 内启动下载任务（st={s.LastSt}）");

        Log("⚠ 引擎报 9128（同种子任务已存在）且 guest 侧 stopTask 清理无效 —— 重启 guest 重来一次");
        _history.TryRemove(s.Magnet, out _);

        // 重启 VM：guest 的引擎任务表随进程消失。数据面（下载目录 tmpfs）会丢，
        // 但宿主磁盘缓存里的块还在，重下走磁盘命中。
        try { _server.SetCommand("STOP"); } catch { }
        try { _runtime?.Stop(); } catch { }
        _runtime = null;
        _session = null;
        s.Cancelled = true;              // 让可能还在跑的旧等待段退出

        if (!await EnsureStartedLockedAsync(ct).ConfigureAwait(false))
            return (false, "引擎任务冲突（9128），且重启迅雷 VM 失败");

        // 重新走「磁力 → 种子」：VM 重启后 tmpfs 是空的，种子文件必须重新落盘
        var s2 = await PrepareSessionLockedAsync(s.Magnet, pick.Name, ct).ConfigureAwait(false);
        if (s2 is null) return (false, "引擎任务冲突（9128）；重启 VM 后重新解析磁力失败");

        var pick2 = SelectFile(s2.Files, pick.Name);
        var exclude2 = s2.Files.Count <= 65
            ? string.Join(",", s2.Files.Where(f => f.Index != pick2.Index).Select(f => f.Index))
            : "";
        s2.PickIndex = pick2.Index; s2.PickName = pick2.Name; s2.PickSize = pick2.Size;
        s2.DlSent = true; s2.Played = false; s2.PlayUrlPath = ""; s2.LastError = null;
        s2.Others = exclude2;
        s2.DLTorrentPath = TorrentPathFor(s2);
        _server.SetCommand($"DL {s2.DLTorrentPath}|{s2.Dir}|{pick2.Name}|{pick2.Index}|{exclude2}");

        if (await WaitDlStartedAsync(s2, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false))
        {
            Log("✅ 重启 guest 后任务建立成功");
            return (true, "");
        }
        return (false, s2.LastError ?? $"重启 guest 后仍无法建立下载任务（st={s2.LastSt}）");
    }

    /// <summary>等任务真正起来（<c>st∈{1,2,4}</c> = 已建并可供数）；期间若报错立即返回 false。</summary>
    private async Task<bool> WaitDlStartedAsync(Session s, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested || s.Cancelled) return false;
            if (s.LastSt is 1 or 2 or 4) return true;
            if (s.LastError is not null) return false;
            try { await Task.Delay(500, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }
        return false;
    }

    /// <summary>当前会话的失败是否就是 <c>9128</c>（任务已存在）。</summary>
    private static bool IsTaskAlreadyExists(Session s) =>
        (s.LastError?.Contains("9128", StringComparison.Ordinal) ?? false) || s.LastSt == 9128;

    /// <summary>
    /// 下载用**种子文件路径**（给 guest 的 <c>createBtTask</c> 用）。
    ///
    /// <para>⚠️ 只能用「文件系统路径」<c>/thunder-data/&lt;任务名&gt;</c>，**绝不能**拿引擎的
    /// URL 路径顶替 —— 那是双重 URL 编码的形式（<c>/%252Fthunder-data%252Fxxx</c>），
    /// unescape 两次会得到 <c>//thunder-data/xxx</c>（开头多一个斜杠）。</para>
    ///
    /// <para><b>这正是 2026-09-20「暂停后恢复提示引擎被占用」的真根因</b>：当宿主走
    /// 「直取种子」路径成功时，guest 不会上报 <c>ev=torrent</c>，<c>TorrentRawPath</c> 为空，
    /// 旧代码就回落到 unescape 引擎 URL → 传下去的双斜杠路径 <c>createBtTask</c> 认不出，
    /// 任务号恒为 -1，引擎回 <c>9128（任务已存在）</c>；guest 的 <c>stopTask</c> 清理又拦不住
    /// （同一 btih 的旧任务句柄还在），四次重试全撞 9128 → 最终报成「引擎被占用」。</para>
    ///
    /// <para>宿主侧日志证据：<c>DL //thunder-data/4e34b15b9cfe.mp4|...</c>（双斜杠）。</para>
    /// </summary>
    private static string TorrentPathFor(Session s)
    {
        // guest 阶段一的落盘约定（ctrlloop.c）：snprintf(g_mag_torrent, "%s/%s", EMU_SAVE_PATH, name)
        // EMU_SAVE_PATH 固定为 /thunder-data，所以规范路径就是「/thunder-data/ + 任务名」。
        if (s.TorrentRawPath.Length > 0
            && s.TorrentRawPath.StartsWith("/thunder-data/", StringComparison.Ordinal))
            return s.TorrentRawPath;
        return "/thunder-data/" + s.Name;
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

                // ★ 进度上报必须节流（2026-09-19 用户实测「下载时窗口未响应」）：
                //   导出以 256KB 为一块，1.3GB ≈ 5200 次；每次回调都经宿主 UpdateTask 投递
                //   MainThread，几千个任务瞬间灌满 UI 消息队列 → 界面整体卡死。
                //   这里限到 ~2 次/秒（与引擎下载阶段的 1s 节拍一致），只影响展示精度，不影响速度。
                var lastReport = Environment.TickCount64;
                void Report(bool force)
                {
                    var now = Environment.TickCount64;
                    if (!force && now - lastReport < 500) return;
                    lastReport = now;
                    progress?.Invoke(read, total);
                }

                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
                    read += n;
                    Report(force: false);
                }
                Report(force: true);   // 收尾必报一次（否则进度停在节流点、显示不满）
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

    /// <summary>显式停止当前任务：结束会话并释放 VM（5GB 内存归还）。</summary>
    public void Stop() => Teardown("外部显式停止");

    // ═══════════ 退出播放页：冻结而非丢弃 ═══════════

    /// <summary>
    /// 播放器已退出（播放页离开）：**冻结 VM** 让迅雷侧下载立刻停下。
    ///
    /// <para>只对**播放会话**生效（<see cref="Session.Played"/>）—— 磁力下载任务（下载管理页）
    /// 与详情页磁力展开都不受影响；<paramref name="playedUrl"/> 与当前播放会话地址不符时也不动
    /// （防止把别的页面正在使用的会话停掉）。</para>
    ///
    /// <para>冻结不杀进程：任务句柄与 /thunder-data 里已下载的数据都留着，用户回来点同一磁力时
    /// <see cref="EnsureStartedLockedAsync"/> 会先唤醒 VM，直接续播（无需重建任务、无需冷启动）。
    /// 冻结不可用（monitor 未启用/端口被占）时退化为杀 VM —— 「不再下载」优先于「回来快」。</para>
    /// </summary>
    public void ReleasePlaybackSession(string? playedUrl)
    {
        var s = _session;
        if (s is null || !s.Played) return;

        // 地址不符有两种可能：① 用户中途切到了直链/别的线路，磁力会话被晾着（该冻）；
        // ② 本页的会话已被别的页面接管（不该冻）。用「代理还有没有读者」区分：
        //  ② 有人在读；① 磁力代理上早就没人读了（播放器读的是新线路的流）。
        // ⚠ 不能用「无读者」当作冻的唯一条件：正常退出时播放器刚被 Pause，HTTP 连接可能还挂着，
        //   那样就永远冻不上，等于没修。
        if (playedUrl is not null && !OwnsUrl(playedUrl) && _streamProxy?.HasReaders == true)
        {
            Log("退出播放页：引擎会话地址与本页不符且仍有读者 —— 判为其他页面在用，让位不冻");
            return;
        }

        ReleaseProxy();
        if (_runtime is { IsRunning: true } && _runtime.SetPaused(true))
        {
            _vmPaused = true;
            _playbackIdleSinceUtc = null;
            Log("退出播放页：已冻结 QEMU 下载（任务与已下载数据保留，回来可续）");
        }
        else
        {
            Log("退出播放页：冻结不可用，直接收掉 VM（保证不再下载）");
            Teardown("冻结不可用");
        }
    }

    /// <summary>该地址是否属于本引擎当前的播放会话（缓存代理地址或媒体口直连地址）。</summary>
    private bool OwnsUrl(string url) =>
        url.Length > 0 && (url == _streamProxy?.Url || url == LastDirectMediaUrl);

    /// <summary>关掉读前缓存代理（播放器手里的地址随之失效）。</summary>
    private void ReleaseProxy()
    {
        var p = _streamProxy;
        _streamProxy = null;
        p?.Dispose();
    }

    /// <summary>
    /// 真正结束会话并杀掉 VM：冻结态空闲超时 / 冻结不可用 / 显式 Stop 走这条路。
    /// 与冻结不同，这里会剔除历史缓存 —— VM 一死 tmpfs 就空了，下次必须重新走 TASK MAGNET。
    /// </summary>
    private void Teardown(string reason)
    {
        var s = _session;
        Log($"结束磁力会话（{reason}）");
        // 先请 guest 自己停任务（harness 已修真调 stopTask；旧 harness 只是让它停轮询），
        // 再杀 VM 兜底 —— 顺序反过来就没机会发了。
        try { _server?.SetCommand("STOP"); } catch { }
        _session = null;
        if (s is not null)
        {
            s.Cancelled = true;                 // 打断进行中的等待（否则会挂满超时）
            _history.TryRemove(s.Magnet, out _);
        }
        ReleaseProxy();
        _vmPaused = false;
        _playbackIdleSinceUtc = null;
        try { _runtime?.Stop(); } catch { }
    }

    /// <summary>唤醒被冻结的 VM（新会话前的必要动作：冻着的机器媒体口不会应答）。</summary>
    private void ResumeVmLocked()
    {
        if (!_vmPaused) return;
        _vmPaused = false;
        _runtime?.SetPaused(false);
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
            // 磁盘缓存（2026-09-17 用户要求 5~15GB LRU）：按 btih+文件索引分目录，
            // 重看/换集回看直接磁盘秒供，不再依赖引擎 tmpfs（重启即空）
            string? cacheDir = null;
            if (StreamCacheRoot is not null)
            {
                try
                {
                    cacheDir = StreamCache.DirFor(StreamCacheRoot, ResolveInfoHashHex(s.Magnet), s.PickIndex);
                    var root = StreamCacheRoot;
                    var cap = StreamCacheCapBytes;
                    _ = Task.Run(() => StreamCache.EnforceCap(root, cap, Log));   // 后台清理，不拖慢起播
                }
                catch (Exception ex)
                {
                    cacheDir = null;
                    Log($"[缓存] 磁盘缓存目录初始化失败（本次会话不落盘）：{ex.Message}");
                }
            }
            var proxy = new QemuStreamProxy(_mediaPort, s.PlayUrlPath, s.PickSize,
                QemuStreamProxy.ContentTypeFor(s.PickName), Log, cacheDir, _runtime?.BlockStore);
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
            _monitorPort = PickFreePort(_monitorPort);
            // 数据面块设备镜像：每实例一张（多实例=播放/下载各一，互不干扰）
            string? blkPath = null;
            try
            {
                if (!string.IsNullOrEmpty(BlockDeviceRoot))
                {
                    var tag = string.IsNullOrEmpty(_consoleTag) ? "main" : _consoleTag.Trim('-');
                    blkPath = Path.Combine(BlockDeviceRoot!, $"store{tag}.img");
                }
            }
            catch (Exception ex)
            {
                Log($"[qemu] 数据面镜像路径无效（退化为纯 HTTP 通道）：{ex.Message}");
                blkPath = null;
            }
            _runtime = new QemuHostRuntime(_runtimeDir, _mediaPort, Log, _initrdName, _consoleTag, _monitorPort,
                blkPath, blkPath is null ? 0 : BlockDeviceCapacityBytes);
            _server.ResetFirstPoll();
            if (!await _runtime.StartAsync(ct).ConfigureAwait(false)) return false;

            var ready = await _server.WaitFirstPollAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(false);
            if (!ready)
            {
                Log("VM 已启动但 90s 内 guest 未连上控制端");
                return false;
            }
            Log($"VM 就绪（媒体口 {_mediaPort}，monitor {_monitorPort}）");
        }
        else
        {
            // 上次退出播放页把 VM 冻住了：必须先唤醒，否则媒体口/控制口都不会应答
            ResumeVmLocked();
        }

        _lastActiveUtc = DateTime.UtcNow;
        _playbackIdleSinceUtc = null;
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

        // ① 等种子落盘：ev=torrent（原路径）与阶段一 ev=play（引擎 URL 路径）先到哪个算哪个。
        //   ⚠ 2026-09-17 压测实测真凶：guest 只在 poll_task() 里 stat 种子并上报，而 poll_task 是
        //   **5 秒节拍**（ctrlloop.c 的 `now - last_poll >= 5`）→ 每条磁力白等最多 5s，9 条磁力 =
        //   40s，占「海报→起播」65s 的 69%（第 1 条因 last_poll=0 立即命中，只花 0.3s —— 这正说明
        //   瓶颈不是引擎解析而是上报节拍）。种子落盘路径是**确定**的（guest 侧
        //   `snprintf(g_mag_torrent, "%s/%s", EMU_SAVE_PATH, name)`，即 /thunder-data/<任务名>），
        //   故宿主直接向媒体口探取，命中即走（~0.25s/轮）；探不到仍回落 guest 上报，行为不回退。
        //   ⚠ 直取可能拿到**正在写**的种子（bencode 半截）→ 解析失败必须继续等，不能当终局失败，
        //   否则比原逻辑更糟，故把 ParseTorrent 放进等待循环里，只有解析成功才结束等待。
        var directTorrentPath = SynthesizeUrlPath("/thunder-data/" + s.Name);
        (List<TorrentEntry> Files, string Name)? parsed = null;
        // ★ 自适应超时（原固定 120s）：种子通常 0.2~3s 就落盘（实测），而**死链磁力**会一路
        //   空等到 120s —— 用户看到的是「选集栏转了 2 分钟然后啥也没有」。改为：
        //   前 15s 高频轮询（正常磁力都在这段内落盘），之后退到 1s 一次，总上限 45s。
        //   15s 内没动静基本可判死链（引擎拿不到任何节点），早失败早提示。
        var startUtc = DateTime.UtcNow;
        var hardDeadline = startUtc + TimeSpan.FromSeconds(45);
        var softDeadline = startUtc + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < hardDeadline)
        {
            if (s.Cancelled) return null;
            if (s.LastError is not null) { Log(s.LastError); _session = null; return null; }

            var data = await TryFetchTorrentQuietAsync(directTorrentPath, ct).ConfigureAwait(false);
            if (data is null && (s.TorrentRawPath.Length > 0 || s.TorrentUrlPath.Length > 0))
            {
                // 回落：guest 上报的官方路径（原逻辑）
                var reportPath = s.TorrentUrlPath.Length > 0
                    ? s.TorrentUrlPath
                    : SynthesizeUrlPath(s.TorrentRawPath);
                data = await FetchTorrentAsync(reportPath, ct).ConfigureAwait(false);
            }
            if (data is not null)
            {
                try
                {
                    var (entries, tname) = Bencode.ParseTorrent(data);
                    if (entries.Count > 0) { parsed = (entries, tname); break; }   // 半截种子会抛/空 → 继续等
                }
                catch { /* 种子仍在写：解析失败不算失败，继续等 */ }
            }
            // 慢档：15s 后降频，避免长尾死链把控制口刷满
            var delayMs = DateTime.UtcNow < softDeadline ? 250 : 1000;
            try { await Task.Delay(delayMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }
        }
        if (parsed is null)
        {
            Log($"等种子落盘超时（{(DateTime.UtcNow - startUtc).TotalSeconds:F0}s，疑似死链或无资源）");
            _session = null;
            return null;
        }

        {
            var (entries, tname) = parsed.Value;
            s.Files = entries.Select(e => new MagnetFile(e.Index, e.Size, e.Rel)).ToList();
            s.AtUtc = DateTime.UtcNow;
            _history[magnet] = s;   // 进程级历史：重复点播/换集不再重复建任务（9128）
            Log($"文件列表 {entries.Count} 项（种子名 {tname}）");
            foreach (var f in s.Files.Take(12)) Log($"  #{f.Index,3}  {f.Size / 1048576.0,8:F1}MB  {f.Name}");
            if (s.Files.Count > 12) Log($"  … 共 {s.Files.Count} 项");
            return s;
        }
    }

    private async Task<byte[]?> FetchTorrentAsync(string path, CancellationToken ct)
    {
        var (code, body) = await FetchAsync(path, 0, ct).ConfigureAwait(false);
        if (body.Length >= 100 && body[0] == (byte)'d') return body;
        Log($"种子内容异常（HTTP={code} len={body.Length} path={path}）");
        return null;
    }

    /// <summary>静默探测种子是否已落盘（不写日志）：成功返回 bencode 字节，未就绪/不是种子返回 null。
    /// <para>用于绕开 guest 的 5s 上报节拍 —— 媒体口对不存在的文件**快速 404**（实测 215ms），
    /// 可安全高频轮询；失败不打日志，避免每 250ms 刷屏。</para></summary>
    private async Task<byte[]?> TryFetchTorrentQuietAsync(string path, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, MediaUrl(path));
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)resp.StatusCode is not (200 or 206)) return null;
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buf = new byte[4 * 1024 * 1024];
            var off = 0;
            int n;
            while (off < buf.Length && (n = await stream.ReadAsync(buf.AsMemory(off, buf.Length - off), ct).ConfigureAwait(false)) > 0)
                off += n;
            if (off < 100 || buf[0] != (byte)'d') return null;   // 不是 bencode（种子还没写完/已是媒体文件）
            var body = new byte[off];
            Buffer.BlockCopy(buf, 0, body, 0, off);
            return body;
        }
        catch { return null; }
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
                // ⚠ 2026-09-17 压测实测：死亡不一定报 st=3。tmpfs 写满（err=114011）报 st=3、有恢复；
                //   而任务创建期失败（err=114009）报的是 **st=4** 且 err≠0，此前不满足 `== 3` →
                //   完全不进恢复循环，日志里 st=4 err=114009 一直刷到测试结束（引擎永久死锁）。
                //   st=4 与 st=3 同属「带错误码的死亡」，都必须触发重建。
                if (r.St is 3 or 4 && r.Err != 0 && s.DlSent && !s.Cancelled && s.Played)
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
            if (!s.Cancelled && s.RetryCount >= MaxTaskRetries && s.LastSt is 3 or 4)
            {
                // ── 最后一招：VM 级恢复（重启 guest） ──
                //
                // 实测（2026-09-20 hosttest download）：任务在 45% 处以 err=114010 死亡后，
                // 引擎里该 btih 的任务句柄**清不掉** —— guest 侧重发 DL 五轮全部撞 9128，
                // 手工 stopTask 也无济于事。此时唯一可靠的办法是重启 guest（任务表随进程清空）。
                // 代价：丢 tmpfs 里未导出的进度（已落宿主磁盘缓存的块仍在，重下走磁盘命中）。
                _ = Task.Run(async () =>
                {
                    try
                    {
                        Log("任务自动恢复次数用尽 → 尝试 VM 级恢复（重启 guest 清空引擎任务表）");
                        _history.TryRemove(s.Magnet, out _);
                        try { _server?.SetCommand("STOP"); } catch { }
                        try { _runtime?.Stop(); } catch { }
                        _runtime = null;
                        _session = null;
                        s.Cancelled = true;   // 打断下载主循环的等待，让它重新走一轮

                        await _gate.WaitAsync().ConfigureAwait(false);
                        try
                        {
                            if (_disposed) return;
                            if (!await EnsureStartedLockedAsync(CancellationToken.None).ConfigureAwait(false))
                            { Log("VM 级恢复失败：guest 起不来"); return; }
                            Log("guest 已重启，引擎任务表已清空（下载可重新发起）");
                        }
                        finally { _gate.Release(); }
                    }
                    catch (Exception ex) { Log($"VM 级恢复异常：{ex.GetType().Name}: {ex.Message}"); }
                });
            }
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

    /// <summary>单次取字节（会话复用前的活性探测）。⚠ 上限 3s：这里的结论只影响日志文案，
    /// 不该让换集流程干等媒体口 30s（未下载到的新集会让请求一直挂着 → 实测每次换集卡 30s）。</summary>
    private async Task<long> VerifyOnceAsync(string path, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var (code, data) = await FetchAsync(path, 64 * 1024, cts.Token).ConfigureAwait(false);
            return code is 200 or 206 ? data.Length : 0;
        }
        catch (OperationCanceledException) { return 0; }
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

            // ① 冻结态（用户已退出播放页）：下载已停，但 5GB 内存还占着 → 15 分钟后收机器。
            //   冻结期间 guest 不再上报，_lastActiveUtc 自然停止刷新，窗口能真正走完。
            if (_vmPaused)
            {
                if (DateTime.UtcNow - _lastActiveUtc < TimeSpan.FromMinutes(15)) return;
                Teardown("冻结已满 15 分钟（回收 5GB 内存）");
                return;
            }

            // ② 播放会话：以「缓存代理还有没有读者」为准 —— 宿主看不到播放器本身，有没有人在读
            //   是最接近的事实（暂停时读者仍挂着，只有页面退出/播放器断开才会清零）。
            //   ⚠ 旧代码这里是 `if (_session is { Played: true }) return;`，等于给 VM 发了永久免死金牌：
            //   而 OnReport **每 5 秒**刷新一次 _lastActiveUtc（guest 只要任务还在就报 status，
            //   下载中每次都有进度变化）→ 15 分钟空闲窗口永远走不完 → VM 永不回收、
            //   迅雷一路下载。这是「退出播放页后视频还在下载」的直接原因之一（2026-09-18 用户实测）。
            if (_session is { Played: true })
            {
                if (_streamProxy?.HasReaders == true)
                {
                    _lastActiveUtc = DateTime.UtcNow;
                    _playbackIdleSinceUtc = null;
                    return;
                }
                // 无读者：给 5 分钟余量（播放器缓冲/短时重连），超时即认定播放已结束
                _playbackIdleSinceUtc ??= DateTime.UtcNow;
                if (DateTime.UtcNow - _playbackIdleSinceUtc.Value < TimeSpan.FromMinutes(5)) return;
                Log("播放会话已连续 5 分钟无读者（页面退出钩子没走到？）→ 冻结下载");
                ReleaseProxy();
                if (_runtime.SetPaused(true)) { _vmPaused = true; _playbackIdleSinceUtc = null; }
                else Teardown("无读者且冻结不可用");
                return;
            }

            // ③ 其余情形（下载会话 / 只展开过文件列表）：沿用原 15 分钟规则。
            //   下载会话在下载中每 5 秒有 status 上报 → 会自然续命，行为不变。
            _playbackIdleSinceUtc = null;
            if (DateTime.UtcNow - _lastActiveUtc < TimeSpan.FromMinutes(15)) return;

            Log("空闲 15 分钟，停掉 QEMU 释放内存");
            Teardown("空闲 15 分钟");
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
