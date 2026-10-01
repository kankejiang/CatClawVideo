using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CatClawVideo.Core.Services.QemuGuest;

/// <summary>
/// ART guest：在 QEMU(TCG) 里启一套<b>真 Android ART</b>，桥（<c>bridge.GuestMain</c>）跑在 guest 内
/// 并监听端口，宿主经 slirp hostfwd 说<b>与桌面 JRE 完全相同的行协议</b>。
///
/// <para><b>为什么要它</b>（2026-09-25 定案 + 当天实测）：所有 ARM 原生码必须在真 ARM 运行时里执行 ——
/// Guard 壳 jar 的 <c>DexNative</c> 8 个 native、TVBox 解析侧的 5 个 arm64 .so 都是这个性质。
/// 而壳的 <c>assets/ftyguard_v8.so</c> 是 arm64 ELF，只有 ART 能给它真 JNI。
/// 实测（<c>D:\Code\.shot\p4run4.log</c>）：<c>load</c>=2662ms、<c>homeContent</c>=203ms、
/// <c>searchContent</c>=146ms，5/5 应答，0 个 UnsatisfiedLinkError。</para>
///
/// <para><b>这条链路可用时的连锁效果</b>：dex2jar / unpacker / <c>converted/*-java.jar</c> /
/// 手写 <c>DexNative</c> 替身 / 独立的 Guard VM 全部不再需要 —— 壳框架在 guest 里按真机的样子自己跑。</para>
///
/// <para>initrd 由 <c>JavaBridge/qemu-src/tools/mk_art_initrd.py</c> 造（含 API28 的 /system、
/// artlaunch、proppreload.so、gb.dex、tvbox.apk），文件名 <see cref="InitrdName"/>；
/// guest 侧桥端口从 kernel cmdline 的 <c>guardport=</c> 取（复用 QemuHostRuntime 现成的那条参数）。</para>
/// </summary>
public sealed class QemuArtGuest : IDisposable
{
    /// <summary>产品 initrd 名（放在 <c>QemuGuest</c> 目录下，与 pkg_initrd.gz 并存）。</summary>
    public const string InitrdName = "art_initrd.gz";

    /// <summary>guest 架构：Arm64（现网 TCG 调优组合）或 X86_64（WHPX mini guest，2026-09-27）。
    /// x86 模式的内核/initrd/引擎文件名由下方三个属性给出；缺省 Arm64 时现网行为零变化。</summary>
    public GuestArch GuestArch { get; set; } = GuestArch.Arm64;
    /// <summary>guest 内核文件名（x86 mini guest 为 Debian 6.1 vmlinuz，含 binder）。</summary>
    public string KernelFileName { get; set; } = "pkg_kernel";
    /// <summary>x86 模式的 initrd 文件名（aarch64 恒为 <see cref="InitrdName"/>）。</summary>
    public string GuestInitrdName { get; set; } = InitrdName;
    /// <summary>x86 模式的 QEMU 引擎文件名（与 aarch64 引擎共享 MSYS2 依赖 DLL）。</summary>
    public string GuestQemuExeName { get; set; } = "qemu-system-x86_64.exe";

    /// <summary>guest 里 ART 桥的监听端口基准（与 Guard VM 的 18481、迅雷的 18080/18090 错开）。</summary>
    private const int PortSeed = 18600;

    private readonly string _runtimeDir;
    private readonly Action<string>? _log;
    // ⚠ 连接全程互斥（不能用 lock：里面要 await）。此前只在「创建 _vm」一小段加锁，
    //   StartAsync 与探针循环都在锁外 —— 两个并发调用（后台预热 + 用户首次调用）会双双连上
    //   同一 guest 桥，_stdin/_stdout 被后者覆盖、两个 ReadLoop 抢同一条流，请求应答被错分，
    //   表现为「Java 桥响应超时（90s，id=N）」（2026-09-26 Debug 日志实锤，日志里还能看到
    //   18600/18603 两台 VM 并存的痕迹）。整体串行后，后来者进来时前者已就绪，直接复用。
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    private QemuHostRuntime? _vm;
    private ArtDnsServer? _dns;   // guest 的域名解析靠它（没有 netd，bionic 自己一台服务器都拿不到）
    private TcpClient? _sock;
    private StreamWriter? _w;
    private StreamReader? _r;

    /// <summary>本 guest 的桥端口（未启动为 0）。日志与排障用。</summary>
    public int BridgePort { get; private set; }

    /// <summary>guest 里爬虫自带 /proxy 服务的端口（桥的 Art.serveProxy 绑的就是它，TVBox 惯例）。</summary>
    public const int GuestProxyPort = 9978;

    /// <summary>宿主→guest /proxy 隧道的宿主端口（0 = 没开成）。见 <see cref="QemuHostRuntime.ProxyTunnel"/>。</summary>
    public int ProxyTunnelPort { get; private set; }

    /// <summary>B1.0：宿主侧 adb 隧道端口（0 = 未开）。guest 内 adbd 由桥拉起并监听 5555，
    /// 宿主用 <c>adb connect 127.0.0.1:&lt;该端口&gt;</c> 直连进去排障。</summary>
    public int AdbTunnelPort { get; private set; }

    /// <summary>把爬虫写的 guest 地址换成宿主隧道地址；没开隧道时原样返回。</summary>
    public string? ProxyBase => ProxyTunnelPort > 0 ? $"http://127.0.0.1:{ProxyTunnelPort}" : null;

    // ── 迅雷引擎合并（2026-09-27，docs 交接 §6.9）────────────────────────────────
    /// <summary>合并模式：本 VM 用「ART+迅雷」合并 initrd（<see cref="MergedInitrdName"/>），
    /// 桥与迅雷 harness 同 guest 跑。启用后额外：挂数据面（store-art.img→/dev/vda）与
    /// 交换区（swap-art.img→/dev/vdb）块设备；cmdline 增 <c>thunderport=</c>——合并 initrd
    /// 的 /init「迅雷段」据此拉起 harness 回连宿主该端口。宿主侧 QemuThunderEngine 切
    /// 「外部 VM 模式」复用本 VM（不再自起第二个 QEMU，省 ~2.5GB RAM 与一次内核启动）。</summary>
    public bool ThunderMerged { get; set; }

    /// <summary>合并模式下迅雷 harness 的控制口（回连宿主；与 QemuThunderEngine 的控制服务器同号）。</summary>
    public int ThunderPort { get; set; }

    /// <summary>块设备镜像目录（与 QemuThunderEngine.BlockDeviceRoot 同目录约定）。
    /// 合并模式非空时挂 <c>store-art.img</c> + <c>swap-art.img</c>（稀疏，实际只占写入量）。</summary>
    public string? BlockDeviceRoot { get; set; }

    /// <summary>数据面块设备容量（合并模式）。</summary>
    public long BlockDeviceCapacityBytes { get; set; } = 64L * 1024 * 1024 * 1024;

    /// <summary>交换区容量（合并模式；0 = 不挂）。</summary>
    public long SwapDeviceCapacityBytes { get; set; } = 6L * 1024 * 1024 * 1024;

    /// <summary>合并模式使用的 initrd 文件名。</summary>
    public string MergedInitrdName { get; set; } = "art_initrd_merged.gz";

    public QemuArtGuest(string runtimeDir, Action<string>? log = null)
    {
        _runtimeDir = runtimeDir;
        _log = log;
        // 进程退出兜底停 VM。⚠ 存成字段并在 Dispose 里摘掉：桥超时重置会反复 new 本类，
        // 用 lambda 直接挂就是每次重置漏一个订阅（且会去 Dispose 一个已经废掉的实例）。
        _onExit = (_, _) => Dispose();
        AppDomain.CurrentDomain.ProcessExit += _onExit;
    }

    private readonly EventHandler? _onExit;

    /// <summary>已 Dispose 标志：Dispose 后的实例一律禁止再连（防「僵尸复活」）。
    /// 背景（2026-09-27 多 qemu 事故）：重置窗口期的竞态调用者曾拿到已判死的实例引用，
    /// 经 ConnectAsync→StartAsync 把已 Stop 的 QemuHostRuntime 重新拉起——每次重置叠一个
    /// 孤儿 qemu。Dispose 先置本标志 + 下游 QemuHostRuntime 生命周期串行，双保险。</summary>
    private volatile bool _disposed;

    private void Log(string m) => _log?.Invoke("[art-vm] " + m);

    /// <summary>ART 运行时是否已部署（缺 art_initrd.gz 时整条 ART 路静默不启用）。</summary>
    public static bool IsAvailable(string runtimeDir) => QemuHostRuntime.IsPresent(runtimeDir, InitrdName);

    public bool IsUp => _sock is { Connected: true } && _vm is { IsRunning: true };

    /// <summary>
    /// 起 VM 并连上桥，返回桥的行列式传输流（宿主侧写请求 / 读响应）。
    /// 失败返回 null —— 调用方据此回落（宿主 JRE 那条路仍在）。
    /// </summary>
    public async Task<(StreamWriter Stdin, StreamReader Stdout)?> ConnectAsync(CancellationToken ct = default)
    {
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return null;   // 已收尾的实例禁止复活（并发/竞态引用兜底）
            if (IsUp && _w is not null && _r is not null) return (_w, _r);
            if (!await StartVmLockedAsync(ct).ConfigureAwait(false)) return null;
        }
        finally { _connectGate.Release(); }

        // 探针循环不占 gate：VM 起来后等就绪可能要几分钟，握着会饿死其他等待者
        // （此刻 IsUp 仍 false，其他 ConnectAsync 调用会重进 StartAsync —— StartAsync 对
        //   已运行进程直接返回 true，然后同样进入探针循环，两条探针各自探测、谁先通谁设流，
        //   仍是并发覆盖。所以真正的互斥必须在「设流」这一步，见下方 lock）。

        // ⚠ 不能把「TCP 连上了」当成「桥就绪」：slirp 自己就把三次握手做掉，guest 里还没 listen
        //   也一样 connect 成功（实测：1.5s 就"连上"，随后 ping 15s 超时）。
        //   所以这里用一次真的 ping 当就绪探针，不通就换一条连接重来。
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(4);   // 冷启动：解 226MB initrd + ART 起 VM
        // _disposed 进入条件：实例被并发收尾时探针立即退出返回 null，不再傻等满 4 分钟
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested && !_disposed)
        {
            TcpClient? c = null;
            try
            {
                c = new TcpClient { NoDelay = true };
                using var connCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connCts.CancelAfter(3000);
                await c.ConnectAsync(IPAddress.Loopback, BridgePort, connCts.Token).ConfigureAwait(false);
                var ns = c.GetStream();
                var w = new StreamWriter(ns, new UTF8Encoding(false)) { AutoFlush = true };
                var r = new StreamReader(ns, Encoding.UTF8);
                await w.WriteLineAsync("{\"id\":0,\"op\":\"ping\"}").ConfigureAwait(false);
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(4000);
                var line = await WaitForLineAsync(r, readCts.Token).ConfigureAwait(false);
                if (line is not null && line.Contains("\"ok\""))
                {
                    // 设流必须与「检查 IsUp」互斥：两条并发探针都通了时，只能有一条拿到所有权，
                    // 另一条把连接作废重试 —— 否则两条流同时写桥、两个读循环分吃应答。
                    bool owns;
                    lock (_ownership)
                    {
                        if (IsUp && _w is not null && _r is not null)
                        {
                            owns = false;
                            try { c.Dispose(); } catch { }
                        }
                        else
                        {
                            _sock = c; _w = w; _r = r;
                            owns = true;
                            // ⚠ 所有权已移交 _sock，必须置空 c：否则 return 触发 finally 的
                            //   c?.Dispose() 把桥连接杀掉 → 全部调用「Cannot access a disposed
                            //   object」→ 站点整排「拉取失败」（2026-09-26 实测回归，原代码
                            //   就有 c=null 这行，改并发仲裁时弄丢了）。
                            c = null;
                        }
                    }
                    if (owns)
                    {
                        Log($"guest 桥就绪 127.0.0.1:{BridgePort}（探针应答 {line}）");
                        return (_w, _r);
                    }
                    Log("guest 桥已被并发调用方接管，本探针让位重试");
                    continue;
                }
            }
            catch { /* 没通：下面重连 */ }
            finally { try { c?.Dispose(); } catch { } }
            await Task.Delay(2000, ct).ConfigureAwait(false);
        }
        Log($"等不到 guest 桥（端口 {BridgePort}，超时）");
        return null;
    }

    /// <summary>桥连接的所有权（_sock/_w/_r 三个字段的写入互斥）。</summary>
    private readonly object _ownership = new();

    /// <summary>创建（若未建）+ 启动 VM。持 <see cref="_connectGate"/> 调用。
    /// <para>ConnectAsync（桥会话）与 <see cref="EnsureVmRunningAsync"/>（合并模式迅雷引擎）
    /// 共用：后者只要 QEMU 进程起来——harness 由合并 initrd 的迅雷段拉起并回连宿主控制口，
    /// 与本类的桥就绪探针互不依赖（TCG 下桥就绪要几分钟，磁力任务不该等）。</para></summary>
    private async Task<bool> StartVmLockedAsync(CancellationToken ct)
    {
        // 三件套校验按架构取文件名（x86 mini guest：Debian 内核 + x86 引擎 + art_initrd_x64.gz）
        var qemuExe = GuestArch == GuestArch.X86_64 ? GuestQemuExeName : "qemu-system-aarch64.exe";
        // ⚠ 架构优先于合并：merged initrd 是 aarch64 专属（x86 的迅雷走 qemu-aarch64-static
        //   转译路线、资产独立）——2026-09-27 实测「合并优先」会让 x86 内核拿到 aarch64
        //   rootfs（busybox ENOEXEC）→ kernel panic "No working init found"。
        var initrdFile = GuestArch == GuestArch.X86_64
            ? GuestInitrdName
            : ThunderMerged ? MergedInitrdName : InitrdName;
        var kernelFile = KernelFileName;
        var missing = new[] { qemuExe, kernelFile, initrdFile }
            .Where(f => !File.Exists(Path.Combine(_runtimeDir, f))).ToList();
        if (missing.Count > 0) { Log("运行时不齐全，缺：" + string.Join(", ", missing)); return false; }

        if (_vm is null)
        {
            var bridge = PickFreePort(PortSeed);
            var media = PickFreePort(bridge + 1);       // QemuHostRuntime 总要一条 -:20080 的 hostfwd，别撞号
            // 爬虫的播放地址写的是它自己那个 /proxy（guest 里的 9978），给它开一条宿主隧道。
            var tunnel = PickFreePort(media + 1);
            // B1.0（2026-10-01）：guest 里 adbd（5555）的宿主隧道 —— 排障用 `adb connect 127.0.0.1:<port>`。
            // adbd 由 guest 内的桥（Java）拉起（见 Server.startAdbd），这里只做端口映射；
            // 想关掉就设环境变量 CATCLAW_ART_ADB=0。
            var adbPort = Environment.GetEnvironmentVariable("CATCLAW_ART_ADB") == "0"
                ? 0 : PickFreePort(tunnel + 1);
            if (bridge == 0 || media == 0) { Log("找不到可用端口"); return false; }
            BridgePort = bridge;
            ProxyTunnelPort = tunnel;
            AdbTunnelPort = adbPort;
            _dns ??= new ArtDnsServer(_log);      // guest 里所有 Java 域名解析都问到这（见 ArtDnsServer 注释）
            // 合并模式：数据面/交换区镜像（与 QemuThunderEngine 同目录约定，文件名带 -art 区分）
            string? blkPath = null, swapPath = null;
            if (ThunderMerged && !string.IsNullOrEmpty(BlockDeviceRoot))
            {
                try
                {
                    blkPath = Path.Combine(BlockDeviceRoot!, "store-art.img");
                    if (SwapDeviceCapacityBytes > 0)
                        swapPath = Path.Combine(BlockDeviceRoot!, "swap-art.img");
                }
                catch (Exception ex)
                {
                    Log("合并模式镜像路径无效（退纯 HTTP 通道）：" + ex.Message);
                    blkPath = null; swapPath = null;
                }
            }
            _vm = new QemuHostRuntime(_runtimeDir, media, _log, initrdFile, consoleLogTag: "-art",
                    monitorPort: 0, ctrlPort: _dns.Port, guardPort: bridge, magnetOverride: "none",
                    blockImagePath: blkPath, blockImageBytes: blkPath is null ? 0 : BlockDeviceCapacityBytes,
                    swapImagePath: swapPath, swapImageBytes: swapPath is null ? 0 : SwapDeviceCapacityBytes,
                    thunderPort: ThunderMerged ? ThunderPort : 0, adbPort: adbPort)
            {
                Arch = GuestArch,
                QemuExeName = qemuExe,
                KernelName = kernelFile,
                // 实测：2048MB 够 ART + 桥 + 一个源（TCG 下 -smp>4 反而更慢，见 QemuHostRuntime 注释）。
                // vCPU 2 → 4（2026-09-26）：桥已 per-site 并行（4 线程池），聚合网盘源的 detail
                // 里几十次 TLS 握手在 TCG 下是纯 CPU 计算，多核能让它们真并行；TCG 实测吞吐峰值
                // 在 2~4 vCPU（docs/qemu-tcg-tuning.md §6，>4 反而更慢），4 是上限取值。
                // 合并模式要同时扛「桥 + 迅雷引擎」，内存上调（有 swap 时冷页可换出）
                GuestMemoryMb = ThunderMerged ? 3072 : 2048,
                // vCPU 按虚拟化方式分（2026-09-27 用户提出 x86 方案应吃更多核）：
                // · aarch64 = TCG 软件模拟：2~4 峰值、>4 反而更慢（翻译块缓存与翻译锁全局共享，
                //   12 vCPU 实测 0.78×）→ 保持 4，弱机按宿主核数收缩（QemuHostRuntime 默认语义）。
                // · x86 = WHPX 硬件虚拟化：vCPU 是真宿主线程、真并行，TCG 的上限理由全部不成立
                //   → 给宿主逻辑核的一半（留核给宿主侧代理/SLIRP/应用本体），下限 4 上限 12。
                //   ⚠ WHPX 不可用时 QEMU 自动落 TCG，此时该值偏大（TCG 多 vCPU 反噬）——
                //   x86 guest 目前仅 CATCLAW_X86_GUEST=1 实验开关启用，验收时如遇无 WHPX
                //   机器再按需降。
                SmpCount = GuestArch == GuestArch.X86_64
                    ? Math.Clamp(Environment.ProcessorCount / 2, 4, 12)
                    : Math.Clamp(Environment.ProcessorCount, 1, 4),
                // ⚠ aarch64 必须是 virtio-net-device：ART initrd 只 insmod virtio_mmio+virtio_net，
                //   用 PCI 版 guest 里没有 eth0，hostfwd 永远连不上（实测踩过）。
                //   x86（q35）走 PCI：virtio-net-pci + Debian 内核模块链（见 mk_x86_initrd.sh）。
                NetDevice = GuestArch == GuestArch.X86_64
                    ? "virtio-net-pci,netdev=n0"
                    : "virtio-net-device,netdev=n0",
                ProxyTunnel = tunnel > 0 ? (tunnel, GuestProxyPort) : null,
            };
            // 合并模式：给迅雷引擎建租约（媒体口 / 数据盘 / swap 全租用；VM 生命周期仍归本类）。
            // Died 转发：QemuThunderEngine 借它感知「桥侧把 VM 收走了」（桥重置会连带杀迅雷会话）。
            if (ThunderMerged)
            {
                Lease = new ThunderLease(this, media, _vm.BlockStore, _vm.SwapStore);
                _vm.Died += () => Lease?.NotifyDied();
            }
        }
        if (!await _vm!.StartAsync(ct).ConfigureAwait(false)) { Log("QEMU 启动失败"); return false; }
        return true;
    }

    /// <summary>只确保 QEMU 进程起来（不等桥就绪）——合并模式迅雷引擎用：harness 由合并
    /// initrd 的迅雷段拉起并回连宿主控制口，与桥会话互不依赖。幂等；失败返回 false。</summary>
    public async Task<bool> EnsureVmRunningAsync(CancellationToken ct = default)
    {
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return false;
            if (_vm is { IsRunning: true }) return true;
            return await StartVmLockedAsync(ct).ConfigureAwait(false);
        }
        finally { _connectGate.Release(); }
    }

    /// <summary>QEMU 进程是否在跑（外部租约判活用）。</summary>
    public bool IsVmRunning => _vm is { IsRunning: true };

    /// <summary>合并模式给迅雷引擎的租约（VM 未创建为 null；见 <see cref="ThunderLease"/>）。</summary>
    public ThunderLease? Lease { get; private set; }

    /// <summary>
    /// 合并模式的外借租约：<c>QemuGuestEngine</c>「外部 VM 模式」借本 VM 跑迅雷 harness ——
    /// 不自起第二个 QEMU，媒体口（guest 代理 20080 的宿主 hostfwd）/ 数据面块设备 / 交换区
    /// 全租用（省 ~2.5GB RAM 与一次内核启动）。VM 生命周期仍归本类：桥重置会连带杀迅雷
    /// 会话（<see cref="Died"/> 通知），阶段一接受该耦合（docs 交接 §6.9）。
    /// </summary>
    public sealed class ThunderLease
    {
        internal ThunderLease(QemuArtGuest owner, int mediaPort, SparseBlockStore? blockStore, SparseBlockStore? swapStore)
        {
            Owner = owner;
            MediaPort = mediaPort;
            BlockStore = blockStore;
            SwapStore = swapStore;
        }

        /// <summary>租出 VM 的 ART guest（判活/日志用）。</summary>
        public QemuArtGuest Owner { get; }

        /// <summary>媒体口（宿主的 hostfwd → guest 20080，迅雷 harness 的代理就在那）。</summary>
        public int MediaPort { get; }

        /// <summary>数据面块设备（guest /dev/vdX ↔ 宿主 store-art.img 同一物理文件）。</summary>
        public SparseBlockStore? BlockStore { get; }

        /// <summary>交换区（guest mkswap/swapon 的那块；无 = 纯内存 tmpfs）。</summary>
        public SparseBlockStore? SwapStore { get; }

        /// <summary>VM 是否还在跑。</summary>
        public bool IsRunning => Owner.IsVmRunning;

        /// <summary>VM 退出（含桥重置收走）——迅雷引擎据此结束会话。</summary>
        public event Action? Died;

        internal void NotifyDied() => Died?.Invoke();
    }

    private static async Task<string?> WaitForLineAsync(StreamReader r, CancellationToken ct)
    {
        var readTask = r.ReadLineAsync();
        var finished = await Task.WhenAny(readTask, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
        return finished == readTask ? await readTask.ConfigureAwait(false) : null;
    }

    /// <summary>收尾：关掉桥连接并停 VM。VM 平时靠 KillOnClose job 兜底，这里管主动退出。
    /// ⚠ _disposed 置位必须先于停 VM：在飞的 ConnectAsync 探针/后续调用见到标志即止。</summary>
    public void Dispose()
    {
        _disposed = true;
        if (_onExit is not null) AppDomain.CurrentDomain.ProcessExit -= _onExit;
        try { _sock?.Close(); } catch { }
        _sock = null; _w = null; _r = null;
        try { _vm?.Stop(); } catch { }
        try { _dns?.Dispose(); } catch { }
        _dns = null;
    }

    /// <summary>三件套里缺了哪几件（<c>IsPresent</c> 只回布尔，排障时要知道是哪件）。</summary>
    private string MissingPieces(string dir)
    {
        var need = new[] { "qemu-system-aarch64.exe", "pkg_kernel", InitrdName };
        var miss = need.Where(f => !File.Exists(Path.Combine(dir, f)));
        return string.Join(", ", miss);
    }

    private static int PickFreePort(int seed)
    {
        for (var p = seed; p < seed + 120; p++)
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, p);
                l.Start();
                l.Stop();
                return p;
            }
            catch (SocketException) { /* 占了，试下一个 */ }
        }
        return 0;
    }
}
