using System.Text;
using System.Text.Json;

namespace CatClawVideo.Core.Services;

/// <summary>
/// 局域网遥控（对位 TVBox <c>server/RemoteServer</c> + <c>res/raw</c> 那套前端）。
///
/// <para><b>TVBox 全程无鉴权</b>：它的 <c>/action</c> 能改配置、<c>/upload</c> 能落文件（zip 还会自动解压）、
/// <c>/file/**</c> 能翻存储目录 —— 等于在局域网里开一个谁都能动的后门。
/// 这里**照做功能但反过来做安全**：① 除 <c>/rc/ping</c> 外全部要求 <c>token</c>；
/// ② 不提供文件浏览、不提供上传、不提供改配置的口子，只做「推一条待播 + 读当前播放 + 搜索」。</para>
///
/// <para><see cref="Handle"/> 是纯函数（进 query 参数、出状态码与响应体），
/// 所以不依赖真实 socket 就能全量测；<see cref="SpiderProxyServer"/> 只负责把它接到连接上。</para>
/// </summary>
public static class RemoteControlHub
{
    /// <summary>访问令牌。宿主启动时从持久化里写入（为空时这里现生成一个，由宿主决定要不要存）。</summary>
    public static string Token
    {
        get
        {
            if (string.IsNullOrEmpty(_token))
                _token = Guid.NewGuid().ToString("N")[..16];
            return _token;
        }
        set => _token = value;
    }
    private static string? _token;

    /// <summary>展示用的设备名（让手机上看得出推的是哪台设备）。</summary>
    public static string DeviceName { get; set; } = "猫爪影视";

    /// <summary>当前播放信息（由播放页写入）。空表示没在播。</summary>
    public static string? CurrentMedia { get; set; }

    static readonly List<string> _pending = [];
    static readonly object Gate = new();

    /// <summary>搜索处理器（宿主注入：跨源搜完返回 json）。没接上时 <c>/rc/search</c> 回 501 而不是假装成功。</summary>
    public static Func<string, CancellationToken, Task<string>>? SearchHandler { get; set; }

    public static int PendingCount { get { lock (Gate) return _pending.Count; } }

    /// <summary>播放页取走并清空待播队列。元素是 <c>{"name":…,"url":…}</c> 的 json 串。</summary>
    public static List<string> TakePending()
    {
        lock (Gate)
        {
            if (_pending.Count == 0) return [];
            var copy = new List<string>(_pending);
            _pending.Clear();
            return copy;
        }
    }

    /// <summary>
    /// 这是给人看的调试/遥控接口，所以关掉默认的 non-ASCII 转义：
    /// 否则设备名与错误说明都会变成 猫爪… ，浏览器里没法直接读。
    /// 响应只在 application/json 里出现、不会被当 HTML 渲染，所以这里不做 HTML 转义是安全的。
    /// </summary>
    static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>内置遥控页：搜索 → 推送，或直接把播放地址推过来。单文件、无外部资源。</summary>
    static string Page
    {
        get
        {
            var head = "<!doctype html><html lang=zh><head><meta charset=utf-8>"
                + "<meta name=viewport content='width=device-width,initial-scale=1'>"
                + "<title>" + DeviceName + " 遥控</title><style>"
                + "body{background:#12141d;color:#e8eaf6;font:15px/1.6 system-ui;margin:18px}"
                + "h1{font-size:17px}.row{display:flex;gap:8px;margin:10px 0}"
                + "input{flex:1;background:#1b1f2b;color:#e8eaf6;border:1px solid #2c3242;border-radius:9px;padding:9px}"
                + "button{background:#2f6bff;color:#fff;border:0;border-radius:9px;padding:9px 14px}"
                + ".it{display:flex;align-items:center;gap:10px;padding:9px 0;border-bottom:1px solid #232838}"
                + ".it span{color:#868cae;font-size:12px;flex:1}#msg{color:#8fb4ff;min-height:22px}"
                + "</style></head><body><h1>" + DeviceName + " 遥控</h1>";
            var js = "<script>var T=" + JsonSerializer.Serialize(Token, Json) + ";"
                + "function q(s){return document.getElementById(s)}"
                + "function esc(s){return String(s==null?'':s).replace(/[&<>]/g,function(c){return({'&':'&amp;','<':'&lt;','>':'&gt;'})[c]})}"
                + "function say(m){q('msg').textContent=m}"
                + "function api(p){return fetch(p+(p.indexOf('?')<0?'?':'&')+'token='+encodeURIComponent(T))}"
                + "function search(){var w=q('kw').value.trim();if(!w)return;say('搜索中…');"
                + "api('/rc/search?text='+encodeURIComponent(w)).then(function(r){return r.json()}).then(function(a){"
                + "var l=q('list');l.innerHTML='';if(!a||!a.length){say('没有结果');return}say('共 '+a.length+' 条，点推送即在本机打开');"
                + "a.forEach(function(x){var d=document.createElement('div');d.className='it';"
                + "d.innerHTML='<b>'+esc(x.name)+'</b><span>'+esc(x.siteName||x.site)+'</span>';var b=document.createElement('button');"
                + "b.textContent='推送';b.onclick=function(){api('/rc/push?site='+encodeURIComponent(x.sourceKey)"
                + "+'&id='+encodeURIComponent(x.id)+'&name='+encodeURIComponent(x.name)).then(function(){say('已推送：'+x.name)})};"
                + "d.appendChild(b);l.appendChild(d)})}).catch(function(e){say('失败：'+e)})}"
                + "function pushUrl(){var u=q('url').value.trim();if(!u)return;"
                + "api('/rc/push?url='+encodeURIComponent(u)+'&name=网页推送').then(function(){say('已推送地址，本机 3 秒内打开')})}"
                + "q('go').onclick=search;q('push').onclick=pushUrl;"
                + "q('kw').addEventListener('keydown',function(e){if(e.key=='Enter')search()});</script>";
            var body = "<div class=row><input id=kw placeholder='片名 / 关键词'><button id=go>搜索</button></div>"
                + "<div id=list></div>"
                + "<div class=row><input id=url placeholder='或直接粘贴 m3u8 / mp4 播放地址'><button id=push>推送地址</button></div>"
                + "<p id=msg></p></body></html>";
            return head + js + body;
        }
    }

    /// <summary>响应体：<see cref="Ok"/> 为 false 时 <see cref="Body"/> 是错误说明。</summary>
    public sealed record Reply(int Status, string Body, string Mime = "application/json")
    {
        public bool Ok => Status is >= 200 and < 300;
    }

    public static Reply Handle(string path, IReadOnlyDictionary<string, string> args, CancellationToken ct = default)
    {
        var route = path.TrimStart('/');
        var slash = route.IndexOf('/');
        var verb = slash < 0 ? route : route[(slash + 1)..];

        switch (verb)
        {
            // 发现用的：不要 token（手机只知道 IP:端口 时先问一句「这是谁、要不要钥匙」）
            case "ping":
                return new Reply(200, JsonSerializer.Serialize(new
                {
                    ok = true,
                    name = DeviceName,
                    needToken = true,
                    endpoints = new[] { "ping", "push", "pending", "media", "search" },
                }, Json));

            // 遥控网页（对位 TVBox res/raw 那套前端）。必须带 token —— 页面里就嵌着口令，
            // 谁都能打开等于整套接口无鉴权，正是 TVBox 的问题。
            case "":
            case "rc":            // 只敲 http://ip:9978/rc（没带斜杠）也要能打开页面
            case "ui":
            case "index":
            {
                var deny = RequireToken(args);
                if (deny is not null) return deny;
                return new Reply(200, Page, "text/html; charset=utf-8");
            }

            case "push":
            {
                var deny = RequireToken(args);
                if (deny is not null) return deny;
                var name = Dec(args.GetValueOrDefault("name"));
                if (string.IsNullOrWhiteSpace(name)) name = "远程推送";
                var url = Dec(args.GetValueOrDefault("url"));
                var site = Dec(args.GetValueOrDefault("site"));
                var itemId = Dec(args.GetValueOrDefault("id"));

                // 两种推送形态：**条目引用**（搜索/详情页推过来的，进 App 后走正常选集/线路/历史）
                // 与 **裸直链**（用户在别处拷到的 m3u8/mp4）。前者更该优先支持 —— 它推的是「这部片」，
                // 而不是「这一条会过期的地址」。
                string item;
                if (site.Length > 0 && itemId.Length > 0)
                    item = JsonSerializer.Serialize(new { kind = "item", site, id = itemId, name }, Json);
                else if (url.Length > 0)
                {
                    if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        && !url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                        return new Reply(400, Err("url 必须是 http(s) 或 file 地址"));
                    item = JsonSerializer.Serialize(new { kind = "url", url, name }, Json);
                }
                else return new Reply(400, Err("需要 site+id，或者 url"));

                lock (Gate)
                {
                    // 只留最近 20 条：遥控器连点不该把队列撑爆
                    if (_pending.Count >= 20) _pending.RemoveAt(0);
                    _pending.Add(item);
                }
                return new Reply(200, Err("queued", key: "ok"));
            }

            case "pending":
            {
                var deny = RequireToken(args);
                if (deny is not null) return deny;
                var items = TakePending();
                return new Reply(200, "[" + string.Join(",", items) + "]");
            }

            case "media":
            {
                var deny = RequireToken(args);
                if (deny is not null) return deny;
                return new Reply(200, CurrentMedia ?? "null");
            }

            case "search":
            {
                var deny = RequireToken(args);
                if (deny is not null) return deny;
                var text = Dec(args.GetValueOrDefault("text")).Length > 0
                    ? Dec(args.GetValueOrDefault("text")).Trim()
                    : Dec(args.GetValueOrDefault("wd")).Trim();
                if (text.Length == 0) return new Reply(400, Err("missing text"));
                var handler = SearchHandler;
                if (handler is null)
                    return new Reply(501, Err("search 未接入（宿主没有注入搜索处理器）"));
                try
                {
                    return new Reply(200, handler(text, ct).GetAwaiter().GetResult());
                }
                catch (OperationCanceledException)
                {
                    return new Reply(499, Err("cancelled"));
                }
                catch (Exception ex)
                {
                    return new Reply(500, Err(ex.Message));
                }
            }

            default:
                // 明确列出被禁掉的那几类，别让调用方以为是自己参数写错了
                return new Reply(404, Err("未知端点。本实现不提供文件浏览 / 上传 / 改配置（TVBox 有，但它无鉴权）"));
        }
    }

    /// <summary>
    /// 查询参数还原。<c>ParseQuery</c> 不解码（各处理器自己解），遥控这边不解的话
    /// 浏览器 <c>encodeURIComponent</c> 过的播放地址会被原样存下来 —— 台架 E4 就是这么抓出来的。
    /// <para>刻意不做 <c>'+' → 空格</c>（Java URLDecoder 会做）：媒体地址里未编码的 <c>+</c> 很常见，
    /// 按空格处理会把能播的地址改坏。</para>
    /// </summary>
    static string Dec(string? value) =>
        string.IsNullOrEmpty(value) ? "" : Uri.UnescapeDataString(value);

    static Reply? RequireToken(IReadOnlyDictionary<string, string> args)
    {
        var given = args.GetValueOrDefault("token");
        return string.Equals(given, Token, StringComparison.Ordinal)
            ? null
            : new Reply(401, Err("token 不对或缺失"));
    }

    static string Err(string message, string key = "error") =>
        JsonSerializer.Serialize(new Dictionary<string, string> { [key] = message }, Json);
}
