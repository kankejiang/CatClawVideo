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
public class JavaSpiderRuntime : ISpiderRuntime
{
    public string Id => "jvm-dex";

    private readonly string _bridgeDir;
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

    public JavaSpiderRuntime(string bridgeDir, string javaExe, Action<string>? log = null)
    {
        _bridgeDir = bridgeDir;
        _javaExe = javaExe;
        _log = log;
        IsSupported = File.Exists(Path.Combine(bridgeDir, "bridge.jar"))
                      && Directory.Exists(Path.Combine(bridgeDir, "vendor", "deps"))
                      && Directory.Exists(Path.Combine(bridgeDir, "vendor", "dex2jar"));
        _unidbgReady = DetectUnidbg(bridgeDir);
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

    /// <summary>
    /// 查找系统里的 java.exe，取**版本最高**的那个。
    /// <para>扫描顺序：JAVA_HOME → <c>C:\Program Files\Java\*</c> → <c>C:\Program Files\Microsoft\jdk-*</c> → PATH。
    /// 必须取最高版本：<c>bridge.jar</c> 与 unidbg 解壳器都是按 JDK 21 编译的（class file 65），
    /// 落到 JDK 17 会抛 <c>UnsupportedClassVersionError</c>；而本机常见「PATH 里 17、JAVA_HOME 里 21」
    /// 或同时装了两套 JDK 的情况，所以按目录名里的版本号排序取最大。</para>
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

    /// <summary>向上查找 JavaBridge 目录（bridge.jar 所在，App 部署目录或仓库根）</summary>
    public static string? FindBridgeDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && d.Parent != null; d = d.Parent)
        {
            var cand = Path.Combine(d.FullName, "JavaBridge");
            if (File.Exists(Path.Combine(cand, "bridge.jar"))) return cand;
        }
        return null;
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
            WorkingDirectory = _bridgeDir,
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
        psi.ArgumentList.Add("-cp");
        psi.ArgumentList.Add("bridge.jar;vendor\\deps\\*");
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

        // 握手
        var pong = await RoundTripAsync(new JsonObject { ["id"] = 0, ["op"] = "ping" }, TimeSpan.FromSeconds(15), ct);
        if (!pong.ContainsKey("ok") || pong["ok"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("Java 桥握手失败");
        Log("桥进程就绪");
        return proc;
    }

    private async Task<JsonObject> RoundTripAsync(JsonObject req, TimeSpan timeout, CancellationToken ct)
    {
        await _ioLock.WaitAsync(ct);
        try
        {
            var expectId = req["id"]?.GetValue<int>();
            await _stdin!.WriteLineAsync(req.ToJsonString().AsMemory(), ct);
            await _stdin.FlushAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try
            {
                while (true)
                {
                    var raw = await _stdout!.ReadLineAsync(timeoutCts.Token);
                    if (raw == null)
                        throw new InvalidOperationException("Java 桥进程已退出（可查看应用日志定位桥启动失败原因）");
                    var resp = raw.Trim();
                    if (resp.Length == 0) continue;
                    JsonObject obj;
                    try { obj = JsonNode.Parse(resp)!.AsObject(); }
                    catch { continue; }
                    if (expectId is int id)
                    {
                        if (obj["id"]?.GetValue<int>() != id) continue;
                        return obj;
                    }
                    return obj;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("Java 桥响应超时");
            }
        }
        finally { _ioLock.Release(); }
    }

    private async Task<string> CallAsync(VodSiteInfo site, string method, JsonArray args, CancellationToken ct)
    {
        await EnsureProcessAsync(ct);
        var jar = await EnsureConvertedJarAsync(site, ct);
        await EnsureSiteLoadedAsync(site, jar, ct);

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

    private async Task EnsureSiteLoadedAsync(VodSiteInfo site, string jarPath, CancellationToken ct)
    {
        if (_loadedSites.TryGetValue(site.Key, out _)) return;
        var req = new JsonObject
        {
            ["id"] = Interlocked.Increment(ref _id),
            ["op"] = "load",
            ["site"] = site.Key,
            ["className"] = _nonGuardClass.TryGetValue(site.Key, out var altName)
                ? altName
                : site.Api.StartsWith("csp_", StringComparison.OrdinalIgnoreCase) ? site.Api[4..] : site.Api,
            ["ext"] = await PrepareExtAsync(site, ct),
            ["jars"] = new JsonArray(jarPath),
        };
        var resp = await RoundTripAsync(req, TimeSpan.FromSeconds(60), ct);
        if (resp["ok"]?.GetValue<bool>() != true)
            throw new InvalidOperationException($"spider {site.Key} 加载失败: {resp["error"]}");
        _loadedSites[site.Key] = true;
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

        var conv = await ConvertJarAsync(jarUrl, expectMd5, ct, null);
        if (conv is { } ok)
        {
            // Guard 包解壳后真实类名不带 Guard 后缀（DouDouGuard → DouDou），桥必须按真实名加载
            if (ok.GuardUnpacked && configured.EndsWith("Guard", StringComparison.Ordinal))
                _nonGuardClass[site.Key] = configured[..^"Guard".Length];
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

    /// <summary>一次 jar 转换的结果。<paramref name="GuardUnpacked"/> 表示该 jar 是 Guard 加固包、走了 unidbg 解壳。</summary>
    private readonly record struct JarConversion(string Path, bool GuardUnpacked);

    /// <summary>
    /// 下载 → 校验 → Guard 解壳 → dex2jar 转换。
    /// <para>返回转换后的 java jar；**返回 null 表示这个 jar 在本平台不可用**
    /// （Guard 且解壳失败/解壳器缺失，或 <paramref name="requireClass"/> 指定的类不在其中），
    /// 由调用方决定换哪个 jar —— 用 null 而不是抛异常，是为了让「换 jar」成为正常流程而不是错误路径。</para>
    /// </summary>
    private async Task<JarConversion?> ConvertJarAsync(string jarUrl, string? expectMd5, CancellationToken ct, string? requireClass)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(jarUrl)))[..24].ToLowerInvariant();
        var rawPath = Path.Combine(_bridgeDir, "converted", "raw-" + hash + ".jar");
        var outPath = Path.Combine(_bridgeDir, "converted", hash + "-java.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(rawPath)!);

        // 已转换过：但**仍要按需校验类是否存在** —— 同一个 jar 对不同站点可能「有的类在、有的不在」
        // （实测：非 Guard fty.jar 有 SixV 却没有 JPJ）。直接用缓存会让缺失的类漏到 load 阶段，
        // 报成 ClassNotFoundException，掩盖「该站无替代实现」这个真实结论。
        if (File.Exists(outPath))
        {
            if (requireClass is null || !File.Exists(rawPath) || JarHasClass(rawPath, requireClass))
                return new JarConversion(outPath, IsGuarded(rawPath));
            return null;
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
        var dexSource = rawPath;
        var guardUnpacked = false;
        if (IsGuarded(rawPath))
        {
            if (!_unidbgReady)
            {
                Log($"跳过 Guard 加固 jar（未部署 unidbg 解壳器 vendor/unidbg/）: {Path.GetFileName(rawPath)}");
                return null;
            }
            var unpacked = Path.Combine(_bridgeDir, "converted", "raw-" + hash + "-unpacked.jar");
            if (!File.Exists(unpacked) && !await UnpackGuardAsync(rawPath, unpacked, ct))
                return null;
            dexSource = unpacked;
            guardUnpacked = true;
        }

        if (requireClass is not null && !JarHasClass(dexSource, requireClass))
        {
            Log($"跳过不含类 {requireClass} 的 jar: {Path.GetFileName(rawPath)}");
            return null;
        }

        // dex2jar 转换（普通 jar 直接用原件；Guard 包用解壳后的明文 dex）
        var psi = new ProcessStartInfo
        {
            FileName = _javaExe,
            Arguments = $"-cp \"vendor\\dex2jar\\*\" com.googlecode.dex2jar.tools.Dex2jarCmd \"{dexSource}\" -o \"{outPath}\" --force",
            WorkingDirectory = _bridgeDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("dex2jar 启动失败");
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0 || !File.Exists(outPath))
            throw new InvalidOperationException($"dex2jar 转换失败: {Path.GetFileName(dexSource)}");
        Log($"jar 转换完成: {Path.GetFileName(outPath)}");
        return new JarConversion(outPath, guardUnpacked);
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
                $"-cp \"vendor\\unidbg\\*\" bridge.GuardUnpacker \"{rawPath}\" \"{outPath}\"",
            WorkingDirectory = _bridgeDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 解壳器按 -Dfile.encoding=UTF-8 输出，接收端也必须按 UTF-8 解，否则中文日志变乱码
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("unidbg 解壳器启动失败");
        // 两端都要读，否则管道写满会死锁
        var errTask = p.StandardError.ReadToEndAsync(ct);
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        // unidbg 自身会往 stderr 打大量栈信息；只保留解壳器的 [unpack] 结论行
        foreach (var line in (await errTask + "\n" + await outTask).Split('\n'))
        {
            var t = line.Trim();
            if (t.Contains("[unpack]")) Log("  " + t);
        }
        if (p.ExitCode != 0 || !File.Exists(outPath))
        {
            Log($"unidbg 解壳失败（exit={p.ExitCode}）: {Path.GetFileName(rawPath)}");
            return false;
        }
        Log($"Guard 解壳完成: {Path.GetFileName(outPath)}（{new FileInfo(outPath).Length} 字节）");
        return true;
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
