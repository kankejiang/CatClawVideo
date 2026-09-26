using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CatClawVideo.Core.Services.QemuThunder;

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
    /// <summary>产品 initrd 名（放在 <c>ThunderRuntime</c> 目录下，与 pkg_initrd.gz 并存）。</summary>
    public const string InitrdName = "art_initrd.gz";

    /// <summary>guest 里 ART 桥的监听端口基准（与 Guard VM 的 18481、迅雷的 18080/18090 错开）。</summary>
    private const int PortSeed = 18600;

    private readonly string _runtimeDir;
    private readonly Action<string>? _log;
    private readonly object _sync = new();

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

    /// <summary>把爬虫写的 guest 地址换成宿主隧道地址；没开隧道时原样返回。</summary>
    public string? ProxyBase => ProxyTunnelPort > 0 ? $"http://127.0.0.1:{ProxyTunnelPort}" : null;

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
        lock (_sync)
        {
            if (IsUp && _w is not null && _r is not null) return (_w, _r);
            if (!IsAvailable(_runtimeDir)) { Log("运行时不齐全，缺：" + MissingPieces(_runtimeDir)); return null; }
            if (_vm is null)
            {
                var bridge = PickFreePort(PortSeed);
                var media = PickFreePort(bridge + 1);       // QemuHostRuntime 总要一条 -:20080 的 hostfwd，别撞号
                // 爬虫的播放地址写的是它自己那个 /proxy（guest 里的 9978），给它开一条宿主隧道。
                var tunnel = PickFreePort(media + 1);
                if (bridge == 0 || media == 0) { Log("找不到可用端口"); return null; }
                BridgePort = bridge;
                ProxyTunnelPort = tunnel;
                _dns ??= new ArtDnsServer(_log);      // guest 里所有 Java 域名解析都问到这（见 ArtDnsServer 注释）
                _vm = new QemuHostRuntime(_runtimeDir, media, _log, InitrdName, consoleLogTag: "-art",
                        monitorPort: 0, ctrlPort: _dns.Port, guardPort: bridge, magnetOverride: "none")
                {
                    // 实测：2048MB / 2 vCPU 够 ART + 桥 + 一个源（TCG 下 -smp>4 反而更慢，见 QemuHostRuntime 注释）
                    GuestMemoryMb = 2048,
                    SmpCount = 2,
                    // ⚠ 必须是 virtio-net-device：ART initrd 只 insmod virtio_mmio+virtio_net，
                    //   用 PCI 版 guest 里没有 eth0，hostfwd 永远连不上（实测踩过）。
                    NetDevice = "virtio-net-device,netdev=n0",
                    ProxyTunnel = tunnel > 0 ? (tunnel, GuestProxyPort) : null,
                };
            }
        }
        if (!await _vm!.StartAsync(ct).ConfigureAwait(false)) { Log("QEMU 启动失败"); return null; }

        // ⚠ 不能把「TCP 连上了」当成「桥就绪」：slirp 自己就把三次握手做掉，guest 里还没 listen
        //   也一样 connect 成功（实测：1.5s 就"连上"，随后 ping 15s 超时）。
        //   所以这里用一次真的 ping 当就绪探针，不通就换一条连接重来。
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(4);   // 冷启动：解 226MB initrd + ART 起 VM
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
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
                    lock (_sync) { _sock = c; _w = w; _r = r; }
                    c = null;
                    Log($"guest 桥就绪 127.0.0.1:{BridgePort}（探针应答 {line}）");
                    return (_w, _r);
                }
            }
            catch { /* 没通：下面重连 */ }
            finally { try { c?.Dispose(); } catch { } }
            await Task.Delay(2000, ct).ConfigureAwait(false);
        }
        Log($"等不到 guest 桥（端口 {BridgePort}，超时）");
        return null;
    }

    private static async Task<string?> WaitForLineAsync(StreamReader r, CancellationToken ct)
    {
        var readTask = r.ReadLineAsync();
        var finished = await Task.WhenAny(readTask, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
        return finished == readTask ? await readTask.ConfigureAwait(false) : null;
    }

    /// <summary>收尾：关掉桥连接并停 VM。VM 平时靠 KillOnClose job 兜底，这里管主动退出。</summary>
    public void Dispose()
    {
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
