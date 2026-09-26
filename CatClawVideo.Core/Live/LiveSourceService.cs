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

    /// <summary>直播设置文件路径（配置包按原样带走这个文件）。</summary>
    public static string SettingsFilePath => SettingsPath;

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

    // ═══════════════════ 订阅自动导入 ═══════════════════

    /// <summary>点播订阅自带 lives 时的明文配置落盘位置（由 TvBoxSubscriptionManager 写入）</summary>
    public static string CapturePath => AppPaths.Sub("live", "subscription-capture.json");

    /// <summary>是否存在可自动导入的订阅直播配置</summary>
    public static bool HasCapturedSubscription
    {
        get
        {
            try
            {
                var ok = File.Exists(CapturePath);
                if (!ok) CatClawVideo.Core.Providers.CatClawLog.Write($"[直播] 订阅 capture 不存在: {CapturePath}");
                return ok;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// 订阅解析侧调用：把（解密后的）明文 TVBox 配置落盘，供 <see cref="LoadAsync"/> 在
    /// 用户未手动配置直播源时自动采用。失败静默，不影响点播订阅本身。
    /// </summary>
    public static void CaptureSubscriptionConfig(string jsonText)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CapturePath)!);
            File.WriteAllText(CapturePath, jsonText, Encoding.UTF8);
            CatClawVideo.Core.Providers.CatClawLog.Write($"[直播] 已捕获订阅 lives → {CapturePath}（{jsonText.Length} 字符）");
        }
        catch (Exception ex)
        {
            // 原来这里无声吞掉：结果订阅里带着 lives、直播页却一直要求「配置直播源」，查不到是谁拒的
            CatClawVideo.Core.Providers.CatClawLog.Write($"[直播] 捕获订阅 lives 失败: {ex.Message}");
        }
    }

    // ═══════════════════ 加载 ═══════════════════

    /// <summary>
    /// 加载直播源（入口）。
    /// </summary>
    /// <param name="apiOverride">临时覆盖源地址（为空用 <see cref="LiveSourcePrefs.ApiUrl"/>）</param>
    /// <param name="livesIndex">多源下标（-1 = 用记忆值）</param>
    public async Task<LiveLoadResult> LoadAsync(string? apiOverride = null, int livesIndex = -1, CancellationToken ct = default)
    {
        // clan:// / file:// 形态先换成可播地址（本机那支锁在数据目录内，远端那支只是改写成 http://host/file/…）
        var raw = (apiOverride ?? Prefs.ApiUrl).Trim();
        var api = Services.ClanScheme.Resolve(raw);
        // 曾经自动导入过、但那个内部 capture 文件已经不在了（重装 / 清缓存 / 订阅换地址后重建目录）：
        // 不能拿它当「用户配置的地址」去报「地址无效」，否则订阅里明明带 lives 却再也进不去。
        var staleCapture = api.Length > 0 && raw == CapturePath && !File.Exists(api);
        if (api.Length == 0 || staleCapture)
        {
            // 订阅自动导入（2026-09-25 用户反馈「饭太硬点播源里有直播源，直播页却还要配置」）：
            // 点播订阅解析出 lives 时订阅管理器已把明文配置落盘 → 用户没手动配过直播源就直接采用，
            // 首次进直播免配置。文件随订阅刷新而更新，不进历史（内部路径）。
            if (HasCapturedSubscription)
            {
                // ⚠ 只在这次加载里用，绝不写回 Prefs.ApiUrl：capture 路径是内部实现细节，
                // 持久化它等于把「每次重估的回退」变成「用户的手动配置」，文件一丢就死在「地址无效」上。
                api = CapturePath;
            }
            else
                return new LiveLoadResult { Error = "未配置直播源，请先在「直播源」页填入地址" };
        }

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

        // 订阅自带多路 lives 时，TVBox 的行为是「可切源」而不是「第 0 条挂了就整页作废」。
        // 真机实测（2026-09-26）：某订阅 8 条 lives 里第 0 条 NAS 文件已删（404）、第 1 条反代 403，
        // 剩 6 条都是 200 —— 只取第 0 条会让用户以为「订阅里没有直播源」，反而被要求手填地址。
        // 只在用户没有显式指定下标时自动回退，避免覆盖他手动选定的源。
        if (!result.Ok && result.Groups.Count == 0 && result.Lives.Count > 1 && livesIndex < 0)
        {
            var firstError = result.Error;
            for (int alt = 0; alt < result.Lives.Count; alt++)
            {
                if (alt == result.LivesIndex) continue;
                var altResult = await ParseContentAsync(content, api, alt, ct);
                if (!altResult.Ok && altResult.Groups.Count == 0) continue;
                CatClawVideo.Core.Providers.CatClawLog.Write(
                    $"[直播] 第 {result.LivesIndex + 1} 路不可用（{firstError}），已回退到第 {alt + 1} 路「{result.Lives[alt].Name}」");
                result = altResult;
                break;
            }
        }

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
                if (ParseJsonTolerant(content) is { } root && root["lives"] is JsonArray livesArr)
                {
                    var lives = ParseLives(livesArr);
                    if (lives.Count == 0)
                        return new LiveLoadResult { Error = "配置中的 lives 数组为空" };

                    var idx = livesIndex >= 0 ? livesIndex : GetSavedLivesIndex(sourceTag);
                    if (idx < 0 || idx >= lives.Count) idx = 0;
                    var entry = lives[idx];

                    if (entry.IsSpider)
                    {
                        // spider 型直播源（对位 TVBox LivePlayActivity:2855-2905）：
                        // 交给 liveContent 的是**真实地址**（url 优先、回退 api），不是
                        // proxy?do=live&type=txt&ext=<b64> 那层包装 —— TVBox 在调用前已把 ext 解回。
                        var fetch = LiveSpiderBridge.Fetcher;
                        if (fetch is null)
                            return new LiveLoadResult
                            {
                                Lives = lives, LivesIndex = idx,
                                Error = $"「{entry.Name}」是 spider 直播源（{entry.Api}），当前平台没有可用的爬虫运行时；请改选文本源",
                            };

                        var seconds = Math.Clamp(entry.TimeoutSeconds > 0 ? entry.TimeoutSeconds : Prefs.TimeoutSeconds, 5, 30);
                        string? txt;
                        try
                        {
                            using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            to.CancelAfter(TimeSpan.FromSeconds(seconds));
                            txt = await fetch(entry, to.Token);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            return new LiveLoadResult { Lives = lives, LivesIndex = idx, Error = $"「{entry.Name}」拉取超时（{seconds}s）" };
                        }
                        catch (Exception ex)
                        {
                            return new LiveLoadResult { Lives = lives, LivesIndex = idx, Error = $"「{entry.Name}」spider 直播源失败：{ex.Message}" };
                        }
                        if (string.IsNullOrWhiteSpace(txt))
                            return new LiveLoadResult
                            {
                                Lives = lives, LivesIndex = idx,
                                Error = txt is null ? $"「{entry.Name}」的爬虫类型不支持 liveContent" : $"「{entry.Name}」未返回频道列表",
                            };

                        txt = txt.TrimStart('﻿');
                        var spiderGroups = LiveParser.BuildGroups(LiveParser.ParseToNormalizedArray(txt));
                        if (entry.Header.Count > 0) Prefs.WebHeaders = entry.Header;
                        SetSavedLivesIndex(sourceTag, idx);
                        return new LiveLoadResult
                        {
                            Groups = spiderGroups,
                            Lives = lives,
                            LivesIndex = idx,
                            EpgUrl = entry.Epg.Length > 0 ? entry.Epg : LiveParser.ExtractLiveTextEpg(txt),
                            Ua = entry.Ua,
                            Error = spiderGroups.Count == 0 ? "该 spider 未解析出任何频道" : "",
                        };
                    }

                    var entryUrl = Services.ClanScheme.Resolve(entry.Url.Length > 0 ? entry.Url : entry.Api);
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

    /// <summary>
    /// 宽容 JSON 解析：饭太硬等明文配置常带整行 <c>//</c> 注释（官方解密通道的返回也带注释头），
    /// System.Text.Json 不容忍 → 先原样解析，失败再剥掉「行首 //」的整行注释重试一次。
    /// </summary>
    private static JsonObject? ParseJsonTolerant(string content)
    {
        try
        {
            return JsonNode.Parse(content) as JsonObject;
        }
        catch
        {
            try
            {
                var cleaned = string.Join('\n', content.Split('\n')
                    .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
                return cleaned != content ? JsonNode.Parse(cleaned) as JsonObject : null;
            }
            catch
            {
                return null;
            }
        }
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
            // 订阅级 catchup（只对 JsonObject 形态生效，与 TVBox initLiveObj 一致）
            if (obj["catchup"] is JsonObject catchupObj)
            {
                entry.Catchup = new LiveCatchup.Config
                {
                    Type = Str(catchupObj["type"]).Trim(),
                    Source = Str(catchupObj["source"]).Trim(),
                    Regex = Str(catchupObj["regex"]).Trim(),
                    Replace = Str(catchupObj["replace"]).Trim(),
                };
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
        // 直播源列表与 EPG 一样是最容易被污染名单照顾的一跳 → 走 DoH（关闭时行为不变）
        return Services.Doh.NewClient(25);
    }
}
