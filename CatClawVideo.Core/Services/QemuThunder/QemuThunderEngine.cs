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
public sealed class QemuThunderEngine : IPreferredMagnetEngine, IDisposable
{
    /// <summary>控制口：烧死在 initrd 里（guest 每秒回连宿主 10.0.2.2:18080），改不了。</summary>
    public const int CtrlPort = 18080;

    /// <summary>媒体口首选值（实际会在被占时向后探测空闲端口）。</summary>
    public const int PreferredMediaPort = 20092;

    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".ts", ".m2ts", ".wmv", ".flv", ".mov",
        ".rmvb", ".rm", ".webm", ".m4v", ".mpg", ".mpeg", ".3gp", ".vob", ".iso",
    };

    private static readonly Regex BtihHex = new(@"xt=urn:btih:([0-9a-fA-F]{40})", RegexOptions.Compiled);

    private readonly string _runtimeDir;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly Timer _idleTimer;

    private QemuHostRuntime? _runtime;
    private QemuControlServer? _server;
    private int _mediaPort = PreferredMediaPort;
    private Session? _session;
    private QemuStreamProxy? _streamProxy;
    private DateTime _lastActiveUtc = DateTime.UtcNow;
    private bool _disposed;

    public QemuThunderEngine(string runtimeDir, Action<string>? log = null)
    {
        _runtimeDir = runtimeDir;
        _log = log;
        _idleTimer = new Timer(_ => IdleCheck(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
    }

    public string Name => "迅雷(QEMU)";

    /// <summary>运行时文件已部署即算「就绪」；VM 懒启动发生在首次任务。</summary>
    public bool IsReady => QemuHostRuntime.IsPresent(_runtimeDir);

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

            // 会话已在播同一切片（播放器重试/续连）：直接复用（缓存代理还活着时最省事）
            if (s.Played && s.PlayUrlPath.Length > 0 && s.PickIndex == pick.Index)
            {
                if (_streamProxy is not null)
                {
                    Log($"复用已在播会话（走缓存代理）：#{pick.Index} {pick.Name}");
                    _lastActiveUtc = DateTime.UtcNow;
                    return new MagnetPlayback(hash, pick.Index, pick.Size, Path.GetFileName(pick.Name), _streamProxy.Url);
                }
                var reuse = await VerifyOnceAsync(s.PlayUrlPath, ct).ConfigureAwait(false);
                if (reuse > 0)
                {
                    Log($"复用已在播会话：#{pick.Index} {pick.Name}");
                    _lastActiveUtc = DateTime.UtcNow;
                    StartStreamProxy(s);
                    return new MagnetPlayback(hash, pick.Index, pick.Size, Path.GetFileName(pick.Name),
                        _streamProxy?.Url ?? MediaUrl(s.PlayUrlPath));
                }
                Log("旧播放地址已失效，重新下发 DL");
            }

            s.PickIndex = pick.Index; s.PickName = pick.Name; s.PickSize = pick.Size;
            s.DlSent = true; s.Played = false; s.PlayUrlPath = ""; s.LastError = null;
            LastDirectMediaUrl = null;

            var others = string.Join(',', s.Files.Where(f => f.Index != pick.Index).Take(100).Select(f => f.Index));
            Log($"选片 #{pick.Index}（{pick.Size / 1048576.0:F1}MB，共 {s.Files.Count} 项）→ 下发 DL");
            _server!.SetCommand($"DL -|{s.Dir}|{pick.Name}|{pick.Index}|{others}");
            _lastActiveUtc = DateTime.UtcNow;

            // 等阶段二 ev=play（视频地址）
            await PollUntilAsync(() => s.Cancelled || s.Played || s.LastError is not null, TimeSpan.FromSeconds(240), ct).ConfigureAwait(false);
            if (!s.Played)
            {
                if (!s.Cancelled) Log(s.LastError ?? "等播放地址超时（240s）");
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

    /// <summary>停止当前任务（保留 VM；空闲计时器过会儿会收掉它）。</summary>
    public void Stop()
    {
        var s = _session;
        _session = null;
        if (s is not null) s.Cancelled = true;   // 打断进行中的等待（否则会挂满超时）
        var p = _streamProxy;
        _streamProxy = null;
        p?.Dispose();                            // 停止播放即关闭缓存代理（播放器地址随之失效）
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
            var proxy = new QemuStreamProxy(_mediaPort, s.PlayUrlPath, s.PickSize,
                QemuStreamProxy.ContentTypeFor(s.PickName), Log);
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
                _server = new QemuControlServer(CtrlPort);
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
            _mediaPort = PickFreePort(PreferredMediaPort);
            _runtime = new QemuHostRuntime(_runtimeDir, _mediaPort, Log);
            _server.ResetFirstPoll();
            if (!await _runtime.StartAsync(ct).ConfigureAwait(false)) return false;

            var ready = await _server.WaitFirstPollAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(false);
            if (!ready)
            {
                Log("VM 已启动但 90s 内 guest 未连上控制端");
                return false;
            }
            Log($"VM 就绪（媒体口 {_mediaPort}）");
        }

        _lastActiveUtc = DateTime.UtcNow;
        return true;
    }

    /// <summary>Phase 1：下发磁力并把 .torrent 拉回来展开文件列表（成功即缓存会话）。</summary>
    private async Task<Session?> PrepareSessionLockedAsync(string magnet, string? preferName, CancellationToken ct)
    {
        if (_session is { } cached && cached.Magnet == magnet && cached.Files.Count > 0
            && DateTime.UtcNow - cached.AtUtc < TimeSpan.FromMinutes(10))
        {
            _lastActiveUtc = DateTime.UtcNow;
            return cached;
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

        // ① 等种子落盘：ev=torrent（原路径）与阶段一 ev=play（引擎 URL 路径）先到哪个算哪个
        await PollUntilAsync(
            () => s.Cancelled || s.TorrentRawPath.Length > 0 || s.TorrentUrlPath.Length > 0 || s.LastError is not null,
            TimeSpan.FromSeconds(120), ct).ConfigureAwait(false);
        if (s.Cancelled) return null;
        if (s.LastError is not null) { Log(s.LastError); _session = null; return null; }
        if (s.TorrentRawPath.Length == 0 && s.TorrentUrlPath.Length == 0)
        {
            Log("等种子落盘超时（120s）");
            _session = null;
            return null;
        }

        // ② 取 .torrent：优先用阶段一 play 的 URL 路径（mag26/mag28 验证过的格式），
        //    拿不到时用 torrent 原路径合成「双重 URL 编码」（引擎的编码格式："/" + 两次 escape 的绝对路径）
        var fetchPath = s.TorrentUrlPath.Length > 0
            ? s.TorrentUrlPath
            : SynthesizeUrlPath(s.TorrentRawPath);

        var data = await FetchTorrentAsync(fetchPath, ct).ConfigureAwait(false);
        if (data is null && s.TorrentUrlPath.Length == 0)
        {
            // 合成路径失败 → 再等 30s 让 play 事件到，用官方路径重试
            await PollUntilAsync(() => s.TorrentUrlPath.Length > 0, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            if (s.TorrentUrlPath.Length > 0)
                data = await FetchTorrentAsync(s.TorrentUrlPath, ct).ConfigureAwait(false);
        }
        if (data is null)
        {
            Log("展开种子失败（两种路径都取不到 bencode）");
            _session = null;
            return null;
        }

        try
        {
            var (entries, tname) = Bencode.ParseTorrent(data);
            if (entries.Count == 0)
            {
                Log("种子文件列表为空");
                _session = null;
                return null;
            }
            s.Files = entries.Select(e => new MagnetFile(e.Index, e.Size, e.Rel)).ToList();
            s.AtUtc = DateTime.UtcNow;
            Log($"文件列表 {entries.Count} 项（种子名 {tname}）");
            foreach (var f in s.Files.Take(12)) Log($"  #{f.Index,3}  {f.Size / 1048576.0,8:F1}MB  {f.Name}");
            if (s.Files.Count > 12) Log($"  … 共 {s.Files.Count} 项");
            return s;
        }
        catch (Exception ex)
        {
            Log($"种子解析失败：{ex.Message}");
            _session = null;
            return null;
        }
    }

    private async Task<byte[]?> FetchTorrentAsync(string path, CancellationToken ct)
    {
        var (code, body) = await FetchAsync(path, 0, ct).ConfigureAwait(false);
        if (body.Length >= 100 && body[0] == (byte)'d') return body;
        Log($"种子内容异常（HTTP={code} len={body.Length} path={path}）");
        return null;
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
            case "error":
                s.LastError = $"guest 报错：{r.Msg}（st={r.St} err={r.Err}）";
                break;
        }
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

    /// <summary>单次取字节（会话复用前的活性探测）。</summary>
    private async Task<long> VerifyOnceAsync(string path, CancellationToken ct)
    {
        var (code, data) = await FetchAsync(path, 64 * 1024, ct).ConfigureAwait(false);
        return code is 200 or 206 ? data.Length : 0;
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

    // ═══════════ 空闲自停 / 释放 ═══════════

    private void IdleCheck()
    {
        try
        {
            if (_disposed || _runtime is not { IsRunning: true }) return;
            if (DateTime.UtcNow - _lastActiveUtc < TimeSpan.FromMinutes(15)) return;
            // 有「已交播放地址」的会话时不停（播放器可能随时来取流，宿主侧不可见）
            if (_session is { Played: true }) return;

            Log("空闲 15 分钟，停掉 QEMU 释放内存");
            _session = null;
            _runtime.Stop();
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
        public bool DlSent;
        public bool Played;
        public string PlayUrlPath = "";
        public string? LastError;
        public volatile bool Cancelled;
        public DateTime AtUtc = DateTime.UtcNow;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _idleTimer.Dispose(); } catch { }
        try { _streamProxy?.Dispose(); } catch { }
        try { _runtime?.Dispose(); } catch { }
        try { _server?.Dispose(); } catch { }
        try { _http.Dispose(); } catch { }
    }
}
