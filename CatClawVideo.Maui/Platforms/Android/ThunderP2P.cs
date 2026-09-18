namespace CatClawVideo.Maui.Platforms.Android;

/// <summary>
/// 迅雷下载引擎（磁力）宿主服务（Android）。
///
/// <para>真实逻辑在 Java 侧 <c>com.catclaw.video.ThunderBridge</c> ——
/// <c>XLTaskHelper</c> 的静态块经 <c>XLLoader</c> 调 <c>System.loadLibrary</c>，
/// 只有从 Java 帧发起才能用 App 的 classloader 命中 <c>lib/arm64/</c>（详见桥的注释与荐片那次的踩坑）。
/// 本类只做反射调用与生命周期编排。</para>
///
/// <para>⚠️ 许可：SDK 二进制与 appKey 来自 TVBox 仓库，属迅雷商业闭源 SDK 的未授权使用；
/// 用户已知情并选择打包，见 THIRD-PARTY-NOTICES.md。</para>
/// </summary>
internal sealed class ThunderP2P : CatClawVideo.Core.Interfaces.IPreferredMagnetEngine
{
    private const string BridgeClass = "com.catclaw.video.ThunderBridge";

    /// <summary>视频扩展名（挑文件用；与 BtStreamService.SelectFile 同族，宽松些以便覆盖 ts/m2ts）</summary>
    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".ts", ".m2ts", ".wmv", ".flv", ".mov",
        ".rmvb", ".rm", ".webm", ".m4v", ".mpg", ".mpeg", ".3gp", ".vob", ".iso",
    };

    public string Name => "迅雷";

    private readonly string _cacheRoot;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Java.Lang.Class? _cls;
    private bool _attempted;
    private bool _ready;

    public ThunderP2P(string cacheRoot, Action<string>? log = null)
    {
        _cacheRoot = cacheRoot;
        _log = log;
    }

    public bool IsReady => _ready;

    /// <summary>
    /// 是否有活跃的播放/下载任务。
    ///
    /// <para>详情页的磁力展开（探测）必须让位：Android 侧的迅雷是**单会话**，探测会
    /// stopTask 掉正在播放的任务（黑屏），且下载中建新任务会被拒（9111）——
    /// 与 PC 侧 <c>IsBusy</c> 同一语义（见 d5a82fc）。</para>
    ///
    /// <para>判据 = bridge 里还挂着任务号：<c>taskProgress()</c> 无任务时恒返回 "0|0|0"，
    /// 有任务时返回 "已下载|总大小|速度"，故取中段 &gt; 0 即视为活跃。
    /// 取不到/异常一律按「不忙」处理 —— 让探测继续，功能优先。</para>
    /// </summary>
    public bool IsBusy
    {
        get
        {
            if (!_ready || _cls is null) return false;
            try
            {
                string? s;
                lock (_callLock)
                {
                    s = (FindStatic("taskProgress", 0)?.Invoke(null, []) as Java.Lang.String)?.ToString();
                }
                if (string.IsNullOrEmpty(s)) return false;
                var parts = s.Split('|');
                return parts.Length == 3 && long.TryParse(parts[1], out var total) && total > 0;
            }
            catch { return false; }
        }
    }

    // ═══════════ IPreferredMagnetEngine ═══════════

    public Task<List<CatClawVideo.Core.Interfaces.MagnetFile>?> ListFilesAsync(
        string magnet, string? preferName = null, CancellationToken ct = default)
        => ListFilesCoreAsync(magnet, preferName);

    /// <summary>
    /// 用迅雷起播：挑一个视频文件（preferName 命中优先，否则体积最大）再让它边下边播。
    /// 任何一步失败返回 null —— 调用方回落内置 BT。
    /// </summary>
    public async Task<CatClawVideo.Core.Interfaces.MagnetPlayback?> TryOpenAsync(
        string magnet, string? preferName = null, CancellationToken ct = default)
    {
        if (!_ready)
        {
            await EnsureReadyAsync().ConfigureAwait(false);
            if (!_ready) return null;
        }

        var files = await ListFilesCoreAsync(magnet, preferName).ConfigureAwait(false);
        if (files is null || files.Count == 0) return null;

        var videos = files.Where(f => VideoExt.Contains(Path.GetExtension(f.Name))).ToList();
        var pool = videos.Count > 0 ? videos : files;

        var picked = pool
            .OrderByDescending(f => string.IsNullOrEmpty(preferName) ? 0
                : (f.Name.Contains(Path.GetFileNameWithoutExtension(preferName), StringComparison.OrdinalIgnoreCase)
                   || Path.GetFileNameWithoutExtension(preferName).Contains(Path.GetFileNameWithoutExtension(f.Name), StringComparison.OrdinalIgnoreCase)) ? 1 : 0)
            .ThenByDescending(f => f.Size)
            .First();

        var url = await PlayFileAsync(picked.Index).ConfigureAwait(false);
        if (string.IsNullOrEmpty(url)) return null;

        _log?.Invoke($"[迅雷] 命中：{picked.Name}（{picked.Size / 1048576.0:F1} MB，共 {files.Count} 个文件）");
        return new CatClawVideo.Core.Interfaces.MagnetPlayback(
            ResolveInfoHash(magnet), picked.Index, picked.Size, picked.Name, url);
    }

    /// <summary>从磁力里取 40 位 btih（v1）</summary>
    private static string ResolveInfoHash(string magnet)
    {
        var i = magnet.IndexOf("btih:", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return "";
        var rest = magnet[(i + 5)..];
        var end = rest.IndexOf('&');
        var h = (end < 0 ? rest : rest[..end]).Trim();
        return h.Length >= 40 ? h[..40].ToUpperInvariant() : h.ToUpperInvariant();
    }

    /// <summary>初始化迅雷引擎（幂等，只真正尝试一次）</summary>
    public Task<bool> EnsureReadyAsync()
    {
        if (_ready) return Task.FromResult(true);
        return Task.Run(() =>
        {
            _gate.Wait();
            try
            {
                if (_ready) return true;
                if (_attempted) return false;
                _attempted = true;
                try
                {
                    _cls = Java.Lang.Class.ForName(
                        BridgeClass, true, global::Android.App.Application.Context.ClassLoader);
                    if (_cls is null)
                    {
                        _log?.Invoke($"[迅雷] 桥接类 {BridgeClass} 未找到（AndroidJavaSource 未编译进来？）");
                        return false;
                    }

                    // init(Context, String cacheRoot)
                    var init = FindStatic("init", 2);
                    if (init is null) { _log?.Invoke("[迅雷] 桥接缺少 init(Context,String)"); return false; }

                    var r = init.Invoke(null,
                    [
                        global::Android.App.Application.Context,
                        new Java.Lang.String(_cacheRoot),
                    ]);
                    var s = (r as Java.Lang.String)?.ToString() ?? "";

                    if (s.StartsWith("OK:", StringComparison.Ordinal))
                    {
                        _ready = true;
                        _log?.Invoke($"[迅雷] 引擎就绪，SDK 版本 {s[3..]}（缓存 {_cacheRoot}）");
                        return true;
                    }
                    _log?.Invoke($"[迅雷] 引擎初始化失败：{s}");
                    return false;
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"[迅雷] 初始化异常：{ex.GetType().Name}: {ex.Message}");
                    return false;
                }
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    /// <summary>
    /// 解析磁力并返回种子内文件列表（TVBox「一个磁力展开成多集」的来源）。
    /// 返回 null 表示失败/不可用（调用方应回落内置 BT）。
    /// </summary>
    public Task<List<CatClawVideo.Core.Interfaces.MagnetFile>?> ListFilesCoreAsync(string magnet, string? preferName = null)
    {
        if (!_ready) return Task.FromResult<List<CatClawVideo.Core.Interfaces.MagnetFile>?>(null);
        return Task.Run(() =>
        {
            try
            {
                lock (_callLock)
                {
                    var m = FindStatic("listFiles", 2);
                    if (m is null) return null;
                    var r = m.Invoke(null, [new Java.Lang.String(magnet), new Java.Lang.String(preferName ?? "")]);
                    var s = (r as Java.Lang.String)?.ToString() ?? "";
                    if (s.StartsWith("ERR:", StringComparison.Ordinal))
                    {
                        _log?.Invoke($"[迅雷] 解析文件列表失败：{s}");
                        return null;
                    }

                    var list = new List<CatClawVideo.Core.Interfaces.MagnetFile>();
                    foreach (var line in s.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var p = line.Split('\t');
                        if (p.Length < 3) continue;
                        if (!int.TryParse(p[0], out var idx)) continue;
                        _ = long.TryParse(p[1], out var size);
                        list.Add(new CatClawVideo.Core.Interfaces.MagnetFile(idx, size, p[2]));
                    }
                    _log?.Invoke($"[迅雷] 文件列表 {list.Count} 项");
                    return list.Count == 0 ? null : list;
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[迅雷] 解析异常：{ex.GetType().Name}: {ex.Message}");
                return null;
            }
        });
    }

    /// <summary>让迅雷起播第 index 个文件，返回本地可播 http 地址；失败返回 null</summary>
    public Task<string?> PlayFileAsync(int index)
    {
        if (!_ready) return Task.FromResult<string?>(null);
        return Task.Run(() =>
        {
            try
            {
                lock (_callLock)
                {
                    var m = FindStatic("playFile", 1);
                    if (m is null) return null;
                    var r = m.Invoke(null, [new Java.Lang.Integer(index)]);
                    var s = (r as Java.Lang.String)?.ToString() ?? "";
                    if (s.StartsWith("OK:", StringComparison.Ordinal))
                    {
                        var url = s[3..];
                        _log?.Invoke($"[迅雷] 起播 index={index} → {url}");
                        return url;
                    }
                    _log?.Invoke($"[迅雷] 起播失败 index={index}：{s}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[迅雷] 起播异常：{ex.GetType().Name}: {ex.Message}");
                return null;
            }
        });
    }

    /// <summary>停止当前下载任务</summary>
    public void Stop()
    {
        if (!_ready) return;
        try
        {
            lock (_callLock) { FindStatic("stop", 0)?.Invoke(null, []); }
        }
        catch { }
    }

    private readonly object _callLock = new();

    /// <summary>
    /// 按「方法名 + 参数个数」定位静态方法。
    /// ⚠️ 反射调用 Java 静态方法必须能过 JNI 的参数类型校验，所以这里仍按个数匹配 ——
    /// 桥类是我们自己的、方法名唯一，不存在重载歧义。
    /// </summary>
    private Java.Lang.Reflect.Method? FindStatic(string name, int paramCount)
    {
        var cls = _cls ?? throw new InvalidOperationException("桥接类未加载");
        foreach (var m in cls.GetDeclaredMethods() ?? [])
        {
            if (m?.Name == name && (m.GetParameterTypes()?.Length ?? -1) == paramCount) return m;
        }
        return null;
    }
}
