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

    public SpiderLocalStore(string cacheDir)
    {
        Directory.CreateDirectory(cacheDir);
        _filePath = Path.Combine(cacheDir, "jslocal.json");
        _map = Load();
    }

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
}
