using CatClawVideo.Core.Interfaces;

namespace CatClawVideo.Maui.Platforms.Android;

/// <summary>
/// 荐片 P2P 宿主支持（Android 实现）。
///
/// <para>真实逻辑在 Java 侧 <c>com.catclaw.video.JpP2P</c>（保留 JVM 的 GBK / URLDecoder / Uri 语义），
/// 本类只做 JNI 反射调用与启动编排。</para>
///
/// <para><b>启动时机</b>：<see cref="EnsureStartedAsync"/> 必须在爬虫调用 <c>playerContent</c> 之前
/// 让本地 httpd 起来；否则 spider 的 adjustPort 会白探 22 个端口并最终拼出空端口地址。</para>
/// </summary>
internal sealed class JianpianP2P : IJpP2P
{
    private const string BridgeClass = "com.catclaw.video.JpP2P";

    private readonly string _cacheDir;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>/proxy 回调处理器（绑定到 DexSpiderRuntime.ProxyAsync）。创建后再设置也可生效。</summary>
    public Func<IDictionary<string, string>, CancellationToken, Task<(int Status, string Mime, byte[]? Body)?>>? ProxyHandler
    {
        get => _proxyHandler;
        set
        {
            _proxyHandler = value;
            if (_proxy is not null) _proxy.ProxyHandler = value;
        }
    }

    private Func<IDictionary<string, string>, CancellationToken, Task<(int Status, string Mime, byte[]? Body)?>>? _proxyHandler;

    private Java.Lang.Class? _cls;
    private JianpianLocalProxy? _proxy;
    private bool _attempted;
    private bool _started;
    private int _port;

    public JianpianP2P(string cacheDir, Action<string>? log = null)
    {
        _cacheDir = cacheDir;
        _log = log;
    }

    /// <summary>实际监听端口（0 = 未启动/失败）</summary>
    public int Port => _port;

    public bool IsReady => _started && _port > 0;

    /// <summary>
    /// 加载 libp2p.so 并启动本地 httpd（幂等、线程安全）。
    /// 会阻塞到 httpd 就绪，故内部切到线程池执行；失败只尝试一次，避免每次解析都重试。
    /// </summary>
    public Task<bool> EnsureReadyAsync()
    {
        if (IsReady) return Task.FromResult(true);
        return Task.Run(() =>
        {
            _gate.Wait();
            try
            {
                if (IsReady) return true;
                if (_attempted) return false;   // 已经试过且失败 → 不再重试
                _attempted = true;
                try
                {
                    // ⚠️ 绝对不要在这里（C# 侧）调用 JavaSystem.LoadLibrary("p2p")：
                    // System.loadLibrary 内部用 Reflection.getCallerClass() 决定去哪个
                    // nativeLibraryDir 找库；从 C# 经 JNI 调用时**没有 Java 调用帧**，
                    // caller 会被判成 java.lang.System（boot classloader）→ 只搜 /system/lib64
                    // → 必然 dlopen failed: library "libp2p.so" not found，
                    // 即使 libp2p.so 明明已在 /data/app/.../lib/arm64/ 下（2026-09-14 真机实测）。
                    // 正确做法：让 Java 侧 P2PClass 的 static{} 去 loadLibrary —— 那时 caller 是
                    // com.p2p.P2PClass，走 app classloader 的 nativeLibraryDir 就能命中。
                    // ⚠️ 必须传显式 classloader 的重载。单参 Class.forName(String) 同样走
                    // Reflection.getCallerClass()：从 C# 经 JNI 调用时没有 Java 帧，
                    // caller 被判成 java.lang.Class（boot classloader）→ 只搜系统类
                    // → ClassNotFoundException: com.catclaw.video.JpP2P，
                    // 即使该类明明已在 classes2.dex 里（2026-09-14 真机实测）。
                    _cls = Java.Lang.Class.ForName(
                        BridgeClass, true, global::Android.App.Application.Context.ClassLoader);
                    if (_cls is null)
                    {
                        _log?.Invoke($"[荐片] 桥接类 {BridgeClass} 未找到（AndroidJavaSource 未编译进来？）");
                        return false;
                    }

                    var r = InvokeStatic("start", _cacheDir);
                    _port = r is Java.Lang.Integer i ? i.IntValue() : 0;
                    _started = _port > 0;
                    _log?.Invoke(_started
                        ? $"[荐片] 本地 P2P httpd 已启动，端口 {_port}（缓存 {_cacheDir}）"
                        : "[荐片] 本地 P2P httpd 启动失败");

                    // P2P httpd 的实际端口（实测 8087+）不在 spider 扫描的 9978…9999 区间内，
                    // 所以在扫描区间上再起一层反代转发过去，spider 才探得到。
                    if (_started)
                    {
                        _proxy = new JianpianLocalProxy(_port, _log) { ProxyHandler = _proxyHandler };
                        _proxy.Start();
                    }
                    return _started;
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"[荐片] P2P 启动异常（荐片将不可播）：{ex.GetType().Name}: {ex.Message}");
                    return false;
                }
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    public bool IsJpUrl(string url)
    {
        if (!_started || _cls is null || string.IsNullOrEmpty(url)) return false;
        try
        {
            return InvokeStatic("isJpUrl", url) is Java.Lang.Boolean b && b.BooleanValue();
        }
        catch
        {
            return false;
        }
    }

    public string? Decode(string url)
    {
        if (!IsReady || _cls is null || string.IsNullOrEmpty(url)) return null;
        try
        {
            // TVBox 播放链路会先剥掉 9 字符的 "tvbox-xg:" 前缀再交给 JPUrlDec
            var payload = url.StartsWith("tvbox-xg:", StringComparison.Ordinal) ? url[9..] : url;
            var r = InvokeStatic("decode", payload);
            var s = (r as Java.Lang.String)?.ToString();
            if (string.IsNullOrEmpty(s))
            {
                _log?.Invoke($"[荐片] 地址解码失败：{url}");
                return null;
            }
            _log?.Invoke($"[荐片] {url} → {s}");
            return s;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[荐片] 地址解码异常：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public void Finish()
    {
        if (_cls is null) return;
        try { InvokeStatic("finish"); } catch { }
    }

    /// <summary>
    /// 按「方法名 + 参数个数」定位静态方法再调用。
    /// 不用 <c>Class.FromType</c> 指定参数类型：Java 侧有 byte[] / String 混用，
    /// 按名+个数定位更稳，且桥接类是我们自己的、不存在重载歧义。
    /// </summary>
    private Java.Lang.Object? InvokeStatic(string name, params string[] stringArgs)
    {
        var cls = _cls ?? throw new InvalidOperationException("桥接类未加载");
        Java.Lang.Reflect.Method? target = null;
        foreach (var m in cls.GetDeclaredMethods() ?? [])
        {
            if (m?.Name == name && (m.GetParameterTypes()?.Length ?? 0) == stringArgs.Length)
            {
                target = m;
                break;
            }
        }
        if (target is null)
            throw new MissingMethodException($"{BridgeClass}.{name}/{stringArgs.Length} 不存在");

        Java.Lang.Object[] args = stringArgs.Length == 0
            ? []
            : [.. stringArgs.Select(s => (Java.Lang.Object)new Java.Lang.String(s))];
        return target.Invoke(null, args);
    }
}
