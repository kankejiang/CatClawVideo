using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;

namespace CatClawVideo.Core.Providers;

/// <summary>
/// jar/dex 爬虫运行时（桌面）：<b>桥只跑在 QEMU 的 ART guest 里</b> —— QEMU 跑真 Android 9 的
/// ART，桥与 TVBox/壳 jar 都在里面，ARM 原生码就地执行，行协议走 TCP
/// （2026-09-26 实测荐片┃多线全链路：detail 0.5s/4347B → player 1.8s 拿到真 m3u8）。
/// <para><b>JRE 全退役（2026-09-29）</b>：随包 jre 与「宿主 JRE 回落桥」一并撤除，宿主不再
/// 常驻任何 Java 进程，ART guest 是唯一运行时；<c>CATCLAW_NO_ART=1</c> 的语义从「切回落」
/// 变成「整体禁用 jar 爬虫链路」。宿主侧对 jar 只做两件事：下载（<see cref="FetchJarAsync"/>）
/// 与纯 .class jar 的 d8 预转换（<see cref="EnsureDexJarAsync"/>）。</para>
/// <para><b>Guard 加固包为什么必须在 guest 里解</b>：壳把真 spider dex 加密成
/// <c>assets/ftyshinidie.guard</c>，解密器是 ARM Android native（<c>assets/ftyguard_v8.so</c>，
/// <c>JNI_OnLoad</c> + <c>RegisterNatives</c> 注册 <c>DexNative</c> 的 8 个 native）。
/// x64 JVM 跑不了 ARM 指令；曾用 <b>unidbg 模拟 ARM64</b> 离线解壳，但它自建一套 Android 桩，
/// 结果"看着对而语义不同"（同一条 <c>DECRYPT 10232B</c> 出过两种结果），且随包多 33MB ——
/// <b>2026-09-26 退役</b>。现在由 guest 里的真 ART 让壳自己 <c>System.load()</c> 那个 so，
/// 明文 dex（<c>cache/sharedb/config.db</c>）我们从不接触。</para>
/// <para>认证预处理：ext global 含 username/password 而缺 token 时，自动向
/// {server}/api/auth/login 登录注入 token（小雅 AListSh 需要）。</para>
/// </summary>
public class JavaSpiderRuntime : ISpiderRuntime, ISpiderProxyRuntime, ISpiderActionRuntime, IDisposable, ISpiderLiveRuntime
{
    public string Id => "jvm-dex";

    /// <summary>
    /// **可写**工作目录 —— 必须与只读的程序目录（<c>bridge.jar</c> 所在，安装版是
    /// <c>C:\Program Files\CatClawVideo\JavaBridge</c>）分开：jar 下载产物与 d8 转换产物
    /// 都要落盘，写程序目录会抛 <c>UnauthorizedAccessException</c>。
    ///
    /// <para>2026-09-19 用户实测（安装版）：磁力/自带源拉取失败，报
    /// 「Access to the path 'C:\Program Files\CatClawVideo\JavaBridge\converted' is denied.」
    /// —— 开发机跑仓库目录（可写）从不触发，只有安装包才暴露。</para>
    ///
    /// <para>落在 <c>%APPDATA%\CatClawVideo\javabridge\</c>，与其余可写数据同一处
    /// （见 <see cref="AppPaths"/>）。</para>
    /// </summary>
    private readonly string _workDir;

    /// <summary>jar 工作目录：下载的原始 spider jar 与 d8 预转换产物落这里（可写区）。</summary>
    private readonly string _convertedDir;

    private readonly Action<string>? _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private StreamWriter? _stdin;
    private StreamReader? _stdout;

    /// <summary>
    /// ART guest（QEMU 里真 Android ART 跑桥）；桥的唯一运行时。
    /// 桥的行协议走 TCP，<see cref="_stdin"/>/<see cref="_stdout"/> 架在 socket 流上，
    /// 请求/响应/事件分发都吃这一套。
    /// </summary>
    private CatClawVideo.Core.Services.QemuGuest.QemuArtGuest? _art;

    /// <summary>_art 懒创建的互斥：桥会话与迅雷合并（两个调用方）可能并发首建同一实例。</summary>
    private readonly object _artCreateLock = new();

    /// <summary>ART guest 的 jar 供给服务（guest 读不到宿主的盘，只能经 slirp 用 http 取）。</summary>
    private CatClawVideo.Core.Services.QemuGuest.ArtJarServer? _jarServer;
    private int _id;

    private readonly ConcurrentDictionary<string, bool> _loadedSites = new();
    private readonly ConcurrentDictionary<string, string> _convertedJars = new();

    /// <summary>最近装载成功的 Guard 家族站点键（<c>csp_*Guard</c>）。
    /// csp_*Guard 同 jar 家族的多个源共享壳内部的约定流服务端口（6678）——任何兄弟源在本源
    /// 之后被装载（跨源预取/手动浏览）都会把本源的流服务顶掉，本源 playerContent 返回的 6678
    /// 地址全 0B（handler NPE，「unexpected end of stream」）。播放前用它判断本源是否需要
    /// force 重装载抢回端口（2026-09-27 实锤，见 docs 交接 §6.8）。</summary>
    private string? _lastGuardSiteKey;

    /// <summary>
    /// 站点 → 改用替代 jar 后的类名（去掉 <c>Guard</c> 后缀）。
    /// <para>Guard 外壳的类名都带 <c>Guard</c> 后缀（<c>DouDouGuard</c> / <c>SixVGuard</c>），
    /// 而解壳出来的真实 dex 里**不带**后缀（<c>DouDou</c> / <c>SixV</c>），桥必须按真实名加载。
    /// guest 解壳与换非 Guard 同族 jar 两条路的映射规则一致。</para>
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _nonGuardClass = new();

    public bool IsSupported { get; }

    /// <summary>QEMU 运行时目录（<c>art_initrd.gz</c> 与 <c>pkg_kernel</c> 所在处）。</summary>
    public string ArtRuntimeDir { get; set; }

    /// <summary>
    /// 是否用 ART guest 跑桥。<b>默认：装了 <c>art_initrd.gz</c> 就开</b>（见构造函数注释）。
    /// <para>开着时 Guard 壳 jar 按真机的样子装载：ART 直接吃 <c>classes.dex</c>，
    /// 壳自己 <c>System.load()</c> 那个 arm64 <c>ftyguard_v8.so</c>，native 解出的真 dex 由
    /// 壳内部的 <c>DexClassLoader</c> 承载 —— 桥不再需要 dex2jar / 解壳 / 手写 DexNative 替身。</para>
    /// </summary>
    public bool ArtGuestMode { get; set; }

    // ── x86 mini guest（实验性，2026-09-27）——
    //    CATCLAW_X86_GUEST=1 时 ART guest 切到 x86_64 架构（Waydroid Android 13 子集 +
    //    Debian 6.1 内核 + WHPX 硬件加速），联调进行中（JavaBridge/qemu-src/tools/x86guest/）。
    //    ⚠ 默认关：aarch64 现网行为零变化；开关打开且 x86 运行时（QemuGuest\x86guest\
    //    下的内核/initrd + qemu-system-x86_64.exe）齐全时才生效，否则回落 aarch64。
    private CatClawVideo.Core.Services.QemuGuest.GuestArch? _guestArchOverride;
    private string? _guestKernelFile;
    private string? _guestInitrdFile;
    private string? _guestQemuExe;
    /// <summary>选中 x86 的原因（写进桥链路日志，排障不再猜）；null = 走 aarch64。</summary>
    private string? _x86Why;

    public JavaSpiderRuntime(string bridgeDir, Action<string>? log = null,
        string? workDir = null, Func<int>? proxyPort = null)
    {
        _log = log;
        _proxyPort = proxyPort;
        // 可写目录默认落用户数据区；显式传入只是为了测试/特殊部署。
        // ⚠ 绝不回落到 bridgeDir：安装版那是 Program Files，写它就是本次故障。
        _workDir = workDir ?? AppPaths.Sub("javabridge");
        _convertedDir = Path.Combine(_workDir, "converted");
        // ── ART guest（2026-09-25 定案；2026-09-29 起是**唯一**链路）——
        // ARM 原生码 + TVBox/壳 jar 的 dex 全进 QEMU 里的真 ART。宿主 JRE 回落桥退役后，
        // CATCLAW_NO_ART=1 的语义从「切回落」变成「整体禁用 jar 爬虫链路」（排障用）。
        ArtRuntimeDir = Path.Combine(AppContext.BaseDirectory, "QemuGuest");
        // 合并 initrd（art_initrd_merged.gz）= art_initrd.gz 的超集（桥 + 迅雷引擎同 guest，
        // 见 §6.9）：只部署了合并版时也走 ART 链路（桥用合并 initrd）。两者都在时由合并
        // 配置注入与否决定（QemuArtGuest.ThunderMerged 分支）。
        ArtGuestMode = (CatClawVideo.Core.Services.QemuGuest.QemuArtGuest.IsAvailable(ArtRuntimeDir)
                        || File.Exists(Path.Combine(ArtRuntimeDir, ThunderMergeInitrdName)))
                       && Environment.GetEnvironmentVariable("CATCLAW_NO_ART") != "1";
        // ── guest 架构路由（2026-09-29）──
        // 默认 aarch64 TCG（现网已验收）。x86_64 + WHPX 硬件虚拟化**显式 opt-in**：
        //   · CATCLAW_X86_GUEST=1 且 x86 三件套齐全 → x86（联调/尝鲜）
        //   · 其余一律 aarch64。
        // ⚠ 2026-09-29 真机验收实测：x86 WHPX 下桥就绪 3.0s（TCG 39s），Guard 壳
        //   load/home/detail 全通，**但玩偶 playerContent 撞 ART 13 verifier 硬拒**
        //   （ProxyOrigin.getan「同名 Drawable 寄存器冲突」，-Xverify:softfail/none 均
        //   无效——ART 12+ 移除了运行时禁用验证）。修复方向 = §6.10 的 boot 镜像 +
        //   预置 oat x86 化（aarch64 在 AOT 模式下同 jar 已验收通过）。转正待该卡点清零。
        // WhpxProbe 探测保留（后续「WHPX 不可用引导」用）。
        bool X86AssetsPresent() =>
            File.Exists(Path.Combine(ArtRuntimeDir, "qemu-system-x86_64.exe"))
            && File.Exists(Path.Combine(ArtRuntimeDir, @"x86guest\vmlinuz-6.1.0-50-amd64"))
            && File.Exists(Path.Combine(ArtRuntimeDir, @"x86guest\art_initrd_x64.gz"));
        if (Environment.GetEnvironmentVariable("CATCLAW_X86_GUEST") == "1")
        {
            _x86Why = "CATCLAW_X86_GUEST=1 显式启用（"
                + (X86AssetsPresent() ? "运行时齐全；注意：Guard 网盘源 playerContent 有 ART 13 verifier 卡点" : "⚠ 运行时缺失，起 VM 会失败回落") + "）";
            _guestArchOverride = CatClawVideo.Core.Services.QemuGuest.GuestArch.X86_64;
            _guestKernelFile = @"x86guest\vmlinuz-6.1.0-50-amd64";
            _guestInitrdFile = @"x86guest\art_initrd_x64.gz";
            _guestQemuExe = "qemu-system-x86_64.exe";
        }
        // 启动就把桥链路状态写进日志：链路差异只会以"某个站点不对"的形式浮现，
        // 不写明模式的话排障第一步会变成猜。
        var isX86 = _x86Why is not null;
        var initrdDesc = isX86
            ? @"x86guest\art_initrd_x64.gz"
            : File.Exists(Path.Combine(ArtRuntimeDir, ThunderMergeInitrdName))
                ? ThunderMergeInitrdName + "，含迅雷引擎"
                : CatClawVideo.Core.Services.QemuGuest.QemuArtGuest.InitrdName;
        Log(ArtGuestMode
            ? $"桥链路：ART guest（{(isX86 ? "x86_64 · " + _x86Why : "aarch64 · TCG 软件模拟")}；{initrdDesc}）"
            : "桥链路：未启用 —— " + (Environment.GetEnvironmentVariable("CATCLAW_NO_ART") == "1"
                ? "CATCLAW_NO_ART=1 显式禁用"
                : "缺 QemuGuest 运行时")
              + "；jar 爬虫链路不可用（宿主 JRE 回落桥已于 2026-09-29 退役）");
        // 桥可用 = bridge.jar + vendor/deps（JavaBridge 部署完整性检查；deps 里的 okhttp 等
        // 是 jar 爬虫的硬依赖）。Guard 解壳能力单独判定（2026-09-16 拆分：此前把 dex2jar 也
        // 算进来，缺它就把全部 jar 源判死 —— 用户实测 46 个源整体消失；dex2jar 现已随 JRE 桥退役）。
        IsSupported = File.Exists(Path.Combine(bridgeDir, "bridge.jar"))
                      && Directory.Exists(Path.Combine(bridgeDir, "vendor", "deps"));
        // 正常退出先走一次优雅收尾（VM 进程另有 KillOnClose job 兜底崩溃/被 kill 的情况）
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
        StartBridgeDebugPump();
    }

    /// <summary>
    /// 桥调试直通（2026-09-27 播放取证）：env <c>CATCLAW_BRIDGE_DEBUG=1</c> 时，后台轮询
    /// <c>&lt;workDir&gt;\bridge-debug-in.jsonl</c> 的新行（每行一个桥请求 JSON），经现有
    /// 桥会话发送，响应/异常追加进 bridge-debug-out.log。
    /// 为什么需要它（不能直接连桥）：GuestMain 是单连接串行模型（accept → Server.main 直到
    /// EOF），宿主应用占用唯一会话，外部客户端连上也只会干等；且此路复用宿主已装配的会话状态
    /// （玩偶已装载），可直接 call/fetch 取证壳的流服务（6678）等运行时事实。
    /// </summary>
    private void StartBridgeDebugPump()
    {
        if (Environment.GetEnvironmentVariable("CATCLAW_BRIDGE_DEBUG") != "1") return;
        var inPath = Path.Combine(_workDir, "bridge-debug-in.jsonl");
        var outPath = Path.Combine(_workDir, "bridge-debug-out.log");
        Log("[dbg] 桥调试直通已开启：" + inPath);
        _ = Task.Run(async () =>
        {
            int cursor = 0;
            // 启动时跳过已有行：in.jsonl 是跨会话的追加文件，从 0 重放会在启动后重跑全部
            // 历史请求（每个 1~5s），新请求排队尾 → 实际超时（2026-09-27 实测踩过）。
            try { if (File.Exists(inPath)) cursor = (await File.ReadAllLinesAsync(inPath)).Length; }
            catch { }
            while (true)
            {
                try
                {
                    await Task.Delay(2000).ConfigureAwait(false);
                    if (!File.Exists(inPath)) continue;
                    var lines = await File.ReadAllLinesAsync(inPath).ConfigureAwait(false);
                    while (cursor < lines.Length)
                    {
                        var t = lines[cursor].Trim();
                        if (t.Length == 0 || t.StartsWith("#")) { cursor++; continue; }
                        if (!IsBridgeReady) break;   // 桥未就绪：停在此行，下一轮重试
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        try
                        {
                            var req = System.Text.Json.Nodes.JsonNode.Parse(t)!.AsObject();
                            var resp = await RoundTripAsync(req, TimeSpan.FromSeconds(300), CancellationToken.None).ConfigureAwait(false);
                            sw.Stop();
                            await File.AppendAllTextAsync(outPath,
                                $"### {DateTime.Now:HH:mm:ss} <- {t}\n{sw.ElapsedMilliseconds}ms {resp.ToJsonString()}\n").ConfigureAwait(false);
                            Log($"[dbg] {t[..Math.Min(t.Length, 70)]} → {sw.ElapsedMilliseconds}ms");
                        }
                        catch (Exception ex)
                        {
                            await File.AppendAllTextAsync(outPath,
                                $"### {DateTime.Now:HH:mm:ss} <- {t}\nEX {ex.Message}\n").ConfigureAwait(false);
                            Log("[dbg] 失败: " + ex.Message);
                        }
                        cursor++;
                    }
                }
                catch { }
            }
        });
    }

    // ═══════════ 迅雷引擎合并（外部 VM 模式，2026-09-27，docs 交接 §6.9）═══════════

    /// <summary>合并模式配置（MauiProgram 装配时注入；非 null 且合并 initrd 在 = 迅雷引擎
    /// 走「外部 VM 模式」——不再自起第二个 QEMU，与爬虫桥同 guest）。</summary>
    public sealed record ThunderMergeConfig(int ControlPort, string? BlockDeviceRoot);

    /// <summary>迅雷合并配置；null = 不合并（迅雷引擎自起 VM，行为同合并前）。</summary>
    public ThunderMergeConfig? ThunderMerge { get; set; }

    /// <summary>合并 initrd 文件名（与 <c>QemuArtGuest.MergedInitrdName</c> 同约定）。</summary>
    public const string ThunderMergeInitrdName = "art_initrd_merged.gz";

    /// <summary>能否提供迅雷外部 VM（同步探针：ART 链路可用 + 配置已注入 + 合并 initrd 在）。
    /// 三缺一 → 迅雷引擎回落自起 VM 模式。
    /// ⚠ 架构门控：合并 initrd 是 aarch64 专属（x86 guest 的 initrd 无迅雷段，2026-09-27
    /// 定案时 x86 的迅雷走独立转译路线、资产未做）——x86 模式恒 false，让迅雷引擎自起
    /// aarch64 VM，否则会白等一个永远不会来的 harness 回连。</summary>
    public bool CanProvideThunderVm =>
        ArtGuestMode && ThunderMerge is not null
        && _guestArchOverride != CatClawVideo.Core.Services.QemuGuest.GuestArch.X86_64
        && File.Exists(Path.Combine(ArtRuntimeDir, ThunderMergeInitrdName));

    /// <summary>给迅雷引擎（<c>QemuGuestEngine</c> 外部 VM 模式）提供租约：确保 ART VM（合并
    /// initrd）起来并返回租约；不可用/起不来返回 null（引擎回落自起 VM 模式）。</summary>
    public async Task<CatClawVideo.Core.Services.QemuGuest.QemuArtGuest.ThunderLease?> EnsureThunderVmAsync(
        CancellationToken ct = default)
    {
        if (!CanProvideThunderVm) return null;
        CatClawVideo.Core.Services.QemuGuest.QemuArtGuest art;
        lock (_artCreateLock) { _art ??= CreateArtGuest(); art = _art; }
        // VM 已按「纯桥」配置在跑（上一次创建时合并配置还没注入？）：中途换 initrd/换盘不可能，
        // 本轮让位自起 VM 模式（合并配置只在建 VM 前生效）。
        if (art.IsVmRunning && !art.ThunderMerged)
        {
            Log("合并迅雷：ART VM 已按纯桥配置运行 —— 本次回自起 VM 模式（下次冷启动生效）");
            return null;
        }
        ApplyThunderMerge(art);
        if (!await art.EnsureVmRunningAsync(ct).ConfigureAwait(false))
        {
            Log("合并迅雷：ART VM 起不来（详见上文 [art-vm] 日志）");
            return null;
        }
        if (art.Lease is null) { Log("合并迅雷：VM 已起但租约未建（ThunderMerged 未生效？）"); return null; }
        Log($"合并迅雷：外部 VM 就绪（媒体口 {art.Lease.MediaPort}，harness 回连宿主控制口 {ThunderMerge!.ControlPort}）");
        return art.Lease;
    }

    /// <summary>把合并配置灌进 art（幂等；<see cref="CreateArtGuest"/> 与
    /// <see cref="EnsureThunderVmAsync"/> 共用——后者兜住「_art 建于配置注入之前」）。</summary>
    private void ApplyThunderMerge(CatClawVideo.Core.Services.QemuGuest.QemuArtGuest art)
    {
        if (ThunderMerge is null) return;
        art.ThunderMerged = true;
        art.ThunderPort = ThunderMerge.ControlPort;
        art.BlockDeviceRoot = ThunderMerge.BlockDeviceRoot;
    }

    /// <summary>创建 ART guest 实例（统一带架构路由与迅雷合并配置；懒创建唯一入口）。</summary>
    private CatClawVideo.Core.Services.QemuGuest.QemuArtGuest CreateArtGuest()
    {
        var art = new CatClawVideo.Core.Services.QemuGuest.QemuArtGuest(ArtRuntimeDir, _log)
        {
            // 架构路由（见构造函数）：x86_64（WHPX 默认/强制）或 aarch64（TCG 回落）
            GuestArch = _guestArchOverride ?? CatClawVideo.Core.Services.QemuGuest.GuestArch.Arm64,
            KernelFileName = _guestKernelFile ?? "pkg_kernel",
            GuestInitrdName = _guestInitrdFile ?? CatClawVideo.Core.Services.QemuGuest.QemuArtGuest.InitrdName,
            GuestQemuExeName = _guestQemuExe ?? "qemu-system-aarch64.exe",
        };
        // 合并配置只灌 aarch64（x86 initrd 无迅雷段，见 CanProvideThunderVm 门控注释）
        if ((_guestArchOverride ?? CatClawVideo.Core.Services.QemuGuest.GuestArch.Arm64)
            == CatClawVideo.Core.Services.QemuGuest.GuestArch.Arm64)
            ApplyThunderMerge(art);
        return art;
    }

    /// <summary>
    /// 收尾桥：发 <c>exit</c> 让它自己结束读循环，然后关 socket 并停 VM
    /// （VM 进程另有 KillOnClose job 兜底崩溃/被 kill 的情况）。幂等，可重复调用。
    /// </summary>
    public void Shutdown()
    {
        // ⚠ 先原子摘引用再销毁（2026-09-27 多 qemu 事故修复）：此前 Dispose 期间 _art
        //   仍是非 null，竞态窗口里并发调用者拿旧引用进 ConnectAsync → 旧 QemuHostRuntime
        //   被 StartAsync「复活」→ 每次重置叠一个孤儿 qemu（实锤：同一媒体口反复出现）。
        //   摘引 + 代际号自增建立不变量：已判死的会话实例永远不会被新调用者复用/复活。
        var art = Interlocked.Exchange(ref _art, null);
        var stdin = Interlocked.Exchange(ref _stdin, null);
        _stdout = null;
        if (art is null && stdin is null) return;
        try
        {
            // 收尾阶段不再有新请求进来了，直接写：拿 _stdinLock 反而可能在别的线程手里卡死。
            try { stdin?.Write("{\"op\":\"exit\"}"); stdin?.Flush(); } catch { }
        }
        catch { /* 桥可能已经自己断了 */ }
        finally
        {
            try { stdin?.Dispose(); } catch { }
            try { art?.Dispose(); } catch { }
        }
    }

    public void Dispose() => Shutdown();


    private void Log(string m) => _log?.Invoke("[jvm] " + m);

    /// <summary>宿主本地 SpiderProxyServer 主端口（懒访问器）；桥进程启动后经 setProxyPort 下发。</summary>
    private readonly Func<int>? _proxyPort;

    /// <summary>最近一次调用的站点（spider 发起的 /proxy 请求不携带 siteKey，回调时按它定位）。</summary>
    private volatile VodSiteInfo? _lastSite;

    /// <summary>
    /// 查找系统里的 java.exe，取**版本最高**的那个 —— <b>只服务纯 .class jar 的 d8 预转换</b>
    /// （d8.jar 来自 Android SDK build-tools，本就不随包；桥本身不再用宿主 Java）。
    /// <para>2026-09-29 JRE 全退役：随包 jre 已撤、桥只跑 ART guest，宿主不再常驻 Java 进程；
    /// 这里的系统 Java 只是跑 d8 转换工具的便利路径，没有 → 纯 .class jar 源在转换时给
    /// 明确报错（换用含 classes.dex 的 jar 源即可）。
    /// 扫描顺序：JAVA_HOME → <c>C:\Program Files\Java\*</c> → <c>C:\Program Files\Microsoft\jdk-*</c>
    /// → PATH，按目录名版本号取最大（本机常见「PATH 里 17、JAVA_HOME 里 21」）。</para>
    /// </summary>
    public static string? FindJavaExe()
    {
        var candidates = new List<(int Major, string Path)>();
        void Consider(string? p)
        {
            if (string.IsNullOrEmpty(p) || !File.Exists(p)) return;
            var dir = new DirectoryInfo(Path.GetDirectoryName(p)!);
            var m = Regex.Match(dir.Name, @"(\d+)");
            candidates.Add((m.Success ? int.Parse(m.Groups[1].Value) : 0, p));
        }

        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(home)) Consider(Path.Combine(home, "bin", "java.exe"));

        foreach (var root in new[] { @"C:\Program Files\Java", @"C:\Program Files\Microsoft", @"C:\Program Files\Android\openjdk" })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root))
                Consider(Path.Combine(dir, "bin", "java.exe"));
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var d in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { Consider(Path.Combine(d.Trim(), "java.exe")); }
            catch { }
        }

        return candidates.Count == 0
            ? null
            : candidates.OrderByDescending(c => c.Major).First().Path;
    }

    /// <summary>
    /// 向上查找 JavaBridge 目录（bridge.jar 所在，App 部署目录或仓库根）。
    /// <para>并列时取最近的（OrderByDescending 稳定排序，候选按由近到远收集）。
    /// 历史上还按 <c>vendor/dex2jar</c> 加权挑「能力最全」的候选（2026-09-16 实测随包副本
    /// 优先后全部 jar 站点转换失败）——dex2jar 已随 JRE 桥退役（2026-09-29），候选能力
    /// 重新同质，只看 deps 完整性与距离即可。</para>
    /// </summary>
    public static string? FindBridgeDir()
    {
        var candidates = new List<string>();
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && d.Parent != null; d = d.Parent)
        {
            var cand = Path.Combine(d.FullName, "JavaBridge");
            if (File.Exists(Path.Combine(cand, "bridge.jar"))) candidates.Add(cand);
        }
        if (candidates.Count == 0) return null;
        return candidates.OrderByDescending(Score).First();

        static int Score(string dir)
        {
            var s = 0;
            try
            {
                if (Directory.Exists(Path.Combine(dir, "vendor", "deps"))) s += 1;
            }
            catch { }
            return s;
        }
    }

    // ═══════════ ISpiderRuntime 协议 ═══════════

    public Task<string> HomeContentAsync(VodSiteInfo site, CancellationToken ct = default) =>
        CallAsync(site, "homeContent", new JsonArray(1), ct);

    public Task<string> CategoryContentAsync(VodSiteInfo site, string tid, string pg,
        IReadOnlyDictionary<string, string>? filter = null, CancellationToken ct = default) =>
        // 桥侧 Server.java 读第 3 参：extend 非空 → filter=true。Android 的 DexSpiderRuntime 同语义。
        CallAsync(site, "categoryContent", CategoryArgs(tid, pg, filter), ct);

    /// <summary>categoryContent 的参数数组：[tid, pg, {筛选键→值}]（无筛选时给空对象）。</summary>
    static JsonArray CategoryArgs(string tid, string pg, IReadOnlyDictionary<string, string>? filter)
    {
        var map = new JsonObject();
        if (filter is not null)
            foreach (var (k, v) in filter) map[k] = v;
        return new JsonArray(tid, pg, map);
    }

    // detailContent 会话级缓存：聚合网盘源的 detail 要串行探测多个网盘（实测玩偶 71~90s+），
    // 重进详情页/重试绝不该再付一遍。键 = site|vodId；成功结果才缓存，上限 40 条超出清空。
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _detailCache =
        new(StringComparer.Ordinal);

    public async Task<string> DetailContentAsync(VodSiteInfo site, string id, CancellationToken ct = default)
    {
        var key = site.Key + "|" + id;
        if (_detailCache.TryGetValue(key, out var hit)) return hit;
        var raw = await CallAsync(site, "detailContent", new JsonArray(id), ct).ConfigureAwait(false);
        if (_detailCache.Count >= 40) _detailCache.Clear();
        _detailCache[key] = raw;
        return raw;
    }

    /// <summary>桥生死探针（超时处置前用）：call 异步化后 ping 能穿透慢 call——
    /// ping 得通说明桥活着、只是该调用慢，此时杀桥重置（15s 起桥 + 全站重载）是双输。</summary>
    private async Task<bool> PingBridgeAliveAsync(TimeSpan timeout)
    {
        try
        {
            var resp = await RoundTripAsync(new JsonObject
            {
                ["id"] = Interlocked.Increment(ref _id),
                ["op"] = "ping",
            }, timeout, CancellationToken.None, resetOnTimeout: false).ConfigureAwait(false);
            return resp["ok"]?.GetValue<bool>() == true;
        }
        catch { return false; }
    }

    // ── 搜索速度档案 ──
    // 按站点记录上次 searchContent 实测耗时（含桥内排队），SearchPage 据此把快源排前、
    // 慢源延后发起（「快的立即显示、慢的延后搜索」）；MacCMS/未搜过的源无记录 = 视为最快。
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> SearchElapsedMs =
        new(StringComparer.Ordinal);

    public static long? LastSearchMs(string siteKey) =>
        SearchElapsedMs.TryGetValue(siteKey, out var ms) ? ms : null;

    public async Task<string> SearchContentAsync(VodSiteInfo site, string keyword, string pg, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // 搜索专用 25s 短超时：搜索是全员扫描，个别慢源（实测某源 18s 且 0 结果）不该占桥 90s；
            // 超时不重置桥——慢源只占自己那把站点锁（桥已 per-site 并行），重置会连累正在跑的其它源。
            return await CallAsync(site, "searchContent", new JsonArray(keyword, pg), ct,
                TimeSpan.FromSeconds(25), resetOnTimeout: false).ConfigureAwait(false);
        }
        finally { SearchElapsedMs[site.Key] = sw.ElapsedMilliseconds; }
    }

    /// <summary>
    /// playerContent 结果直通宿主，但先做一处<b>端口改写</b>：壳把播放地址构造成它自己的
    /// 本地流中转服务（<c>http://127.0.0.1:6678/proxy/play/…</c>，壳拿 Cookie 中转夸克直链）。
    /// 壳跑在 guest 里，宿主播放器够不到 —— 改写成 <see cref="QemuArtGuest.ProxyTunnelPort"/>
    /// （hostfwd 直达 guest 桥；guest 桥把该路径透传给壳的流服务）。danmaku 钩子同改。
    /// <para><b>Guard 端口抢占守卫</b>（2026-09-27 实锤）：csp_*Guard 同 jar 家族的多个源共享
    /// 壳内部的约定流服务端口（6678）——兄弟源一旦在本源之后装载（跨源预取/手动浏览），本源
    /// 的流服务就被顶掉，playerContent 返回的 6678 地址全 0B（handler NPE → 播放器
    /// 「unexpected end of stream／源不受支持」）。修复：播放前本源不是「最近装载的 Guard 源」
    /// 时先 force 重装载本源（新 ClassLoader 重建壳状态 → 重占回端口；实测 3.4s、100% 抢回）。</para>
    /// </summary>
    public async Task<string> PlayerContentAsync(VodSiteInfo site, string flag, string id, CancellationToken ct = default)
    {
        if (IsGuardSite(site) && _lastGuardSiteKey != site.Key)
        {
            try
            {
                Log($"{site.Name}: Guard 端口守卫——最近装载为 {_lastGuardSiteKey ?? "(无)"}，force 重装载本源抢回 6678");
                await EnsureBridgeAsync(ct).ConfigureAwait(false);
                var jar = await EnsureConvertedJarAsync(site, ct).ConfigureAwait(false);
                await EnsureSiteLoadedAsync(site, jar, ct, force: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 抢回失败不阻断播放尝试：仍可能（本次/窗口内）可用，失败则播放器侧报原错误
                Log($"{site.Name}: Guard 端口守卫重装失败（继续尝试播放）: {ex.Message}");
            }
        }
        var raw = await CallAsync(site, "playerContent", new JsonArray(flag ?? "", id), ct).ConfigureAwait(false);
        if (ArtGuestMode && raw.Contains("127.0.0.1:6678") && _art is { ProxyTunnelPort: > 0 } art)
        {
            Log($"{site.Name}: 壳流地址端口改写 6678 → {art.ProxyTunnelPort}");
            _ = DiagnoseGuestPortsAsync(ct);
            return raw.Replace("127.0.0.1:6678", "127.0.0.1:" + art.ProxyTunnelPort);
        }
        return raw;
    }

    /// <summary>guest 监听端口盘点（/proc/net/tcp）——诊断壳的流服务是否真的在听。</summary>
    private async Task DiagnoseGuestPortsAsync(CancellationToken ct)
    {
        try
        {
            var req = new JsonObject { ["id"] = Interlocked.Increment(ref _id), ["op"] = "netstat" };
            var resp = await RoundTripAsync(req, TimeSpan.FromSeconds(5), ct);
            Log("guest 监听端口: " + resp["result"]?.GetValue<string>());
        }
        catch { }
    }

    /// <inheritdoc/>
    public async Task<string?> InvokeDanmakuHookAsync(VodSiteInfo site, string hookUrl, CancellationToken ct = default)
    {
        try
        {
            using var h = new HttpRequestMessage(HttpMethod.Get, hookUrl);
            using var r = await _http.SendAsync(h, ct).ConfigureAwait(false);
            var body = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            Log($"[解析] {site.Key}.danmaku钩子 → {(int)r.StatusCode} {body.Length}B");
            string? real = r.Headers.Location?.ToString();
            if (string.IsNullOrEmpty(real))
            {
                var t = body.Trim();
                if (t.StartsWith("http", StringComparison.OrdinalIgnoreCase)) real = t;
                else if (t.StartsWith("{"))
                {
                    try { real = System.Text.Json.Nodes.JsonNode.Parse(t)?["url"]?.GetValue<string>(); }
                    catch { }
                }
            }
            if (string.IsNullOrEmpty(real)) return null;
            // 壳的流服务在 guest 里监听 6678：它给出的地址（绝对/相对）都要换算成宿主隧道端口
            if (!real.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                real = "http://127.0.0.1:6678" + real;
            if (ArtGuestMode && _art is { ProxyTunnelPort: > 0 } art)
                real = real.Replace("127.0.0.1:6678", "127.0.0.1:" + art.ProxyTunnelPort);
            Log($"[解析] {site.Key}.danmaku钩子给出播放地址 → {real}");
            return real;
        }
        catch (Exception ex)
        {
            Log($"[解析] {site.Key}.danmaku钩子失败: {ex.Message}");
            return null;
        }
    }

    public Task<string> ActionAsync(VodSiteInfo site, string actionJson, CancellationToken ct = default) =>
        CallAsync(site, "action", new JsonArray(actionJson ?? ""), ct);

    /// <summary>
    /// 桌面 JVM 桥的 <c>liveContent</c>：把站点自带的真实地址交给爬虫，回 TXT/M3U 频道表。
    /// <para>桥侧 <c>Server.java</c> 的 switch 已有 <c>liveContent</c> case（2026-09-26 补，随
    /// <c>bridge.jar</c> 重编）。爬虫没实现该方法时由 <see cref="CallAsync"/> 把桥的异常原文抛上去，
    /// 而不是静默返回空 —— 静默空值会让上层误判成「该源没有频道」，排查方向就错了。</para>
    /// </summary>
    public Task<string> LiveContentAsync(VodSiteInfo site, string url, CancellationToken ct = default) =>
        CallAsync(site, "liveContent", new JsonArray(url), ct);

    // ═══════════ 进程与调用 ═══════════

    /// <summary>
    /// 后台预热桥（启动后调用）：ART guest VM 冷启动要 5~11s，此前发生在**第一次 jar 站点
    /// 调用**上——首选站点是 jar 源时，整段冷启动直接叠进「首页首载/切站」的等待里。
    /// 放到启动后台跑（宿主侧对迅雷 VM 已有同款预热先例），用户浏览首页的时间里 VM 就绪。
    /// <para>幂等：桥已就绪时零开销直接返回；失败静默（首次真实调用仍会懒启动重试）。
    /// ⚠ 限时 75s：预热与用户调用在 <see cref="_bridgeGate"/> 上互斥，guest 冷启动最坏要
    /// 数分钟（226MB initrd 解压 + ART 起 VM），不限时的话一次卡住的预热会把用户的第一次
    /// 调用也堵在队列里到天荒地老（2026-09-26 实测：预热占道 243s，用户点播放 90s 超时）。</para>
    /// </summary>
    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(75));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await EnsureBridgeAsync(cts.Token).ConfigureAwait(false);
            Log($"桥预热完成（{sw.ElapsedMilliseconds}ms，ART guest）");
        }
        catch (Exception ex)
        {
            Log($"桥预热失败/放弃（不影响后续懒启动）: {ex.Message}");
        }
    }

    /// <summary>起桥全程互斥：并发 EnsureBridgeAsync 会双双连桥、覆盖 _stdin/_stdout、
    /// 起两个 ReadLoop 分吃应答 —— 请求永远等不到回包（90s 超时的根因，2026-09-26 修复）。</summary>
    private readonly SemaphoreSlim _bridgeGate = new(1, 1);

    /// <summary>桥是否已就绪（不必重连）。快路径无锁检查。</summary>
    private bool IsBridgeReady => _art is { IsUp: true } && _stdin is not null;

    /// <summary>
    /// 确保桥可用。桥唯一跑在 ART guest 里（QEMU 里真 ART，行协议走 TCP）；
    /// 握手在 <see cref="HandshakeAsync"/> 里。
    /// </summary>
    private async Task EnsureBridgeAsync(CancellationToken ct)
    {
        if (IsBridgeReady) return;
        // 重置窗口期直接快速失败（避免在 ResetBridge 的 Shutdown 旁边抢旧实例）：
        // 上层调用点拿到这条消息会告诉用户稍后重试，而不是干等一轮冷启动。
        if (Volatile.Read(ref _resetting) != 0)
            throw new InvalidOperationException("爬虫引擎正在重置（上一次调用无响应），请稍后重试");
        // JRE 回落桥已退役（2026-09-29）：guest 未启用就再没有第二条路，直接给明确原因。
        if (!ArtGuestMode)
            throw new InvalidOperationException(
                "ART guest 未启用，jar 爬虫链路不可用（宿主 JRE 回落桥已退役，桥只跑 QEMU 的 ART guest）。原因："
                + (Environment.GetEnvironmentVariable("CATCLAW_NO_ART") == "1"
                    ? "CATCLAW_NO_ART=1 显式禁用"
                    : "缺 QemuGuest 运行时（需 art_initrd_merged.gz 等随包文件）"));
        await _bridgeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsBridgeReady) return;   // 排队期间别的调用已把桥拉起来
            // 代际快照：ConnectAsync 冷启动可耗分钟级，期间可能发生 ResetBridge——
            // 完成后必须校验会话没换代，否则拿到的链接/实例已属上一代（复活的旧 VM）。
            for (var attempt = 0; ; attempt++)
            {
                var epoch = Volatile.Read(ref _bridgeEpoch);
                // 懒创建走统一入口（含 x86 override 与迅雷合并配置）；锁防「桥会话与迅雷合并并发首建」
                lock (_artCreateLock) { _art ??= CreateArtGuest(); }
                var art = _art;
                var link = await art.ConnectAsync(ct).ConfigureAwait(false);
                if (epoch != Volatile.Read(ref _bridgeEpoch))
                {
                    // 长等待期间被重置：本会话（及其 VM）已由 ResetBridge 收走，
                    // 不设流、不复用——链路/实例都已在 Shutdown 里 Dispose，重来一轮。
                    if (attempt == 0) { Log("桥会话在冷启动期间被重置，重试一轮"); continue; }
                    throw new InvalidOperationException("爬虫引擎连续重置，请稍后重试");
                }
                if (link is null)
                    // 起不来不再有 JRE 可回落：ArtGuestMode 保持不变（下次调用还会重试），
                    // 失败原因直接抛给调用方（QemuArtGuest 已写 [art-vm] 明细日志）。
                    throw new InvalidOperationException(
                        "ART guest 起不来（详见 [art-vm] 日志）；宿主 JRE 回落桥已退役，jar 爬虫链路不可用");
                if (!ReferenceEquals(art, _art) || IsBridgeReady)
                {
                    // 并发调用方已接管（ownership 仲裁让位场景）→ 换流重试
                    if (attempt == 0) { Log("ART 会话被并发调用方接管，换流重试"); continue; }
                    throw new InvalidOperationException("ART 桥会话竞争冲突，请稍后重试");
                }
                (_stdin, _stdout) = link.Value;
                Log($"桥已连上 ART guest（127.0.0.1:{art.BridgePort}）");
                _ = Task.Run(ReadLoopAsync);
                break;
            }

            await HandshakeAsync(ct).ConfigureAwait(false);
            // guest 的 /data 是 tmpfs（VM 冷启即清）：把上次会话持久化的偏好（网盘 Cookie 等）
            // 回灌进 guest，必须发生在任何 spider 代码运行之前（PrefsStore 按名惰性读盘）
            await RestoreGuestPrefsAsync(ct).ConfigureAwait(false);
        }
        finally { _bridgeGate.Release(); }
    }

    /// <summary>
    /// 把上次会话持久化的 guest 偏好（<c>guest-prefs/*.xml</c>，由 <c>prefs-sync</c> 事件写来）
    /// 回灌进 guest —— 走 <c>prefsput</c> op 写 guest 的 shared_prefs，网盘 Cookie 等
    /// 因此能在 VM 冷启后存活。
    /// </summary>
    private async Task RestoreGuestPrefsAsync(CancellationToken ct)
    {
        try
        {
            var dir = Path.Combine(_workDir, "guest-prefs");
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "*.xml"))
            {
                var xml = await File.ReadAllTextAsync(f, ct);
                var req = new JsonObject
                {
                    ["id"] = Interlocked.Increment(ref _id),
                    ["op"] = "prefsput",
                    ["name"] = Path.GetFileNameWithoutExtension(f),
                    ["xml"] = xml,
                };
                var resp = await RoundTripAsync(req, TimeSpan.FromSeconds(10), ct);
                Log(resp["ok"]?.GetValue<bool>() == true
                    ? $"guest 偏好回灌：{Path.GetFileName(f)}（{xml.Length}B）"
                    : $"guest 偏好回灌失败 {Path.GetFileName(f)}: {resp["error"]}");
            }
        }
        catch (Exception ex) { Log($"guest 偏好回灌异常: {ex.Message}"); }
    }

    /// <summary>握手：ping 通了才算就绪，然后把宿主 proxy 端口下发给桥。</summary>
    private async Task HandshakeAsync(CancellationToken ct)
    {
        var pong = await RoundTripAsync(new JsonObject { ["id"] = 0, ["op"] = "ping" }, TimeSpan.FromSeconds(15), ct);
        if (!pong.ContainsKey("ok") || pong["ok"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("Java 桥握手失败");
        Log("ART guest 里的桥就绪");

        // 下发宿主 proxy 端口：Guard 系网盘源靠 SpiderApi.getAddress/getPort 拼「云盘配置」
        // 数据端点 URL，桥桩返回空会让 spider 内部 Gson 解析到错误文本直接炸（Expected
        // BEGIN_OBJECT but was STRING → detailContent 整体失败，2026-09-24 实测）。
        // 每次新桥都要重发（ART guest 的桥一连接一个会话）。
        var port = _proxyPort?.Invoke() ?? 0;
        if (port > 0)
        {
            var pp = await RoundTripAsync(new JsonObject { ["id"] = 0, ["op"] = "setProxyPort", ["port"] = port },
                TimeSpan.FromSeconds(10), ct);
            Log(pp["ok"]?.GetValue<bool>() == true ? $"已下发 proxy 端口 {port}" : $"proxy 端口下发失败: {pp["error"]}");
        }
    }

    /// <summary>桥上行 UI 事件（ui-dialog/ui-dismiss/ui-toast）；宿主 MAUI 层订阅渲染。</summary>
    public Action<JsonObject>? UiEvent { get; set; }

    /// <summary>响应分发表：RoundTripAsync 注册、读循环按 id 完成之。</summary>
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pendingResponses = new();
    private readonly SemaphoreSlim _stdinLock = new(1, 1);   // 仅保护 stdin 写

    /// <summary>
    /// 桥正在重置（上一次调用没回来）。1 = 重置中。
    /// <para>为什么要有这个状态：桥的 <c>call</c> 在主循环里 <c>synchronized(LOCK)</c> 串行执行，
    /// 一次超时说明它**还在里面跑**，后面每个请求都只能排队到自己那条超时。实测（2026-09-26）
    /// 玩偶的 playerContent 卡 90s 期间，新6V 的 load 明明只要 2669ms，却跟着撞满 60s ——
    /// 用户看到的就是「整个软件卡半分钟，然后满屏超时」。</para>
    /// </summary>
    private int _resetting;

    /// <summary>桥会话代际（ResetBridge 递增）：在飞的 EnsureBridgeAsync 用它识别
    /// 「长等待（冷启动探针）期间会话已被重置」——拿到的旧实例/链接一律作废，
    /// 防止旧 QemuHostRuntime 被复活成孤儿 qemu（2026-09-27 多 qemu 事故）。</summary>
    private int _bridgeEpoch;

    /// <summary>会话级加载失败负缓存（站点键 → 失败原因）：命中直接快速失败，不反复烧桥。
    /// 只记结构性失败（VerifyError/类不存在等）；桥重置时清空。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _loadFailedSites = new();

    /// <summary>
    /// 把当前桥会话判废并后台重开：期间请求快速失败，重开后 <see cref="_loadedSites"/> 已清空，
    /// 下一次调用会在新桥（或重启后的 ART guest）上真跑。
    /// </summary>
    private void ResetBridge(string why)
    {
        if (Interlocked.CompareExchange(ref _resetting, 1, 0) != 0) return;
        // 代际 +1（同步、先于后台 Shutdown）：在飞的 EnsureBridgeAsync 在长等待（冷启动
        // 探针可到分钟级）后发现自己拿的是上一代会话 → 主动放弃，绝不复活旧 VM。
        Interlocked.Increment(ref _bridgeEpoch);
        Log($"桥无响应（{why}）→ 重置爬虫引擎：丢掉当前桥会话，期间请求立刻报错，不再让后面的一条条排队等超时");
        _ = Task.Run(() =>
        {
            try
            {
                Shutdown();
                _loadedSites.Clear();
                _loadFailedSites.Clear();
                _lastGuardSiteKey = null;
                Log("爬虫引擎已重置，下一次调用会重新起桥（ART guest 约 15s）");
            }
            catch (Exception ex)
            {
                Log($"桥重置失败：{ex.GetType().Name} {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _resetting, 0);
            }
        });
    }

    /// <summary>常驻读桥输出（ART guest 的 socket 流）：按 id 分发响应、按 ev 分发 UI 事件。</summary>
    private async Task ReadLoopAsync()
    {
        var rd = _stdout;
        if (rd is null) return;
        try
        {
            while (true)
            {
                var raw = await rd.ReadLineAsync();
                if (raw == null) break;
                var line = raw.Trim();
                if (line.Length == 0) continue;
                JsonObject? obj;
                try { obj = JsonNode.Parse(line)!.AsObject(); }
                catch { continue; }

                // 桥主动上行的 UI 事件（ui-dialog / ui-dismiss / ui-toast）
                if (obj.ContainsKey("ev"))
                {
                    // prefs-sync：guest（tmpfs）的偏好落盘上行 —— 存宿主盘，下次 VM 冷启回灌
                    if (obj["ev"]?.GetValue<string>() == "prefs-sync")
                    {
                        try
                        {
                            var pname = obj["name"]?.GetValue<string>() ?? "default";
                            var xml = obj["xml"]?.GetValue<string>() ?? "";
                            var dir = Path.Combine(_workDir, "guest-prefs");
                            Directory.CreateDirectory(dir);
                            File.WriteAllText(Path.Combine(dir, pname + ".xml"), xml);
                            Log($"guest 偏好同步落盘：{pname}（{xml.Length}B）");
                        }
                        catch { }
                    }
                    try { UiEvent?.Invoke(obj); } catch { }
                    continue;
                }

                var id = obj["id"]?.GetValue<int>() ?? int.MinValue;
                if (_pendingResponses.TryRemove(id, out var tcs))
                    _ = tcs.TrySetResult(obj);
                else
                    Log($"桥上行未匹配响应 id={id}: {line[..Math.Min(line.Length, 120)]}");
            }
        }
        catch { }
        Log("桥读循环退出");
        FailAllPending("ART guest 里的桥崩溃退出，引擎已自动重置——请稍候重试该站点");
    }

    /// <summary>桥会话断开（socket EOF）：pending 请求立即失败并触发引擎重置。
    /// 为什么必须（2026-09-27 实测）：Guard 壳真实类初始化 SIGSEGV 带走整个桥，
    /// load 的应答永远不会来——不清的话上层干等 60s 超时 + 4s 生死探针才报「不可用」，
    /// UI 就是一分钟白屏；清了之后秒级报错，且下一次调用自动重新起桥。
    /// ART 链路的 IsUp 只看 socket.Connected 与 VM 进程，桥崩后两者仍真、不会自动
    /// 重拉，必须 ResetBridge 清态。它自带 _resetting 防重入：正常收尾/超时重置
    /// 杀桥导致的 EOF 在这里直接 return，不会二次重置。</summary>
    private void FailAllPending(string why)
    {
        foreach (var kv in _pendingResponses)
            if (_pendingResponses.TryRemove(kv.Key, out var tcs))
                _ = tcs.TrySetException(new InvalidOperationException(why));
        ResetBridge("读循环退出（桥进程死亡）");
    }

    /// <summary>宿主回传对话框用户操作（which≥0=列表项，-1/-2/-3=肯定/否定/中性按钮）。</summary>
    public async Task SendUiResultAsync(int seq, int which)
    {
        if (_stdin is null) return;
        var req = new JsonObject { ["id"] = -1, ["op"] = "ui-result", ["seq"] = seq, ["which"] = which };
        await _stdinLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _stdin.WriteLineAsync(req.ToJsonString().AsMemory()).ConfigureAwait(false);
            await _stdin.FlushAsync().ConfigureAwait(false);
        }
        finally { _stdinLock.Release(); }
    }

    /// <summary>
    /// 读取 jar 侧 SharedPreferences（MemPrefs 全量内容）：网盘 Cookie/Token 登录态。
    /// Guard 系网盘源的 proxyInput/do=xx 推送写它、Cloud_* 类读它——「已登录+启用中」
    /// 对话框按它渲染状态。桥未启动返回 null。
    /// </summary>
    public async Task<JsonArray?> GetPrefsAsync()
    {
        try
        {
            await EnsureBridgeAsync(CancellationToken.None).ConfigureAwait(false);
            var resp = await RoundTripAsync(new JsonObject { ["id"] = Interlocked.Increment(ref _id), ["op"] = "get-prefs" },
                TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
            if (resp["ok"]?.GetValue<bool>() != true) return null;
            return resp["result"]?.GetValue<string>() is { } s ? JsonNode.Parse(s) as JsonArray : null;
        }
        catch (Exception ex)
        {
            Log($"get-prefs 失败: {ex.Message}");
            return null;
        }
    }

    private async Task<JsonObject> RoundTripAsync(JsonObject req, TimeSpan timeout, CancellationToken ct,
        bool resetOnTimeout = true)
    {
        var expectId = req["id"]?.GetValue<int>()
            ?? throw new InvalidOperationException("桥请求缺少 id");
        if (Volatile.Read(ref _resetting) != 0)
            throw new InvalidOperationException("爬虫引擎正在重置（上一次调用无响应），请稍后重试");
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingResponses[expectId] = tcs;
        try
        {
            // 先注册再写（响应可能在写返回前就到）——读循环按 id 完成之
            await _stdinLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _stdin!.WriteLineAsync(req.ToJsonString().AsMemory(), ct).ConfigureAwait(false);
                await _stdin.FlushAsync(ct).ConfigureAwait(false);
            }
            finally { _stdinLock.Release(); }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try { return await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 握手那条 ping 不触发重置：它本来就是"桥还没起来"的信号，交给连接流程自己收尾。
                var op = req["op"]?.GetValue<string>() ?? "";
                if (op != "ping" && op != "exit" && resetOnTimeout)
                {
                    // 先探桥生死再决定重置：call 已异步化（CALL_POOL），ping 能穿透慢 call——
                    // ping 得通说明桥活着、只是该调用慢（实测聚合网盘源 detailContent 71s 才完成），
                    // 杀桥重置（15s 起桥 + 全站重载）纯属双输；探针也无应答才是真死，走重置。
                    if (await PingBridgeAliveAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false))
                        throw new TimeoutException(
                            $"线路爬虫（{InFlightTag}）{timeout.TotalSeconds:F0}s 无响应，但桥仍存活——该线路服务端极慢，建议换线路（id={expectId}）");
                    ResetBridge($"op={op} id={expectId}{InFlightTag} 在 {timeout.TotalSeconds:F0}s 内没回来且探针无应答");
                }
                throw new TimeoutException(BuildTimeoutMessage(op, timeout, expectId));
            }
        }
        finally { _pendingResponses.TryRemove(expectId, out _); }
    }

    /// <summary>正在桥上执行的调用（site.method）：超时/重置日志能直接指出卡死的是谁。
    /// 典型现场（2026-09-26 实测）：聚合网盘源的 detailContent 串行探测阿里/夸克/UC/百度等
    /// 网盘接口，其中一个挂死 → 桥全局锁被占 → 整桥 90s 无响应。</summary>
    private volatile string _inFlight = "";

    private string InFlightTag => _inFlight.Length > 0 ? $"（{_inFlight}）" : "";

    /// <summary>超时报错要给用户出路：call 超时几乎都是爬虫内部对网盘 API 的请求挂死
    /// （桥全局锁被占），引擎已自动重置，剩下的动作是换线路；其余 op 保持原口径。</summary>
    private string BuildTimeoutMessage(string op, TimeSpan timeout, int id)
    {
        if (op != "call") return $"Java 桥响应超时（{timeout.TotalSeconds:F0}s，id={id}）";
        var what = _inFlight.Length > 0 ? $"线路爬虫（{_inFlight}）" : "线路爬虫";
        return $"{what} {timeout.TotalSeconds:F0}s 无响应——常见于网盘接口被限流或挂起；引擎已自动重置，请换其它线路或稍后重试（id={id}）";
    }

    private async Task<string> CallAsync(VodSiteInfo site, string method, JsonArray args, CancellationToken ct,
        TimeSpan? timeout = null, bool resetOnTimeout = true)
    {
        await EnsureBridgeAsync(ct);
        var jar = await EnsureConvertedJarAsync(site, ct);
        await EnsureSiteLoadedAsync(site, jar, ct);
        _lastSite = site;   // spider 稍后发起的 /proxy 回调不带 siteKey，靠它定位

        _inFlight = $"{site.Key}.{method}";
        try
        {
            var req = new JsonObject
            {
                ["id"] = Interlocked.Increment(ref _id),
                ["op"] = "call",
                ["site"] = site.Key,
                ["method"] = method,
                ["args"] = args,
            };
            var resp = await RoundTripAsync(req, timeout ?? TimeSpan.FromSeconds(90), ct, resetOnTimeout);
            if (resp["ok"]?.GetValue<bool>() != true)
                throw new InvalidOperationException($"spider {site.Key}.{method}: {resp["error"]}");
            return resp["result"]?.GetValue<string>() ?? "{}";
        }
        finally { _inFlight = ""; }
    }

    // ═══════════ ISpiderProxyRuntime（宿主本地 /proxy 回调 → 桥 proxy op） ═══════════

    /// <summary>
    /// 处理宿主本地 <c>/proxy?…</c> 回调：转给桥进程的 <c>proxy</c> op，由爬虫自身的
    /// <c>proxy(Map)</c> 产生响应体（TVBox <c>ApiConfig.proxyLocal</c> 语义）。
    /// <para>Guard 系网盘源（csp_MDriveGuard 等）在 detailContent 内部就会请求
    /// <c>do=config</c> 端点取「云盘配置」JSON——此前桌面桥没有 proxy 通道，宿主回 502 文本，
    /// 爬虫内 Gson 解析炸 <c>Expected BEGIN_OBJECT but was STRING</c> → 整个 detailContent 失败，
    /// 用户点「登入自己网盘」进的是播放页而非配置页（2026-09-24 实测）。</para>
    /// <para>响应体经临时文件回传（对齐 Android 端 SpiderProxyBridge：stdout 每行一条 JSON，
    /// body 内联会被转义/体积问题拖垮）；无法处理返回 null（调用方回 502）。</para>
    /// </summary>
    public async Task<(int Status, string Mime, byte[]? Body)?> ProxyAsync(
        IReadOnlyDictionary<string, string> query, CancellationToken ct = default)
    {
        // 按 siteKey 定位站点（SpiderProxyServer 分派时携带）；spider 自己发起的请求
        // （如 detailContent 内部的 do=config）不带该参数 → 回退最近调用站点（对齐 Dex 版 _lastUsed）
        var key = query.GetValueOrDefault("siteKey");
        var site = string.IsNullOrEmpty(key) ? null : SiteRegistry.Find(key);
        site ??= _lastSite;
        if (site is null)
        {
            Log("proxy 回调：siteKey 缺失且无最近站点");
            return null;
        }
        try
        {
            await EnsureBridgeAsync(ct).ConfigureAwait(false);
            var jarPath = await EnsureConvertedJarAsync(site, ct).ConfigureAwait(false);
            await EnsureSiteLoadedAsync(site, jarPath, ct).ConfigureAwait(false);

            // 爬虫自带的 /proxy 就在 guest 里跑着（桥的 Art.serveProxy），宿主经 slirp
            // 隧道直接要字节。（行协议那条 outFile 代理通道随宿主 JRE 桥退役：guest 里的
            // Linux 进程写 Windows 路径宿主本就读不到，2026-09-26 实测。）
            if (_art?.ProxyBase is not { } tunnel)
            {
                Log("proxy 隧道不可用（ART guest 未连接）");
                return ((int)502, "text/plain", (byte[]?)null);
            }
            return await ProxyViaTunnelAsync(tunnel, site.Key, query, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"proxy 回调异常：{ex.GetType().Name}: {ex.Message}");
            return ((int)502, "text/plain", (byte[]?)null);
        }
    }

    /// <summary>宿主→ART guest 的 /proxy 隧道客户端（一次请求一次应答，超时按爬虫取流的慢样子给）。</summary>
    private static readonly HttpClient TunnelHttp =
        new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) })
        { Timeout = TimeSpan.FromSeconds(120) };

    /// <summary>
    /// 把爬虫的 proxy 回调原样交给 guest 里那个由壳自己应答的服务
    /// （<c>bridge.Art.serveProxy</c> 起的，内部就是 jar 的 <c>Proxy.proxy → Init.proxyInvoke</c>）。
    /// </summary>
    private async Task<(int Status, string Mime, byte[]? Body)?> ProxyViaTunnelAsync(
        string baseUrl, string siteKey, IReadOnlyDictionary<string, string> query, CancellationToken ct)
    {
        var q = new Dictionary<string, string>(query) { ["site"] = siteKey };
        var url = baseUrl + "/proxy?" + string.Join("&",
            q.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value ?? "")));
        using var resp = await TunnelHttp.GetAsync(url, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        Log($"proxy 隧道：{siteKey} do={query.GetValueOrDefault("do")} → {(int)resp.StatusCode} {body.Length}B");
        return ((int)resp.StatusCode,
            resp.Content.Headers.ContentType?.MediaType ?? "text/plain", body);
    }

    /// <summary>
    /// 桥侧要加载的类名：默认取 api 去掉 <c>csp_</c> 前缀；
    /// 换了 jar/解了 Guard 壳后以 <see cref="_nonGuardClass"/> 的映射为准。
    /// </summary>
    private string classNameOf(VodSiteInfo site) =>
        _nonGuardClass.TryGetValue(site.Key, out var alt)
            ? alt
            : site.Api.StartsWith("csp_", StringComparison.OrdinalIgnoreCase) ? site.Api[4..] : site.Api;

    /// <summary>Guard 家族站点：<c>csp_*Guard</c>——同 jar 的多个源共享壳内 6678 流端口，
    /// 播放前需要做端口抢占守卫（见 <see cref="PlayerContentAsync"/>）。</summary>
    private static bool IsGuardSite(VodSiteInfo site) =>
        site.Api.StartsWith("csp_", StringComparison.OrdinalIgnoreCase) &&
        site.Api.EndsWith("Guard", StringComparison.OrdinalIgnoreCase) && site.Api.Length > 6;

    private async Task EnsureSiteLoadedAsync(VodSiteInfo site, string jarPath, CancellationToken ct, bool force = false)
    {
        if (_loadedSites.TryGetValue(site.Key, out _) && !force) return;
        // 会话级负缓存：结构性加载失败（VerifyError 等，重试也不会好）不再反复烧桥——
        // 实测 Wogg/Douban 每轮搜索都要白跑 5~7s 的 load。重置引擎时清空，给重试机会。
        if (_loadFailedSites.TryGetValue(site.Key, out var failedWhy))
            throw new InvalidOperationException($"站点 {site.Key} 此前加载失败，本轮跳过：{failedWhy}");
        var className = classNameOf(site);
        var req = new JsonObject
        {
            ["id"] = Interlocked.Increment(ref _id),
            ["op"] = "load",
            ["site"] = site.Key,
            ["className"] = className,
            ["ext"] = await PrepareExtAsync(site, ct),
            ["jars"] = new JsonArray(jarPath),
        };
        // force 重装载（桥侧绕过「site 已装载」幂等缓存）：Guard 家族端口抢占的抢回手段
        // （见 PlayerContentAsync 的 Guard 端口守卫与字段 _lastGuardSiteKey 注释）。
        if (force) req["force"] = true;
        // 桥在 guest 里，rawJar 由 ART 直接吃（classes.dex）、Guard 壳自己 System.load()
        // 那个 arm64 ftyguard so 并解出真 dex（2026-09-25 实测：装载 3272ms、homeContent 183ms）。
        // guest 读不到宿主的盘：换成宿主 jar 服务的 URL，桥里 Art.materialize() 取回归档。
        // 纯 .class jar（无 classes.dex，如 fty.jar 一族）guest 的 ART 吃不了：先 d8 转 dex 再供。
        var serve = IsPureClassJar(jarPath) ? await EnsureDexJarAsync(jarPath, ct) : jarPath;
        _jarServer ??= new CatClawVideo.Core.Services.QemuGuest.ArtJarServer(_log);
        var url = _jarServer.UrlFor(Path.GetFileName(serve).Replace("raw-", "").Replace(".jar", ""), serve);
        req["jars"] = new JsonArray(url);
        req["rawJar"] = url;
        Log($"{site.Name}: ART guest 取 jar ← {url}");
        var resp = await RoundTripAsync(req, TimeSpan.FromSeconds(60), ct);
        if (resp["ok"]?.GetValue<bool>() != true)
        {
            // 加载失败必须留痕：此前只 log 成功分支，导致「站点没反应」无从查因
            Log($"站点 {site.Key} 加载失败（类名 {className}，jar {Path.GetFileName(jarPath)}）: {resp["error"]}");
            _loadFailedSites[site.Key] = resp["error"]?.GetValue<string>() ?? "";
            throw new InvalidOperationException($"spider {site.Key} 加载失败: {resp["error"]}");
        }
        _loadedSites[site.Key] = true;
        _loadFailedSites.TryRemove(site.Key, out _);
        // Guard 家族装载次序跟踪（端口抢占守卫用，见 _lastGuardSiteKey 注释）：
        // 只有带 Guard 后缀的壳类名才占 6678；降级到真实类（无 Guard）的不算。
        if (className.EndsWith("Guard", StringComparison.Ordinal)) _lastGuardSiteKey = site.Key;
        Log($"站点 {site.Key} 已加载");
    }

    // ═══════════ jar 转换管线 ═══════════

    private async Task<string> EnsureConvertedJarAsync(VodSiteInfo site, CancellationToken ct)
    {
        if (_convertedJars.TryGetValue(site.Key, out var cached) && File.Exists(cached)) return cached;

        var jarUrl = site.Jar ?? "";
        var semi = jarUrl.IndexOf(";md5;", StringComparison.OrdinalIgnoreCase);
        var expectMd5 = semi > 0 ? jarUrl[(semi + 5)..].Trim() : null;
        if (semi > 0) jarUrl = jarUrl[..semi];
        if (string.IsNullOrEmpty(jarUrl))
            throw new InvalidOperationException($"站点 {site.Name} 缺少 spider jar 地址");

        var configured = site.Api.StartsWith("csp_", StringComparison.OrdinalIgnoreCase) ? site.Api[4..] : site.Api;

        // 宿主只负责把原始 jar 拿到手；Guard 解壳/dex 装载全在 ART guest 里完成
        var jar = await FetchJarAsync(jarUrl, expectMd5, ct).ConfigureAwait(false)
                  ?? throw new InvalidOperationException($"站点 {site.Name} 的 spider jar 拉取失败");

        // Guard 外壳类名带 Guard 后缀，真实 dex 里不带（DouDouGuard → DouDou）。
        // 判定依据必须是「dex 里实际存在哪个类」，不能靠「原包是否 IsGuarded」推断 ——
        // 离线导入/复用手工解好的产物时，那份 raw 已经不含 .so/.guard，会误判成非 Guard 包，
        // 于是按 DouDouGuard 去加载 → 必然 ClassNotFoundException（实测踩过）。
        if (configured.EndsWith("Guard", StringComparison.Ordinal))
        {
            var real = configured[..^"Guard".Length];
            // 纯 .class jar 的类按 zip 条目名查（JarHasClass 搜 dex 字节，对它恒 false）
            bool ClassExists(string cn) => IsPureClassJar(jar) ? JarHasClassFile(jar, cn) : JarHasClass(jar, cn);
            if (!ClassExists(configured) && ClassExists(real))
            {
                _nonGuardClass[site.Key] = real;
                Log($"{site.Name}: 真实类名 {configured} → {real}");
            }
        }
        _convertedJars[site.Key] = jar;
        return jar;
    }

    // ═══════════ 纯 .class jar 的 d8 预转换（ART guest 专用）═══════════

    private static string? _d8JarPath;
    private static string? _androidJarPath;
    private readonly ConcurrentDictionary<string, string> _dexJars = new();

    /// <summary>
    /// jar 里只有 .class 没有 .dex —— TVBox 生态的「纯 java 构建」（fty.jar 一族）。
    /// guest 的 ART 只吃 dex，这类 jar 必须先经 d8 转换才能进 guest。
    /// </summary>
    private static bool IsPureClassJar(string jarPath)
    {
        try
        {
            bool hasClass = false;
            using var zip = System.IO.Compression.ZipFile.OpenRead(jarPath);
            foreach (var e in zip.Entries)
            {
                var n = e.FullName;
                if (n.EndsWith(".dex", StringComparison.OrdinalIgnoreCase)) return false;
                if (n.EndsWith(".class", StringComparison.OrdinalIgnoreCase)) hasClass = true;
            }
            return hasClass;
        }
        catch { return false; }
    }

    /// <summary>Android SDK 的 d8（<c>build-tools/*/lib/d8.jar</c>，版本取最高）与配套 android.jar。</summary>
    private static string? FindSdkTool(out string? androidJar)
    {
        androidJar = null;
        var roots = new List<string>();
        void AddRoot(string? p) { if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) roots.Add(p); }
        AddRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk"));
        AddRoot(Environment.GetEnvironmentVariable("ANDROID_HOME"));
        AddRoot(Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"));
        AddRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Android", "android-sdk"));
        foreach (var root in roots)
        {
            var bt = Path.Combine(root, "build-tools");
            if (!Directory.Exists(bt)) continue;
            string? best = null;
            Version? bestV = null;
            foreach (var d in Directory.GetDirectories(bt))
            {
                var cand = Path.Combine(d, "lib", "d8.jar");
                if (!File.Exists(cand)) continue;
                var v = Version.TryParse(Path.GetFileName(d), out var pv) ? pv : new Version(0, 0);
                if (bestV is null || v > bestV) { best = cand; bestV = v; }
            }
            if (best is null) continue;
            var pf = Path.Combine(root, "platforms");
            if (Directory.Exists(pf))
            {
                androidJar = Directory.GetDirectories(pf)
                    .Select(d => (Path: d, Ver: int.TryParse(Path.GetFileName(d)["android-".Length..], out var n) ? n : -1))
                    .Where(x => x.Ver > 0)
                    .OrderByDescending(x => x.Ver)
                    .Select(x => Path.Combine(x.Path, "android.jar"))
                    .FirstOrDefault(File.Exists);
            }
            return best;
        }
        return null;
    }

    /// <summary>
    /// 纯 .class jar → dex jar（d8），产物缓存在 converted 目录（按内容哈希命名，跨启动复用）。
    /// <para>guest 的 ART 吃不了 .class 字节码；转换产物的 android.* 引用在 guest 里经
    /// 桩优先链（ui_stub.dex）解析到 JRE 同款桩，UI/偏好行为与宿主 JRE 链路一致。</para>
    /// <para>⚠️ d8 直接吃 jar 输入（不解包）：dex2jar 产物里有仅大小写不同的重复条目，
    /// 解到 Windows 大小写不敏感的盘上会互相覆盖丢类；类数超 64K 时 d8 会产出
    /// classes2.dex…，重打包时全部收进去。</para>
    /// </summary>
    private async Task<string> EnsureDexJarAsync(string rawJar, CancellationToken ct)
    {
        if (_dexJars.TryGetValue(rawJar, out var hit) && File.Exists(hit)) return hit;

        var java = FindJavaExe() ?? throw new InvalidOperationException(
            "纯 .class jar 的 d8 预转换需要系统 Java（本机未找到 java.exe）；换用含 classes.dex 的 jar 源可不依赖");
        _d8JarPath ??= FindSdkTool(out _androidJarPath);
        var d8 = _d8JarPath ?? throw new InvalidOperationException(
            "纯 .class jar 的 d8 预转换需要 Android build-tools 的 d8（未找到 build-tools/*/lib/d8.jar）");

        var h = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(rawJar))).ToLowerInvariant()[..24];
        var outJar = Path.Combine(_convertedDir, h + "-d8.jar");
        if (File.Exists(outJar)) return _dexJars[rawJar] = outJar;

        var work = Path.Combine(_convertedDir, "d8-" + h);
        var outDir = Path.Combine(work, "out");
        Directory.CreateDirectory(outDir);
        try
        {
            var t0 = Environment.TickCount64;
            var psi = new ProcessStartInfo
            {
                FileName = java,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-Xmx1g");
            psi.ArgumentList.Add("-cp");
            psi.ArgumentList.Add(d8);
            psi.ArgumentList.Add("com.android.tools.r8.D8");
            psi.ArgumentList.Add("--min-api");
            psi.ArgumentList.Add("28");
            psi.ArgumentList.Add("--release");
            psi.ArgumentList.Add("--output");
            psi.ArgumentList.Add(outDir);
            psi.ArgumentList.Add(rawJar);
            if (!string.IsNullOrEmpty(_androidJarPath))
            {
                psi.ArgumentList.Add("--lib");
                psi.ArgumentList.Add(_androidJarPath);
            }

            Log($"d8 转换（{Path.GetFileName(rawJar)}，{new FileInfo(rawJar).Length / 1024}KB）…");
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("d8 进程启动失败");
            var err = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0)
                throw new InvalidOperationException(
                    $"d8 失败（exit {p.ExitCode}）: {err[..Math.Min(err.Length, 2000)]}");

            // d8 可能按 64K 限制拆多个 dex（classes.dex/classes2.dex/...），全部收进输出 jar
            var dexFiles = Directory.GetFiles(outDir, "*.dex");
            if (dexFiles.Length == 0) throw new InvalidOperationException("d8 没产出任何 dex");
            using (var outZip = System.IO.Compression.ZipFile.Open(outJar, System.IO.Compression.ZipArchiveMode.Create))
            {
                foreach (var df in dexFiles)
                {
                    var entry = outZip.CreateEntry(Path.GetFileName(df), System.IO.Compression.CompressionLevel.Optimal);
                    using var es = entry.Open();
                    using var src = File.OpenRead(df);
                    await src.CopyToAsync(es, ct);
                }
            }
            Log($"d8 完成：{dexFiles.Length} 个 dex → {Path.GetFileName(outJar)}"
                + $"（{(new FileInfo(outJar).Length / 1024)}KB，{Environment.TickCount64 - t0}ms）");
            return _dexJars[rawJar] = outJar;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// <summary>下载 spider jar 到 <paramref name="rawPath"/>（订阅里常见「伪装成 jpg」的走私）。</summary>
    private async Task FetchRawJarAsync(string rawPath, string jarUrl, string? expectMd5, CancellationToken ct)
    {
        Log($"下载 spider jar: {jarUrl[..Math.Min(80, jarUrl.Length)]}");
        using var resp = await _http.GetAsync(jarUrl, ct);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        // 伪装 jpg：剥前导字节定位 PK
        for (int i = 0; i + 1 < bytes.Length && i < 4096; i++)
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B) { if (i > 0) bytes = bytes[i..]; break; }

        if (expectMd5 is not null)
        {
            var actual = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
            if (!actual.Equals(expectMd5, StringComparison.OrdinalIgnoreCase))
                Log($"jar md5 不匹配（期望 {expectMd5} 实际 {actual}）");
        }
        await File.WriteAllBytesAsync(rawPath, bytes, ct);
    }

    /// <summary>
    /// 拿到 jar 文件：下载 + md5 校验（<see cref="FetchRawJarAsync"/>）+ 可选的类存在性检查，
    /// 产物按 URL 哈希命名缓存复用。
    /// <para>宿主侧不再做 Guard 解壳/dex2jar 转换（JRE 桥退役，2026-09-29）：Guard 壳在
    /// ART guest 里由真 ART + arm64 so 自己解；纯 .class jar 由 <see cref="EnsureDexJarAsync"/>
    /// 用 d8 预转换。<b>返回 null = jar 里没有 <paramref name="requireClass"/> 指定的类</b>
    /// （同一个 jar 对不同站点可能「有的类在、有的不在」，实测 fty.jar 有 SixV 却没有 JPJ）；
    /// 下载失败直接抛异常。</para>
    /// </summary>
    private async Task<string?> FetchJarAsync(string jarUrl, string? expectMd5, CancellationToken ct,
        string? requireClass = null)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(jarUrl)))[..24].ToLowerInvariant();
        var rawPath = Path.Combine(_convertedDir, "raw-" + hash + ".jar");
        // jar 必须落**可写目录**（_convertedDir = %APPDATA%\...\javabridge\converted）。
        // 原先写 _bridgeDir/converted：安装版是 Program Files，创建即被拒
        // （2026-09-19 用户实测报错原文：Access to the path '...\JavaBridge\converted' is denied.）
        Directory.CreateDirectory(_convertedDir);

        if (!File.Exists(rawPath)) await FetchRawJarAsync(rawPath, jarUrl, expectMd5, ct).ConfigureAwait(false);
        if (requireClass is null) return rawPath;

        // 类存在性按 jar 形态选检查法：dex jar 搜 dex 字节串；纯 .class jar 按 zip 条目名查
        var hasClass = IsPureClassJar(rawPath)
            ? JarHasClassFile(rawPath, requireClass)
            : JarHasClass(rawPath, requireClass);
        if (!hasClass)
            Log($"跳过不含类 {requireClass} 的 jar: {Path.GetFileName(rawPath)}");
        return hasClass ? rawPath : null;
    }

    /// <summary>
    /// 判断 jar 的 dex 里是否定义了某个类（传**简单类名**，如 <c>SixV</c>）。
    /// <para>dex 的 type descriptor（<c>Lcom/foo/Bar;</c>）在字符串池里以**明文 MUTF-8** 存放，
    /// 所以直接按字节搜完整描述符即可 —— 完整描述符误命中概率可忽略，无需完整解析 dex。</para>
    /// <para>⚠️ 必须搜**完整描述符**：只搜 <c>SixV</c> 这样的简单名会落空，
    /// 因为 dex 里存的是 <c>Lcom/github/catvod/spider/SixV;</c>。
    /// 候选前缀与桥的类名解析顺序一致（见 JavaBridge <c>bridge.Server.load</c>）：
    /// <c>com.github.catvod.spider.</c> → <c>com.github.catvod.crawler.</c> → 裸名。</para>
    /// </summary>
    private static bool JarHasClass(string jarPath, string className)
    {
        try
        {
            string[] candidates =
            [
                $"Lcom/github/catvod/spider/{className};",
                $"Lcom/github/catvod/crawler/{className};",
                $"L{className};",
            ];
            var needles = candidates.Select(Encoding.UTF8.GetBytes).ToArray();

            using var zip = System.IO.Compression.ZipFile.OpenRead(jarPath);
            foreach (var e in zip.Entries)
            {
                var n = e.FullName;
                if (!n.StartsWith("classes", StringComparison.OrdinalIgnoreCase) || !n.EndsWith(".dex", StringComparison.OrdinalIgnoreCase))
                    continue;
                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                var span = ms.GetBuffer().AsSpan(0, (int)ms.Length);
                foreach (var needle in needles)
                    if (span.IndexOf(needle) >= 0) return true;
            }
        }
        catch { }
        return false;
    }
    /// <summary>纯 .class jar 的类存在性：按 zip 条目名查（桥的类名解析顺序同 JarHasClass）。</summary>
    private static bool JarHasClassFile(string jarPath, string className)
    {
        try
        {
            string[] candidates =
            [
                $"com/github/catvod/spider/{className}.class",
                $"com/github/catvod/crawler/{className}.class",
                $"{className}.class",
            ];
            using var zip = System.IO.Compression.ZipFile.OpenRead(jarPath);
            foreach (var e in zip.Entries)
                if (candidates.Contains(e.FullName, StringComparer.Ordinal)) return true;
        }
        catch { }
        return false;
    }

    // ═══════════ ext 认证预处理 ═══════════

    /// <summary>ext global 含 username/password 而缺 token 时，自动登录 {server}/api/auth/login 注入 token。
    /// 凭据来源：site.Ext 自带，或本地凭据文件 %APPDATA%\CatClawVideo\spider-creds.json
    /// （格式 {"host:port": {"username":"..","password":".."}}）。</summary>
    private async Task<string> PrepareExtAsync(VodSiteInfo site, CancellationToken ct)
    {
        var ext = site.Ext ?? "";
        if (string.IsNullOrWhiteSpace(ext) || !ext.TrimStart().StartsWith("[")) return ext;
        try
        {
            var arr = JsonNode.Parse(ext)!.AsArray();
            if (arr.Count == 0) return ext;
            var global = arr[0] as JsonObject;
            if (global == null || global["type"]?.GetValue<string>() != "global") return ext;

            var hasToken = global.ContainsKey("token") && !string.IsNullOrEmpty(global["token"]?.GetValue<string>());
            var user = global["username"]?.GetValue<string>();
            var pass = global["password"]?.GetValue<string>();

            // 本地凭据文件兜底（按 server host 匹配，统一走 SpiderCredentials 存储）
            var creds = SpiderCredentials.Load();
            var servers = arr.OfType<JsonObject>()
                .Where(o => o["server"] != null)
                .Select(o => o["server"]!.GetValue<string>().TrimEnd('/'))
                .Distinct().ToList();

            if ((string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass)) && creds.Count > 0)
            {
                foreach (var server in servers)
                {
                    var host = new Uri(server).Authority;
                    if (creds.TryGetValue(host, out var c))
                    {
                        user ??= c.User;
                        pass ??= c.Pass;
                        global["username"] = user;
                        global["password"] = pass;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass) || hasToken) return ext;

            foreach (var server in servers)
            {
                try
                {
                    var resp = await _http.PostAsync(server + "/api/auth/login",
                        new StringContent(JsonSerializer.Serialize(new { username = user, password = pass }),
                            Encoding.UTF8, "application/json"), ct);
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    var token = JsonNode.Parse(body)?["data"]?["token"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(token))
                    {
                        global["token"] = token;
                        Log($"已为 {server} 注入认证 token");
                        break;
                    }
                }
                catch { }
            }
            return arr.ToJsonString();
        }
        catch { return ext; }
    }
}
