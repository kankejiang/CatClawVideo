using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace CatClawVideo.Core.Services.QemuThunder;

/// <summary>
/// Guard 解密 VM（独立 QEMU 实例）：把 ftyguard*.so 的解密/签名/proxyInvoke（ARM 原生）
/// 跑在 guest 里，桥进程（JavaSpiderRuntime 的 x64 JVM）经 hostfwd 直连 harness 的
/// 行式 TCP 服务调用。与迅雷引擎（<see cref="QemuThunderEngine"/>）完全独立的进程与控制口，
/// 互不影响生命周期。
///
/// <para><b>链路</b>（2026-09-24 与用户对齐的最终架构）：
/// 桥 DexNative.decrypt → 127.0.0.1:<see cref="GuardPort"/>（hostfwd）→ guest guard 服务
/// → ftyguard so（dlopen + RegisterNatives 捕获）→ 明文回传。so 运行中弹的对话框/二维码
/// 经控制口 /report?ev=ui 上行 → <see cref="UiEvent"/> → MAUI 渲染；用户操作经
/// <see cref="SendUiResult"/> → 控制口 UIR 命令 → guest 回调 so 的 native listener。</para>
///
/// <para><b>so 与资源的供给</b>：guest 不带 jar。harness 需要时经控制口
/// <c>/res?jar=&lt;hash&gt;&amp;name=&lt;entry&gt;</c> 向宿主取（<see cref="ResolveResource"/>）：
/// <c>__so__</c> = 从注册的 raw jar 挑 aarch64 so，其余按 zip 条目名（exact/assets 前缀/后缀兜底）。</para>
/// </summary>
public sealed class QemuGuardEngine : IDisposable
{
    /// <summary>Guard VM 的控制口（与迅雷 VM 的 18080 错开，双 VM 并存互不串扰）。</summary>
    public const int CtrlPort = 18090;

    /// <summary>Guard 解密服务端口（guest 监听 + 宿主 hostfwd 同号）。</summary>
    public const int GuardPort = 18481;

    private readonly string _runtimeDir;
    private readonly Action<string>? _log;
    private readonly object _sync = new();

    private QemuHostRuntime? _vm;
    private QemuControlServer? _ctrl;
    private readonly ConcurrentDictionary<string, string> _jars = new();   // hash → raw jar 路径
    private TaskCompletionSource<bool>? _loadTcs;

    private static int _mediaPortSeed = 20100;

    /// <summary>so 加载就绪（GLOAD 已完成、RegisterNatives 已捕获）。</summary>
    public volatile bool Ready;

    /// <summary>loadedJarHash：当前 VM 里已加载的 guard so 所属 jar（同 jar 复用会话）。</summary>
    private volatile string? _loadedJarHash;

    /// <summary>Guard VM 的 UI 事件（ui-dialog/ui-dismiss/ui-toast，src=qemu）。</summary>
    public event Action<JsonObject>? UiEvent;

    public QemuGuardEngine(string runtimeDir, Action<string>? log = null)
    {
        _runtimeDir = runtimeDir;
        _log = log;
        // 与 QemuThunderEngine 对齐：迅雷 VM 早就注册了这条，Guard VM 漏了 ——
        // 实测关应用后 Guard VM（18481 那台）活了下来，迅雷 VM 没有（2026-09-25）。
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
    }

    private void Log(string m) => _log?.Invoke("[guard-vm] " + m);

    /// <summary>注册 raw jar（Guard 源加载时调用；hash = jar 地址的 SHA256 前 24 位，与转换管线一致）。</summary>
    public void RegisterJar(string hash, string rawJarPath) => _jars[hash] = rawJarPath;

    /// <summary>
    /// 确保 VM 已启动且指定 jar 的 guard so 已加载（同 hash 复用，失败返回 false——调用方不下发 guardPort，ARM 调用将明确报错）。
    /// </summary>
    public async Task<bool> EnsureLoadedAsync(string jarHash, CancellationToken ct = default)
    {
        if (Ready && _loadedJarHash == jarHash) return true;
        lock (_sync)
        {
            if (Ready && _loadedJarHash == jarHash) return true;
            if (_vm is null && !StartVm()) return false;
            _loadTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            _ctrl!.SetCommand($"GLOAD {jarHash}");
            Log($"已下发 GLOAD {jarHash}（等 so 下载 + JNI_OnLoad + getLoader 预热）");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(180));
            var ok = await _loadTcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (ok) { Ready = true; _loadedJarHash = jarHash; }
            return ok;
        }
        catch (Exception ex)
        {
            Log($"GLOAD 等待失败: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>宿主回传对话框用户操作（which≥0=列表项，-1/-2/-3=肯定/否定/中性按钮）。</summary>
    public void SendUiResult(int seq, int which)
    {
        if (_ctrl is null) { Log($"UIR {seq}/{which} 丢弃（VM 未启动）"); return; }
        Log($"UIR {seq}/{which} 下发");
        _ctrl.SetCommand($"UIR {seq} {which}");
    }

    private bool StartVm()
    {
        try
        {
            // 媒体口对 Guard VM 无用，但 -netdev 需要 hostfwd：取一个空闲口占位
            var mediaPort = GetFreePort(ref _mediaPortSeed);
            var vm = new QemuHostRuntime(_runtimeDir, mediaPort, Log,
                initrdName: "pkg_initrd.gz", consoleLogTag: "-guard",
                monitorPort: 0, ctrlPort: CtrlPort, guardPort: GuardPort, magnetOverride: "none")
            {
                GuestMemoryMb = 1024,      // guard 解密是轻负载：1GB 足够（/init 会把 tmpfs 收敛到安全值）
                SmpCount = 2,
            };
            if (!vm.IsRuntimePresent)
            {
                Log($"运行时缺失：{_runtimeDir}（Guard 解密通道不可用）");
                return false;
            }
            var ctrl = new QemuControlServer(CtrlPort) { ResolveResource = ResolveResource };
            ctrl.Log += Log;
            ctrl.ReportReceived += OnReport;
            ctrl.Start();
            var started = vm.StartAsync().GetAwaiter().GetResult();
            if (!started)
            {
                ctrl.Dispose();
                return false;
            }
            _vm = vm;
            _ctrl = ctrl;
            Log($"VM 启动中：控制口 {CtrlPort} / 解密服务 {GuardPort}");
            return true;
        }
        catch (Exception ex)
        {
            Log($"VM 启动失败: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static int GetFreePort(ref int seed)
    {
        // 从 seed 起找空闲 TCP 口（Guard VM 的媒体口占位；简单递增试探）
        for (var i = 0; i < 100; i++)
        {
            var p = Interlocked.Increment(ref seed);
            try
            {
                var l = new TcpListener(System.Net.IPAddress.Loopback, p);
                l.Start();
                l.Stop();
                return p;
            }
            catch { /* 被占，试下一个 */ }
        }
        return Interlocked.Increment(ref seed);
    }

    private void OnReport(QemuReport r)
    {
        switch (r.Ev)
        {
            case "guard":
                // GLOAD 的完成信号：msg 形如 "ready: natives=8" / "error: ..."
                if (r.Msg.StartsWith("ready", StringComparison.Ordinal))
                {
                    Log($"so 加载完成（{r.Msg}）");
                    _loadTcs?.TrySetResult(true);
                }
                else
                {
                    Log($"so 加载失败：{r.Msg}");
                    _loadTcs?.TrySetResult(false);
                }
                break;
            case "ui":
                try
                {
                    if (JsonNode.Parse(r.Msg) is JsonObject ev) UiEvent?.Invoke(ev);
                }
                catch (Exception ex)
                {
                    Log($"UI 事件解析失败: {ex.Message}");
                }
                break;
        }
    }

    /// <summary>
    /// /res 解析：<c>__so__</c> = 从注册 jar 挑 aarch64（ELF machine 0xB7）so 条目；
    /// 其余按 zip 条目名查找（exact → assets/ 前缀 → 后缀兜底）。
    /// </summary>
    private byte[]? ResolveResource(string jarHash, string name)
    {
        if (!_jars.TryGetValue(jarHash, out var jarPath) || !File.Exists(jarPath))
        {
            Log($"/res：jar {jarHash} 未注册或文件缺失");
            return null;
        }
        using var zip = ZipFile.OpenRead(jarPath);
        if (name == "__so__")
        {
            foreach (var e in zip.Entries)
            {
                if (!e.Name.EndsWith(".so", StringComparison.OrdinalIgnoreCase)) continue;
                using var es = e.Open();
                using var ms = new MemoryStream();
                es.CopyTo(ms);
                var bytes = ms.ToArray();
                // ELF header：e_machine 在偏移 18-19（小端）——0xB7=aarch64
                if (bytes.Length > 20 && bytes[0] == 0x7F && bytes[1] == (byte)'E' &&
                    bytes[2] == (byte)'L' && bytes[3] == (byte)'F' &&
                    (bytes[18] | (bytes[19] << 8)) == 0xB7)
                {
                    bytes = PatchGuardSo(bytes);
                    Log($"/res __so__ → {e.FullName}（{bytes.Length / 1024}KB）");
                    return bytes;
                }
            }
            Log($"/res __so__：jar 里没有 aarch64 so");
            return null;
        }
        foreach (var cand in new[] { name, "assets/" + name })
        {
            var e = zip.GetEntry(cand);
            if (e is null) continue;
            using var es = e.Open();
            using var ms = new MemoryStream();
            es.CopyTo(ms);
            return ms.ToArray();
        }
        foreach (var e in zip.Entries)
        {
            if (e.FullName.EndsWith(name, StringComparison.Ordinal))
            {
                using var es = e.Open();
                using var ms = new MemoryStream();
                es.CopyTo(ms);
                return ms.ToArray();
            }
        }
        return null;
    }

    /// <summary>
    /// guard so 的依赖瘦身（2026-09-24 实测 dlopen 失败）：ftyguard*.so 的 DT_NEEDED 带
    /// <c>libandroid.so</c>，而它又 NEEDED libhidlbase/libbinder/libgui 等 26 个 framework
    /// 库——guest initrd 只有 bionic + 迅雷引擎库，整条链不存在。实测其未定义符号
    /// <b>全部是 @LIBC</b>（无任何 libandroid 引用），纯属链接依赖 → 原地等长改写
    /// DT_NEEDED 字符串（13 字符 ↔ 13 字符）指向 initrd 已有的 <c>libxl_stat.so</c>，
    /// dlopen 即通过。
    /// </summary>
    private byte[] PatchGuardSo(byte[] so)
    {
        ReadOnlySpan<byte> from = "libandroid.so\0"u8;
        ReadOnlySpan<byte> to = "libxl_stat.so\0"u8;
        var patched = (byte[])so.Clone();
        var span = patched.AsSpan();
        var count = 0;
        int i;
        while ((i = span.IndexOf(from)) >= 0)
        {
            to.CopyTo(span.Slice(i));
            span = span.Slice(i + to.Length);
            count++;
        }
        Log($"__so__ DT_NEEDED patch：libandroid.so → libxl_stat.so（{count} 处）");
        return patched;
    }

    public void Dispose()
    {
        try { _vm?.Stop(); } catch { }
        try { _vm?.Dispose(); } catch { }
        try { _ctrl?.Dispose(); } catch { }
        _vm = null;
        _ctrl = null;
        Ready = false;
    }
}

/// <summary>
/// Guard 解密 VM 的全局门面：JavaSpiderRuntime（Core）与 SpiderUiHost（MAUI）都经它
/// 取引擎，避免 DI 改动扩散。MauiProgram 装配时赋值 <see cref="Engine"/>。
/// </summary>
public static class GuardRuntime
{
    /// <summary>Guard 解密 VM 引擎（未装配 = Android/运行时缺失，ARM 调用将明确报错）。</summary>
    public static QemuGuardEngine? Engine { get; private set; }

    /// <summary>装配（MauiProgram 启动时调用一次）。</summary>
    public static void Attach(QemuGuardEngine engine)
    {
        Engine = engine;
        Engine.UiEvent += ev => UiEvent?.Invoke(ev);
    }

    /// <summary>Guard VM 的 UI 事件（聚合自引擎；SpiderUiHost 订阅渲染）。</summary>
    public static event Action<JsonObject>? UiEvent;
}
