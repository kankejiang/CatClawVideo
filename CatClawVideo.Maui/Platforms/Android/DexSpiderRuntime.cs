using System.Collections.Concurrent;
using System.Security.Cryptography;
using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;
using Java.Lang;
using Java.Util;

namespace CatClawVideo.Maui.Platforms.Android;

/// <summary>
/// jar/dex 爬虫运行时（仅 Android）：加载订阅的 spider jar（PKZip 内含 classes.dex，
/// 常伪装成 .jpg 并带 ";md5;hash" 校验后缀），经 DexClassLoader + Java 反射
/// 调用 com.github.catvod.spider.* 类的 TVBox Spider 协议方法。
/// <para>协议方法签名（与 TVBox 一致）：</para>
/// - init(Context, String ext)
/// - homeContent(boolean filter)
/// - categoryContent(String tid, String pg, boolean filter, HashMap extend)
/// - detailContent(List&lt;String&gt; ids)
/// - searchContent(String key, boolean quick[, int pg])
/// - playerContent(String flag, String id, List vipFlags)
/// </summary>
public class DexSpiderRuntime : ISpiderRuntime, ISpiderProxyRuntime, ISpiderActionRuntime, ISpiderLiveRuntime
{
    public string Id => "android-dex";
    public bool IsSupported => true;

    private sealed class SpiderHolder
    {
        public Java.Lang.Object Instance = null!;
        public string SiteKey = "";
        public Java.Lang.Reflect.Method? Init;
        public Java.Lang.Reflect.Method? Home;
        public Java.Lang.Reflect.Method? Category;
        public Java.Lang.Reflect.Method? Detail;
        public Java.Lang.Reflect.Method? Search2;
        public Java.Lang.Reflect.Method? Search3;
        public Java.Lang.Reflect.Method? Player;
        /// <summary>Spider.action(String) —— 卡片是「操作入口」时用（网盘登录/扫码对话框由它弹出）</summary>
        /// <summary>Spider.liveContent(String) —— spider 型直播源（返回 txt/m3u/JSON 频道列表）</summary>
        public Java.Lang.Reflect.Method? Live;
        public Java.Lang.Reflect.Method? Action;
        /// <summary>Spider.proxy(Map) —— 宿主本地 HTTP 服务器回调用（荐片 /proxy?do=… 走这条）</summary>
        public Java.Lang.Reflect.Method? Proxy;
        /// <summary>
        /// proxy 回调专用锁。⚠️ 不能复用 <see cref="Lock"/>：调用链是
        /// <c>playerContent(持 Lock) → HTTP 到本机反代 → 反代回调 proxy() → 抢 Lock</c>，
        /// 复用必然自锁（真机实测：每 2 秒被 spider 超时重试一次，永不返回）。
        /// </summary>
        public readonly object ProxyLock = new();
        public readonly object Lock = new();
        /// <summary>该 jar 的 DexClassLoader —— 用于解析 <c>com.github.catvod.spider.Proxy</c> 静态代理入口</summary>
        public ClassLoader? Loader;
        public bool Initialized;
    }

    private readonly ConcurrentDictionary<string, SpiderHolder> _spiders = new();

    /// <summary>站点 → 初始化单飞信号量（见 <see cref="EnsureSpiderAsync"/> 的说明）。</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _initGates = new();

    /// <summary>
    /// jar 路径 → 保护壳绑定信号量。**比站点锁更粗一层**：
    /// 多个站点可共用同一个 jar，而它们会在 <c>BindProtectedJar</c> 里向**同一个**
    /// <c>Init</c> 静态单例写字段。只用站点锁挡不住「不同站点、同一 jar」的并发，
    /// 仍可能让 Guard 壳在构造期读到 null loader 而 abort。
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _jarGates = new();

    /// <summary>最近一次使用的 spider —— /proxy 请求常常不带 siteKey，用它兜底（对齐 TVBox getCurrentProxySource）</summary>
    private volatile SpiderHolder? _lastUsed;
    private readonly HttpClient _http = new();
    private readonly string _cacheDir;
    private readonly Action<string>? _log;
    private readonly Func<global::Android.App.Activity?>? _currentActivity;

    /// <summary>
    /// <param name="currentActivity">前台 Activity 访问器：Guard 系 jar 的 init(context, ext) 拿到
    /// Activity 而非 Application 才能弹网盘配置对话框（「已登录+启用中」列表/扫码登录浮层，
    /// Alert 弹窗需要 Activity token）。返回 null 时退化为 Application.Context。</param>
    /// </summary>
    public DexSpiderRuntime(string cacheDir, Action<string>? log = null,
        Func<global::Android.App.Activity?>? currentActivity = null)
    {
        _cacheDir = cacheDir;
        _log = log;
        _currentActivity = currentActivity;
        Directory.CreateDirectory(cacheDir);
    }

    /// <summary>给 jar 的 Context：前台 Activity 优先（可弹窗），否则 Application.Context。</summary>
    private global::Android.Content.Context UiContext
        => _currentActivity?.Invoke() ?? global::Android.App.Application.Context;

    private void Log(string m) => _log?.Invoke("[dex] " + m);

    // ═══════════ ISpiderRuntime ═══════════

    public Task<string> HomeContentAsync(VodSiteInfo site, CancellationToken ct = default) =>
        InvokeAsync(site, h => CallSafe(h, h.Home, new Java.Lang.Boolean(true)), ct);

    public Task<string> CategoryContentAsync(VodSiteInfo site, string tid, string pg,
        IReadOnlyDictionary<string, string>? filter = null, CancellationToken ct = default)
    {
        // 协议第 3 参 filter 是「本次是否带筛选条件」，第 4 参才是键值表；
        // 之前恒传 false + 空 map，等于把所有站点的筛选器都关掉了。
        var map = new HashMap();
        var has = filter is { Count: > 0 };
        if (has)
            foreach (var (k, v) in filter!)
                map.Put(new Java.Lang.String(k), new Java.Lang.String(v));
        return InvokeAsync(site, h => CallSafe(h, h.Category,
            new Java.Lang.String(tid), new Java.Lang.String(pg),
            new Java.Lang.Boolean(has), map), ct);
    }

    public Task<string> DetailContentAsync(VodSiteInfo site, string id, CancellationToken ct = default)
    {
        var list = new ArrayList();
        list.Add(new Java.Lang.String(id));
        return InvokeAsync(site, h => CallSafe(h, h.Detail, list), ct);
    }

    public Task<string> SearchContentAsync(VodSiteInfo site, string keyword, string pg, CancellationToken ct = default) =>
        InvokeAsync(site, h => h.Search3 != null
            ? CallSafe(h, h.Search3, new Java.Lang.String(keyword), new Java.Lang.Boolean(false), new Java.Lang.Integer(pg))
            : CallSafe(h, h.Search2, new Java.Lang.String(keyword), new Java.Lang.Boolean(false)), ct);

    public Task<string> PlayerContentAsync(VodSiteInfo site, string flag, string id, CancellationToken ct = default)
    {
        var flags = new ArrayList();
        return InvokeAsync(site, h => CallSafe(h, h.Player,
            new Java.Lang.String(flag), new Java.Lang.String(id), flags), ct);
    }

    public Task<string> ActionAsync(VodSiteInfo site, string actionJson, CancellationToken ct = default) =>
        InvokeAsync(site, h => CallSafe(h, h.Action, new Java.Lang.String(actionJson)), ct);

    /// <summary>spider 型直播源：<c>liveContent(真实地址)</c> 返回频道列表文本。</summary>
    public Task<string> LiveContentAsync(VodSiteInfo site, string url, CancellationToken ct = default) =>
        InvokeAsync(site, h => h.Live is null
            ? throw new NotSupportedException($"「{site.Key}」的爬虫未实现 liveContent")
            : CallSafe(h, h.Live, new Java.Lang.String(url)), ct);

    // ═══════════ 装配 ═══════════

    /// <summary>jar URL 形如 "url;md5;hash"，剥离校验段取真实地址</summary>
    private static string JarUrl(VodSiteInfo site)
    {
        var jar = site.Jar ?? "";
        var i = jar.IndexOf(";md5;", StringComparison.OrdinalIgnoreCase);
        return i > 0 ? jar[..i] : jar;
    }

    private static string? JarMd5(VodSiteInfo site)
    {
        var jar = site.Jar ?? "";
        var i = jar.IndexOf(";md5;", StringComparison.OrdinalIgnoreCase);
        return i > 0 ? jar[(i + 5)..].Trim() : null;
    }

    private async Task<ClassLoader> GetLoaderAsync(VodSiteInfo site, CancellationToken ct)
    {
        var jarUrl = JarUrl(site);
        if (string.IsNullOrEmpty(jarUrl))
            throw new InvalidOperationException($"站点 {site.Name} 缺少 spider jar 地址");

        var fileName = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(jarUrl)))[..24].ToLowerInvariant();
        var jarPath = Path.Combine(_cacheDir, fileName + ".jar");

        // ⚠ 缓存必须**校验后再用**，不能只看 File.Exists。
        //
        // 实测（2026-09-19 用户手机）：缓存里留下过一个 **0 字节**的 jar —— 并发下载同一 jar 时
        // 多个线程写同一个路径、互相截断；而这里原先只判 File.Exists，于是这个坏文件被当成
        // 有效缓存、**永不重下**。DexClassLoader 加载它时 ART 直接 abort
        // （JNI DETECTED ERROR，C# 侧 try/catch 拦不住）→ 该站点以后**每次搜索都闪退**。
        if (!IsUsableJar(jarPath))
        {
            if (File.Exists(jarPath))
                Log($"jar 缓存损坏（{new FileInfo(jarPath).Length} 字节，非 ZIP），删除重下: {fileName}.jar");
            else
                Log($"下载 spider jar: {jarUrl[..System.Math.Min(80, jarUrl.Length)]}");

            TryDelete(jarPath);

            using var resp = await _http.GetAsync(jarUrl, ct);
            resp.EnsureSuccessStatusCode();
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);

            // 伪装 jpg 头（FF D8）时剥掉前导字节定位 PK
            var pk = IndexOfPk(bytes);
            if (pk > 0) bytes = bytes[pk..];

            // 非 ZIP 一律拒收：HTML 错误页 / 半截响应若落盘，下次会被当成有效缓存
            if (bytes.Length < 4 || bytes[0] != 0x50 || bytes[1] != 0x4B)
                throw new InvalidOperationException(
                    $"spider jar 不是有效的 ZIP（{bytes.Length} 字节）: {jarUrl[..System.Math.Min(80, jarUrl.Length)]}");

            var expect = JarMd5(site);
            if (!string.IsNullOrEmpty(expect))
            {
                var actual = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
                if (!actual.Equals(expect, StringComparison.OrdinalIgnoreCase))
                    Log($"jar md5 不匹配（期望 {expect} 实际 {actual}），继续尝试加载");
            }

            // 原子落盘（先写临时文件再改名）：直接写 jarPath 的话，并发下载会写坏同一个文件
            // —— 那个 0 字节缓存就是这么来的。
            await WriteJarAtomicallyAsync(jarPath, bytes, ct);

            // Android 10+ W^X：可写的 dex 文件会被 DexClassLoader 拒绝执行（Writable dex file 错误）
            File.SetAttributes(jarPath, FileAttributes.ReadOnly);
            Log($"jar 缓存完成 len={bytes.Length}");
        }

        var optDir = Path.Combine(_cacheDir, "opt");
        Directory.CreateDirectory(optDir);
        // 无论新下载还是缓存命中，都必须只读（Android 10+ W^X：可写 dex 拒绝执行）
        if ((File.GetAttributes(jarPath) & FileAttributes.ReadOnly) == 0)
        {
            File.SetAttributes(jarPath, FileAttributes.ReadOnly);
            Log("jar 已置只读（W^X）");
        }

        // parent 必须是 App 自身的 ClassLoader（对照 TVBox 的 App.getInstance().getClassLoader()）：
        // spider jar 里的爬虫类继承宿主基类 com.github.catvod.crawler.Spider（见 Platforms/Android 下的
        // Spider.java），该基类只存在于 App 自己的 APK 中，只能由 parent 解析到。parent 若解析不到，
        // 加载任何爬虫类都会抛 NoClassDefFoundError。
        var parent = global::Android.App.Application.Context.ClassLoader ?? ClassLoader.SystemClassLoader;
        return new global::Dalvik.SystemInterop.DexClassLoader(jarPath, optDir, null, parent);
    }

    /// <summary>找 PK（Zip 头），兼容伪装成图片的 jar</summary>
    private static int IndexOfPk(byte[] bytes)
    {
        for (int i = 0; i + 1 < bytes.Length && i < 4096; i++)
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B)
                return i;
        return 0;
    }

    /// <summary>
    /// 缓存 jar 是否可用：存在、非空、且以 ZIP 头（<c>PK</c>）开头。
    ///
    /// <para>只判 <c>File.Exists</c> 是不够的 —— 空文件 / 半截下载 / HTML 错误页都会通过，
    /// 而 <c>DexClassLoader</c> 加载它们时 ART 会直接 abort（不可捕获）。</para>
    /// </summary>
    private static bool IsUsableJar(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < 4) return false;
            using var fs = File.OpenRead(path);
            return fs.ReadByte() == 0x50 && fs.ReadByte() == 0x4B;   // "PK"
        }
        catch
        {
            return false;
        }
    }

    /// <summary>原子写文件：先写同目录临时文件再改名（避免并发写坏目标文件）。</summary>
    private static async Task WriteJarAtomicallyAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var tmp = path + ".tmp-" + Environment.CurrentManagedThreadId;
        try
        {
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    /// <summary>删除文件（只读属性先摘掉）；失败静默 —— 调用方随后会重新下载/重新校验。</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
        }
        catch { }
        try { File.Delete(path); } catch { }
    }

    private static Class? TryLoad(ClassLoader loader, string className) =>
        TryLoad(loader, className, out _);

    /// <summary>
    /// 按 TVBox 的类名前缀约定查找爬虫类。
    /// <para>⚠️ 失败原因必须回传：这里的失败几乎都不是「类不存在」，而是类加载器解析依赖时
    /// 失败。典型例子是 spider jar 的爬虫类继承宿主基类
    /// <c>com.github.catvod.crawler.Spider</c>，宿主没提供该基类时抛 NoClassDefFoundError，
    /// 静默吞掉异常会让调用方永远只看到「jar 中找不到爬虫类」这种误导性描述。</para>
    /// </summary>
    private static Class? TryLoad(ClassLoader loader, string className, out string? lastError)
    {
        string[] prefixes =
        {
            "com.github.catvod.spider.",
            "com.github.catvod.crawler.",
            "",
        };
        lastError = null;
        foreach (var p in prefixes)
        {
            try { return loader.LoadClass(p + className); }
            catch (System.Exception ex) { lastError = $"{ex.GetType().Name}: {ex.Message}"; }
        }
        return null;
    }

    private async Task<SpiderHolder> EnsureSpiderAsync(VodSiteInfo site, CancellationToken ct)
    {
        if (_spiders.TryGetValue(site.Key, out var ready) && ready.Initialized)
            return ready;

        // ── 单飞（single-flight）：同一站点同时只允许一个初始化在跑 ──
        //
        // ⚠️ 这是 2026-09-19 手机搜索闪退的**根因**。原实现从上面的 TryGetValue 到下面
        // 发布 _spiders[site.Key] 之间没有任何同步（中间还夹着 await 与 Task.Run），
        // 而搜索会**并发遍历全部站点**（SearchPage 里 sites.Select(...) 无并发上限）。
        //
        // 后果有两层：
        // ① 同一站点被并行初始化多次 —— 重复下载/加载 jar、重复建 DexClassLoader；
        // ② 更致命：多个**共用同一 jar** 的站点会同时执行 BindProtectedJar，各自向
        //    同一个 jar 内的 Init 静态单例**写字段**（Context / DexClassLoader）。
        //    而 Guard 壳的构造函数要在构造期间读这个单例取真实实现 →
        //    读到 null 时 ART 直接 abort（JNI DETECTED ERROR: obj == null /
        //    can't call ClassLoader.loadClass on null object），C# 侧 try/catch 拦不住，
        //    表现就是「搜着搜着毫无征兆闪退」。
        //
        // 对照实现都是加锁的：桌面桥 JavaBridge/Server.java 用 synchronized(LOCK)、
        // JS 运行时 DrpyJsSpiderRuntime 用 GetOrAdd + 双重检查。这里此前是唯一漏网的。
        //
        // 用「等待信号量」而不是直接 lock：初始化内部有 await，持锁跨 await 会长时间
        // 阻塞其它线程；信号量方案下后来者只需等前一个跑完再取缓存，语义相同但不死锁。
        var gate = _initGates.GetOrAdd(site.Key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 双重检查：等锁期间前一个可能已经把该站点初始化好了
            if (_spiders.TryGetValue(site.Key, out var done) && done.Initialized)
                return done;

            return await InitSpiderAsync(site, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>真正执行站点初始化（调用方已持该站点的单飞锁，见 <see cref="EnsureSpiderAsync"/>）。</summary>
    private async Task<SpiderHolder> InitSpiderAsync(VodSiteInfo site, CancellationToken ct)
    {
        var loader = await GetLoaderAsync(site, ct).ConfigureAwait(false);

        // ⚠️ DexClassLoader 加载 jar + 反射查找/实例化爬虫类 + 调 init()（爬虫内部通常还要建网络、
        // 解析配置）都是耗时的同步 JNI 操作，必须整体放到线程池执行。
        // 此前它们直接跑在 await 之后的续体上（即 UI 线程），加载大 jar 时会冻住界面直到 ANR。
        // 初始化的站点必须原子发布（先建好 holder、置 Initialized 再入字典），避免并发调用拿到半成品。
        return await Task.Run(() =>
        {
            var className = site.Api.StartsWith("csp_", StringComparison.OrdinalIgnoreCase)
                ? site.Api[4..]
                : site.Api;

            var cls = TryLoad(loader, className, out var loadError)
                ?? throw new InvalidOperationException($"jar 中找不到爬虫类: {className}（{loadError ?? "无异常信息"}）");

            // ⚠⚠ 关键区：BindProtectedJar + 实例化必须**按 jar 串行**。
            //
            // 这两步共同操作 jar 内**共享的** Init 静态单例：
            //   BindProtectedJar 往里写 Context / DexClassLoader 字段；
            //   NewInstance 触发 Guard 壳构造函数，它在构造期读同一单例取真实实现。
            // 订阅里多个站点共用同一个 jar（很常见），站点级锁挡不住它们 —— 一旦交错执行，
            // 壳就可能读到「另一个线程刚覆盖/还没写完」的字段（null）→ ART 直接 abort
            // （2026-09-19 手机搜索闪退）。这里再按 jar 串起来，窗口才真正关闭。
            //
            // 锁粒度：只在绑定与实例化期间持有，init() 等后续步骤在锁外（它们不再碰共享字段，
            // 且 init 可能较慢，持锁会拖长其它站点等待）。同步等锁即可 —— 本段本就跑在线程池。
            var jarKey = JarUrl(site);
            var jarGate = _jarGates.GetOrAdd(jarKey, _ => new SemaphoreSlim(1, 1));
            jarGate.Wait();
            Java.Lang.Object instance;
            try
            {
                // 保护壳 jar 引导（对照 jun 分支 ProtectedInitJar，纯反射绑定）：
                // Guard 系 jar 的 Init 单例需要外部注入 Context 与 DexClassLoader 才能工作。
                // 必须在实例化爬虫类之前完成：Guard 壳类的构造函数会通过 Init.getSpider() 取真实实现。
                //
                // 返回 false = 是 Guard 壳但壳没绑好 —— 此时**绝不能再 NewInstance**：
                // 构造函数会以 null 接收者发 JNI 调用，ART 直接 abort 整个进程
                // （JNI DETECTED ERROR，C# 拦不住）。改抛托管异常，只废掉这一个站点。
                if (!BindProtectedJar(loader, site))
                    throw new InvalidOperationException(
                        $"Guard 保护壳初始化失败（Init 未能绑定 DexClassLoader），已跳过站点 {site.Key} 以避免进程崩溃");

                // ⚠️ 实例化与 init() 是 Guard 壳最容易失败的两步（真实实现由 native 解密后反射调用），
                // 失败时抛的是 InvocationTargetException，其 Message 是 .NET 兜底的英文文案、毫无信息量，
                // 真实异常在 Cause 链里。必须显式展开，否则调用方只能看到
                // 「Exception of type 'Java.Lang.Reflect.InvocationTargetException' was thrown.」
                try
                {
                    instance = (Java.Lang.Object)cls.GetConstructor().NewInstance();
                }
                catch (Java.Lang.Throwable t)
                {
                    var d = $"爬虫类实例化失败: {className} → {Describe(t)}";
                    Log(d);
                    throw new InvalidOperationException(d, t);
                }
            }
            finally
            {
                jarGate.Release();
            }

            // 与 TVBox JarLoader.getSpider 的调用序列严格一致：siteKey -> initApi -> init。
            // ① siteKey：部分爬虫在 init 之前就读自身 key（默认 null）。
            // ② initApi：注入宿主 SpiderApi。XBPQ（饭太硬/海阔系，订阅里占比最高的 jar 爬虫）
            //    覆盖了 initApi 并调用 super.initApi，还把实例存进字段供后续日志调用；
            //    不注入则其字段恒为 null，出错路径直接 NPE，且 super 调用无法完成链接。
            //    参数类型直接从方法签名取（避免 Class.ForName 走错类加载器导致类型不一致）。
            try
            {
                cls.GetField("siteKey")?.Set(instance, new Java.Lang.String(site.Key));
            }
            catch (Java.Lang.Throwable t)
            {
                Log($"siteKey 注入跳过: {className} → {Describe(t)}");
            }

            try
            {
                Java.Lang.Reflect.Method? initApi = null;
                Java.Lang.Class? apiType = null;
                foreach (var m in cls.GetMethods() ?? System.Array.Empty<Java.Lang.Reflect.Method>())
                {
                    if (m.Name != "initApi") continue;
                    var ps = m.GetParameterTypes();
                    if (ps is { Length: 1 }) { initApi = m; apiType = ps[0]; break; }
                }
                if (initApi != null && apiType != null)
                {
                    var apiInstance = (Java.Lang.Object)apiType.GetConstructor().NewInstance();
                    initApi.Invoke(instance, apiInstance);
                    Log($"initApi 注入完成: {className}");
                }
            }
            catch (Java.Lang.Throwable t)
            {
                Log($"initApi 注入失败: {className} → {Describe(t)}");
            }

            var holder = new SpiderHolder
            {
                Instance = instance,
                Init = Find(cls, "init", Java.Lang.Class.FromType(typeof(global::Android.Content.Context)), Java.Lang.Class.FromType(typeof(Java.Lang.String))),
                Home = Find(cls, "homeContent", Java.Lang.Boolean.Type),
                Category = Find(cls, "categoryContent",
                    Java.Lang.Class.FromType(typeof(Java.Lang.String)),
                    Java.Lang.Class.FromType(typeof(Java.Lang.String)),
                    Java.Lang.Boolean.Type,
                    Java.Lang.Class.FromType(typeof(HashMap))),
                Detail = Find(cls, "detailContent", Java.Lang.Class.FromType(typeof(Java.Util.IList))),
                Search2 = Find(cls, "searchContent",
                    Java.Lang.Class.FromType(typeof(Java.Lang.String)), Java.Lang.Boolean.Type),
                Search3 = Find(cls, "searchContent",
                    Java.Lang.Class.FromType(typeof(Java.Lang.String)), Java.Lang.Boolean.Type,
                    Java.Lang.Integer.Type),
                Live = Find(cls, "liveContent", Java.Lang.Class.FromType(typeof(Java.Lang.String))),
                Player = Find(cls, "playerContent",
                    Java.Lang.Class.FromType(typeof(Java.Lang.String)),
                    Java.Lang.Class.FromType(typeof(Java.Lang.String)),
                    Java.Lang.Class.FromType(typeof(Java.Util.IList))),
                Action = Find(cls, "action", Java.Lang.Class.FromType(typeof(Java.Lang.String))),
                Proxy = FindByName(cls, "proxy", 1),
                SiteKey = site.Key,
                Loader = loader,
            };

            if (holder.Init != null)
            {
                var ext = site.Ext ?? "";
                Log($"init {className} ext={ext[..System.Math.Min(60, ext.Length)]}");
                try
                {
                    holder.Init.Invoke(holder.Instance, UiContext, new Java.Lang.String(ext));
                }
                catch (Java.Lang.Throwable t)
                {
                    var d = $"爬虫 init() 失败: {className} → {Describe(t)}";
                    Log(d);
                    throw new InvalidOperationException(d, t);
                }
            }

            holder.Initialized = true;
            _spiders[site.Key] = holder;
            _lastUsed = holder;
            Log($"站点 {site.Key} spider 就绪: {cls.Name}");
            return holder;
        }, ct).ConfigureAwait(false);
    }

    // ═══════════ Guard 明文 dex 导出（PC 端离线使用）═══════════
    //
    // 背景：Guard 加固 jar 的解密器是 ARM Android native，Windows 执行不了。
    // 但 DexNative.getLoader() 返回的是 **DexClassLoader**（Android 8+ 必须由磁盘文件支撑）
    // ⇒ 解密后的明文 dex 就在磁盘上。这里顺手导出成 jar（内含 classes.dex），
    // PC 端 JavaSpiderRuntime 用它替代 Guard jar → 该 jar 的全部 Guard 站点在 Windows 可用，
    // 且之后**不再依赖手机**。

    private Java.Lang.Class? _exporterClass;
    private readonly ConcurrentDictionary<string, long> _exportedDex = new();
    private readonly object _exportLock = new();

    private Java.Lang.Class? EnsureExporter()
    {
        try
        {
            return _exporterClass ??= Java.Lang.Class.ForName(
                "com.catclaw.video.DexExporter", true,
                global::Android.App.Application.Context.ClassLoader);
        }
        catch (System.Exception ex)
        {
            Log($"DexExporter 未找到: {ex.Message}");
            return null;
        }
    }

    private void ExportDecryptedDex(Java.Lang.Object dexClassLoader, VodSiteInfo site)
    {
        try
        {
            var jarUrl = JarUrl(site);
            if (string.IsNullOrEmpty(jarUrl)) return;

            // 命名键与两端既有的 jar 缓存键一致：SHA256(jarUrl)[..24]
            var key = Convert.ToHexString(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(jarUrl)))[..24].ToLowerInvariant();

            var root = global::Android.App.Application.Context.GetExternalFilesDir(null)?.AbsolutePath ?? _cacheDir;
            var dir = Path.Combine(root, "dexdump");
            var dst = Path.Combine(dir, key + ".jar");

            var exporter = EnsureExporter();
            if (exporter is null) return;

            // ① 取 dexclassloader 实际加载的 dex 文件路径
            var dexPathOf = Find(exporter, "dexPathOf",
                Java.Lang.Class.FromType(typeof(Java.Lang.Object)));
            var src = (dexPathOf?.Invoke(null, [dexClassLoader]) as Java.Lang.String)?.ToString();

            if (string.IsNullOrEmpty(src) || !File.Exists(src))
            {
                Log($"解密 dex 路径未取到（{(string.IsNullOrEmpty(src) ? "反射为空" : src)}）");
                return;
            }

            var len = new FileInfo(src).Length;

            lock (_exportLock)
            {
                // 同长度视为同一份，跳过重复导出
                if (_exportedDex.TryGetValue(key, out var done) && done == len && File.Exists(dst)) return;

                var pack = Find(exporter, "packDexToJar",
                    Java.Lang.Class.FromType(typeof(Java.Lang.String)),
                    Java.Lang.Class.FromType(typeof(Java.Lang.String)));
                var r = (pack?.Invoke(null, [new Java.Lang.String(src), new Java.Lang.String(dst)])
                         as Java.Lang.String)?.ToString() ?? "";

                if (!r.StartsWith("OK:", StringComparison.Ordinal))
                {
                    Log($"导出解密 dex 失败：{r}");
                    return;
                }
                _exportedDex[key] = len;
            }

            try
            {
                File.AppendAllText(Path.Combine(dir, "index.txt"),
                    $"{key}\t{len}\t{JarMd5(site) ?? "-"}\t{jarUrl}{Environment.NewLine}");
            }
            catch { }

            Log($"✅ 已导出解密 dex：{Path.GetFileName(dst)}（{len / 1024} KB）← {site.Name}");
        }
        catch (System.Exception ex)
        {
            Log($"导出解密 dex 异常：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 保护壳 jar 引导（移植自 jun 分支 ProtectedInitJar，纯 Java 反射，无 native 依赖）：
    /// ① Init.get() 取单例 → 绑定 Context 字段（"c" 优先，退化任意实例 Context 字段）
    /// ② jar 内 DexNative.getLoader(context) 取 DexClassLoader → 绑入 Init 的实例字段
    /// ③ best-effort 调 replaceCloudDiskNames / startGoProxy
    /// 普通 jar 没有这些字段/类时全部静默跳过，无副作用。
    /// </summary>
    /// <returns>
    /// 是否可以安全实例化爬虫类。
    ///
    /// <para><b>为什么需要这个返回值</b>：Guard 壳的构造函数会在构造期读 <c>Init</c> 单例取真实实现，
    /// 而该单例的 <c>DexClassLoader</c> 字段若没绑上（我们注入失败 / 取不到），壳里的 native
    /// 回调就会以 null 接收者发 JNI 调用 —— <b>ART 直接 abort 进程</b>，
    /// <c>Java.Lang.Throwable</c> 与 <c>System.Exception</c> 都拦不住，用户看到的是
    /// 「搜着搜着毫无征兆闪退」（2026-09-19 用户实测）。</para>
    ///
    /// <para>所以在调用 <c>NewInstance()</c> 之前就把「壳存在但没绑好」这一状态识别出来，
    /// 改抛**托管异常**（可捕获）→ 该站点标记失败、其余站点照常出结果，
    /// 而不是让整个应用陪葬。</para>
    /// </returns>
    private bool BindProtectedJar(ClassLoader loader, VodSiteInfo site)
    {
        try
        {
            var initCls = TryLoad(loader, "Init");
            if (initCls == null) return true;

            Java.Lang.Object? init = null;
            try
            {
                var get = initCls.GetMethod("get");
                init = (Java.Lang.Object?)get.Invoke(null);
            }
            catch { }

            var appCtx = global::Android.App.Application.Context;
            // Guard 系网盘源（csp_MyDriveGuard 等）弹「云盘配置」对话框需要 **Activity** token：
            // 优先把前台 Activity 绑进 "c" 字段（jar 内部 instanceof Activity 判断 UI 能力）；
            // 字段声明类型是 Application 时（jun 壳常见）绑 Activity 会 IllegalArgumentException，回落 Application。
            var uiActivity = _currentActivity?.Invoke();
            var uiCtx = (global::Android.Content.Context?)uiActivity ?? appCtx;

            // ① bindContext（对照 jun ProtectedInitJar.bindContext）：先试字段名 "c"，否则按类型匹配实例字段。
            // ⚠️ 必须用「类型可赋值性」判断，不能用字段类型名字符串：混淆后的 Guard jar 里该字段的声明类型是
            // android.app.Application（Application 是 Context 的子类，类型名里没有 "Context" 字样），
            // 用 Contains("Context") 会漏绑 → Init 拿不到 Context → DexNative.getLoader 失败 →
            // 实例化爬虫类时抛 InvocationTargetException。TVBox 用的正是 Context.isAssignableFrom。
            if (init != null)
            {
                var contextType = Java.Lang.Class.FromType(typeof(global::Android.Content.Context));
                var bound = false;
                try
                {
                    var f = initCls.GetField("c");
                    Java.Lang.Reflect.AccessibleObject.SetAccessible(new Java.Lang.Reflect.AccessibleObject[] { f }, true);
                    f.Set(init, uiCtx);
                    bound = true;
                }
                catch { }

                if (!bound)
                {
                    for (var type = initCls; type != null && !bound; type = type.Superclass)
                    {
                        foreach (var f in type.GetDeclaredFields())
                        {
                            try
                            {
                                if (Java.Lang.Reflect.Modifier.IsStatic(f.Modifiers)) continue;
                                if (!contextType.IsAssignableFrom(f.Type)) continue;
                                // 声明类型是 Application → 只能绑 Application（绑 Activity 会 IllegalArgumentException）
                                var value = f.Type == Java.Lang.Class.FromType(typeof(global::Android.App.Application))
                                    ? (global::Android.Content.Context)appCtx
                                    : uiCtx;
                                Java.Lang.Reflect.AccessibleObject.SetAccessible(new Java.Lang.Reflect.AccessibleObject[] { f }, true);
                                f.Set(init, value);
                                bound = true;
                            }
                            catch { }
                        }
                    }
                }
            }

            // ② bindDexLoader：DexNative.getLoader(context) → DexClassLoader，绑入 Init 的实例字段。
            // 同样按类型可赋值性匹配并向上遍历继承链（对照 jun ProtectedInitJar.bindDexLoader）。
            //
            // ⚠️ 这一段的失败**必须留痕**：原实现外层是裸 catch{}，loader 没绑上时一行日志都没有，
            // 而下一个阶段（Guard 构造函数读单例）会直接 ART abort —— 事后完全无法定位。
            // 实测（2026-09-19 手机搜索闪退）崩溃栈正是
            // 「DexNative.getSpider ... can't call ClassLoader.loadClass on null object」。
            bool hasGuardNative = false;   // jar 里有没有 DexNative（有 = Guard 壳，构造函数会读 Init 单例）
            var loaderBound = false;

            try
            {
                var nativeCls = TryLoad(loader, "DexNative");
                hasGuardNative = nativeCls != null;

                var getLoader = nativeCls?.GetMethod("getLoader",
                    Java.Lang.Class.FromType(typeof(Java.Lang.Object)));
                var cl = getLoader?.Invoke(null, appCtx);
                if (cl is global::Dalvik.SystemInterop.DexClassLoader dexCl && init != null)
                {
                    // ★ A 方案：顺手把解密后的明文 dex 导出，供 Windows 端离线使用
                    ExportDecryptedDex(dexCl, site);

                    var dexLoaderType = Java.Lang.Class.FromType(typeof(global::Dalvik.SystemInterop.DexClassLoader));
                    for (var type = initCls; type != null && !loaderBound; type = type.Superclass)
                    {
                        foreach (var f in type.GetDeclaredFields())
                        {
                            try
                            {
                                if (Java.Lang.Reflect.Modifier.IsStatic(f.Modifiers)) continue;
                                if (!dexLoaderType.IsAssignableFrom(f.Type)) continue;
                                Java.Lang.Reflect.AccessibleObject.SetAccessible(new Java.Lang.Reflect.AccessibleObject[] { f }, true);
                                f.Set(init, dexCl);
                                loaderBound = true;
                                break;
                            }
                            catch { }
                        }
                    }
                    if (!loaderBound)
                        Log($"⚠ 保护壳 DexLoader 未绑定（Init 里没有 DexClassLoader 类型的实例字段）: {site.Key}");
                }
                else if (init != null)
                {
                    // init 拿到了、但 getLoader 没给出 DexClassLoader —— 这正是会 abort 的前置状态
                    Log($"⚠ 保护壳 DexLoader 取不到（DexNative.getLoader 返回 {(cl is null ? "null" : cl.GetType().Name)}）: {site.Key}");
                }
            }
            catch (Java.Lang.Throwable t)
            {
                Log($"⚠ 保护壳 DexLoader 绑定异常: {site.Key} → {Describe(t)}");
            }
            catch (System.Exception ex)
            {
                Log($"⚠ 保护壳 DexLoader 绑定异常: {site.Key} → {ex.GetType().Name}: {ex.Message}");
            }

            // ③ 可选引导
            try { initCls.GetMethod("replaceCloudDiskNames").Invoke(null); } catch { }
            try
            {
                initCls.GetMethod("startGoProxy", Java.Lang.Class.FromType(typeof(global::Android.Content.Context)))
                    .Invoke(null, appCtx);
            }
            catch { }

            Log("保护壳引导完成（Init/Context/DexLoader 绑定）");

            // Guard 壳（有 DexNative）但 DexLoader 没绑上 → 构造函数必定以 null 调用 native → ART abort。
            // 返回 false 让调用方改抛**可捕获的托管异常**，牺牲这一个站点、保住整个应用。
            if (hasGuardNative && !loaderBound)
            {
                Log($"⛔ {site.Key} 是 Guard 加固包但保护壳未绑定成功，跳过实例化以避免进程 abort");
                return false;
            }

            return true;
        }
        catch (System.Exception ex)
        {
            Log($"保护壳引导跳过: {ex.Message}");
            return true;
        }
    }

    /// <summary>
    /// 展开 Java 异常的完整 cause 链。<c>Throwable.ToString()</c> 给出「类名: message」，
    /// 但反射调用相关的 <c>InvocationTargetException</c> 的 message 往往为空，
    /// 真正的失败原因（NoClassDefFoundError / NPE / IO 异常…）挂在 Cause 上，必须逐层打印。
    /// </summary>
    private static string Describe(Java.Lang.Throwable t)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            sb.Append(t.ToString());
            for (var c = t.Cause; c != null; c = c.Cause)
                sb.Append("\n    ---> ").Append(c.ToString());
        }
        catch { }
        return sb.ToString();
    }

    private static Java.Lang.Reflect.Method? Find(Class cls, string name, params Class[] sig)
    {
        try { return cls.GetMethod(name, sig); }
        catch { return null; }
    }

    /// <summary>按名字 + 形参个数找方法（形参类型不便用 Class 表达时用，如 proxy(Map) 的 java.util.Map）</summary>
    private static Java.Lang.Reflect.Method? FindByName(Class cls, string name, int paramCount)
    {
        try
        {
            foreach (var m in cls.GetMethods() ?? [])
            {
                if (m?.Name == name && (m.GetParameterTypes()?.Length ?? -1) == paramCount) return m;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 回调爬虫自身的 <c>proxy(Map)</c>：宿主本地 HTTP 服务器收到 <c>/proxy?do=…</c> 时使用。
    /// <para><b>为什么必须回调爬虫</b>：荐片（JPJ）的 playerContent 会先请求
    /// <c>http://127.0.0.1:&lt;port&gt;/proxy?do=ck</c> 做握手，而这个端点的响应体只能由
    /// 爬虫自己产生 —— TVBox 的 <c>ApiConfig.proxyLocal</c> 正是 <c>spider.proxy(param)</c>。
    /// 宿主回 200 空体只能让探测分支走"成功"，握手数据不对，最终仍会拼出空端口地址。</para>
    /// <para>返回 <c>(status, mime, body)</c>；无法处理时返回 null。</para>
    /// </summary>
    public async Task<(int Status, string Mime, byte[]? Body)?> ProxyAsync(
        IReadOnlyDictionary<string, string> query, CancellationToken ct = default)
    {
        // 优先按 siteKey 定位（与 TVBox getCurrentProxySource 一致），否则用最近一次使用的爬虫
        SpiderHolder? holder = null;
        if (query.TryGetValue("siteKey", out var key) && !string.IsNullOrEmpty(key))
        {
            var site = Core.Models.SiteRegistry.Find(key);
            if (site is not null)
            {
                try { holder = await EnsureSpiderAsync(site, ct).ConfigureAwait(false); }
                catch { }
            }
        }
        holder ??= _lastUsed;
        if (holder is null)
        {
            Log("proxy 回调：没有可用的爬虫实例");
            return null;
        }

        return await Task.Run(() =>
        {
            lock (holder.ProxyLock)
            {
                try
                {
                    var cls = EnsureBridge();
                    var m = EnsureBridgeMethod(cls);
                    if (m is null) return ((int)500, "text/plain", (byte[]?)null);

                    var map = new HashMap();
                    foreach (var kv in query)
                        map.Put(new Java.Lang.String(kv.Key), new Java.Lang.String(kv.Value ?? ""));

                    // 响应体经临时文件回传：反射调用拿不到 Java 基本类型数组，见 SpiderProxyBridge 注释
                    var outPath = Path.Combine(Path.GetTempPath(), $"spproxy-{Guid.NewGuid():N}.bin");
                    var r = m.Invoke(null, [holder.Instance, holder.Loader!, map, new Java.Lang.String(outPath)]);
                    var head = (r as Java.Lang.String)?.ToString() ?? "";

                    if (head.StartsWith("ERR:", StringComparison.Ordinal))
                    {
                        Log($"proxy 回调失败：{head}");
                        return ((int)502, "text/plain", (byte[]?)null);
                    }

                    var parts = head.Split('|');
                    if (parts.Length != 3 || !int.TryParse(parts[0], out var status))
                    {
                        Log($"proxy 回调：返回头异常 {head}");
                        return ((int)502, "text/plain", (byte[]?)null);
                    }
                    var mime = parts[1];
                    var len = long.TryParse(parts[2], out var l) ? l : 0;

                    byte[]? body = null;
                    if (len > 0 && File.Exists(outPath)) body = File.ReadAllBytes(outPath);
                    try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }

                    Log($"proxy 回调 ok：do={(query.TryGetValue("do", out var dv) ? dv : "?")} → {status} {mime} {body?.Length ?? 0}B");
                    return (status, mime, body);
                }
                catch (System.Exception ex)
                {
                    Log($"proxy 回调异常：{ex.GetType().Name}: {ex.Message}");
                    return ((int)502, "text/plain", (byte[]?)null);
                }
            }
        }).ConfigureAwait(false);
    }

    private Java.Lang.Class? _bridgeClass;
    private Java.Lang.Reflect.Method? _bridgeMethod;

    private Java.Lang.Class EnsureBridge() =>
        _bridgeClass ??= Java.Lang.Class.ForName(
            "com.catclaw.video.SpiderProxyBridge", true,
            global::Android.App.Application.Context.ClassLoader)
        ?? throw new InvalidOperationException("SpiderProxyBridge 未找到");

    private Java.Lang.Reflect.Method? EnsureBridgeMethod(Java.Lang.Class cls) =>
        _bridgeMethod ??= Find(cls, "proxyToFile",
            Java.Lang.Class.FromType(typeof(Java.Lang.Object)),
            Java.Lang.Class.FromType(typeof(Java.Lang.ClassLoader)),
            Java.Lang.Class.FromType(typeof(HashMap)),
            Java.Lang.Class.FromType(typeof(Java.Lang.String)));


    /// <summary>反射调用并容错：方法缺失返回 null，Java 异常包装为 {"__error":...}</summary>
    private Java.Lang.Object? CallSafe(SpiderHolder h, Java.Lang.Reflect.Method? m, params Java.Lang.Object[] args)
    {
        if (m == null) return null;
        try
        {
            return (Java.Lang.Object?)m.Invoke(h.Instance, args);
        }
        catch (Java.Lang.Throwable t)
        {
            // ⚠️ 只打印 Message 会丢失根因：反射调用抛的是 InvocationTargetException，其 Message 常为空，
            // 真实异常在其 Cause 里（爬虫方法内部抛的才是有效信息）。这里展开整条 cause 链。
            var detail = Describe(t);
            Log($"{m.Name} 异常: {detail}");
            var msg = System.Text.Json.JsonSerializer.Serialize(detail);
            return new Java.Lang.String("{\"__error\":" + msg + "}");
        }
        catch
        {
            return null;
        }
    }

    private async Task<string> InvokeAsync(VodSiteInfo site, Func<SpiderHolder, Java.Lang.Object?> call,
        CancellationToken ct = default)
    {
        // ⚠ 原先这里写死 CancellationToken.None，把调用方（搜索页退出/重新搜索）的取消
        //   完全吞掉：用户离开页面后几十个站点的 JNI 调用仍在后台跑，白白扩大并发窗口。
        //   现在透传 —— 传 default 时行为与从前一致。
        // ConfigureAwait(false)：避免续体被拉回 UI 线程（爬虫 JNI 调用是重活，且要求与 UI 无关的上下文）
        var h = await EnsureSpiderAsync(site, ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            lock (h.Lock)
            {
                var result = call(h)?.ToString();
                return string.IsNullOrEmpty(result) ? "{}" : result;
            }
        }).ConfigureAwait(false);
    }
}
