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
/// jar/dex 爬虫运行时（桌面 JVM 版）：启动常驻 Java 桥进程（JavaBridge/bridge.Server，
/// stdin/stdout 每行一条 JSON），spider jar 经 dex2jar 转换为标准 jar 后由桥加载。
/// <para><b>Guard 加固包的 PC 侧脱壳</b>：Guard 把真正的 spider dex 加密成
/// <c>assets/ftyshinidie.guard</c>，解密器是 ARM Android native
/// （<c>assets/ftyguard_v8.so</c>，<c>JNI_OnLoad</c> + <c>RegisterNatives</c> 注册
/// <c>com.github.catvod.spider.DexNative</c>）；x64 JVM 跑不了 ARM 指令。
/// 本运行时改用 <b>unidbg（Unicorn）模拟 ARM64 Android 进程</b>执行该 SO 完成解密
/// （见 <c>JavaBridge/unidbg-src/bridge/GuardUnpacker.java</c>），拿回明文 dex 后照常走 dex2jar。
/// 该壳经实测<b>没有反模拟检测</b>（SO 只有 18 个外部符号且全是 libc，不读 /proc、不 ptrace），
/// 因此解壳可离线稳定复现。</para>
/// <para>解壳器不可用或解壳失败时，退而求其次改用**同族「非 Guard 构建」**的 jar
/// （见 <see cref="NonGuardFallbackJars"/>），它提供同名去掉 <c>Guard</c> 后缀的真实实现
/// （<c>csp_SixVGuard</c> → <c>SixV</c>）。</para>
/// <para>认证预处理：ext global 含 username/password 而缺 token 时，自动向
/// {server}/api/auth/login 登录注入 token（小雅 AListSh 需要）。</para>
/// </summary>
public class JavaSpiderRuntime : ISpiderRuntime, ISpiderProxyRuntime
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
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private int _id;

    private readonly ConcurrentDictionary<string, bool> _loadedSites = new();
    private readonly ConcurrentDictionary<string, string> _convertedJars = new();

    /// <summary>
    /// 站点 → 改用替代 jar 后的类名（去掉 <c>Guard</c> 后缀）。
    /// <para>Guard 外壳的类名都带 <c>Guard</c> 后缀（<c>DouDouGuard</c> / <c>SixVGuard</c>），
    /// 而解壳出来的真实 dex 里**不带**后缀（<c>DouDou</c> / <c>SixV</c>），桥必须按真实名加载。
    /// 无论是 unidbg 解壳还是换非 Guard 同族 jar，映射规则一致。</para>
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _nonGuardClass = new();

    /// <summary>unidbg 解壳器是否就绪：<c>vendor/unidbg/unpacker.jar</c> + <c>unidbg-android-*.jar</c>。</summary>
    private readonly bool _unidbgReady;

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

    /// <summary>
    /// 能否在本机解开 **Guard 加固**（需要 <c>vendor/dex2jar</c> + unidbg 解壳器）。
    /// <para>与 <see cref="IsSupported"/> 分开：桥本身能跑就够跑非 Guard 的 jar 爬虫；
    /// 之前把 dex2jar 也算进 IsSupported，导致缺一个目录就把**全部** jar 源判为不可播
    /// （2026-09-16 实测：订阅里 46 个 csp_*Guard 站点整体消失，只剩 3 个 http 脚本源）。</para>
    /// </summary>
    public bool GuardUnpackAvailable { get; }

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
        // 桥可用 = bridge.jar + deps（能跑非 Guard 的 jar 爬虫）；Guard 解壳能力单独判定
        // （2026-09-16 拆分：此前把 dex2jar 也算进来，缺它就把全部 jar 源判死 —— 用户实测 46 个源整体消失）
        IsSupported = File.Exists(Path.Combine(bridgeDir, "bridge.jar"))
                      && Directory.Exists(Path.Combine(bridgeDir, "vendor", "deps"));
        _unidbgReady = DetectUnidbg(bridgeDir);
        GuardUnpackAvailable = IsSupported
                               && Directory.Exists(Path.Combine(bridgeDir, "vendor", "dex2jar"))
                               && _unidbgReady;
    }

    /// <summary>探测 unidbg 解壳器是否已随包部署（缺省时 Guard 站点自动退回非 Guard 同族 jar）。</summary>
    private static bool DetectUnidbg(string bridgeDir)
    {
        try
        {
            var dir = Path.Combine(bridgeDir, "vendor", "unidbg");
            return File.Exists(Path.Combine(dir, "unpacker.jar"))
                   && Directory.EnumerateFiles(dir, "unidbg-android-*.jar").Any();
        }
        catch
        {
            return false;
        }
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
    /// 查找系统里的 java.exe，取**版本最高**的那个。
    /// <para>扫描顺序：JAVA_HOME → <c>C:\Program Files\Java\*</c> → <c>C:\Program Files\Microsoft\jdk-*</c> → PATH。
    /// 必须取最高版本：<c>bridge.jar</c> 与 unidbg 解壳器都是按 JDK 21 编译的（class file 65），
    /// 落到 JDK 17 会抛 <c>UnsupportedClassVersionError</c>；而本机常见「PATH 里 17、JAVA_HOME 里 21」
    /// 或同时装了两套 JDK 的情况，所以按目录名里的版本号排序取最大。</para>
    /// </summary>
    public static string? FindJavaExe()
    {
        // ★ 随包的精简运行时**优先**（JavaBridge/jre，jlink 自 Microsoft OpenJDK 21，MIT 许可）。
        //   两个理由：
        //   ① 开箱即用 —— 装了这份就不要求用户自备 JDK。社区反馈里最常见的「显示需要 spider 运行」
        //      根因就是用户机器上没有 Java（2026-09-22 用户反馈）。
        //   ② 版本可控 —— bridge.jar 是 **major 65（Java 21）** 编译的，而下面按"版本号最大"
        //      挑系统 Java 的做法，在用户装了 Java 17 时会挑中 17 ⇒ 桥 UnsupportedClassVersionError
        //      直接起不来。随包运行时永远是对的版本，所以**短路返回**、不与系统 Java 比大小。
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
    /// 而开发机的仓库目录通常连 `vendor/dex2jar + vendor/unidbg`（Guard 解壳）一起有 —— 就近返回会把解壳能力丢掉
    /// （2026-09-16 实测：随包副本优先后，所有 csp_*Guard 站点都报「未部署 unidbg 解壳器」解不开）。
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
                var un = Path.Combine(dir, "vendor", "unidbg");
                if (File.Exists(Path.Combine(un, "unpacker.jar")) &&
                    Directory.EnumerateFiles(un, "unidbg-android-*.jar").Any()) s += 4;
            }
            catch { }
            return s;
        }
    }

    // ═══════════ ISpiderRuntime 协议 ═══════════

    public Task<string> HomeContentAsync(VodSiteInfo site, CancellationToken ct = default) =>
        CallAsync(site, "homeContent", new JsonArray(1), ct);

    public Task<string> CategoryContentAsync(VodSiteInfo site, string tid, string pg, CancellationToken ct = default) =>
        CallAsync(site, "categoryContent", new JsonArray(tid, pg), ct);

    public Task<string> DetailContentAsync(VodSiteInfo site, string id, CancellationToken ct = default) =>
        CallAsync(site, "detailContent", new JsonArray(id), ct);

    public Task<string> SearchContentAsync(VodSiteInfo site, string keyword, string pg, CancellationToken ct = default) =>
        CallAsync(site, "searchContent", new JsonArray(keyword, pg), ct);

    public Task<string> PlayerContentAsync(VodSiteInfo site, string flag, string id, CancellationToken ct = default) =>
        CallAsync(site, "playerContent", new JsonArray(flag ?? "", id), ct);

    // ═══════════ 进程与调用 ═══════════

    private async Task<Process> EnsureProcessAsync(CancellationToken ct)
    {
        if (_proc is { HasExited: false }) return _proc;
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
        psi.ArgumentList.Add("-cp");
        // 绝对路径：工作目录已改为 _workDir（可写区），相对路径会解析不到 bridge.jar
        psi.ArgumentList.Add($"{Path.Combine(_bridgeDir, "bridge.jar")};{Path.Combine(_bridgeDir, "vendor", "deps", "*")};{Path.Combine(_bridgeDir, "vendor", "unidbg", "*")}");
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

        // 常驻读循环：请求-响应按 id 分发；桥主动上行的 UI 事件（ev 字段）回调 UiEvent
        _ = Task.Run(() => ReadLoopAsync(proc));

        // 握手
        var pong = await RoundTripAsync(new JsonObject { ["id"] = 0, ["op"] = "ping" }, TimeSpan.FromSeconds(15), ct);
        if (!pong.ContainsKey("ok") || pong["ok"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("Java 桥握手失败");
        Log("桥进程就绪");

        // 下发宿主 proxy 端口：Guard 系网盘源靠 SpiderApi.getAddress/getPort 拼「云盘配置」
        // 数据端点 URL，桥桩返回空会让 spider 内部 Gson 解析到错误文本直接炸（Expected
        // BEGIN_OBJECT but was STRING → detailContent 整体失败，2026-09-24 实测）。
        // 每次新桥进程都要重发（进程内静态字段随进程重置）。
        var port = _proxyPort?.Invoke() ?? 0;
        if (port > 0)
        {
            var pp = await RoundTripAsync(new JsonObject { ["id"] = 0, ["op"] = "setProxyPort", ["port"] = port },
                TimeSpan.FromSeconds(10), ct);
            Log(pp["ok"]?.GetValue<bool>() == true ? $"已下发 proxy 端口 {port}" : $"proxy 端口下发失败: {pp["error"]}");
        }
        return proc;
    }

    /// <summary>桥上行 UI 事件（ui-dialog/ui-dismiss/ui-toast）；宿主 MAUI 层订阅渲染。</summary>
    public Action<JsonObject>? UiEvent { get; set; }

    /// <summary>响应分发表：RoundTripAsync 注册、读循环按 id 完成之。</summary>
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pendingResponses = new();
    private readonly SemaphoreSlim _stdinLock = new(1, 1);   // 仅保护 stdin 写

    /// <summary>常驻读 stdout：按 id 分发响应、按 ev 分发 UI 事件；进程退出时结束。</summary>
    private async Task ReadLoopAsync(Process proc)
    {
        try
        {
            while (!proc.HasExited)
            {
                var raw = await proc.StandardOutput.ReadLineAsync();
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
            await EnsureProcessAsync(CancellationToken.None).ConfigureAwait(false);
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
                throw new TimeoutException($"Java 桥响应超时（{timeout.TotalSeconds:F0}s，id={expectId}）");
            }
        }
        finally { _pendingResponses.TryRemove(expectId, out _); }
    }

    private async Task<string> CallAsync(VodSiteInfo site, string method, JsonArray args, CancellationToken ct)
    {
        await EnsureProcessAsync(ct);
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
            await EnsureProcessAsync(ct).ConfigureAwait(false);
            var jarPath = await EnsureConvertedJarAsync(site, ct).ConfigureAwait(false);
            await EnsureSiteLoadedAsync(site, jarPath, ct).ConfigureAwait(false);

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
        // 壳框架模式（Guard 包）：className 用壳名（MyDriveGuard），壳/原始/解壳三 jar 下发——
        // 壳框架的「已登录+启用中」对话框/扫码/网盘管理原生运行，UI 经 UiBridge 上行宿主渲染
        if (_convertedJars.TryGetValue(site.Key, out _) &&
            _shellJars.TryGetValue(site.Key, out var shellJar) && File.Exists(shellJar))
        {
            req["className"] = className.EndsWith("Guard", StringComparison.Ordinal) ? className : className + "Guard";
            req["shellJar"] = shellJar;
            req["rawJar"] = _rawJars.TryGetValue(site.Key, out var rj) ? rj : "";
            req["realJar"] = jarPath;

            // Guard QEMU 解密通道（2026-09-24 用户拍板架构）：解密/签名/proxyInvoke 走独立
            // Guard VM 里的 ftyguard so（ARM）；VM 就绪后把解密服务端口下发给桥，桥的
            // DexNative 调用直连之。VM 缺失/启动失败 → 不带 guardPort，桥回落 unidbg 会话。
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
                        Log("Guard QEMU 通道未就绪（桥将回落 unidbg 解密）");
                    }
                }
                catch (Exception gex)
                {
                    Log($"Guard QEMU 通道异常（回落 unidbg）: {gex.Message}");
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

        var conv = await ConvertJarAsync(jarUrl, expectMd5, ct, null);
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
            $"{site.Name} 的 spider jar 是 Guard 加固包，unidbg 解壳失败且未找到提供 {alt} 的非 Guard 替代 jar");
    }

    /// <summary>
    /// 一次 jar 转换的结果。
    /// <para><paramref name="DexSource"/> 是**含 classes*.dex 的那份 jar**（普通包=下载原件，
    /// Guard 包=解壳产物）；调用方据此查真实类名。为 null 表示 raw 文件缺失、无法查证。</para>
    /// </summary>
    /// <summary>Path=真实类转换产物；DexSource=转换输入（解壳产物/原始 jar）；RawJar=原始 Guard jar（含解密 so，壳框架用）；ShellJar=壳 dex 转换产物（壳框架类）。</summary>
    private readonly record struct JarConversion(string Path, string? DexSource, string? RawJar = null, string? ShellJar = null);

    /// <summary>
    /// 下载 → 校验 → Guard 解壳 → dex2jar 转换。
    /// <para>返回转换后的 java jar；**返回 null 表示这个 jar 在本平台不可用**
    /// （Guard 且解壳失败/解壳器缺失，或 <paramref name="requireClass"/> 指定的类不在其中），
    /// 由调用方决定换哪个 jar —— 用 null 而不是抛异常，是为了让「换 jar」成为正常流程而不是错误路径。</para>
    /// </summary>
    private async Task<JarConversion?> ConvertJarAsync(string jarUrl, string? expectMd5, CancellationToken ct, string? requireClass)
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
        if (File.Exists(outPath))
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

        if (!File.Exists(rawPath))
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

        // ── Guard 加固（assets 下有 .so native 解密器 + .guard 密文）：先 unidbg 解壳 ──
        string? shellJar = null;
        var dexSource = rawPath;
        if (IsGuarded(rawPath))
        {
            if (!_unidbgReady)
            {
                Log($"跳过 Guard 加固 jar（未部署 unidbg 解壳器 vendor/unidbg/）: {Path.GetFileName(rawPath)}");
                return null;
            }
            var unpacked = Path.Combine(_convertedDir, "raw-" + hash + "-unpacked.jar");
            if (!File.Exists(unpacked) && !await UnpackGuardAsync(rawPath, unpacked, ct))
                return null;
            dexSource = unpacked;
            shellJar = await EnsureShellJarAsync(rawPath, hash, ct).ConfigureAwait(false);
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
    /// 并剔除 DexNative.class（桌面由桥内置替身接 unidbg）。失败返回 null（回退真实类模式）。
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
            // 同 unidbg 解壳器：批处理型 java 工具一律给「立即 EOF 的 stdin」，
            // 杜绝「从 GUI 宿主继承到永不 EOF 的管道 → 等 stdin 卡死」这一类问题
            RedirectStandardInput = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("dex2jar 启动失败");
        try { p.StandardInput.Close(); } catch { }
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0 || !File.Exists(outPath))
            throw new InvalidOperationException($"dex2jar 转换失败: {Path.GetFileName(dexSource)}");
    }

    /// <summary>从 zip 产物里剔除一个条目（壳 jar 剔除 DexNative.class——桌面由桥内置替身接 unidbg）。</summary>
    private static void StripEntry(string jarPath, string entryName)
    {
        using var fs = new FileStream(jarPath, FileMode.Open, FileAccess.ReadWrite);
        using var archive = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Update);
        var e = archive.GetEntry(entryName);
        e?.Delete();
    }

    /// <summary>
    /// 用 unidbg 解 Guard 壳：模拟 ARM64 Android 进程执行 <c>assets/ftyguard_v8.so</c> 的原生解密器，
    /// 把 <c>assets/ftyshinidie.guard</c> 还原成明文 dex（ZIP），产物写入 <paramref name="outPath"/>。
    /// </summary>
    /// <returns>解壳成功且产物存在返回 true；否则 false（调用方据此退回非 Guard 同族 jar）。</returns>
    private async Task<bool> UnpackGuardAsync(string rawPath, string outPath, CancellationToken ct)
    {
        Log($"Guard 加固包 → unidbg 解壳: {Path.GetFileName(rawPath)}");
        var psi = new ProcessStartInfo
        {
            FileName = _javaExe,
            // unidbg 要反射访问 JDK 内部（Module / 直接内存），JDK 17+ 必须显式 add-opens
            Arguments =
                "--add-opens java.base/java.lang=ALL-UNNAMED " +
                "--add-opens java.base/java.util=ALL-UNNAMED " +
                "--add-opens java.base/java.nio=ALL-UNNAMED " +
                "--add-opens java.base/sun.nio.ch=ALL-UNNAMED " +
                "-Dfile.encoding=UTF-8 " +
                "-Dorg.slf4j.simpleLogger.defaultLogLevel=error " +
                // classpath 用绝对路径（工作目录已改为可写区，相对路径解析不到解壳器）
                $"-cp \"{Path.Combine(_bridgeDir, "vendor", "unidbg", "*")}\" bridge.GuardUnpacker \"{rawPath}\" \"{outPath}\"",
            WorkingDirectory = _workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // ⚠️ 必须让子进程拿到一个**立即 EOF 的 stdin**（见下方 StandardInput.Close）。
            // unidbg 遇到良性异常时会进入 SimpleARM64Debugger，用 Scanner(System.in) 等调试命令；
            // 若 stdin 是从 GUI 宿主继承来、永不 EOF 的管道，解壳会永久卡死
            // （实测 jstack 证据：main 线程停在 SimpleARM64Debugger.loop → Scanner.nextLine）。
            RedirectStandardInput = true,
            // 解壳器按 -Dfile.encoding=UTF-8 输出，接收端也必须按 UTF-8 解，否则中文日志变乱码
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start 返回 null");
        }
        catch (Exception ex)
        {
            Log($"unidbg 解壳器启动失败: {ex.GetType().Name} {ex.Message}");
            return false;
        }

        using (proc)
        {
            // 立刻关闭 stdin 写端 → 子进程读到 EOF。这一步是解壳能否收敛的**关键**：
            // 我们不给它任何输入，也绝不允许它等输入（调试器会一直等到 EOF）。
            try { proc.StandardInput.Close(); } catch { }

            // 两端都要读，否则管道写满会死锁。**不要传 ct** —— ct 取消会让读取提前抛，
            // 后续拿不到诊断输出（进程杀死后管道即 EOF，自然结束）。
            var errTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);
            var outTask = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
            try
            {
                // 正常解壳 ~3s；给 90s 上限，避免异常时把首页挂死
                using var guard = CancellationTokenSource.CreateLinkedTokenSource(ct);
                guard.CancelAfter(TimeSpan.FromSeconds(90));
                await proc.WaitForExitAsync(guard.Token);
            }
            catch (Exception ex)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                Log($"unidbg 解壳进程中止: {ex.GetType().Name} {ex.Message}");
                return false;
            }

            // stderr 里有：① 解壳器自己的 [unpack] 结论行；② unidbg 的反汇编/栈噪声。
            // 过滤掉噪声，但**必须放行报错行** —— 否则 JVM 起不来的原因会被静默吞掉。
            var combined = (await errTask) + "\n" + (await outTask);
            foreach (var line in combined.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length == 0 || t.StartsWith("at ") || t.StartsWith("=>")) continue;
                if (t.StartsWith("[unpack]") || t.Contains("[unpack] ")
                    || t.Contains("Exception") || t.Contains("Error") || t.Contains("error")
                    || t.Contains("Could not") || t.Contains("Unrecognized") || t.Contains("无法"))
                    Log("  " + t[..Math.Min(220, t.Length)]);
            }

            if (proc.ExitCode != 0 || !File.Exists(outPath))
            {
                Log($"unidbg 解壳失败（exit={proc.ExitCode}）: {Path.GetFileName(rawPath)}");
                return false;
            }
            Log($"Guard 解壳完成: {Path.GetFileName(outPath)}（{new FileInfo(outPath).Length} 字节）");
            return true;
        }
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
