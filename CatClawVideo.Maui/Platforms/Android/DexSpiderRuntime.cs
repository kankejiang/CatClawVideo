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
            Log($"jar 缓存完成 len={bytes.Length}");
        }

        var optDir = Path.Combine(_cacheDir, "opt");
        Directory.CreateDirectory(optDir);
        return new global::Dalvik.SystemInterop.DexClassLoader(
            jarPath, optDir, null, ClassLoader.SystemClassLoader);
    }

    /// <summary>找 PK（Zip 头），兼容伪装成图片的 jar</summary>
    private static int IndexOfPk(byte[] bytes)
    {
        for (int i = 0; i + 1 < bytes.Length && i < 4096; i++)
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B)
                return i;
        return 0;
    }

    private static Class? TryLoad(ClassLoader loader, string className)
    {
        string[] prefixes =
        {
            "com.github.catvod.spider.",
            "com.github.catvod.crawler.",
            "",
        };
        foreach (var p in prefixes)
        {
            try { return loader.LoadClass(p + className); }
            catch { }
        }
        return null;
    }

    private async Task<SpiderHolder> EnsureSpiderAsync(VodSiteInfo site, CancellationToken ct)
    {
        if (_spiders.TryGetValue(site.Key, out var ready) && ready.Initialized)
            return ready;

        var loader = await GetLoaderAsync(site, ct);
        var className = site.Api.StartsWith("csp_", StringComparison.OrdinalIgnoreCase)
            ? site.Api[4..]
            : site.Api;

        var cls = TryLoad(loader, className)
            ?? throw new InvalidOperationException($"jar 中找不到爬虫类: {className}");

        var holder = new SpiderHolder
        {
            Instance = (Java.Lang.Object)cls.GetConstructor().NewInstance(),
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
            holder.Init.Invoke(holder.Instance,
                global::Android.App.Application.Context, new Java.Lang.String(ext));
        }

        _spiders[site.Key] = holder;
        holder.Initialized = true;
        Log($"站点 {site.Key} spider 就绪: {cls.Name}");
        return holder;
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
            Log($"{m.Name} 异常: {t.Message}");
            var msg = System.Text.Json.JsonSerializer.Serialize(t.Message ?? "error");
            return new Java.Lang.String("{\"__error\":" + msg + "}");
        }
        catch
        {
            return null;
        }
    }

    private async Task<string> InvokeAsync(VodSiteInfo site, Func<SpiderHolder, Java.Lang.Object?> call)
    {
        var h = await EnsureSpiderAsync(site, CancellationToken.None);
        return await Task.Run(() =>
        {
            lock (h.Lock)
            {
                var result = call(h)?.ToString();
                return string.IsNullOrEmpty(result) ? "{}" : result;
            }
        });
    }
}
