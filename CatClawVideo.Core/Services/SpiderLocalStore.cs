using System.Collections.Concurrent;
using System.Text.Json;

namespace CatClawVideo.Core.Services;

/// <summary>
/// TVBox JS Spider 的持久化 KV 存储（对齐 <c>crawler.js.local</c>）。
/// <para>网盘类源把夸克/UC 等平台的登录态（token/cookie）存在这里，跨启动必须可恢复。
/// 键为 <c>{siteKey}|{rkey}|{key}</c> 三段（rkey 对齐 Java 版双参键控），JSON 文件落盘 + 内存写穿。</para>
/// </summary>
public sealed class SpiderLocalStore
{
    private readonly string _filePath;
    private readonly ConcurrentDictionary<string, string> _map;
    private readonly object _saveLock = new();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SpiderLocalStore> Shared =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 同一目录只给一个实例。JS 运行时与 <see cref="SpiderProxyServer"/> 的 <c>/cache</c> 端点都要读写
    /// 这张表，各自 new 会让两份内存表互相覆盖落盘（后写的把先写的整表冲掉）。
    /// </summary>
    public static SpiderLocalStore For(string cacheDir) =>
        Shared.GetOrAdd(cacheDir, d => new SpiderLocalStore(d));

    private SpiderLocalStore(string cacheDir)
    {
        Directory.CreateDirectory(cacheDir);
        _filePath = Path.Combine(cacheDir, "jslocal.json");
        _map = Load();
    }

    /// <summary>新建一个独立存储（一般用 <see cref="For"/>，仅当确实要另开一张表时用这个）。</summary>
    public static SpiderLocalStore Create(string cacheDir) => new(cacheDir);

    private ConcurrentDictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (dict != null)
                    return new ConcurrentDictionary<string, string>(dict);
            }
        }
        catch { }
        return new ConcurrentDictionary<string, string>();
    }

    private void Save()
    {
        lock (_saveLock)
        {
            try
            {
                File.WriteAllText(_filePath, JsonSerializer.Serialize(_map));
            }
            catch { }
        }
    }

    private static string Key(string siteKey, string rkey, string key) => $"{siteKey}|{rkey}|{key}";

    public string Get(string siteKey, string rkey, string key) =>
        _map.TryGetValue(Key(siteKey, rkey, key), out var v) ? v : "";

    public void Set(string siteKey, string rkey, string key, string value)
    {
        _map[Key(siteKey, rkey, key)] = value;
        Save();
    }

    public void Delete(string siteKey, string rkey, string key)
    {
        _map.TryRemove(Key(siteKey, rkey, key), out _);
        Save();
    }

    // ── /cache?do=get|set|del 端点（TVBox CacheRequestProcess 用 Hawk 存扁平键）──────
    // 键由调用方按 "cache_<rule>_<key>" 组好；与三段键共用一张表不会撞（三段键必含 '|'）。

    public string CacheGet(string cacheKey) => _map.TryGetValue(cacheKey, out var v) ? v : "";

    public void CacheSet(string cacheKey, string value)
    {
        _map[cacheKey] = value;
        Save();
    }

    public void CacheDelete(string cacheKey)
    {
        _map.TryRemove(cacheKey, out _);
        Save();
    }
}
