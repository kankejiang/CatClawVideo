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
/// jar/dex 爬虫运行时（桌面）：<b>两条链路共用同一套行协议</b> ——
/// <list type="bullet">
///   <item><b>ART guest（默认，装了 <c>ThunderRuntime/art_initrd.gz</c> 就启用）</b>：QEMU 里跑真
///   Android 9 的 ART，桥与 TVBox/壳 jar 都在里面，<b>ARM 原生码就地执行</b>。
///   2026-09-26 实测荐片┃多线全链路：detail 0.5s/4347B → player 1.8s 拿到真 m3u8。</item>
///   <item><b>宿主 JRE（回落/纯 .class 的 java 源）</b>：常驻 Java 桥进程，spider jar 经 dex2jar
///   转换后加载。<b>它解不了 Guard 壳</b>（见下），所以 Guard 源只在 guest 里可用。</item>
/// </list>
/// <para><b>Guard 加固包为什么必须在 guest 里解</b>：壳把真 spider dex 加密成
/// <c>assets/ftyshinidie.guard</c>，解密器是 ARM Android native（<c>assets/ftyguard_v8.so</c>，
/// <c>JNI_OnLoad</c> + <c>RegisterNatives</c> 注册 <c>DexNative</c> 的 8 个 native）。
/// x64 JVM 跑不了 ARM 指令；曾用 <b>unidbg 模拟 ARM64</b> 离线解壳，但它自建一套 Android 桩，
/// 结果"看着对而语义不同"（同一条 <c>DECRYPT 10232B</c> 出过两种结果），且随包多 33MB ——
/// <b>2026-09-26 退役</b>。现在由 guest 里的真 ART 让壳自己 <c>System.load()</c> 那个 so，
/// 明文 dex（<c>cache/sharedb/config.db</c>）我们从不接触。</para>
/// <para>解壳器不可用或解壳失败时，退而求其次改用**同族「非 Guard 构建」**的 jar
/// （见 <see cref="NonGuardFallbackJars"/>），它提供同名去掉 <c>Guard</c> 后缀的真实实现
/// （<c>csp_SixVGuard</c> → <c>SixV</c>）。</para>
/// <para>认证预处理：ext global 含 username/password 而缺 token 时，自动向
/// {server}/api/auth/login 登录注入 token（小雅 AListSh 需要）。</para>
/// </summary>
public class JavaSpiderRuntime : ISpiderRuntime, ISpiderProxyRuntime, ISpiderActionRuntime, IDisposable, ISpiderLiveRuntime
{
    public string Id => "jvm-dex";

    /// <summary>
    /// **只读**程序目录：<c>bridge.jar</c> 与 <c>vendor/*</c> 的所在地（安装版是
    /// <c>C:\Program Files\CatClawVideo\JavaBridge</c>，普通用户无写权限）。
    /// </summary>
    private readonly string _bridgeDir;

    /// <summary>
    /// **可写**工作目录 —— 必须与 <see cref="_bridgeDir"/> 分开：jar 转换产物与桥进程的
    /// <c>data</c> 目录都要落盘，写程序目录会抛 <c>UnauthorizedAccessException</c>。
    ///
    /// <para>2026-09-19 用户实测（安装版）：磁力/自带源拉取失败，报
    /// 「Access to the path 'C:\Program Files\CatClawVideo\JavaBridge\converted' is denied.」
    /// —— 开发机跑仓库目录（可写）从不触发，只有安装包才暴露。</para>
    ///
    /// <para>落在 <c>%APPDATA%\CatClawVideo\javabridge\</c>，与其余可写数据同一处
    /// （见 <see cref="AppPaths"/>）。</para>
    /// </summary>
    private readonly string _workDir;

    /// <summary>jar 转换产物目录（原始 jar、Guard 解壳产物、dex2jar 输出）。</summary>
    private readonly string _convertedDir;

    private readonly string _javaExe;
    private readonly Action<string>? _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private Process? _proc;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;

    /// <summary>
    /// ART guest（QEMU 里真 Android ART 跑桥）；只在 <see cref="ArtGuestMode"/> 时用。
    /// 桥的行协议改为走 TCP，<see cref="_stdin"/>/<see cref="_stdout"/> 直接架在 socket 流上，
    /// 因此请求/响应/事件分发那套代码两条链路完全共用。
    /// </summary>
    private CatClawVideo.Core.Services.QemuThunder.QemuArtGuest? _art;

    /// <summary>ART guest 的 jar 供给服务（guest 读不到宿主的盘，只能经 slirp 用 http 取）。</summary>
    private CatClawVideo.Core.Services.QemuThunder.ArtJarServer? _jarServer;
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private int _id;

    /// <summary>桥 JVM 的 KillOnClose job —— 句柄一关，内核就把 JVM 一起收走，不留孤儿。</summary>
    private CatClawVideo.Core.Services.KillOnCloseJob? _job;

    private readonly ConcurrentDictionary<string, bool> _loadedSites = new();
    private readonly ConcurrentDictionary<string, string> _convertedJars = new();

    /// <summary>
    /// 站点 → 改用替代 jar 后的类名（去掉 <c>Guard</c> 后缀）。
    /// <para>Guard 外壳的类名都带 <c>Guard</c> 后缀（<c>DouDouGuard</c> / <c>SixVGuard</c>），
    /// 而解壳出来的真实 dex 里**不带**后缀（<c>DouDou</c> / <c>SixV</c>），桥必须按真实名加载。
    /// guest 解壳与换非 Guard 同族 jar 两条路的映射规则一致。</para>
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _nonGuardClass = new();


    /// <summary>
    /// 配置的 jar 是 Guard 加固、而本平台解不开时，按顺序尝试的**非 Guard 同族 jar**。
    /// <para>判定标准（两者都过才用）：① 不含 <c>assets/*.so</c> + <c>*.guard</c>；
    /// ② <c>classes*.dex</c> 里确实存在「去掉 Guard 后缀」的那个类。</para>
    /// <para>默认值是 TVBox 生态里长期使用的非 Guard 同族构建（提供 SixV / Proxy / AList 等 900+ 类）。
    /// 如需替换，改这里即可（例如换成自己镜像）。</para>
    /// </summary>
    public static List<string> NonGuardFallbackJars { get; } =
    [
        "https://raw.liucn.cc/box/fty.jar",
    ];

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

    public JavaSpiderRuntime(string bridgeDir, string javaExe, Action<string>? log = null,
        string? workDir = null, Func<int>? proxyPort = null)
    {
        _bridgeDir = bridgeDir;
        _javaExe = javaExe;
        _log = log;
        _proxyPort = proxyPort;
        // 可写目录默认落用户数据区；显式传入只是为了测试/特殊部署。
        // ⚠ 绝不回落到 bridgeDir：安装版那是 Program Files，写它就是本次故障。
        _workDir = workDir ?? AppPaths.Sub("javabridge");
        _convertedDir = Path.Combine(_workDir, "converted");
        // ── ART guest（2026-09-25 定案：ARM 原生码 + TVBox/壳 jar 的 dex 全进 QEMU 里的真 ART）──
        // 装了 ThunderRuntime\art_initrd.gz 就默认走这条；两条链路的取舍/延迟对比用 CATCLAW_NO_ART=1 关掉。
        ArtRuntimeDir = Path.Combine(AppContext.BaseDirectory, "ThunderRuntime");
        ArtGuestMode = CatClawVideo.Core.Services.QemuThunder.QemuArtGuest.IsAvailable(ArtRuntimeDir)
                       && Environment.GetEnvironmentVariable("CATCLAW_NO_ART") != "1";
        // 启动就把走哪条桥链路写进日志：两条链路的差异只会以"某个站点不对"的形式浮现，
        // 不写明模式的话排障第一步会变成猜。
        Log($"桥链路：{(ArtGuestMode ? "ART guest（" + ArtRuntimeDir + '\\' + CatClawVideo.Core.Services.QemuThunder.QemuArtGuest.InitrdName + '）' : "宿主 JRE")}"
            + (Environment.GetEnvironmentVariable("CATCLAW_NO_ART") == "1" ? "（CATCLAW_NO_ART=1 手动关掉）" : ""));
        // 桥可用 = bridge.jar + deps（能跑非 Guard 的 jar 爬虫）；Guard 解壳能力单独判定
        // （2026-09-16 拆分：此前把 dex2jar 也算进来，缺它就把全部 jar 源判死 —— 用户实测 46 个源整体消失）
        IsSupported = File.Exists(Path.Combine(bridgeDir, "bridge.jar"))
                      && Directory.Exists(Path.Combine(bridgeDir, "vendor", "deps"));
        // 正常退出先走一次优雅收尾（job 只兜崩溃/被 kill 的情况）
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
    }

    /// <summary>
    /// 收尾桥：发 <c>exit</c> 让它自己结束读循环，等不到再 kill。
    /// 两条链路都在这里收：JRE 杀子进程树，ART guest 关 socket 并停 VM（VM 另有 KillOnClose job 兜底）。
    /// 幂等，可重复调用。
    /// </summary>
    public void Shutdown()
    {
        var p = _proc;
        var art = _art;
        if (p is null && art is null) return;
        try
        {
            // 收尾阶段不再有新请求进来了，直接写：拿 _stdinLock 反而可能在别的线程手里卡死
            try { _stdin?.Write("{\"op\":\"exit\"}"); _stdin?.Flush(); } catch { }
            if (p is not null && !p.WaitForExit(1500)) p.Kill(entireProcessTree: true);
        }
        catch { /* 进程可能已经自己退了 */ }
        finally
        {
            _proc = null;
            _stdin = null;
            _stdout = null;
            if (p is not null)
            {
                try { p.StandardInput.Close(); } catch { }
                if (!p.HasExited) { try { p.Kill(entireProcessTree: true); } catch { } }
            }
            try { art?.Dispose(); } catch { }
            _art = null;
        }
    }

    /// <summary>收尾并释放 job：job 句柄一关，内核保证挂在其上的 JVM 不会活过本进程。</summary>
    public void Dispose()
    {
        Shutdown();
        _job?.Dispose();
        _job = null;
    }


    private void Log(string m) => _log?.Invoke("[jvm] " + m);

    /// <summary>宿主本地 SpiderProxyServer 主端口（懒访问器）；桥进程启动后经 setProxyPort 下发。</summary>
    private readonly Func<int>? _proxyPort;

    /// <summary>最近一次调用的站点（spider 发起的 /proxy 请求不携带 siteKey，回调时按它定位）。</summary>
    private volatile VodSiteInfo? _lastSite;

    /// <summary>Guard 源的壳框架 jar（壳 dex 转换产物）与原始 jar（含解密 so）——load 时下发桥。</summary>
    private readonly ConcurrentDictionary<string, string> _shellJars = new();
    private readonly ConcurrentDictionary<string, string> _rawJars = new();

    /// <summary>
    /// 查找系统里的 java.exe，取**版本最高**的那个（随包 <c>JavaBridge/jre</c> 存在时短路返回，见下）。
    /// <para>扫描顺序：JAVA_HOME → <c>C:\Program Files\Java\*</c> → <c>C:\Program Files\Microsoft\jdk-*</c> → PATH。
    /// 取最高版本是通用兜底策略：bridge.jar 是 major 61（--release 17），太老的 JDK 跑不了新字节码；
    /// 本机常见「PATH 里 17、JAVA_HOME 里 21」或同时装两套 JDK，按目录名版本号排序取最大。</para>
    /// </summary>
    public static string? FindJavaExe()
    {
        // ★ 随包的精简运行时**优先**（JavaBridge/jre，jlink 自 Microsoft OpenJDK 21，MIT 许可）。
        //   两个理由：
        //   ① 开箱即用 —— 装了这份就不要求用户自备 Java（2026-09-22 用户反馈：最常见根因就是机器上没 Java）。
        //   ② 版本可控 —— bridge.jar 现在是 **major 61（--release 17**，为了能在 QEMU guest 的
        //      Alpine OpenJDK 17 里跑同一份字节码），但 unpacker.jar 仍是 65，所以运行时下限并没有降；
        //      随包运行时永远满足，短路返回、不与系统 Java 比大小。
        if (FindBridgeDir() is { } bundledDir)
        {
            var bundled = Path.Combine(bundledDir, "jre", "bin", "java.exe");
            if (File.Exists(bundled)) return bundled;
        }

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
    /// <para>⚠ 会**收集全部候选再挑能力最全**的一个：随包分发的那份只带 `bridge.jar + vendor/deps`（约 3.4MB），
    /// 而开发机的仓库目录通常连 `vendor/dex2jar`（JRE 桥转换管线）一起有 —— 就近返回会把转换能力丢掉
    /// （2026-09-16 实测：随包副本优先后，全部 jar 站点转换失败）。
    /// 并列时取最近的（OrderByDescending 稳定排序，候选按由近到远收集）。</para>
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
                if (Directory.Exists(Path.Combine(dir, "vendor", "dex2jar"))) s += 2;
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

    public Task<string> DetailContentAsync(VodSiteInfo site, string id, CancellationToken ct = default) =>
        CallAsync(site, "detailContent", new JsonArray(id), ct);

    public Task<string> SearchContentAsync(VodSiteInfo site, string keyword, string pg, CancellationToken ct = default) =>
        CallAsync(site, "searchContent", new JsonArray(keyword, pg), ct);

    public Task<string> PlayerContentAsync(VodSiteInfo site, string flag, string id, CancellationToken ct = default) =>
        CallAsync(site, "playerContent", new JsonArray(flag ?? "", id), ct);

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
    /// 确保桥可用。<b>两条链路同一套行协议</b>：ART guest（QEMU 里真 ART，走 TCP）优先，
    /// 其次宿主 JRE（子进程标准流）。握手在 <see cref="HandshakeAsync"/> 里，两边共用。
    /// </summary>
    private async Task EnsureBridgeAsync(CancellationToken ct)
    {
        if (ArtGuestMode)
        {
            _art ??= new CatClawVideo.Core.Services.QemuThunder.QemuArtGuest(ArtRuntimeDir, _log);
            if (_art.IsUp && _stdin is not null) return;
            var link = await _art.ConnectAsync(ct).ConfigureAwait(false);
            if (link is null)
            {
                ArtGuestMode = false;
                Log("ART guest 起不来 → 回落宿主 JRE 桥");
            }
            else
            {
                (_stdin, _stdout) = link.Value;
                Log($"桥已连上 ART guest（127.0.0.1:{_art.BridgePort}）");
                _ = Task.Run(ReadLoopAsync);
            }
        }
        else if (_proc is { HasExited: false }) return;

        if (!ArtGuestMode) await StartJvmBridgeAsync(ct).ConfigureAwait(false);
        await HandshakeAsync(ct).ConfigureAwait(false);
    }

    /// <summary>宿主 JRE 那条路：起 java.exe 跑 bridge.Server，标准流当协议通道。</summary>
    private async Task StartJvmBridgeAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _javaExe,
            // 工作目录必须是**可写**的 _workDir：桥进程启动时会把 data 目录建在相对路径
            // 「data」下（见 bridge.Server 的 data.dir）。原先是 _bridgeDir → 安装版
            // 直接写 Program Files 被拒。（classpath 因此改用绝对路径，见下。）
            WorkingDirectory = _workDir,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-Dfile.encoding=UTF-8");
        // TLS 握手日志（stderr → home-debug.log）：排障 spider 的出站 HTTPS 连接目标（SNI 主机名）
        psi.ArgumentList.Add("-Djavax.net.debug=ssl:handshake");
        // ⚠️ 必须关字节码校验：dex2jar 从 OLLVM 混淆过的 dex 还原出来的类，
        // 经常**缺 StackMapTable**（`VerifyError: Expecting a stackmap frame at branch target N`），
        // 校验器直接拒绝加载 → 站点整站不可用（2026-09-15 实测：原创/糯米/海绵/厂长/光影
        // 全部栽在这上面，而它们的内容其实是好的）。JVM 21 起无法按类关闭校验，
        // 只能在 JVM 级关掉；关掉后这类畸形类可以正常加载运行。
        psi.ArgumentList.Add("-Xverify:none");
        // 父进程 PID：桥里的看门狗按它自杀。Windows 上 job object 未必挂得进去
        // （应用本身已在别的 job 里时 AssignProcessToJobObject 直接失败，实测 win32=5），
        // 而 stdin 的写句柄会被其它子进程继承走 → EOF 也不可靠。两条都不靠时才不漏孤儿。
        psi.ArgumentList.Add($"-Dcatclaw.ppid={Environment.ProcessId}");
        psi.ArgumentList.Add("-cp");
        // 绝对路径：工作目录已改为 _workDir（可写区），相对路径会解析不到 bridge.jar
        psi.ArgumentList.Add($"{Path.Combine(_bridgeDir, "bridge.jar")};{Path.Combine(_bridgeDir, "vendor", "deps", "*")}");
        psi.ArgumentList.Add("bridge.Server");
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Java 桥进程启动失败");
        Log($"桥进程已启动 pid={proc.Id}");
        _stdin = proc.StandardInput;
        _stdout = proc.StandardOutput;
        _ = Task.Run(() =>
        {
            try
            {
                string? l;
                while ((l = proc.StandardError.ReadLine()) != null)
                    if (l.Length > 0) Log("stderr: " + l[..Math.Min(300, l.Length)]);
                Log("stderr 流关闭");
            }
            catch { }
        });
        _proc = proc;
        // ⚠ 必须绑 job：.NET 在 Windows 上不会随父进程退出杀掉子进程。
        // 此前每次关应用都留下一个 java.exe（实测一次会话 17 个），其中一个把 bridge.jar
        // 映射住，导致改完桩 build.cmd 报 FileSystemException 重打包失败（2026-09-25）。
        _job ??= CatClawVideo.Core.Services.KillOnCloseJob.Create();
        if (_job is { } j && j.Attach(proc)) { /* 绑上了：应用一死内核就收走 JVM */ }
        else Log($"桥 JVM 未能绑进 KillOnClose job（{_job?.LastError}；"
                 + "父进程被强杀时靠桥自己的 ppid 看门狗退出）");

        // 常驻读循环：请求-响应按 id 分发；桥主动上行的 UI 事件（ev 字段）回调 UiEvent
        _ = Task.Run(ReadLoopAsync);
    }

    /// <summary>握手（两条链路共用）：ping 通了才算就绪，然后把宿主 proxy 端口下发给桥。</summary>
    private async Task HandshakeAsync(CancellationToken ct)
    {
        var pong = await RoundTripAsync(new JsonObject { ["id"] = 0, ["op"] = "ping" }, TimeSpan.FromSeconds(15), ct);
        if (!pong.ContainsKey("ok") || pong["ok"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("Java 桥握手失败");
        Log(ArtGuestMode ? "ART guest 里的桥就绪" : "桥进程就绪");

        // 下发宿主 proxy 端口：Guard 系网盘源靠 SpiderApi.getAddress/getPort 拼「云盘配置」
        // 数据端点 URL，桥桩返回空会让 spider 内部 Gson 解析到错误文本直接炸（Expected
        // BEGIN_OBJECT but was STRING → detailContent 整体失败，2026-09-24 实测）。
        // 每次新桥都要重发（宿主 JRE 随进程重置；ART guest 的桥一连接一个会话）。
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

    /// <summary>
    /// 把当前桥会话判废并后台重开：期间请求快速失败，重开后 <see cref="_loadedSites"/> 已清空，
    /// 下一次调用会在新桥（或重启后的 ART guest）上真跑。
    /// </summary>
    private void ResetBridge(string why)
    {
        if (Interlocked.CompareExchange(ref _resetting, 1, 0) != 0) return;
        Log($"桥无响应（{why}）→ 重置爬虫引擎：丢掉当前桥会话，期间请求立刻报错，不再让后面的一条条排队等超时");
        _ = Task.Run(() =>
        {
            try
            {
                Shutdown();
                _loadedSites.Clear();
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

    /// <summary>常驻读桥输出（JRE 的 stdout 或 ART 的 socket 流）：按 id 分发响应、按 ev 分发 UI 事件。</summary>
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
        Log("桥 stdout 读循环退出");
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

    private async Task<JsonObject> RoundTripAsync(JsonObject req, TimeSpan timeout, CancellationToken ct)
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
                if (op != "ping" && op != "exit")
                    ResetBridge($"op={op} id={expectId} 在 {timeout.TotalSeconds:F0}s 内没回来");
                throw new TimeoutException($"Java 桥响应超时（{timeout.TotalSeconds:F0}s，id={expectId}）");
            }
        }
        finally { _pendingResponses.TryRemove(expectId, out _); }
    }

    private async Task<string> CallAsync(VodSiteInfo site, string method, JsonArray args, CancellationToken ct)
    {
        await EnsureBridgeAsync(ct);
        var jar = await EnsureConvertedJarAsync(site, ct);
        await EnsureSiteLoadedAsync(site, jar, ct);
        _lastSite = site;   // spider 稍后发起的 /proxy 回调不带 siteKey，靠它定位

        var req = new JsonObject
        {
            ["id"] = Interlocked.Increment(ref _id),
            ["op"] = "call",
            ["site"] = site.Key,
            ["method"] = method,
            ["args"] = args,
        };
        var resp = await RoundTripAsync(req, TimeSpan.FromSeconds(90), ct);
        if (resp["ok"]?.GetValue<bool>() != true)
            throw new InvalidOperationException($"spider {site.Key}.{method}: {resp["error"]}");
        return resp["result"]?.GetValue<string>() ?? "{}";
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

            // ART guest：爬虫自带的 /proxy 就在 guest 里跑着（桥的 Art.serveProxy），宿主经 slirp
            // 隧道直接要字节。行协议那条 proxy op 靠 outFile 回传响应体，而 outFile 是 Windows 路径，
            // guest 里的 Linux 进程写出来的东西宿主读不到（2026-09-26 实测）。
            if (ArtGuestMode && _art?.ProxyBase is { } tunnel)
                return await ProxyViaTunnelAsync(tunnel, site.Key, query, ct).ConfigureAwait(false);

            var q = new JsonObject();
            foreach (var kv in query) q[kv.Key] = kv.Value ?? "";
            var outPath = Path.Combine(Path.GetTempPath(), $"spproxy-{Guid.NewGuid():N}.bin");
            var req = new JsonObject
            {
                ["id"] = Interlocked.Increment(ref _id),
                ["op"] = "proxy",
                ["site"] = site.Key,
                ["query"] = q,
                ["outFile"] = outPath,
            };
            var resp = await RoundTripAsync(req, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            if (resp["ok"]?.GetValue<bool>() != true)
            {
                Log($"proxy 回调失败（do={query.GetValueOrDefault("do")}）: {resp["error"]}");
                return ((int)502, "text/plain", (byte[]?)null);
            }
            var head = resp["result"]?.GetValue<string>() ?? "";
            var parts = head.Split('|');
            if (parts.Length != 3 || !int.TryParse(parts[0], out var status))
            {
                Log($"proxy 回调：返回头异常 {head}");
                return ((int)502, "text/plain", (byte[]?)null);
            }
            byte[]? body = null;
            var len = long.TryParse(parts[2], out var l) ? l : 0;
            if (len > 0 && File.Exists(outPath)) body = await File.ReadAllBytesAsync(outPath, ct).ConfigureAwait(false);
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
            Log($"proxy 回调 ok：do={query.GetValueOrDefault("do")} → {status} {parts[1]} {body?.Length ?? 0}B");
            return (status, parts[1], body);
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

    private async Task EnsureSiteLoadedAsync(VodSiteInfo site, string jarPath, CancellationToken ct)
    {
        if (_loadedSites.TryGetValue(site.Key, out _)) return;
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
        // ART guest：桥在 guest 里，rawJar 由 ART 直接吃（classes.dex）、壳自己 System.load()
        // 那个 arm64 ftyguard so 并解出真 dex（2026-09-25 实测：装载 3272ms、homeContent 183ms）。
        // ⇒ 不下发 shellJar/realJar 这两个宿主转换产物，也不启动独立 Guard VM。
        if (ArtGuestMode)
        {
            // guest 读不到宿主的盘：换成宿主 jar 服务的 URL，桥里 Art.materialize() 取回归档
            var raw = _rawJars.TryGetValue(site.Key, out var rp) && File.Exists(rp) ? rp : jarPath;
            // 纯 .class jar（无 classes.dex，如 fty.jar 一族）guest 的 ART 吃不了：先 d8 转 dex 再供
            var serve = IsPureClassJar(raw) ? await EnsureDexJarAsync(raw, ct) : raw;
            _jarServer ??= new CatClawVideo.Core.Services.QemuThunder.ArtJarServer(_log);
            var url = _jarServer.UrlFor(Path.GetFileName(serve).Replace("raw-", "").Replace(".jar", ""), serve);
            req["jars"] = new JsonArray(url);
            req["rawJar"] = url;
            Log($"{site.Name}: ART guest 取 jar ← {url}");
        }
        // 壳框架模式（Guard 包，宿主 JRE 那条路）：className 用壳名（MyDriveGuard），壳/原始/解壳三 jar 下发——
        // 壳框架的「已登录+启用中」对话框/扫码/网盘管理原生运行，UI 经 UiBridge 上行宿主渲染
        else if (_convertedJars.TryGetValue(site.Key, out _) &&
            _shellJars.TryGetValue(site.Key, out var shellJar) && File.Exists(shellJar))
        {
            req["className"] = className.EndsWith("Guard", StringComparison.Ordinal) ? className : className + "Guard";
            req["shellJar"] = shellJar;
            req["rawJar"] = _rawJars.TryGetValue(site.Key, out var rj) ? rj : "";
            req["realJar"] = jarPath;

            // 宿主 JRE 那条路才需要独立 Guard VM（2026-09-24 拍板架构）：解密/签名/proxyInvoke 走
            // Guard VM 里的 ftyguard so；VM 就绪才下发 guardPort，缺失则桥的 GuardSession 直接报错
            // （unidbg 已退出运行时，2026-09-25）。
            if (!string.IsNullOrEmpty(req["rawJar"]?.GetValue<string>()) &&
                CatClawVideo.Core.Services.QemuThunder.GuardRuntime.Engine is { } guard)
            {
                try
                {
                    var rawJarPath = req["rawJar"]!.GetValue<string>()!;
                    var jarHash = Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes(site.Jar?.Split(";md5;")[0] ?? site.Jar ?? "")))[..24].ToLowerInvariant();
                    guard.RegisterJar(jarHash, rawJarPath);
                    if (await guard.EnsureLoadedAsync(jarHash, ct).ConfigureAwait(false))
                    {
                        req["guardPort"] = CatClawVideo.Core.Services.QemuThunder.QemuGuardEngine.GuardPort;
                        Log($"Guard QEMU 通道就绪（jar {jarHash}，端口 {req["guardPort"]}）");
                    }
                    else
                    {
                        Log("Guard QEMU 通道未就绪（guardPort 不下发，ARM 调用将明确报错）");
                    }
                }
                catch (Exception gex)
                {
                    Log($"Guard QEMU 通道异常（guardPort 不下发，ARM 调用将明确报错）: {gex.Message}");
                }
            }
        }
        var resp = await RoundTripAsync(req, TimeSpan.FromSeconds(60), ct);
        if (resp["ok"]?.GetValue<bool>() != true)
        {
            // 加载失败必须留痕：此前只 log 成功分支，导致「站点没反应」无从查因
            Log($"站点 {site.Key} 加载失败（类名 {className}，jar {Path.GetFileName(jarPath)}）: {resp["error"]}");
            // ⚠ 壳框架模式失败（壳 dex 的 OLLVM 混淆代码在桌面 JVM 有未桩化的运行时依赖，
            //   如 merge.Ku 的解密器 NPE）→ **自动降级**真实类模式（去 Guard 后缀 + 解壳 jar）：
            //   壳框架的弹窗/扫码虽丢失，但站点本体恢复可用（2026-09-24 止损）。
            if (req.ContainsKey("shellJar"))
            {
                _shellJars.TryRemove(site.Key, out _);
                _rawJars.TryRemove(site.Key, out _);
                _loadedSites.TryRemove(site.Key, out _);
                var realName = className.EndsWith("Guard", StringComparison.Ordinal)
                    ? className[..^"Guard".Length] : className;
                var req2 = new JsonObject
                {
                    ["id"] = Interlocked.Increment(ref _id),
                    ["op"] = "load",
                    ["site"] = site.Key,
                    ["className"] = realName,
                    ["ext"] = PrepareExtAsync(site, ct).GetAwaiter().GetResult(),
                    ["jars"] = new JsonArray(jarPath),
                };
                var resp2 = await RoundTripAsync(req2, TimeSpan.FromSeconds(60), ct);
                if (resp2["ok"]?.GetValue<bool>() == true)
                {
                    _loadedSites[site.Key] = true;
                    Log($"站点 {site.Key} 已降级为真实类模式加载（壳框架不可用）");
                    return;
                }
                Log($"站点 {site.Key} 真实类降级也失败: {resp2["error"]}");
            }
            throw new InvalidOperationException($"spider {site.Key} 加载失败: {resp["error"]}");
        }
        _loadedSites[site.Key] = true;
        Log($"站点 {site.Key} 已加载");
    }

    // ═══════════ jar 转换管线 ═══════════

    private async Task<string> EnsureConvertedJarAsync(VodSiteInfo site, CancellationToken ct)
    {
        if (_convertedJars.TryGetValue(site.Key, out var cached) && File.Exists(cached))
        {
            // 缓存复用：壳框架 jar 按命名规则推断（ConvertJarAsync 已转换过则文件在）
            if (!_shellJars.ContainsKey(site.Key) && !string.IsNullOrEmpty(site.Jar))
            {
                var h = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(site.Jar.Split(";md5;")[0])))[..24].ToLowerInvariant();
                var sh = Path.Combine(_convertedDir, h + "-shell.jar");
                if (File.Exists(sh))
                {
                    _shellJars[site.Key] = sh;
                    _rawJars[site.Key] = Path.Combine(_convertedDir, "raw-" + h + ".jar");
                }
            }
            return cached;
        }

        var jarUrl = site.Jar ?? "";
        var semi = jarUrl.IndexOf(";md5;", StringComparison.OrdinalIgnoreCase);
        var expectMd5 = semi > 0 ? jarUrl[(semi + 5)..].Trim() : null;
        if (semi > 0) jarUrl = jarUrl[..semi];
        if (string.IsNullOrEmpty(jarUrl))
            throw new InvalidOperationException($"站点 {site.Name} 缺少 spider jar 地址");

        var configured = site.Api.StartsWith("csp_", StringComparison.OrdinalIgnoreCase) ? site.Api[4..] : site.Api;

        var conv = await ConvertJarAsync(jarUrl, expectMd5, ct, null, downloadOnly: ArtGuestMode);
        if (conv is { } ok)
        {
            // Guard 外壳类名带 Guard 后缀，真实 dex 里不带（DouDouGuard → DouDou）。
            // 判定依据必须是「dex 里实际存在哪个类」，不能靠「原包是否 IsGuarded」推断 ——
            // 离线导入/复用手工解好的产物时，那份 raw 已经不含 .so/.guard，IsGuarded 会误判成
            // 非 Guard 包，于是按 DouDouGuard 去加载 → 必然 ClassNotFoundException（实测踩过）。
            // ⚠ 壳框架模式（shellJar 可用）**保留 Guard 名**：壳框架内部经 DexNative 桩取真实类，
            //   其「已登录+启用中」对话框/扫码 UI 只在壳框架里——去掉后缀就丢了（2026-09-24）。
            if (configured.EndsWith("Guard", StringComparison.Ordinal))
            {
                var hasShell = ok.ShellJar is not null;
                var real = configured[..^"Guard".Length];
                if (!hasShell && ok.DexSource is { } src && !JarHasClass(src, configured) && JarHasClass(src, real))
                {
                    _nonGuardClass[site.Key] = real;
                    Log($"{site.Name}: 真实类名 {configured} → {real}");
                }
                if (hasShell)
                {
                    _shellJars[site.Key] = ok.ShellJar;
                    if (ok.RawJar is { } rj) _rawJars[site.Key] = rj;
                    Log($"{site.Name}: 壳框架模式（{Path.GetFileName(ok.ShellJar)}）");
                }
            }
            _convertedJars[site.Key] = ok.Path;
            // ART 模式：ok.Path 就是原始 jar（没转换），后面的 load 请求要拿它喂 guest
            if (ArtGuestMode) _rawJars[site.Key] = ok.Path;
            return ok.Path;
        }

        // ── 解壳不可用/失败：换同族非 Guard 构建 ──
        // 非 Guard 版里真实类名同样不带 Guard 后缀（SixVGuard → SixV）。
        var alt = configured.EndsWith("Guard", StringComparison.Ordinal)
            ? configured[..^"Guard".Length]
            : configured;
        Log($"{site.Name} 的 spider jar 是 Guard 加固包且未能解壳；改试非 Guard 同族 jar（目标类 {alt}）…");

        foreach (var fb in NonGuardFallbackJars)
        {
            var c = await ConvertJarAsync(fb, null, ct, alt);
            if (c is null) continue;
            _nonGuardClass[site.Key] = alt;
            _convertedJars[site.Key] = c.Value.Path;
            Log($"{site.Name}: 已改用非 Guard jar（{fb}），类名 {configured} → {alt}");
            return c.Value.Path;
        }

        throw new NotSupportedException(
            $"{site.Name} 的 spider jar 是 Guard 加固包，仅 ART guest 能解壳（当前不可用）且未找到提供 {alt} 的非 Guard 替代 jar");
    }

    /// <summary>
    /// 一次 jar 转换的结果。
    /// <para><paramref name="DexSource"/> 是**含 classes*.dex 的那份 jar**（普通包=下载原件，
    /// Guard 包=解壳产物）；调用方据此查真实类名。为 null 表示 raw 文件缺失、无法查证。</para>
    /// </summary>
    /// <summary>Path=真实类转换产物；DexSource=转换输入（解壳产物/原始 jar）；RawJar=原始 Guard jar（含解密 so，壳框架用）；ShellJar=壳 dex 转换产物（壳框架类）。</summary>
    private readonly record struct JarConversion(string Path, string? DexSource, string? RawJar = null, string? ShellJar = null);

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
            "ART guest 加载纯 .class jar 需要 java 运行时（本机未找到 java.exe）");
        _d8JarPath ??= FindSdkTool(out _androidJarPath);
        var d8 = _d8JarPath ?? throw new InvalidOperationException(
            "ART guest 加载纯 .class jar 需要 Android build-tools 的 d8（未找到 build-tools/*/lib/d8.jar）");

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
    /// 下载 → 校验 → Guard 解壳 → dex2jar 转换。
    /// <para>返回转换后的 java jar；**返回 null 表示这个 jar 在本平台不可用**</para>
    /// （Guard 且解壳失败/解壳器缺失，或 <paramref name="requireClass"/> 指定的类不在其中），
    /// 由调用方决定换哪个 jar —— 用 null 而不是抛异常，是为了让「换 jar」成为正常流程而不是错误路径。</para>
    /// </summary>
    /// <param name="downloadOnly">ART guest 模式：<b>只取原始 jar</b>，不做解壳也不做 dex2jar
    /// —— 壳在 guest 里由真 ART + arm64 so 自己解，宿主这两步纯属白费（2026-09-25 实测）。</param>
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

    private async Task<JarConversion?> ConvertJarAsync(string jarUrl, string? expectMd5, CancellationToken ct,
        string? requireClass, bool downloadOnly = false)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(jarUrl)))[..24].ToLowerInvariant();
        var rawPath = Path.Combine(_convertedDir, "raw-" + hash + ".jar");
        var outPath = Path.Combine(_convertedDir, hash + "-java.jar");
        // 转换产物必须落**可写目录**（_convertedDir = %APPDATA%\...\javabridge\converted）。
        // 原先写 _bridgeDir/converted：安装版是 Program Files，创建即被拒
        // （2026-09-19 用户实测报错原文：Access to the path '...\JavaBridge\converted' is denied.）
        Directory.CreateDirectory(_convertedDir);

        // 已转换过：但**仍要按需校验类是否存在** —— 同一个 jar 对不同站点可能「有的类在、有的不在」
        // （实测：非 Guard fty.jar 有 SixV 却没有 JPJ）。直接用缓存会让缺失的类漏到 load 阶段，
        // 报成 ClassNotFoundException，掩盖「该站无替代实现」这个真实结论。
        if (File.Exists(outPath) && !downloadOnly)
        {
            // 查类名要查**含 classes.dex 的那份**：Guard 包的 raw 只有外壳 stub，
            // 真实类在解壳产物里（复用缓存时同样适用）
            var src = File.Exists(rawPath) && IsGuarded(rawPath)
                ? Path.Combine(_convertedDir, "raw-" + hash + "-unpacked.jar")
                : rawPath;
            if (!File.Exists(src)) src = File.Exists(rawPath) ? rawPath : null;
            if (requireClass is not null && src is not null && !JarHasClass(src, requireClass))
            {
                Log($"缓存 jar 不含类 {requireClass}: {Path.GetFileName(outPath)}");
                return null;
            }
            // 缓存命中也要留痕：此前静默返回，遇到「没下载没解壳却又没反应」时无从查因
            Log($"复用已转换 jar: {Path.GetFileName(outPath)}");
            var cachedShell = await EnsureShellJarAsync(rawPath, hash, ct).ConfigureAwait(false);
            return new JarConversion(outPath, src,
                File.Exists(rawPath) ? rawPath : null,
                cachedShell);
        }

        if (!File.Exists(rawPath)) await FetchRawJarAsync(rawPath, jarUrl, expectMd5, ct).ConfigureAwait(false);
        if (downloadOnly) return new JarConversion(rawPath, rawPath, rawPath, null);

        // ── Guard 加固（assets 下有 .so native 解密器 + .guard 密文）：只有 ART guest 能解 ──
        string? shellJar = null;
        var dexSource = rawPath;
        if (IsGuarded(rawPath))
        {
            // Guard 加固包**只有 QEMU guest 能解**（2026-09-26 退役 unidbg 离线解壳器）：
            // 它自建一套 Android 桩，结果"看着对而语义不同"，随包还多占 33MB。
            // ART 模式下这段根本到不了（ConvertJarAsync 用 downloadOnly 提前返回），
            // 真到了说明本机没开 guest —— 明确说清楚，别再静默退回非 Guard 同族 jar。
            if (!ArtGuestMode)
            {
                Log($"跳过 Guard 加固 jar：解壳只能在 ART guest 里做（缺 art_initrd.gz 或设了 CATCLAW_NO_ART=1）: {Path.GetFileName(rawPath)}");
                return null;
            }
        }

        if (requireClass is not null && !JarHasClass(dexSource, requireClass))
        {
            Log($"跳过不含类 {requireClass} 的 jar: {Path.GetFileName(rawPath)}");
            return null;
        }

        // dex2jar 转换（普通 jar 直接用原件；Guard 包用解壳后的明文 dex）
        await ConvertDex2JarAsync(dexSource, outPath, ct).ConfigureAwait(false);
        Log($"jar 转换完成: {Path.GetFileName(outPath)}");
        return new JarConversion(outPath, dexSource, rawPath, shellJar);
    }

    /// <summary>
    /// 壳框架 jar 就绪（缓存命中直接返回）：原始 Guard jar 的壳 dex 过 dex2jar，
    /// 并剔除 DexNative.class（桌面由桥内置替身接 GuardSession）。失败返回 null（回退真实类模式）。
    /// </summary>
    private async Task<string?> EnsureShellJarAsync(string rawPath, string hash, CancellationToken ct)
    {
        if (!IsGuarded(rawPath)) return null;
        var shellJar = Path.Combine(_convertedDir, hash + "-shell.jar");
        if (File.Exists(shellJar)) return shellJar;
        try
        {
            await ConvertDex2JarAsync(rawPath, shellJar, ct).ConfigureAwait(false);
            StripEntry(shellJar, "com/github/catvod/spider/DexNative.class");
            Log($"壳框架 jar 转换完成: {Path.GetFileName(shellJar)}");
            return shellJar;
        }
        catch (Exception ex)
        {
            Log($"壳框架 jar 转换失败（回退真实类模式）: {ex.Message}");
            try { File.Delete(shellJar); } catch { }
            return null;
        }
    }

    /// <summary>dex2jar 转换（共用）：dexSource（jar/dex）→ outPath。失败抛异常。</summary>
    private async Task ConvertDex2JarAsync(string dexSource, string outPath, CancellationToken ct)
    {        var psi = new ProcessStartInfo
        {
            FileName = _javaExe,
            // classpath 与工作目录都走绝对路径：工具在只读程序目录里，工作目录却在可写区
            Arguments = $"-cp \"{Path.Combine(_bridgeDir, "vendor", "dex2jar", "*")}\" com.googlecode.dex2jar.tools.Dex2jarCmd \"{dexSource}\" -o \"{outPath}\" --force",
            WorkingDirectory = _workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 与所有批处理型 java 工具一致：一律给「立即 EOF 的 stdin」，
            // 杜绝「从 GUI 宿主继承到永不 EOF 的管道 → 等 stdin 卡死」这一类问题
            RedirectStandardInput = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("dex2jar 启动失败");
        try { p.StandardInput.Close(); } catch { }
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0 || !File.Exists(outPath))
            throw new InvalidOperationException($"dex2jar 转换失败: {Path.GetFileName(dexSource)}");
    }

    /// <summary>从 zip 产物里剔除一个条目（壳 jar 剔除 DexNative.class——桌面由桥内置替身接 GuardSession）。</summary>
    private static void StripEntry(string jarPath, string entryName)
    {
        using var fs = new FileStream(jarPath, FileMode.Open, FileAccess.ReadWrite);
        using var archive = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Update);
        var e = archive.GetEntry(entryName);
        e?.Delete();
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
    private static bool IsGuarded(string jarPath)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(jarPath);
            bool hasSo = false, hasGuard = false;
            foreach (var e in zip.Entries)
            {
                var lower = e.FullName.ToLowerInvariant();
                if (lower.StartsWith("assets/") && lower.EndsWith(".so")) hasSo = true;
                if (lower.EndsWith(".guard")) hasGuard = true;
            }
            return hasSo && hasGuard;
        }
        catch { return false; }
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
