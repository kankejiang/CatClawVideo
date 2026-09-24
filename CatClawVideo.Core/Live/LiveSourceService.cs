using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CatClawVideo.Core.Live;

/// <summary>
/// 直播源加载与偏好服务（TVBox <c>ApiConfig.loadLiveConfig / parseLiveConfigContent / loadLiveApi</c> 的 C# 移植）。
///
/// <para>直播源地址支持三种形态：</para>
/// <list type="number">
/// <item><b>直链文本</b>：http(s) 地址 / 本地文件路径，内容为 TXT / M3U / JSON 数组（按 TxtSubscribe 三态解析）；</item>
/// <item><b>订阅 JSON</b>：内容含 <c>lives</c> 数组 → 多源列表（对应 TVBox 的"多源切换"），取选中条目再拉取其 url；</item>
/// <item><b>内联内容</b>：直接把 TXT/M3U 文本粘进输入框（本地调试用）。</item>
/// </list>
/// 拉取内容带磁盘缓存（按源地址 MD5），网络失败自动回退缓存。
/// </summary>
public class LiveSourceService
{
    private static readonly HttpClient Http = CreateClient();

    private static string SettingsPath => AppPaths.Sub("live", "settings.json");

    /// <summary>持久化偏好（页面直接改字段后调用 <see cref="Save"/>）。</summary>
    public LiveSourcePrefs Prefs { get; private set; }

    /// <summary>最近一次成功加载的结果（频道分组 / 多源列表 / EPG 地址）。</summary>
    public LiveLoadResult? Current { get; private set; }

    /// <summary>当前源的展示名（多源时为条目 Name，否则取地址）。</summary>
    public string CurrentTag { get; private set; } = "";

    private readonly Dictionary<string, EpgCacheSlot> _noop = new(); // 占位，避免误用

    private sealed class EpgCacheSlot { }

    public LiveSourceService()
    {
        Prefs = LoadPrefs();
    }

    public bool HasSource => !string.IsNullOrWhiteSpace(Prefs.ApiUrl);

    // ═══════════════════ 加载 ═══════════════════

    /// <summary>
    /// 加载直播源（入口）。
    /// </summary>
    /// <param name="apiOverride">临时覆盖源地址（为空用 <see cref="LiveSourcePrefs.ApiUrl"/>）</param>
    /// <param name="livesIndex">多源下标（-1 = 用记忆值）</param>
    public async Task<LiveLoadResult> LoadAsync(string? apiOverride = null, int livesIndex = -1, CancellationToken ct = default)
    {
        var api = (apiOverride ?? Prefs.ApiUrl).Trim();
        if (api.Length == 0)
            return new LiveLoadResult { Error = "未配置直播源，请先在「直播源」页填入地址" };

        string content;
        var fromCache = false;

        try
        {
            if (File.Exists(api))
            {
                content = await File.ReadAllTextAsync(api, ct);
            }
            else if (IsInlineContent(api))
            {
                content = api;
            }
            else if (api.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    content = await FetchAsync(api, Prefs.Ua, ct);
                    WriteCache(api, content);
                }
                catch when (!ct.IsCancellationRequested)
                {
                    var cached = ReadCache(api);
                    if (cached == null) throw;
                    content = cached;
                    fromCache = true;
                }
            }
            else
            {
                return new LiveLoadResult { Error = "直播源地址无效：支持 http(s) 地址 / 本地文件路径 / 直接粘贴内容" };
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return new LiveLoadResult { Error = "直播源拉取失败：" + ex.Message };
        }

        content = content.TrimStart('\uFEFF');
        var result = await ParseContentAsync(content, api, livesIndex, ct);
        if (result.Ok || result.Groups.Count > 0)
        {
            Current = result;
            CurrentTag = result.Lives.Count > 0 && result.LivesIndex < result.Lives.Count
                ? result.Lives[result.LivesIndex].Name
                : api;
            if (fromCache) CurrentTag += "（缓存）";
        }
        return result;
    }

    /// <summary>解析已在手的内容（供页面/文件导入复用）。</summary>
    public async Task<LiveLoadResult> ParseContentAsync(string content, string sourceTag, int livesIndex, CancellationToken ct = default)
    {
        // 1) 形如 { ... }：可能是含 lives 的订阅 JSON
        var trimmed = content.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                if (JsonNode.Parse(content) is JsonObject root && root["lives"] is JsonArray livesArr)
                {
                    var lives = ParseLives(livesArr);
                    if (lives.Count == 0)
                        return new LiveLoadResult { Error = "配置中的 lives 数组为空" };

                    var idx = livesIndex >= 0 ? livesIndex : GetSavedLivesIndex(sourceTag);
                    if (idx < 0 || idx >= lives.Count) idx = 0;
                    var entry = lives[idx];

                    if (entry.IsSpider)
                        return new LiveLoadResult
                        {
                            Lives = lives,
                            LivesIndex = idx,
                            Error = $"「{entry.Name}」是 spider 直播源（{entry.Api}），当前版本暂未接入；请改选文本源",
                        };

                    var entryUrl = entry.Url.Length > 0 ? entry.Url : entry.Api;
                    if (entryUrl.Length == 0)
                        return new LiveLoadResult { Lives = lives, LivesIndex = idx, Error = $"「{entry.Name}」未提供 url" };

                    string inner;
                    try
                    {
                        inner = File.Exists(entryUrl)
                            ? await File.ReadAllTextAsync(entryUrl, ct)
                            : await FetchAsync(entryUrl, entry.Ua.Length > 0 ? entry.Ua : Prefs.Ua, ct);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        return new LiveLoadResult { Lives = lives, LivesIndex = idx, Error = $"「{entry.Name}」拉取失败：{ex.Message}" };
                    }

                    inner = inner.TrimStart('\uFEFF');
                    var groups = LiveParser.BuildGroups(LiveParser.ParseToNormalizedArray(inner));
                    var epg = entry.Epg.Length > 0 ? entry.Epg : LiveParser.ExtractLiveTextEpg(inner);
                    var webHeaders = entry.Header.Count > 0 ? entry.Header : null;
                    if (webHeaders != null) Prefs.WebHeaders = webHeaders;
                    SetSavedLivesIndex(sourceTag, idx);
                    return new LiveLoadResult
                    {
                        Groups = groups,
                        Lives = lives,
                        LivesIndex = idx,
                        EpgUrl = epg,
                        Ua = entry.Ua,
                        Error = groups.Count == 0 ? "该源未解析出任何频道" : "",
                    };
                }
            }
            catch
            {
                // 不是合法 JSON → 交给文本解析
            }
        }

        // 2) 文本 / M3U / JSON 数组直连
        var directGroups = LiveParser.BuildGroups(LiveParser.ParseToNormalizedArray(content));
        return new LiveLoadResult
        {
            Groups = directGroups,
            EpgUrl = LiveParser.ExtractLiveTextEpg(content),
            Error = directGroups.Count == 0 ? "未解析出任何频道（检查源内容格式）" : "",
        };
    }

    private static List<LiveLivesEntry> ParseLives(JsonArray livesArr)
    {
        var list = new List<LiveLivesEntry>();
        foreach (var node in livesArr)
        {
            if (node is not JsonObject obj) continue;
            var api = Str(obj["api"]).Trim();
            var url = Str(obj["url"]).Trim();
            var typeText = Str(obj["type"]).Trim();
            var type = 0;
            if (!int.TryParse(typeText, out type)) type = 0;
            if (typeTableMissing(typeText) && (api.Contains(".py") || api.Contains(".js"))) type = 3;

            var entry = new LiveLivesEntry
            {
                Name = Str(obj["name"]).Trim(),
                Type = type,
                Api = api,
                Url = url,
                Jar = Str(obj["jar"]).Trim(),
                Epg = Str(obj["epg"]).Trim(),
                Ua = Str(obj["ua"]).Trim(),
            };
            if (entry.Name.Length == 0) entry.Name = "源" + (list.Count + 1);
            if (obj["timeout"] is JsonNode timeoutNode && int.TryParse(timeoutNode.ToString(), out var t) && t >= 5 && t <= 30)
                entry.TimeoutSeconds = t;
            if (obj["header"] is JsonObject headerObj)
            {
                foreach (var kv in headerObj) entry.Header[kv.Key] = Str(kv.Value);
            }
            list.Add(entry);
        }
        return list;

        static bool typeTableMissing(string s) => s.Length == 0 || s == "0";
    }

    // ═══════════════════ 历史 / 偏好 ═══════════════════

    /// <summary>写入历史（新的在前，最多 30 条；内联内容不进历史）。</summary>
    public void AddHistory(string api)
    {
        if (string.IsNullOrWhiteSpace(api) || IsInlineContent(api)) return;
        Prefs.History.Remove(api);
        Prefs.History.Insert(0, api);
        if (Prefs.History.Count > 30) Prefs.History.RemoveRange(30, Prefs.History.Count - 30);
    }

    /// <summary>设置当前源（写偏好并记历史；加载成功后由页面调用）。</summary>
    public void SetCurrentApi(string api)
    {
        Prefs.ApiUrl = api;
        AddHistory(api);
        Save();
    }

    private int GetSavedLivesIndex(string api) =>
        Prefs.LivesIndexByApi.TryGetValue(api, out var idx) ? idx : 0;

    private void SetSavedLivesIndex(string api, int idx)
    {
        Prefs.LivesIndexByApi[api] = idx;
        Save();
    }

    // ═══════════════════ 持久化 ═══════════════════

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Prefs, PrefsJsonOptions), Encoding.UTF8);
        }
        catch
        {
            // 偏好写失败不致命
        }
    }

    private static LiveSourcePrefs LoadPrefs()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath, Encoding.UTF8);
                return JsonSerializer.Deserialize<LiveSourcePrefs>(json, PrefsJsonOptions) ?? new LiveSourcePrefs();
            }
        }
        catch
        {
        }
        return new LiveSourcePrefs();
    }

    private static readonly JsonSerializerOptions PrefsJsonOptions = new()
    {
        WriteIndented = true,
    };

    // ═══════════════════ 工具 ═══════════════════

    /// <summary>内联内容判定：多行 / 三态特征（避免与地址混淆）。</summary>
    public static bool IsInlineContent(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        if (s.Contains('\n') || s.Contains('\r')) return true;
        var t = s.TrimStart();
        return t.StartsWith("#EXTM3U", StringComparison.Ordinal)
               || t.StartsWith('[')
               || (t.StartsWith('{') && !t.StartsWith("http", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string> FetchAsync(string url, string? ua, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(ua)) req.Headers.TryAddWithoutValidation("User-Agent", ua);
        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private static string CachePath(string api) => AppPaths.Sub("live", "cache-" + Md5(api) + ".txt");

    private static void WriteCache(string api, string content)
    {
        try
        {
            var path = CachePath(api);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static string? ReadCache(string api)
    {
        try
        {
            var path = CachePath(api);
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Md5(string text)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(bytes);
    }

    private static string Str(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v => v.TryGetValue<string>(out var s) ? s : v.ToString(),
        _ => node.ToJsonString(),
    };

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
    }
}
