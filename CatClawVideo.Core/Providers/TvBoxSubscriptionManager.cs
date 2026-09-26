using System.Text.Json;
using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;

namespace CatClawVideo.Core.Providers;

/// <summary>
/// TVBox / 影视仓订阅解析器（明文 JSON 配置），带 okhttp UA（防直连源按 UA 发配置）。
/// 加密源三重解密通道：图片尾部隐写提取（饭太硬 /tv）、饭太硬官方解密接口
/// （饭太硬.net/jm/jiemi.php，2423 前缀密文等）、明文直读。
/// 站点类型映射：type 1 = MacCMS json（MacCmsJsonProvider 可播）、0 = xml（暂不支持）、
/// 3 = spider 爬虫源，再按 api 细分为：
///   · csp_Xxx    → Java jar/dex 爬虫（依赖订阅全局 spider 或站点自带 jar）
///   · http(s) 脚本地址 → JS 脚本爬虫（如 .js / drp，依赖 JS 引擎）
/// 两者当前都不可播，通过 <see cref="VodSiteInfo.StatusNote"/> 给出具体原因。
/// 加密配置（饭太硬等返回 logo 图/密文的源）识别后抛出明确异常。
/// </summary>
public class TvBoxSubscriptionManager : ISubscriptionManager
{
    private static readonly HttpClient Http = CreateHttp();

    /// <summary>
    /// 饭太硬等防直连源按 UA 区分响应：无 UA → 302 跳 HTML 页面；
    /// okhttp/4.x（TVBox/影视仓标准 UA）→ 返回带隐写配置的图片。
    /// </summary>
    private static HttpClient CreateHttp()
    {
        // 走 Doh.NewClient：订阅地址是「最先被 DNS 污染打死」的那一跳，TVBox 正是把 OkGo 整体挂了 DoH
        return Services.Doh.NewClient(15, "okhttp/4.x");
    }

    /// <summary>仓库套仓库到第几层就判定为配置错误（防 urls 互相指向造成无限递归）。</summary>
    const int MaxRepoDepth = 3;

    public Task<List<VodSiteInfo>> LoadSubscriptionAsync(string subscriptionUrl, CancellationToken ct = default)
        => LoadSubscriptionCoreAsync(subscriptionUrl, 0, ct);

    private async Task<List<VodSiteInfo>> LoadSubscriptionCoreAsync(string subscriptionUrl, int depth, CancellationToken ct)
    {
        // 地址尾部的 #line=N 是「多仓选中的第几条线」——把它剥掉再走原有链路，订阅表因此不用改结构
        var (rawUrl, lineIndex) = SplitLine(subscriptionUrl);
        // clan:// / file:// / 裸相对名 先换成本机或远端可用的地址（对位 ApiConfig.clanToAddress）
        subscriptionUrl = Services.ClanScheme.Resolve(rawUrl);

        // 本地源文件（猫爪源生态 / 本地 TVBox json）：与远程同链路解析
        if (File.Exists(subscriptionUrl) || subscriptionUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            var path = subscriptionUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                ? new Uri(subscriptionUrl).LocalPath
                : subscriptionUrl;
            var local = await File.ReadAllTextAsync(path, ct);
            if (local.Contains(CatClawSourceDoc.ProtocolMagic, StringComparison.OrdinalIgnoreCase))
                return BuildCatClawSites(local, subscriptionUrl);
            return await ParseConfigTextAsync(local, System.IO.Path.GetFileNameWithoutExtension(path), ct);
        }

        using var resp = await Http.GetAsync(subscriptionUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        // 失败时把**实际落地地址**报出来：订阅地址常被 302 到导航页/公告图（跟随重定向后
        // RequestMessage 已是最后一跳）。用户报「无法解析这个接口」时，光说「返回的是图片」
        // 没法定位，加上这行才知道站点本身已经不是配置了。
        var landed = $"\n内容来自：{resp.RequestMessage?.RequestUri ?? new Uri(subscriptionUrl)}"
            + (contentType.Length > 0 ? $"（{contentType}）" : "");

        // 图片响应：饭太硬等防直连源把 base64 配置隐写在图片尾部（JPEG FFD9 之后），
        // 先尝试提取隐写配置，失败再抛明确异常
        string text;
        if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || IsBinary(bytes))
        {
            var steganography = TryExtractConfigFromImage(bytes);
            if (steganography is null)
                throw new NotSupportedException(
                    "该订阅地址返回的是图片/二进制内容，且未在图片中找到隐藏配置。" + landed + "\n" +
                    "请使用明文 TVBox json 或 MacCMS 直连地址。");
            text = steganography;
        }
        else
        {
            text = System.Text.Encoding.UTF8.GetString(bytes);
        }

        // 加密/混淆配置（非 JSON 开头，如 2423 前缀密文）：回退饭太硬官方解密接口取明文
        var probe = text.TrimStart();
        if (!probe.StartsWith('{') && !probe.StartsWith('['))
        {
            var decrypted = await TryOfficialDecryptAsync(subscriptionUrl, ct);
            if (decrypted is null)
                throw new NotSupportedException(
                    "该订阅返回的既不是 JSON 配置，也不是能通过官方通道解密的密文（可能是网页或被防直连处理过）。"
                    + landed + "\n" +
                    "可改用明文 TVBox json 或 MacCMS 直连地址。");
            text = decrypted;
        }

        // 影视仓「多仓」：顶层只有 urls、没有 sites，得先挑一条线，再拿那条线的地址去取真配置。
        // 这一步此前没做 —— 接口注释与源配置页文案都写着「支持 urls 多仓」，
        // 实际会在 sites 缺失那里返回 0 个站点（表现为「订阅添加成功但首页是空的」）。
        var lines = ReadLines(text);
        if (lines.Count > 0)
        {
            if (depth >= MaxRepoDepth)
                throw new NotSupportedException($"多仓订阅嵌套超过 {MaxRepoDepth} 层，判定为配置错误。");
            var pick = lines[Math.Clamp(lineIndex, 0, lines.Count - 1)];
            return await LoadSubscriptionCoreAsync(ResolveRepoUrl(pick.Url, subscriptionUrl), depth + 1, ct);
        }

        // 猫爪源（CatClaw Source，自建生态）：单地址即一个原生数据源（type=100，全平台可播）
        if (text.Contains(CatClawSourceDoc.ProtocolMagic, StringComparison.OrdinalIgnoreCase))
            return BuildCatClawSites(text, subscriptionUrl);

        var sites = await ParseConfigTextAsync(text, subscriptionName: new Uri(subscriptionUrl).Host, ct);

        // 相对路径解析：小雅等站点 jar 写作 ./libs/x.jar（相对订阅源目录）。
        // 除 ./ 之外还要认 ../（TVBox fixContentPath 两种都改，此前只认 ./ 会让上一级目录的 jar 直接 404）
        var baseUrl = subscriptionUrl[..(subscriptionUrl.LastIndexOf('/') + 1)];
        foreach (var s in sites)
        {
            s.Jar = FixRelative(s.Jar, baseUrl);
            s.Ext = FixRelative(s.Ext, baseUrl);
        }
        return sites;
    }

    /// <summary>
    /// 只读顶层 urls 做线路探测。加密/隐写的多仓索引探测不到（会返回空表），
    /// 那种情况 <see cref="LoadSubscriptionAsync"/> 仍然会解出多仓并默认取第 0 条线 —— 少一个弹窗，不是少功能。
    /// </summary>
    public async Task<IReadOnlyList<SubscriptionLine>> ProbeLinesAsync(string subscriptionUrl, CancellationToken ct = default)
    {
        try
        {
            var (rawUrl, _) = SplitLine(subscriptionUrl);
            string body;
            if (File.Exists(rawUrl) || rawUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                var path = rawUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                    ? new Uri(rawUrl).LocalPath : rawUrl;
                body = await File.ReadAllTextAsync(path, ct);
            }
            else
            {
                using var resp = await Http.GetAsync(rawUrl, ct);
                if (!resp.IsSuccessStatusCode) return [];
                body = await resp.Content.ReadAsStringAsync(ct);
            }
            return ReadLines(body);
        }
        catch
        {
            // 探测失败一律当「不是多仓」：让主链路去报真正的错（网络/加密/格式），这里不抢话
            return [];
        }
    }

    /// <summary>订阅地址尾部可带 <c>#line=N</c> 指定多仓线路（存回订阅表的就是这个字符串）。</summary>
    public static (string Url, int LineIndex) SplitLine(string url)
    {
        var i = url.LastIndexOf("#line=", StringComparison.OrdinalIgnoreCase);
        if (i <= 0 || !int.TryParse(url[(i + 6)..], out var n) || n < 0) return (url, -1);
        return (url[..i], n);
    }

    /// <inheritdoc/>
    public async Task<List<VodSiteInfo>> LoadAllSubscriptionsAsync(
        IEnumerable<SubscriptionRef> subscriptions, CancellationToken ct = default)
    {
        var merged = new List<VodSiteInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sub in subscriptions)
        {
            try
            {
                var sites = await LoadSubscriptionAsync(sub.SourceUrl, ct);
                int added = 0;
                foreach (var s in sites)
                    if (seen.Add(s.Key)) { merged.Add(s); added++; }
                System.Diagnostics.Debug.WriteLine($"[订阅] {sub.Name}: +{added}/{sites.Count} 站（合并后 {merged.Count}）");
            }
            catch (Exception ex)
            {
                // 单个订阅失败不拖垮其余：它的站点缺席而已，其余订阅照常
                System.Diagnostics.Debug.WriteLine($"[订阅] {sub.Name} 加载失败: {ex.Message}");
            }
        }
        return merged;
    }

    /// <summary>订阅里写的 <c>./x</c>、<c>../x</c> 都相对「这份配置自己在哪」解析。</summary>
    static string? FixRelative(string? value, string baseUrl)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (!value.StartsWith("./", StringComparison.Ordinal) && !value.StartsWith("../", StringComparison.Ordinal))
            return value;
        try
        {
            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var basis)) return new Uri(basis, value).ToString();
        }
        catch { }
        return value.StartsWith("./", StringComparison.Ordinal) ? baseUrl + value[2..] : value;
    }

    /// <summary>顶层 <c>urls[]</c>；不是多仓或形态不对时返回空表。</summary>
    static List<SubscriptionLine> ReadLines(string json)
    {
        var result = new List<SubscriptionLine>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("urls", out var arr)
                || arr.ValueKind != JsonValueKind.Array) return result;
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var u = e.TryGetProperty("url", out var uu) ? uu.GetString() ?? "" : "";
                var n = e.TryGetProperty("name", out var nn) ? nn.GetString() ?? "" : "";
                if (u.Trim().Length == 0) continue;
                result.Add(new SubscriptionLine(n.Trim().Length > 0 ? n.Trim() : u.Trim(), u.Trim()));
            }
        }
        catch { }
        return result;
    }

    /// <summary>子地址可能是相对路径（相对这份仓库 json 所在目录）。</summary>
    static string ResolveRepoUrl(string child, string repoUrl)
    {
        if (child.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || child.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            || File.Exists(child)) return child;
        try
        {
            if (Uri.TryCreate(repoUrl, UriKind.Absolute, out var repo)) return new Uri(repo, child).ToString();
        }
        catch { }
        return child;
    }

    public Task<List<VodSiteInfo>> ParseConfigTextAsync(string jsonText, string subscriptionName, CancellationToken ct = default)
    {
        var trimmed = jsonText.TrimStart();

        // 加密配置识别：base64 大块无 { 开头 / 非 JSON 结构
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
            throw new NotSupportedException(
                "该订阅返回的是加密/混淆配置（非明文 JSON），暂不支持自动解密。\n" +
                "可改用明文 TVBox json 或 MacCMS 直连地址。");

        // 容忍行注释与尾逗号（饭太硬等源的配置常带 // 注释行）
        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        using var doc = JsonDocument.Parse(jsonText, options);
        var root = doc.RootElement;

        // 留存 parses/hosts 到解析配置仓（此前这两个键被整包丢弃，播放解析无依据）
        TvBoxConfigStore.Capture(subscriptionName, root);

        // 订阅自带直播源（lives，如饭太硬）：把解密后的明文配置落盘。
        // 直播模块（LiveSourceService）在用户未手动配置直播源时自动采用 ——
        // 否则点播订阅里明明带了直播，直播页却还要用户再配一次（2026-09-25 用户反馈）。
        if (root.TryGetProperty("lives", out var livesEl) &&
            livesEl.ValueKind == JsonValueKind.Array && livesEl.GetArrayLength() > 0)
            Live.LiveSourceService.CaptureSubscriptionConfig(jsonText);

        var sites = new List<VodSiteInfo>();
        if (!root.TryGetProperty("sites", out var siteArray) || siteArray.ValueKind != JsonValueKind.Array)
            return Task.FromResult(sites);

        // 全局 spider 包（csp_ 类站点未自带 jar 时回退到它）
        var globalSpider = root.TryGetProperty("spider", out var sp) && sp.ValueKind == JsonValueKind.String
            ? sp.GetString()
            : null;

        foreach (var s in siteArray.EnumerateArray())
        {
            var key = s.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
            var name = s.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var api = s.TryGetProperty("api", out var a) ? a.GetString() ?? "" : "";
            var type = s.TryGetProperty("type", out var t) && t.TryGetInt32(out var tv) ? tv : -1;
            if (key.Length == 0 || name.Length == 0) continue;

            // ext 可能是字符串（URL/密文），也可能是内嵌对象或对象数组（如小雅 Alist 的全局配置）
            var ext = s.TryGetProperty("ext", out var e) && e.ValueKind != JsonValueKind.Null
                ? (e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText())
                : null;

            var jar = s.TryGetProperty("jar", out var j) && j.ValueKind == JsonValueKind.String ? j.GetString() : null;
            var timeout = s.TryGetProperty("timeout", out var to) && to.TryGetInt32(out var tov) ? tov : (int?)null;

            // 源里 searchable/quickSearch 常写作 1/0 而非 true/false，此处按两者都兼容读取
            var searchableFlag = ReadFlag(s, "searchable");
            var quickSearchFlag = ReadFlag(s, "quickSearch");

            var (spiderKind, statusNote) = Classify(type, api,
                string.IsNullOrWhiteSpace(jar) ? globalSpider : jar);
            var (needsCreds, credServers) = DetectCredentials(ext);

            // 猫爪源（type=100/101）：原生数据源，地址有效即可播
            bool playable = type is CatClawSourceDoc.SiteType or CatClawSourceWeb.WebSiteType
                ? api.StartsWith("http", StringComparison.OrdinalIgnoreCase) || File.Exists(api)
                : spiderKind == VodSpiderKind.None &&
                  type == 1 &&
                  api.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                  !api.Contains("csp_", StringComparison.OrdinalIgnoreCase);

            sites.Add(new VodSiteInfo
            {
                Key = key,
                Name = name,
                Api = api,
                Type = type,
                Ext = ext,
                Jar = string.IsNullOrWhiteSpace(jar) ? globalSpider : jar,
                SpiderKind = spiderKind,
                TimeoutSeconds = timeout,
                SubscriptionName = subscriptionName,
                Playable = playable,
                Searchable = searchableFlag ?? playable,
                QuickSearch = quickSearchFlag ?? playable,
                // MacCMS / 爬虫源天然带标准搜索 API（一次请求即可）；猫爪 web 源要看是否声明了 searchUrl
                DeclaredSearch = playable && type is not (CatClawSourceDoc.SiteType or CatClawSourceWeb.WebSiteType),
                StatusNote = playable ? null : statusNote,
                NeedsCredentials = needsCreds,
                CredentialServers = credServers,
            });
        }
        return Task.FromResult(sites);
    }

    /// <summary>
    /// 判定爬虫运行时类型与不可播原因。type=3 按 api 形态细分：
    /// csp_ 前缀为爬虫（jar 或 JS，取决于 spider 包扩展名）、http(s) 地址为脚本爬虫。
    /// </summary>
    private static (VodSpiderKind Kind, string Note) Classify(int type, string api, string? spiderPkg = null)
    {
        if (type is CatClawSourceDoc.SiteType or CatClawSourceWeb.WebSiteType)
            return (VodSpiderKind.None, "");

        if (type == 3)
        {
            if (api.StartsWith("csp_", StringComparison.OrdinalIgnoreCase))
            {
                // TVBox 真实形态：spider 包是 .js 文件时 csp_ 站点走 JS 引擎而非 dex
                // （spiderPkg 格式 "url;md5;hash"，取 URL 段并剥掉 ?query 再判扩展名）
                var pkgUrl = spiderPkg?.Split(';')[0];
                var pkgPath = pkgUrl?.Split('?')[0];
                if (!string.IsNullOrEmpty(pkgPath) &&
                    (pkgPath.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
                     pkgPath.EndsWith(".drpy", StringComparison.OrdinalIgnoreCase)))
                    return (VodSpiderKind.Script, "JS 爬虫源 · 需 JS 引擎");
                return (VodSpiderKind.Jar, "jar 爬虫源 · 需 spider 运行时");
            }

            if (api.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return (VodSpiderKind.Script, "脚本爬虫源 · 需 JS 引擎");

            return (VodSpiderKind.Jar, "爬虫源 · 需 spider 运行时");
        }

        return type switch
        {
            0 => (VodSpiderKind.None, "MacCMS xml · 地址不可用"),
            1 => (VodSpiderKind.None, "MacCMS json · 地址不可用"),
            _ => (VodSpiderKind.None, $"type {type} · 暂不支持"),
        };
    }

    /// <summary>
    /// 把猫爪源文档展开为站点列表（static=type100 / web 规则=type101，由 CatClawSourceProvider 承接）。
    /// v2.1 多站点文件（sites 数组）逐站点展开，Key = "catclaw#&lt;id&gt;"；单站点 Key = "catclaw"。
    /// </summary>
    private static List<VodSiteInfo> BuildCatClawSites(string json, string url)
    {
        var result = new List<VodSiteInfo>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
                ? uri.Host : "local";

            // 多站点模式：逐站点展开
            if (root.TryGetProperty("sites", out var sitesEl) && sitesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in sitesEl.EnumerateArray())
                {
                    var id = s.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                        ? idEl.GetString() ?? "" : "";
                    var name = s.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String
                        ? nEl.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) continue;
                    result.Add(new VodSiteInfo
                    {
                        Key = $"catclaw#{id}",
                        Name = name,
                        Api = url,
                        Type = CatClawSourceWeb.WebSiteType,
                        SubscriptionName = host,
                        Playable = true,
                        Searchable = true,
                        QuickSearch = true,
                        // 声明了 searchUrl 才算「一次请求可搜」——跨源封面检索靠它筛选
                        DeclaredSearch = HasSearchUrl(s),
                    });
                }
                if (result.Count > 0) return result;
            }

            // 单站点模式（向后兼容）
            var topName = "猫爪源";
            var isWeb = false;
            if (root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                topName = n.GetString() ?? topName;
            if (root.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String)
                isWeb = m.GetString() == "web";
            result.Add(new VodSiteInfo
            {
                Key = "catclaw",
                Name = topName,
                Api = url,
                Type = isWeb ? CatClawSourceWeb.WebSiteType : CatClawSourceDoc.SiteType,
                SubscriptionName = host,
                Playable = true,
                Searchable = true,
                QuickSearch = true,
                DeclaredSearch = HasSearchUrl(root),
            });
        }
        catch
        {
            result.Add(new VodSiteInfo
            {
                Key = "catclaw",
                Name = "猫爪源",
                Api = url,
                Type = CatClawSourceDoc.SiteType,
                SubscriptionName = "catclaw",
                Playable = true,
                Searchable = true,
                QuickSearch = true,
            });
        }
        return result;
    }

    /// <summary>
    /// 检测站点是否需要账号认证（alist 类）：ext 为 JSON 数组、首个元素 type=global 且含
    /// username/password 字段、无现成 token 时成立；同时收集数组内全部 server 地址。
    /// </summary>
    private static (bool Needs, List<string> Servers) DetectCredentials(string? ext)
    {
        var servers = new List<string>();
        var trimmed = ext?.TrimStart();
        if (string.IsNullOrEmpty(trimmed) || !trimmed.StartsWith('[')) return (false, servers);

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var arr = doc.RootElement;
            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) return (false, servers);

            bool hasCredField = false, hasToken = false;
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                if (item.TryGetProperty("server", out var sv) && sv.ValueKind == JsonValueKind.String)
                {
                    var server = sv.GetString();
                    if (!string.IsNullOrEmpty(server) && !servers.Contains(server)) servers.Add(server);
                }

                if (item.TryGetProperty("type", out var tp) && tp.ValueKind == JsonValueKind.String &&
                    tp.GetString() == "global")
                {
                    if (item.TryGetProperty("username", out _) || item.TryGetProperty("password", out _))
                        hasCredField = true;
                    if (item.TryGetProperty("token", out var tk) &&
                        tk.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(tk.GetString()))
                        hasToken = true;
                }
            }
            return (hasCredField && !hasToken && servers.Count > 0, servers);
        }
        catch
        {
            return (false, servers);
        }
    }

    /// <summary>
    /// 站点定义里是否声明了站内搜索接口（猫爪 web 源的 rules.searchUrl）。
    /// 只有声明了才是一次请求可搜——跨源封面检索靠这个标记避免退化成「扫分类页」。
    /// </summary>
    private static bool HasSearchUrl(JsonElement siteEl)
    {
        try
        {
            return siteEl.TryGetProperty("rules", out var rules)
                && rules.ValueKind == JsonValueKind.Object
                && rules.TryGetProperty("searchUrl", out var su)
                && su.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(su.GetString());
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>读取布尔标记，兼容 true/false、1/0、"1"/"0" 四种写法；字段缺失或类型异常返回 null。</summary>
    private static bool? ReadFlag(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var v)) return null;
        try
        {
            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => v.TryGetInt32(out var n) ? n != 0 : null,
                JsonValueKind.String => bool.TryParse(v.GetString(), out var b)
                    ? b
                    : (int.TryParse(v.GetString(), out var n2) ? n2 != 0 : null),
                _ => null,
            };
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsBinary(byte[] bytes)
    {
        if (bytes.Length < 4) return false;
        // JPEG FF D8 FF / PNG 89 50 4E 47 / GIF / BMP BM
        return (bytes[0] == 0xFF && bytes[1] == 0xD8) ||
               (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) ||
               (bytes[0] == 0x42 && bytes[1] == 0x4D);
    }

    /// <summary>
    /// 饭太硬官方解密通道（饭太硬.net/jm）：GET jiemi.php?url=&lt;订阅地址&gt;，
    /// 返回「// 注释头 + 明文 JSON」。订阅为加密配置（2423 前缀密文等）时作为回退通道；
    /// 解密失败或结果仍非 JSON 返回 null。
    /// </summary>
    private static async Task<string?> TryOfficialDecryptAsync(string subscriptionUrl, CancellationToken ct)
    {
        try
        {
            var jm = "http://www.xn--sss604efuw.net/jm/jiemi.php?url=" + Uri.EscapeDataString(subscriptionUrl);
            using var resp = await Http.GetAsync(jm, ct);
            resp.EnsureSuccessStatusCode();
            var text = await resp.Content.ReadAsStringAsync(ct);
            var idx = text.IndexOf('{');
            if (idx < 0) return null;
            var json = text[idx..].TrimStart();
            return json.StartsWith('{') || json.StartsWith('[') ? json : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从图片中提取隐写配置（饭太硬防直连机制）：真实 base64 配置附加在图片结束标记之后。
    /// 流程：定位图片结束标记（JPEG FFD9 / PNG IEND）→ 清洗非 base64 字符 →
    /// 前缀可能混入干扰字符，按 4 字符对齐逐偏移尝试解码，取能解出 JSON 的起点。
    /// </summary>
    private static string? TryExtractConfigFromImage(byte[] bytes)
    {
        var end = FindImageEnd(bytes);
        if (end < 0 || end + 8 >= bytes.Length) return null;

        var tail = System.Text.Encoding.ASCII.GetString(bytes, end, bytes.Length - end);
        var clean = System.Text.RegularExpressions.Regex.Replace(tail, "[^A-Za-z0-9+/]", "");
        if (clean.Length < 16) return null;

        for (int skip = 0; skip < Math.Min(256, clean.Length - 4); skip += 4)
        {
            var seg = clean[skip..];
            if (seg.Length % 4 != 0) seg += new string('=', 4 - seg.Length % 4);
            try
            {
                var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(seg));
                var trimmed = decoded.TrimStart('\0').TrimStart();
                if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
                    return trimmed;
            }
            catch { }
        }

        // 「盐前缀 + base64」形态（2026-09-26 英格里希嗷呜.top 实测）：IEND 后是
        // 「盐（本身全是 base64 合法字符，如 Rn5dFaW951**）+ base64 配置」——盐与配置
        // 黏连后任何 4 字节对齐都解不出（盐长 10，真起点 index 10 模 4 = 2，上面的
        // += 4 循环永远试不到）。base64("{\"") = "eyJ"（base64("[") = "W1"），找到
        // JSON 开头的铁打标志直接从那里解。
        foreach (var marker in new[] { "eyJ", "W1si", "W3si" })   // {" / [{" / [ {
        {
            var at = clean.IndexOf(marker, StringComparison.Ordinal);
            while (at >= 0)
            {
                var seg = clean[at..];
                if (seg.Length % 4 != 0) seg += new string('=', 4 - seg.Length % 4);
                try
                {
                    var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(seg));
                    var trimmed = decoded.TrimStart('\0').TrimStart();
                    if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
                        return trimmed;
                }
                catch { }
                at = clean.IndexOf(marker, at + marker.Length, StringComparison.Ordinal);
            }
        }
        return null;
    }

    /// <summary>定位图片结束标记位置（JPEG 最后一个 FFD9 / PNG IEND 块尾），未识别返回 -1</summary>
    private static int FindImageEnd(byte[] bytes)
    {
        // PNG：IEND + 4 字节 CRC
        for (int i = 0; i + 8 <= bytes.Length; i++)
        {
            if (bytes[i] == 'I' && bytes[i + 1] == 'E' && bytes[i + 2] == 'N' && bytes[i + 3] == 'D')
                return i + 8;
        }
        // JPEG：从尾往前找 FFD9
        for (int i = bytes.Length - 2; i >= 0; i--)
        {
            if (bytes[i] == 0xFF && bytes[i + 1] == 0xD9) return i + 2;
        }
        return -1;
    }
}
