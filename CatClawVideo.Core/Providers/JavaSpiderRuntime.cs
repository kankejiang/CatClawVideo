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
/// <para>能力边界：仅支持明文 dex jar（如小雅 xiaoya_proxy.jar）；Guard 加固 jar
/// （jar 内带 assets/*.so + *.guard 加密 dex，解密依赖 Android ARM native）无法在 PC 运行。</para>
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
            ["className"] = site.Api.StartsWith("csp_", StringComparison.OrdinalIgnoreCase) ? site.Api[4..] : site.Api,
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

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(jarUrl)))[..24].ToLowerInvariant();
        var rawPath = Path.Combine(_bridgeDir, "converted", "raw-" + hash + ".jar");
        var outPath = Path.Combine(_bridgeDir, "converted", hash + "-java.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(rawPath)!);

        if (!File.Exists(outPath))
        {
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
                throw new NotSupportedException(
                    $"{site.Name} 的 spider jar 是 Guard 加固包（依赖 Android ARM native 解密），Windows 暂不支持");

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
                throw new InvalidOperationException($"dex2jar 转换失败: {site.Name}");
            Log($"jar 转换完成: {Path.GetFileName(outPath)}");
        }

        _convertedJars[site.Key] = outPath;
        return outPath;
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

            // 本地凭据文件兜底（按 server host 匹配）
            var creds = LoadSpiderCreds();
            var servers = arr.OfType<JsonObject>()
                .Where(o => o["server"] != null)
                .Select(o => o["server"]!.GetValue<string>().TrimEnd('/'))
                .Distinct().ToList();

            if ((string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass)) && creds != null)
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

    /// <summary>读取本地 spider 凭据文件（host → 用户名/密码）</summary>
    private static Dictionary<string, (string User, string Pass)>? LoadSpiderCreds()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CatClawVideo", "spider-creds.json");
            if (!File.Exists(path)) return null;
            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var dict = new Dictionary<string, (string, string)>();
            foreach (var kv in root)
            {
                var u = kv.Value?["username"]?.GetValue<string>();
                var p = kv.Value?["password"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(u) && !string.IsNullOrEmpty(p))
                    dict[kv.Key] = (u, p);
            }
            return dict.Count > 0 ? dict : null;
        }
        catch { return null; }
    }
}
