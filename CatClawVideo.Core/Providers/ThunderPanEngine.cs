using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CatClawVideo.Core.Interfaces;

namespace CatClawVideo.Core.Providers;

/// <summary>
/// PC 原生「迅雷磁力播放」：走**迅雷网盘官方 HTTP API**（云添加 + 云播）。
///
/// <para><b>为什么走这条而不是迅雷下载 SDK</b>：那套安卓 SDK 的引导域名
/// <c>speedup-xlmc.xunlei.com</c> 被迅雷在**权威 DNS** 上沉成了 <c>127.0.0.2</c>
/// （阿里/腾讯 DoH 双源验证），真实接口地址只在运行时的配置 JSON 里，
/// 二进制内既无 IP 也无域名（全量扫 12502 个函数解出的 15 个混淆常量里没有 host）
/// ⟹ 「PC 直接跑那个 SDK」被迅雷自己堵死。而**网盘 API 是官方、PC 可达的**
/// （<c>api-pan.xunlei.com</c> / <c>xluser-ssl.xunlei.com</c>）。</para>
///
/// <para><b>播放不经过任何中转</b>：磁力交给迅雷服务器下载（就是迅雷自己的 P2SP），
/// 完成后取 <c>medias[].link.url</c> 直链，PC 直接拉流播放 —— 无手机、无本机 P2P。</para>
///
/// <para>接口契约取自 AList 的 thunder 驱动（AGPL-3.0）实测可用的调用序列。</para>
/// </summary>
public sealed class ThunderPanEngine : IPreferredMagnetEngine
{
    // ═══════════ 客户端常量（AList thunder 驱动的实测值）═══════════

    private const string ApiUrl = "https://api-pan.xunlei.com/drive/v1";
    private const string FileApi = ApiUrl + "/files";
    private const string TaskApi = ApiUrl + "/tasks";
    private const string XlUserBase = "https://xluser-ssl.xunlei.com";
    private const string XlUserApi = XlUserBase + "/v1";

    private const string ClientId = "Xp6vsxz_7IYVw2BB";
    private const string ClientSecret = "Xp6vsy4tN9toTVdMSpomVdXpRmES";
    private const string ClientVersion = "8.31.0.9726";
    private const string PackageName = "com.xunlei.downloadprovider";
    private const string AppId = "40";
    private const string AppKey = "34a062aaa22f906fca4fefe9fb3a3021";
    private const string SignProvider = "access_end_point_token";
    private const string RedirectUri = "xlaccsdk01://xunlei.com/callback?state=harbor";

    private const string UserAgent =
        "ANDROID-com.xunlei.downloadprovider/8.31.0.9726 netWorkType/5G appid/40 "
        + "deviceName/Xiaomi_M2004j7ac deviceModel/M2004J7AC OSVersion/12 protocolVersion/301 "
        + "platformVersion/10 sdkVersion/512000 Oauth2Client/0.9 (Linux 4_14_186-perf-gddfs8vbb238b) (JAVA 0)";

    private const string DownloadUserAgent =
        "Dalvik/2.1.0 (Linux; U; Android 12; M2004J7AC Build/SP1A.210812.016)";

    /// <summary>captcha_sign 的迭代算法串（顺序敏感）</summary>
    private static readonly string[] Algorithms =
    [
        "9uJNVj/wLmdwKrJaVj/omlQ",
        "Oz64Lp0GigmChHMf/6TNfxx7O9PyopcczMsnf",
        "Eb+L7Ce+Ej48u",
        "jKY0",
        "ASr0zCl6v8W4aidjPK5KHd1Lq3t+vBFf41dqv5+fnOd",
        "wQlozdg6r1qxh0eRmt3QgNXOvSZO6q/GXK",
        "gmirk+ciAvIgA/cxUUCema47jr/YToixTT+Q6O",
        "5IiCoM9B1/788ntB",
        "P07JH0h6qoM6TSUAK2aL9T5s2QBVeY9JWvalf",
        "+oK0AN",
    ];

    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".ts", ".m2ts", ".wmv", ".flv", ".mov",
        ".rmvb", ".rm", ".webm", ".m4v", ".mpg", ".mpeg", ".3gp", ".vob", ".iso",
    };

    // ═══════════ 状态 ═══════════

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private bool _attempted;
    private bool _ready;
    private string? _accessToken;
    private string? _refreshToken;
    private string? _creditKey;
    private string? _captchaToken;
    private string? _deviceId;

    public string Name => "迅雷(网盘云播)";

    public bool IsReady => _ready;

    /// <summary>当前是否需要人工登录（供 UI 提示）</summary>
    public string? LoginHint { get; private set; }

    // ═══════════ IPreferredMagnetEngine ═══════════

    public async Task<bool> EnsureReadyAsync()
    {
        if (_ready) return true;
        if (_attempted) return false;
        _attempted = true;

        LoadState();
        if (string.IsNullOrEmpty(_deviceId)) { _deviceId = NewDeviceId(); SaveState(); }

        // 有 refresh_token 就静默续期；没有则尝试账号密码登录
        if (!string.IsNullOrEmpty(_refreshToken))
        {
            if (await RefreshAsync().ConfigureAwait(false)) return Ready();
        }
        var st = LoadCreds();
        if (!string.IsNullOrEmpty(st.Account) && !string.IsNullOrEmpty(st.Password))
        {
            if (await LoginAsync(st.Account!, st.Password!).ConfigureAwait(false)) return Ready();
        }

        LoginHint ??= "迅雷网盘未登录：请在设置里填迅雷账号密码（首次可能需要在浏览器完成一次验证）";
        CatClawLog.Write($"[迅雷网盘] {LoginHint}");
        return false;
    }

    private bool Ready()
    {
        _ready = true;
        LoginHint = null;
        CatClawLog.Write("[迅雷网盘] ✅ 已就绪（磁力将交给迅雷服务器下载，PC 直连播放）");
        return true;
    }

    /// <summary>
    /// 解析磁力 → 文件列表。实现方式：**把磁力云添加到迅雷网盘**，等服务器下完，
    /// 再列出产物（单个文件，或种子对应的文件夹里的视频）。
    /// </summary>
    public async Task<List<MagnetFile>?> ListFilesAsync(string magnet, string? preferName = null, CancellationToken ct = default)
    {
        if (!_ready && !await EnsureReadyAsync().ConfigureAwait(false)) return null;
        var files = await ResolveFilesAsync(magnet, ct).ConfigureAwait(false);
        if (files is null || files.Count == 0) return null;

        var list = new List<MagnetFile>();
        for (int i = 0; i < files.Count; i++)
            list.Add(new MagnetFile(i, ParseSize(files[i].Size), files[i].Name));
        CatClawLog.Write($"[迅雷网盘] 文件列表 {list.Count} 项");
        return list;
    }

    /// <summary>让迅雷服务器下载该磁力并返回**可直接播放的直链**（PC 直连迅雷 CDN）。</summary>
    public async Task<MagnetPlayback?> TryOpenAsync(string magnet, string? preferName = null, CancellationToken ct = default)
    {
        if (!_ready && !await EnsureReadyAsync().ConfigureAwait(false)) return null;

        var files = await ResolveFilesAsync(magnet, ct).ConfigureAwait(false);
        if (files is null || files.Count == 0) return null;

        // 视频优先；preferName 命中优先，其次按体积
        var videos = files.Where(f => VideoExt.Contains(Path.GetExtension(f.Name))).ToList();
        var pool = videos.Count > 0 ? videos : files;
        var want = Path.GetFileNameWithoutExtension(preferName ?? "");
        var picked = pool
            .OrderByDescending(f => want.Length > 0
                && f.Name.Contains(want, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenByDescending(f => ParseSize(f.Size))
            .First();

        var url = await PlayUrlOfAsync(picked.Id, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(url)) return null;

        CatClawLog.Write($"[迅雷网盘] 命中：{picked.Name}（{ParseSize(picked.Size) / 1048576.0:F1} MB）");
        return new MagnetPlayback(picked.Hash ?? "", 0, ParseSize(picked.Size), picked.Name, url!);
    }

    public void Stop() { /* 网盘侧任务由迅雷继续跑，无需停止 */ }

    // ═══════════ 业务：磁力 → 文件 → 直链 ═══════════

    private async Task<List<ThunderFile>?> ResolveFilesAsync(string magnet, CancellationToken ct)
    {
        // ① 云添加（幂等性由迅雷保证：同磁力重复添加会复用已有任务）
        var task = await AddOfflineAsync(magnet, ct).ConfigureAwait(false);
        if (task is null) return null;

        // ② 等服务器下完（首次可能几分钟；已完成则立刻返回）
        for (int i = 0; i < 90 && !ct.IsCancellationRequested; i++)
        {
            var tasks = await ListTasksAsync(ct).ConfigureAwait(false);
            var cur = tasks?.FirstOrDefault(t =>
                t.Id == task.Id
                || (!string.IsNullOrEmpty(task.FileId) && t.FileId == task.FileId)
                || t.Name == task.Name);
            if (cur is not null)
            {
                if (cur.Phase == "PHASE_TYPE_ERROR")
                {
                    CatClawLog.Write($"[迅雷网盘] 离线任务失败：{cur.Message}");
                    return null;
                }
                if (cur.Phase == "PHASE_TYPE_COMPLETE" && !string.IsNullOrEmpty(cur.FileId))
                {
                    task = cur;
                    break;
                }
            }
            await Task.Delay(2000, ct).ConfigureAwait(false);
        }

        if (string.IsNullOrEmpty(task.FileId))
        {
            CatClawLog.Write("[迅雷网盘] 等待离线任务完成超时");
            return null;
        }

        // ③ 取产物：文件本身，或（多文件种子）它的子项
        var f = await GetFileAsync(task.FileId, ct).ConfigureAwait(false);
        if (f is null) return null;
        if (f.Kind == "drive#folder")
            return await ListChildrenAsync(f.Id, ct).ConfigureAwait(false);
        return [f];
    }

    private async Task<ThunderTask?> AddOfflineAsync(string magnet, CancellationToken ct)
    {
        var name = "catclaw-" + DateTime.Now.ToString("yyyyMMddHHmmss");
        var body = new
        {
            kind = "drive#file",
            name,
            parent_id = "",
            upload_type = "UPLOAD_TYPE_URL",
            url = new { url = magnet },
        };
        var json = await PostJsonAsync(FileApi, body, ct).ConfigureAwait(false);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("task", out var t)) return null;
            return new ThunderTask
            {
                Id = Str(t, "id"),
                FileId = Str(t, "file_id"),
                Name = Str(t, "name") ?? name,
                Phase = Str(t, "phase") ?? "PHASE_TYPE_PENDING",
                Message = Str(t, "message"),
            };
        }
        catch (Exception ex)
        {
            CatClawLog.Write($"[迅雷网盘] 云添加响应解析失败：{ex.Message}");
            return null;
        }
    }

    private async Task<List<ThunderTask>?> ListTasksAsync(CancellationToken ct)
    {
        var json = await GetAsync($"{TaskApi}?type=offline&limit=10000&page_token=", ct).ConfigureAwait(false);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tasks", out var arr)) return null;
            var list = new List<ThunderTask>();
            foreach (var t in arr.EnumerateArray())
                list.Add(new ThunderTask
                {
                    Id = Str(t, "id"),
                    FileId = Str(t, "file_id"),
                    Name = Str(t, "name"),
                    Phase = Str(t, "phase"),
                    Message = Str(t, "message"),
                });
            return list;
        }
        catch { return null; }
    }

    private async Task<ThunderFile?> GetFileAsync(string id, CancellationToken ct)
    {
        var json = await GetAsync($"{FileApi}/{id}", ct).ConfigureAwait(false);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ParseFile(doc.RootElement);
        }
        catch { return null; }
    }

    private async Task<List<ThunderFile>?> ListChildrenAsync(string parentId, CancellationToken ct)
    {
        var filters = Uri.EscapeDataString("""{"phase":{"eq":"PHASE_TYPE_COMPLETE"},"trashed":{"eq":false}}""");
        var url = $"{FileApi}?space=&__type=drive&refresh=true&__sync=true"
                  + $"&parent_id={Uri.EscapeDataString(parentId)}&page_token=&with_audit=true"
                  + $"&limit=100&filters={filters}";
        var json = await GetAsync(url, ct).ConfigureAwait(false);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("files", out var arr)) return null;
            var list = new List<ThunderFile>();
            foreach (var f in arr.EnumerateArray())
            {
                var tf = ParseFile(f);
                if (tf is not null) list.Add(tf);
            }
            return list;
        }
        catch { return null; }
    }

    /// <summary>取播放直链：优先 medias[].link.url（转码/云播地址），回退 web_content_link</summary>
    private async Task<string?> PlayUrlOfAsync(string fileId, CancellationToken ct)
    {
        var f = await GetFileAsync(fileId, ct).ConfigureAwait(false);
        if (f is null) return null;

        if (!string.IsNullOrEmpty(f.MediaUrl)) return f.MediaUrl;
        if (!string.IsNullOrEmpty(f.WebContentLink))
        {
            // 直链要带迅雷下载器 UA，否则可能 403
            return f.WebContentLink;
        }
        return null;
    }

    private static ThunderFile? ParseFile(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var f = new ThunderFile
        {
            Kind = Str(e, "kind") ?? "",
            Id = Str(e, "id") ?? "",
            ParentId = Str(e, "parent_id") ?? "",
            Name = Str(e, "name") ?? "",
            Size = Str(e, "size") ?? "0",
            Hash = Str(e, "hash"),
            WebContentLink = Str(e, "web_content_link"),
        };
        if (e.TryGetProperty("medias", out var medias) && medias.ValueKind == JsonValueKind.Array)
            foreach (var m in medias.EnumerateArray())
                if (m.TryGetProperty("link", out var link))
                {
                    var u = Str(link, "url");
                    if (!string.IsNullOrEmpty(u)) { f.MediaUrl = u; break; }
                }
        return f;
    }

    private static long ParseSize(string? s) => long.TryParse(s, out var v) ? v : 0;
    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ═══════════ HTTP ═══════════

    private async Task<string?> GetAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        Decorate(req, pan: true);
        return await SendAsync(req, ct).ConfigureAwait(false);
    }

    private async Task<string?> PostJsonAsync(string url, object body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        Decorate(req, pan: true);
        return await SendAsync(req, ct).ConfigureAwait(false);
    }

    private void Decorate(HttpRequestMessage req, bool pan)
    {
        req.Headers.TryAddWithoutValidation("user-agent", pan ? DownloadUserAgent : UserAgent);
        req.Headers.TryAddWithoutValidation("accept", "application/json;charset=UTF-8");
        req.Headers.TryAddWithoutValidation("x-device-id", _deviceId);
        req.Headers.TryAddWithoutValidation("x-client-id", ClientId);
        req.Headers.TryAddWithoutValidation("x-client-version", ClientVersion);
        if (pan && !string.IsNullOrEmpty(_accessToken))
            req.Headers.TryAddWithoutValidation("authorization", "Bearer " + _accessToken);
        if (pan && !string.IsNullOrEmpty(_captchaToken))
            req.Headers.TryAddWithoutValidation("x-captcha-token", _captchaToken);
    }

    private static async Task<string?> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            CatClawLog.Write($"[迅雷网盘] HTTP {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");
            return null;
        }
        if (body.Contains("\"error_code\"", StringComparison.Ordinal)
            && !body.Contains("\"error\":\"success\"", StringComparison.Ordinal))
        {
            CatClawLog.Write($"[迅雷网盘] 接口报错: {body[..Math.Min(200, body.Length)]}");
            return null;
        }
        return body;
    }

    // ═══════════ 登录 / 续期 ═══════════

    /// <summary>账号密码登录：captcha/init → core.login/v3/login → captcha/init → auth/signin/token</summary>
    public async Task<bool> LoginAsync(string account, string password)
    {
        LoadState();
        if (string.IsNullOrEmpty(_deviceId)) { _deviceId = NewDeviceId(); }

        var signinUrl = XlUserApi + "/auth/signin/token";
        var signinAction = "POST:/v1/auth/signin/token";
        if (!await CaptchaInitAsync(signinAction, MetaForAccount(account), null).ConfigureAwait(false)) return false;

        // ① v3 登录拿 sessionID（密码明文，isMd5Pwd=0）
        var coreBody = new Dictionary<string, string>
        {
            ["protocolVersion"] = "301", ["sequenceNo"] = "1000012", ["platformVersion"] = "10",
            ["isCompressed"] = "0", ["appid"] = AppId, ["clientVersion"] = ClientVersion,
            ["peerID"] = "00000000000000000000000000000000",
            ["appName"] = "ANDROID-com.xunlei.downloadprovider", ["sdkVersion"] = "512000",
            ["devicesign"] = DeviceSign(), ["netWorkType"] = "WIFI", ["providerName"] = "NONE",
            ["deviceModel"] = "M2004J7AC", ["deviceName"] = "Xiaomi_M2004j7ac", ["OSVersion"] = "12",
            ["creditkey"] = _creditKey ?? "", ["hl"] = "zh-CN",
            ["userName"] = account, ["passWord"] = password,
            ["verifyKey"] = "", ["verifyCode"] = "", ["isMd5Pwd"] = "0",
        };
        using (var req = new HttpRequestMessage(HttpMethod.Post, XlUserBase + "/xluser.core.login/v3/login")
        {
            Content = new StringContent(JsonSerializer.Serialize(coreBody), Encoding.UTF8, "application/json"),
        })
        {
            req.Headers.TryAddWithoutValidation("user-agent", "android-ok-http-client/xl-acc-sdk/version-5.0.12.512000");
            req.Headers.TryAddWithoutValidation("accept", "application/json;charset=UTF-8");
            req.Headers.TryAddWithoutValidation("x-device-id", _deviceId);
            req.Headers.TryAddWithoutValidation("x-client-id", ClientId);
            req.Headers.TryAddWithoutValidation("x-client-version", ClientVersion);
            var txt = await SendAsync(req, CancellationToken.None).ConfigureAwait(false);
            if (txt is null) return false;
            using var doc = JsonDocument.Parse(txt);
            var e = doc.RootElement;
            var sid = Str(e, "sessionID");
            var uid = Str(e, "userID");
            _creditKey = Str(e, "creditkey") ?? _creditKey;
            if (string.IsNullOrEmpty(sid)) { LoginHint = "迅雷登录失败：未拿到 sessionID"; return false; }

            // ② 登录后再刷一次 captcha token（带 user_id）
            if (!await CaptchaInitAsync(signinAction,
                    new Dictionary<string, string>
                    {
                        ["client_version"] = ClientVersion,
                        ["package_name"] = PackageName,
                        ["user_id"] = uid ?? "",
                    }, uid).ConfigureAwait(false)) return false;

            // ③ signin/token 换令牌
            var signinBody = new Dictionary<string, string>
            {
                ["client_id"] = ClientId, ["client_secret"] = ClientSecret,
                ["provider"] = SignProvider, ["signin_token"] = sid,
            };
            using var req2 = new HttpRequestMessage(HttpMethod.Post, signinUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(signinBody), Encoding.UTF8, "application/json"),
            };
            Decorate(req2, pan: false);
            var txt2 = await SendAsync(req2, CancellationToken.None).ConfigureAwait(false);
            if (txt2 is null) return false;
            if (!ReadTokens(txt2)) return false;
        }

        SaveState();
        _attempted = true;
        return true;
    }

    /// <summary>用 refresh_token 续期</summary>
    private async Task<bool> RefreshAsync()
    {
        var body = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = _refreshToken!,
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, XlUserApi + "/auth/token")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        Decorate(req, pan: false);
        var txt = await SendAsync(req, CancellationToken.None).ConfigureAwait(false);
        if (txt is null) return false;
        if (!ReadTokens(txt)) return false;
        SaveState();
        return true;
    }

    private bool ReadTokens(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var e = doc.RootElement;
            _accessToken = Str(e, "access_token") ?? _accessToken;
            _refreshToken = Str(e, "refresh_token") ?? _refreshToken;
            if (string.IsNullOrEmpty(_accessToken))
            {
                LoginHint = "迅雷登录失败：未拿到 access_token";
                CatClawLog.Write($"[迅雷网盘] {LoginHint}；原文={json[..Math.Min(200, json.Length)]}");
                return false;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>刷 captcha_token；返回 false 表示需要在浏览器里完成一次验证（Url 已打进日志/提示）</summary>
    private async Task<bool> CaptchaInitAsync(string action, Dictionary<string, string> meta, string? userId)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var sb = new StringBuilder().Append(ClientId).Append(ClientVersion).Append(PackageName).Append(_deviceId).Append(ts);
        foreach (var alg in Algorithms) sb = new StringBuilder(Md5Hex(sb.ToString() + alg));
        if (userId is null) { meta["timestamp"] = ts; meta["captcha_sign"] = "1." + sb; }

        var body = new
        {
            action,
            captcha_token = _captchaToken ?? "",
            client_id = ClientId,
            device_id = _deviceId,
            meta,
            redirect_uri = RedirectUri,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, XlUserApi + "/shield/captcha/init")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        Decorate(req, pan: false);
        var txt = await SendAsync(req, CancellationToken.None).ConfigureAwait(false);
        if (txt is null) return false;
        try
        {
            using var doc = JsonDocument.Parse(txt);
            var e = doc.RootElement;
            var url = Str(e, "url");
            var token = Str(e, "captcha_token");
            if (!string.IsNullOrEmpty(url))
            {
                LoginHint = "迅雷要求额外验证，请在浏览器打开：" + url;
                CatClawLog.Write($"[迅雷网盘] {LoginHint}");
                return false;
            }
            if (string.IsNullOrEmpty(token)) { LoginHint = "captcha_token 为空"; return false; }
            _captchaToken = token;
            return true;
        }
        catch { return false; }
    }

    private static Dictionary<string, string> MetaForAccount(string account)
    {
        var m = new Dictionary<string, string>();
        if (account.Contains('@')) m["email"] = account;
        else if (account.Length is >= 11 and <= 18) m["phone_number"] = account;
        else m["username"] = account;
        return m;
    }

    // ═══════════ 设备标识 / 状态持久化 ═══════════

    /// <summary>devicesign = div101.&lt;deviceId&gt;&lt;md5(hex(sha1(deviceId+package+appid+appkey)))&gt;</summary>
    private string DeviceSign()
    {
        var s = _deviceId + PackageName + AppId + AppKey;
        var sha = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
        return "div101." + _deviceId + Md5Hex(sha);
    }

    private static string NewDeviceId() => Md5Hex(Guid.NewGuid().ToString("N") + Environment.MachineName);

    private static string Md5Hex(string s) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CatClawVideo", "thunder-pan.json");

    private static string CredsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CatClawVideo", "thunder-pan-creds.json");

    private void LoadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            var s = JsonSerializer.Deserialize<PanState>(File.ReadAllText(StatePath));
            if (s is null) return;
            _deviceId = s.DeviceId;
            _accessToken = s.AccessToken;
            _refreshToken = s.RefreshToken;
            _creditKey = s.CreditKey;
            _captchaToken = s.CaptchaToken;
        }
        catch { }
    }

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(new PanState
            {
                DeviceId = _deviceId, AccessToken = _accessToken, RefreshToken = _refreshToken,
                CreditKey = _creditKey, CaptchaToken = _captchaToken,
            }));
        }
        catch { }
    }

    private static PanCreds LoadCreds()
    {
        try
        {
            if (!File.Exists(CredsPath)) return new PanCreds();
            return JsonSerializer.Deserialize<PanCreds>(File.ReadAllText(CredsPath)) ?? new PanCreds();
        }
        catch { return new PanCreds(); }
    }

    /// <summary>供设置页写入迅雷账号（写完调 EnsureReadyAsync 即登录）</summary>
    public static void SaveCredentials(string account, string password)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CredsPath)!);
            File.WriteAllText(CredsPath, JsonSerializer.Serialize(new PanCreds { Account = account, Password = password }));
        }
        catch { }
    }

    private sealed class PanState
    {
        [JsonPropertyName("deviceId")] public string? DeviceId { get; set; }
        [JsonPropertyName("accessToken")] public string? AccessToken { get; set; }
        [JsonPropertyName("refreshToken")] public string? RefreshToken { get; set; }
        [JsonPropertyName("creditKey")] public string? CreditKey { get; set; }
        [JsonPropertyName("captchaToken")] public string? CaptchaToken { get; set; }
    }

    private sealed class PanCreds
    {
        [JsonPropertyName("account")] public string? Account { get; set; }
        [JsonPropertyName("password")] public string? Password { get; set; }
    }

    private sealed class ThunderFile
    {
        public string Kind { get; init; } = "";
        public string Id { get; init; } = "";
        public string ParentId { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Size { get; init; }
        public string? Hash { get; init; }
        public string? WebContentLink { get; init; }
        public string? MediaUrl { get; set; }
    }

    private sealed class ThunderTask
    {
        public string? Id { get; init; }
        public string? FileId { get; init; }
        public string? Name { get; init; }
        public string? Phase { get; init; }
        public string? Message { get; init; }
    }
}
