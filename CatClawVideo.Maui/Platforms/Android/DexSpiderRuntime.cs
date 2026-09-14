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
public class DexSpiderRuntime : ISpiderRuntime
{
    public string Id => "android-dex";
    public bool IsSupported => true;

    private sealed class SpiderHolder
    {
        public Java.Lang.Object Instance = null!;
        public Java.Lang.Reflect.Method? Init;
        public Java.Lang.Reflect.Method? Home;
        public Java.Lang.Reflect.Method? Category;
        public Java.Lang.Reflect.Method? Detail;
        public Java.Lang.Reflect.Method? Search2;
        public Java.Lang.Reflect.Method? Search3;
        public Java.Lang.Reflect.Method? Player;
        public readonly object Lock = new();
        public bool Initialized;
    }

    private readonly ConcurrentDictionary<string, SpiderHolder> _spiders = new();
    private readonly HttpClient _http = new();
    private readonly string _cacheDir;
    private readonly Action<string>? _log;

    public DexSpiderRuntime(string cacheDir, Action<string>? log = null)
    {
        _cacheDir = cacheDir;
        _log = log;
        Directory.CreateDirectory(cacheDir);
    }

    private void Log(string m) => _log?.Invoke("[dex] " + m);

    // ═══════════ ISpiderRuntime ═══════════

    public Task<string> HomeContentAsync(VodSiteInfo site, CancellationToken ct = default) =>
        InvokeAsync(site, h => CallSafe(h, h.Home, new Java.Lang.Boolean(true)));

    public Task<string> CategoryContentAsync(VodSiteInfo site, string tid, string pg, CancellationToken ct = default) =>
        InvokeAsync(site, h => CallSafe(h, h.Category,
            new Java.Lang.String(tid), new Java.Lang.String(pg),
            new Java.Lang.Boolean(false), new HashMap()));

    public Task<string> DetailContentAsync(VodSiteInfo site, string id, CancellationToken ct = default)
    {
        var list = new ArrayList();
        list.Add(new Java.Lang.String(id));
        return InvokeAsync(site, h => CallSafe(h, h.Detail, list));
    }

    public Task<string> SearchContentAsync(VodSiteInfo site, string keyword, string pg, CancellationToken ct = default) =>
        InvokeAsync(site, h => h.Search3 != null
            ? CallSafe(h, h.Search3, new Java.Lang.String(keyword), new Java.Lang.Boolean(false), new Java.Lang.Integer(pg))
            : CallSafe(h, h.Search2, new Java.Lang.String(keyword), new Java.Lang.Boolean(false)));

    public Task<string> PlayerContentAsync(VodSiteInfo site, string flag, string id, CancellationToken ct = default)
    {
        var flags = new ArrayList();
        return InvokeAsync(site, h => CallSafe(h, h.Player,
            new Java.Lang.String(flag), new Java.Lang.String(id), flags));
    }

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

        if (!File.Exists(jarPath))
        {
            Log($"下载 spider jar: {jarUrl[..System.Math.Min(80, jarUrl.Length)]}");
            using var resp = await _http.GetAsync(jarUrl, ct);
            resp.EnsureSuccessStatusCode();
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);

            // 伪装 jpg 头（FF D8）时剥掉前导字节定位 PK
            var pk = IndexOfPk(bytes);
            if (pk > 0) bytes = bytes[pk..];

            var expect = JarMd5(site);
            if (!string.IsNullOrEmpty(expect))
            {
                var actual = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
                if (!actual.Equals(expect, StringComparison.OrdinalIgnoreCase))
                    Log($"jar md5 不匹配（期望 {expect} 实际 {actual}），继续尝试加载");
            }

            await File.WriteAllBytesAsync(jarPath, bytes, ct);
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

            // 保护壳 jar 引导（对照 jun 分支 ProtectedInitJar，纯反射绑定）：
            // Guard 系 jar 的 Init 单例需要外部注入 Context 与 DexClassLoader 才能工作。
            // 必须在实例化爬虫类之前完成：Guard 壳类的构造函数会通过 Init.getSpider() 取真实实现。
            BindProtectedJar(loader);

            // ⚠️ 实例化与 init() 是 Guard 壳最容易失败的两步（真实实现由 native 解密后反射调用），
            // 失败时抛的是 InvocationTargetException，其 Message 是 .NET 兜底的英文文案、毫无信息量，
            // 真实异常在 Cause 链里。必须显式展开，否则调用方只能看到
            // 「Exception of type 'Java.Lang.Reflect.InvocationTargetException' was thrown.」
            Java.Lang.Object instance;
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
                Player = Find(cls, "playerContent",
                    Java.Lang.Class.FromType(typeof(Java.Lang.String)),
                    Java.Lang.Class.FromType(typeof(Java.Lang.String)),
                    Java.Lang.Class.FromType(typeof(Java.Util.IList))),
            };

            if (holder.Init != null)
            {
                var ext = site.Ext ?? "";
                Log($"init {className} ext={ext[..System.Math.Min(60, ext.Length)]}");
                try
                {
                    holder.Init.Invoke(holder.Instance,
                        global::Android.App.Application.Context, new Java.Lang.String(ext));
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
            Log($"站点 {site.Key} spider 就绪: {cls.Name}");
            return holder;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 保护壳 jar 引导（移植自 jun 分支 ProtectedInitJar，纯 Java 反射，无 native 依赖）：
    /// ① Init.get() 取单例 → 绑定 Context 字段（"c" 优先，退化任意实例 Context 字段）
    /// ② jar 内 DexNative.getLoader(context) 取 DexClassLoader → 绑入 Init 的实例字段
    /// ③ best-effort 调 replaceCloudDiskNames / startGoProxy
    /// 普通 jar 没有这些字段/类时全部静默跳过，无副作用。
    /// </summary>
    private void BindProtectedJar(ClassLoader loader)
    {
        try
        {
            var initCls = TryLoad(loader, "Init");
            if (initCls == null) return;

            Java.Lang.Object? init = null;
            try
            {
                var get = initCls.GetMethod("get");
                init = (Java.Lang.Object?)get.Invoke(null);
            }
            catch { }

            var appCtx = global::Android.App.Application.Context;

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
                    f.Set(init, appCtx);
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
                                Java.Lang.Reflect.AccessibleObject.SetAccessible(new Java.Lang.Reflect.AccessibleObject[] { f }, true);
                                f.Set(init, appCtx);
                                bound = true;
                            }
                            catch { }
                        }
                    }
                }
            }

            // ② bindDexLoader：DexNative.getLoader(context) → DexClassLoader，绑入 Init 的实例字段。
            // 同样按类型可赋值性匹配并向上遍历继承链（对照 jun ProtectedInitJar.bindDexLoader）。
            try
            {
                var nativeCls = TryLoad(loader, "DexNative");
                var getLoader = nativeCls?.GetMethod("getLoader",
                    Java.Lang.Class.FromType(typeof(Java.Lang.Object)));
                var cl = getLoader?.Invoke(null, appCtx);
                if (cl is global::Dalvik.SystemInterop.DexClassLoader dexCl && init != null)
                {
                    var dexLoaderType = Java.Lang.Class.FromType(typeof(global::Dalvik.SystemInterop.DexClassLoader));
                    var loaderBound = false;
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
                }
            }
            catch { }

            // ③ 可选引导
            try { initCls.GetMethod("replaceCloudDiskNames").Invoke(null); } catch { }
            try
            {
                initCls.GetMethod("startGoProxy", Java.Lang.Class.FromType(typeof(global::Android.Content.Context)))
                    .Invoke(null, appCtx);
            }
            catch { }

            Log("保护壳引导完成（Init/Context/DexLoader 绑定）");
        }
        catch (System.Exception ex)
        {
            Log($"保护壳引导跳过: {ex.Message}");
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

    private async Task<string> InvokeAsync(VodSiteInfo site, Func<SpiderHolder, Java.Lang.Object?> call)
    {
        // ConfigureAwait(false)：避免续体被拉回 UI 线程（爬虫 JNI 调用是重活，且要求与 UI 无关的上下文）
        var h = await EnsureSpiderAsync(site, CancellationToken.None).ConfigureAwait(false);
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
