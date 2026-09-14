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
/// <para>能力边界：Guard 加固 jar 本身**无法**在 PC 运行 —— 其解密器是 ARM Android native
/// （<c>assets/ftyguard_v8.so</c> 用 <c>JNI_OnLoad</c> + <c>RegisterNatives</c> 动态注册，且只有
/// arm64/armv7），在 x64 JVM 里没有可执行路径。
/// <b>但站点仍可用</b>：检测到 Guard 时会自动改用**同族「非 Guard 构建」**的 jar
/// （见 <see cref="NonGuardFallbackJars"/>），它提供同名去掉 <c>Guard</c> 后缀的真实实现
/// （<c>csp_SixVGuard</c> → <c>SixV</c>），因此 新6V 这类站点在 Windows 上也能播。</para>
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
    /// <para>Guard 加固 jar 的解密器是 **ARM Android native .so**（v8 库用 <c>JNI_OnLoad</c> +
    /// <c>RegisterNatives</c> 动态注册，且只提供 arm64/armv7），Windows 的 JVM 里无法执行 ——
    /// 这是结构性限制，不是配置问题。因此本平台改用**同族「非 Guard 构建」**的 jar：
    /// 它提供同名但去掉 <c>Guard</c> 后缀的真实实现（如 <c>SixVGuard</c> → <c>SixV</c>）。</para>
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

    public JavaSpiderRuntime(string bridgeDir, string javaExe, Action<string>? log = null)
    {
        _bridgeDir = bridgeDir;
        _javaExe = javaExe;
        _log = log;
        IsSupported = File.Exists(Path.Combine(bridgeDir, "bridge.jar"))
                      && Directory.Exists(Path.Combine(bridgeDir, "vendor", "deps"))
                      && Directory.Exists(Path.Combine(bridgeDir, "vendor", "dex2jar"));
    }

    private void Log(string m) => _log?.Invoke("[jvm] " + m);

    /// <summary>查找系统里的 java.exe：JAVA_HOME → C:\Program Files\Java\* → PATH</summary>
    public static string? FindJavaExe()
    {
        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(home))
        {
            var p = Path.Combine(home, "bin", "java.exe");
            if (File.Exists(p)) return p;
        }
        if (Directory.Exists("C:\\Program Files\\Java"))
            foreach (var dir in Directory.GetDirectories("C:\\Program Files\\Java"))
            {
                var p = Path.Combine(dir, "bin", "java.exe");
                if (File.Exists(p)) return p;
            }
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var d in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { var p = Path.Combine(d.Trim(), "java.exe"); if (File.Exists(p)) return p; }
            catch { }
        }
        return null;
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

        var outPath = await ConvertJarAsync(jarUrl, expectMd5, ct, null);
        if (outPath is not null)
        {
            _convertedJars[site.Key] = outPath;
            return outPath;
        }

        // ── 配置的 jar 在本平台不可用（Guard 加固）：换同族非 Guard 构建 ──
        // 非 Guard 版里真实类名不带 Guard 后缀（SixVGuard → SixV）。
        var alt = configured.EndsWith("Guard", StringComparison.Ordinal)
            ? configured[..^"Guard".Length]
            : configured;
        Log($"{site.Name} 的 spider jar 是 Guard 加固包（ARM native 解密，本平台无法执行）；改试非 Guard 同族 jar（目标类 {alt}）…");

        foreach (var fb in NonGuardFallbackJars)
        {
            var p = await ConvertJarAsync(fb, null, ct, alt);
            if (p is null) continue;
            _nonGuardClass[site.Key] = alt;
            _convertedJars[site.Key] = p;
            Log($"{site.Name}: 已改用非 Guard jar（{fb}），类名 {configured} → {alt}");
            return p;
        }

        throw new NotSupportedException(
            $"{site.Name} 的 spider jar 是 Guard 加固包（依赖 Android ARM native 解密），本平台无法执行，" +
            $"且未找到提供 {alt} 的非 Guard 替代 jar");
    }

    /// <summary>
    /// 下载 → 校验 → 查 Guard → dex2jar 转换。
    /// <para>返回转换后的 java jar 路径；**返回 null 表示这个 jar 在本平台不可用**
    /// （Guard 加固，或 <paramref name="requireClass"/> 指定的类不在其中），
    /// 由调用方决定换哪个 jar —— 用 null 而不是抛异常，是为了让「换 jar」成为正常流程而不是错误路径。</para>
    /// </summary>
    private async Task<string?> ConvertJarAsync(string jarUrl, string? expectMd5, CancellationToken ct, string? requireClass)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(jarUrl)))[..24].ToLowerInvariant();
        var rawPath = Path.Combine(_bridgeDir, "converted", "raw-" + hash + ".jar");
        var outPath = Path.Combine(_bridgeDir, "converted", hash + "-java.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(rawPath)!);

        // 已转换过：但**仍要按需校验类是否存在** —— 同一个 jar 对不同站点可能「有的类在、有的不在」
        // （实测：非 Guard fty.jar 有 SixV 却没有 JPJ）。直接用缓存会让缺失的类漏到 load 阶段，
        // 报成 ClassNotFoundException，掩盖「该站无替代实现」这个真实结论。
        if (File.Exists(outPath))
            return requireClass is null || !File.Exists(rawPath) || JarHasClass(rawPath, requireClass)
                ? outPath
                : null;

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

        // Guard 加固检测：assets 下带 .so（ARM native 解密器）+ .guard 加密 dex
        if (IsGuarded(rawPath))
        {
            Log($"跳过 Guard 加固 jar: {Path.GetFileName(rawPath)}");
            return null;
        }

        if (requireClass is not null && !JarHasClass(rawPath, requireClass))
        {
            Log($"跳过不含类 {requireClass} 的 jar: {Path.GetFileName(rawPath)}");
            return null;
        }

        // dex2jar 转换
        var psi = new ProcessStartInfo
        {
            FileName = _javaExe,
            Arguments = $"-cp \"vendor\\dex2jar\\*\" com.googlecode.dex2jar.tools.Dex2jarCmd \"{rawPath}\" -o \"{outPath}\" --force",
            WorkingDirectory = _bridgeDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("dex2jar 启动失败");
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0 || !File.Exists(outPath))
            throw new InvalidOperationException($"dex2jar 转换失败: {Path.GetFileName(rawPath)}");
        Log($"jar 转换完成: {Path.GetFileName(outPath)}");
        return outPath;
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
