using System.Text.Json;
using System.Text.Json.Nodes;
using CatClawVideo.Core.Models;

namespace CatClawVideo.Core.Providers;

/// <summary>
/// spider 站点凭据存储（JSON 文件，按 server authority 索引）：
/// {AppData}\CatClawVideo\spider-creds.json → {"host:port":{"username":"..","password":".."}}
/// JavaSpiderRuntime 加载站点时读取此文件自动登录注入 token。
/// </summary>
public static class SpiderCredentials
{
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CatClawVideo", "spider-creds.json");

    /// <summary>读取全部凭据（authority → 用户名/密码）；文件缺失或损坏返回空表</summary>
    public static Dictionary<string, (string User, string Pass)> Load()
    {
        var dict = new Dictionary<string, (string, string)>();
        try
        {
            if (!File.Exists(FilePath)) return new();
            var root = JsonNode.Parse(File.ReadAllText(FilePath))!.AsObject();
            foreach (var kv in root)
            {
                var u = kv.Value?["username"]?.GetValue<string>();
                var p = kv.Value?["password"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(u) && !string.IsNullOrEmpty(p))
                    dict[kv.Key] = (u, p);
            }
        }
        catch { }
        return dict;
    }

    /// <summary>按 server 地址取凭据（authority 匹配），无则返回 null</summary>
    public static (string User, string Pass)? Get(string server)
    {
        var host = AuthorityOf(server);
        if (host == null) return null;
        return Load().TryGetValue(host, out var c) ? c : null;
    }

    /// <summary>保存/更新一个 server 的凭据（合并写回文件）</summary>
    public static void Set(string server, string user, string pass)
    {
        var host = AuthorityOf(server) ?? throw new ArgumentException($"无法解析 server 地址: {server}");
        var root = new JsonObject();
        try
        {
            if (File.Exists(FilePath))
                root = JsonNode.Parse(File.ReadAllText(FilePath))!.AsObject();
        }
        catch { root = new JsonObject(); }

        root[host] = new JsonObject { ["username"] = user, ["password"] = pass };

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// 扫描站点列表，返回本地缺失凭据的 server（authority 去重），
    /// 供「添加订阅后弹窗录入账号密码」使用。
    /// </summary>
    public static List<string> MissingServers(IEnumerable<VodSiteInfo> sites)
    {
        var saved = Load();
        var missing = new List<string>();
        foreach (var site in sites.Where(s => s.NeedsCredentials))
        {
            foreach (var server in site.CredentialServers)
            {
                var host = AuthorityOf(server);
                if (host == null || saved.ContainsKey(host) || missing.Any(m => AuthorityOf(m) == host)) continue;
                missing.Add(server);
            }
        }
        return missing;
    }

    private static string? AuthorityOf(string server)
    {
        try { return new Uri(server).Authority; }
        catch { return null; }
    }
}
