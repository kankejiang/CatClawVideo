using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Js;
using CatClawVideo.Core.Models;
using CatClawVideo.Core.Services;
using Jint;
using Jint.Native;

namespace CatClawVideo.Core.Providers;

/// <summary>
/// TVBox 社区 JS Spider 运行时（Jint）：承载 spider 包为 .js 的 type=3 站点
/// （api 形如 csp_XXX、订阅全局 <c>spider: "./spider.js;md5;..."</c>，含网盘聚合源）。
/// <para>
/// 协议与宿主注入面 1:1 对齐 TVBoxOSC <c>crawler.js.{JsSpider,Global,Connect,Async,Req}</c>：
/// - Spider 对象三种形态：<c>export default {...}</c> / <c>__JS_SPIDER__ = {...}</c> / <c>__jsEvalReturn()</c> 函数式
/// - JS 全局依赖：req/http（net.js 语义）、pd/pdfh/pdfa/pdffl、aesX/rsaX、local、js2Proxy、setTimeout、console
/// - <c>//bb</c> / <c>//DRPY</c> 开头的是 QuickJS 专有字节码，Jint 不支持 → 显式跳过
/// </para>
/// <para>⚠ 所有调用必须在线程池线程上执行（<see cref="CallAsync"/> 内 Task.Run 包裹），
/// 装配期 lock 块内用 GetAwaiter().GetResult()——UI 线程进入会 sync-over-async 死锁（Drpy 同款教训）。</para>
/// </summary>
public class TvBoxJsSpiderRuntime : ISpiderRuntime, ISpiderProxyRuntime
{
    public string Id => "jint-tvbox-js";
    public bool IsSupported => true;

    private readonly IJsRuntimeService _js;
    private readonly string _cacheDir;
    private readonly Func<int>? _proxyPort;
    private readonly Action<string>? _log;
    private readonly SpiderLocalStore _local;

    private sealed class SiteInstance
    {
        public required Engine Engine;
        public required object Lock;
        /// <summary>proxy 回调专用锁：与协议锁分离，避免 playerContent 持锁期间经
        /// 127.0.0.1 回环请求 /proxy 再抢锁造成死锁（DexSpiderRuntime 同款教训）</summary>
        public readonly object ProxyLock = new();
        public bool Initialized;
        public DateTime LastUsed;
        /// <summary>最近一次调用的快照，proxy 回调在无 siteKey 时使用</summary>
        public VodSiteInfo? Site;
    }

    private readonly ConcurrentDictionary<string, SiteInstance> _sites = new();
    private readonly ConcurrentDictionary<string, byte[]> _fileCache = new();
    private SiteInstance? _lastUsed;

    public TvBoxJsSpiderRuntime(IJsRuntimeService js, string cacheDir,
        Func<int>? proxyPort = null, Action<string>? log = null)
    {
        _js = js;
        _cacheDir = cacheDir;
        _proxyPort = proxyPort;
        _log = log;
        _local = new SpiderLocalStore(cacheDir);
        Directory.CreateDirectory(cacheDir);
    }

    private void Log(string msg) => _log?.Invoke("[tvbox-js] " + msg);

    // ═══════════════════ ISpiderRuntime 协议 ═══════════════════

    public Task<string> HomeContentAsync(VodSiteInfo site, CancellationToken ct = default) =>
        CallAsync(site, "__SPIDER__.home(true)", ct);

    public Task<string> CategoryContentAsync(VodSiteInfo site, string tid, string pg, CancellationToken ct = default) =>
        CallAsync(site, $"__SPIDER__.category({JsStr(tid)}, {JsStr(pg)}, false, {{}})", ct);

    public Task<string> DetailContentAsync(VodSiteInfo site, string id, CancellationToken ct = default) =>
        CallAsync(site, $"__SPIDER__.detail({JsStr(id)})", ct);

    public Task<string> SearchContentAsync(VodSiteInfo site, string keyword, string pg, CancellationToken ct = default) =>
        CallAsync(site, $"__SPIDER__.search({JsStr(keyword)}, false, {JsStr(pg)})", ct);

    public Task<string> PlayerContentAsync(VodSiteInfo site, string flag, string id, CancellationToken ct = default) =>
        CallAsync(site, $"__SPIDER__.play({JsStr(flag)}, {JsStr(id)}, [])", ct);

    /// <summary>
    /// 在站点引擎上执行一个 Spider 协议调用表达式，返回 TVBox 协议 JSON 字符串。
    /// JS 方法可能返回 JSON 字符串、对象或 Promise —— 统一等待落定后 stringify 一次
    /// （C# 侧发现双重编码字符串再解包一层，与 Drpy CallAsync 一致）。
    /// </summary>
    private Task<string> CallAsync(VodSiteInfo site, string jsExpr, CancellationToken ct)
        => Task.Run(async () =>
        {
            var inst = await EnsureSiteAsync(site, ct).ConfigureAwait(false);
            lock (inst.Lock)
            {
                inst.LastUsed = DateTime.UtcNow;
                _lastUsed = inst;

                // 补跑到期的 setTimeout 回调（同步桥模型：见 BuildGlueScript 定时器注释）
                try { inst.Engine.Execute("globalThis.__drainTimers&&__drainTimers()"); } catch { }

                var timeout = TimeSpan.FromSeconds(Math.Clamp(site.TimeoutSeconds ?? 60, 10, 300));
                inst.Engine.Execute(
                    $"try{{globalThis.__R=__SPIDER__&&({jsExpr})}}catch(e){{globalThis.__R='{{\"__error\":'+JSON.stringify(String(e&&e.message||e))+'}}'}}");
                var value = inst.Engine.GetValue("__R").UnwrapIfPromise(timeout);
                inst.Engine.SetValue("__R", value);

                var raw = inst.Engine
                    .Evaluate("typeof __R==='string'?__R:JSON.stringify(__R===undefined?null:__R)")
                    .AsString();
                if (raw.Length > 1 && raw[0] == '"')
                {
                    try
                    {
                        raw = JsonDocument.Parse(raw).RootElement.GetString() ?? raw;
                    }
                    catch { }
                }
                if (raw.Length == 0) raw = "{}";
                Log($"call {jsExpr[..Math.Min(jsExpr.Length, 60)]} → {raw.Length}B {raw[..Math.Min(raw.Length, 150)]}");
                return raw;
            }
        }, ct);

    private static string JsStr(string s) =>
        JsonSerializer.Serialize(s ?? "");

    // ═══════════════════ 引擎装配 ═══════════════════

    private async Task<SiteInstance> EnsureSiteAsync(VodSiteInfo site, CancellationToken ct)
    {
        if (_sites.TryGetValue(site.Key, out var ready) && ready.Initialized)
            return ready;

        var inst = _sites.GetOrAdd(site.Key, _ => new SiteInstance
        {
            Engine = _js.CreateEngine(TimeSpan.FromSeconds(Math.Clamp(site.TimeoutSeconds ?? 60, 10, 300))),
            Lock = new object(),
            Site = site,
        });

        lock (inst.Lock)
        {
            if (inst.Initialized) return inst;
            inst.Site = site;

            InstallGlobalHost(inst.Engine, site);

            // 1. spider 包地址：站点 jar（订阅解析时已做站点 jar → 全局 spider 回退与 ./ 补全）
            var spiderUrl = site.Jar;
            if (string.IsNullOrWhiteSpace(spiderUrl) && site.Api.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                spiderUrl = site.Api;
            if (string.IsNullOrWhiteSpace(spiderUrl))
                throw new InvalidOperationException("站点未声明 .js spider 包（spider/jar 字段缺失）");
            spiderUrl = spiderUrl.Split(';')[0];

            // 2. 下载 spider 源码（sha256 磁盘缓存）；lock 块内同步等待（Drpy 同款）
            var content = LoadModuleTextAsync(spiderUrl, ct).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"spider 脚本下载失败: {spiderUrl}");

            // 3. 形态识别（对齐 JsSpider.initializeJS）
            var cat = false;
            string body;
            if (content.StartsWith("//bb", StringComparison.Ordinal) ||
                content.StartsWith("//DRPY", StringComparison.Ordinal))
                throw new NotSupportedException(
                    "该源为 QuickJS 字节码（//bb//DRPY），当前 JS 引擎暂不支持；请换用提供明文脚本的站点");

            if (content.Contains("__JS_SPIDER__"))
                content = Regex.Replace(content, @"__JS_SPIDER__\s*=", "export default ");

            if (content.Contains("__jsEvalReturn") && !content.Contains("export default"))
                cat = true;

            // 4. 装配：主体统一走 WrapModule（自动识别 cjs/tail-default/export-named 等形态并剥壳）
            var baseDir = JsModuleAssembler.BaseDir(spiderUrl);
            inst.Engine.Execute("globalThis.__M={};");

            var imports = JsModuleAssembler.ParseImports(content);
            Log($"spider {site.Key} 依赖 {imports.Count} 个");
            foreach (var (identifier, moduleUrl) in imports)
            {
                var modUrl = ResolveModuleUrl(moduleUrl, baseDir);
                var modId = SanitizeModId(identifier, modUrl);
                // lock 块内不能 await（Drpy 同款）：同步等待下载/缓存
                var js = LoadModuleTextAsync(modUrl, ct).GetAwaiter().GetResult()
                    ?? JsModuleAssembler.EmptyModuleCode;
                if (JsModuleAssembler.IsInvalidModuleContent(js)) js = JsModuleAssembler.EmptyModuleCode;
                var wrapped = JsModuleAssembler.WrapModule(js, modId, out _);
                inst.Engine.Execute(wrapped);
                if (!identifier.StartsWith("_"))
                    inst.Engine.Execute(
                        $"try{{globalThis.{identifier}=globalThis.__M[\"{modId}\"]&&(globalThis.__M[\"{modId}\"][\"default\"]||globalThis.__M[\"{modId}\"])}}catch(e){{}}");
            }

            var wrappedMain = JsModuleAssembler.WrapModule(content, "spider", out _);
            inst.Engine.Execute(wrappedMain);

            if (cat)
            {
                // 函数式源：export function __jsEvalReturn(){...} → 调用得到对象
                inst.Engine.Execute("""
                    try{
                      var f = globalThis.__M["spider"]["__jsEvalReturn"];
                      globalThis.__SPIDER__ = typeof f==='function' ? f() : f;
                      if(globalThis.__SPIDER__) globalThis.__SPIDER__.is_cat = true;
                    }catch(e){ globalThis.__SPIDER__ = null; }
                    """);
            }
            else
            {
                inst.Engine.Execute("""
                    try{
                      var m = globalThis.__M["spider"];
                      var d = m && (m["default"] !== undefined ? m["default"] : m);
                      globalThis.__SPIDER__ = typeof d==='function' ? d() : d;
                    }catch(e){ globalThis.__SPIDER__ = null; }
                    """);
            }

            var ready0 = inst.Engine.Evaluate(
                "typeof __SPIDER__==='object' && __SPIDER__!==null && typeof __SPIDER__.home==='function'").AsBoolean();
            if (!ready0)
                throw new InvalidOperationException("JS Spider 装配失败（home 协议缺失）");

            // 5. init(ext)：JSON 可解析传对象、否则传字符串（对齐 Json.valid 分支）；cat 模式传 cfg
            var ext = site.Ext ?? "";
            var extExpr = IsValidJson(ext) ? ext : JsStr(ext);
            if (cat)
            {
                var cfg = JsonSerializer.Serialize(new { stype = 3, skey = site.Key, ext });
                inst.Engine.Execute(
                    $"try{{__SPIDER__.init({cfg})}}catch(e){{__hostLog('init: '+e)}}");
            }
            else
            {
                // 部分源不实现 init：守护后再调（真机实测无 init 的源会 TypeError）
                inst.Engine.Execute(
                    $"try{{__SPIDER__.init&&__SPIDER__.init({extExpr})}}catch(e){{__hostLog('init: '+e)}}");
            }

            Log($"站点 {site.Key} 装配完成");
            inst.Initialized = true;
            return inst;
        }
    }

    private static string SanitizeModId(string identifier, string url)
    {
        if (!string.IsNullOrEmpty(identifier) && Regex.IsMatch(identifier, @"^[\w$]+$") && !identifier.StartsWith("_"))
            return identifier;
        // 标识符非法或匿名 import（"_" 占位）：用 URL 哈希做稳定模块名
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..12].ToLowerInvariant();
        return "m" + hash;
    }

    private static bool IsValidJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if (t[0] is not ('{' or '[')) return false;
        try { JsonDocument.Parse(t); return true; }
        catch { return false; }
    }

    // ═══════════════════ ISpiderProxyRuntime（js2Proxy 回环代理回调） ═══════════════════

    public Task<(int Status, string Mime, byte[]? Body)?> ProxyAsync(
        IReadOnlyDictionary<string, string> query, CancellationToken ct = default)
    {
        var inst = ResolveInstance(query);
        if (inst is null) return Task.FromResult<(int, string, byte[]?)?>(null);

        return Task.Run(async () =>
        {
            var taken = Monitor.TryEnter(inst.ProxyLock, TimeSpan.FromSeconds(30));
            if (!taken) return null;
            try
            {
                inst.LastUsed = DateTime.UtcNow;
                var site = inst.Site;
                var timeout = TimeSpan.FromSeconds(30);
                string json;
                var fromCatvod = query.GetValueOrDefault("from") == "catvod";

                if (fromCatvod && site is not null)
                {
                    // proxy2 语义：url 按 "/" 拆数组 + header JSON 对象（对齐 JsSpider.proxyLocal）
                    var url = query.GetValueOrDefault("url") ?? "";
                    var headerJson = query.GetValueOrDefault("header") ?? "{}";
                    var urlParts = string.Join(",", url.Split('/').Select(p => JsonSerializer.Serialize(p)));
                    var headerExpr = IsValidJson(headerJson) ? headerJson : JsonSerializer.Serialize(headerJson);
                    inst.Engine.Execute(
                        $"try{{globalThis.__PR=__SPIDER__.proxy([{urlParts}], ({headerExpr}))}}catch(e){{globalThis.__PR=''}}");
                    var v = inst.Engine.GetValue("__PR").UnwrapIfPromise(timeout);
                    inst.Engine.SetValue("__PR", v);
                    json = inst.Engine.Evaluate("typeof __PR==='string'?__PR:JSON.stringify(__PR)").AsString();
                    if (string.IsNullOrEmpty(json)) return null;
                    return ResToTuple(json);
                }
                else
                {
                    // proxy1 语义：query 对象直传，返回数组 [status, mime, body, header?, base64Flag?]
                    var pairs = string.Join(",", query.Select(kv =>
                        $"{JsonSerializer.Serialize(kv.Key)}:{JsonSerializer.Serialize(kv.Value)}"));
                    inst.Engine.Execute(
                        $"try{{globalThis.__PR=__SPIDER__.proxy({{{pairs}}})}}catch(e){{globalThis.__PR=''}}");
                    var v = inst.Engine.GetValue("__PR").UnwrapIfPromise(timeout);
                    inst.Engine.SetValue("__PR", v);
                    json = inst.Engine.Evaluate("typeof __PR==='string'?__PR:JSON.stringify(__PR)").AsString();
                    if (string.IsNullOrEmpty(json) || json == "null") return null;
                    return ProxyArrayToTuple(json);
                }
            }
            catch (Exception ex)
            {
                Log($"proxy 处理异常: {ex.Message}");
                return null;
            }
            finally
            {
                Monitor.Exit(inst.ProxyLock);
            }
        }, ct);
    }

    private SiteInstance? ResolveInstance(IReadOnlyDictionary<string, string> query)
    {
        var key = query.GetValueOrDefault("siteKey");
        if (!string.IsNullOrEmpty(key) && _sites.TryGetValue(key, out var inst) && inst.Initialized)
            return inst;
        return _lastUsed is { Initialized: true } ? _lastUsed : null;
    }

    /// <summary>proxy2 返回的 Res JSON {content, contentType, buffer} → (status, mime, bytes)。</summary>
    private static (int, string, byte[]?)? ResToTuple(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var content = root.TryGetProperty("content", out var c) ? c.ToString() : "";
            var mime = root.TryGetProperty("contentType", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()! : "application/octet-stream";
            var buffer = root.TryGetProperty("buffer", out var b) && b.TryGetInt32(out var bi) ? bi : 0;
            byte[]? body = buffer == 2
                ? Convert.FromBase64String(content.Contains("base64,")
                    ? content[(content.IndexOf("base64,", StringComparison.Ordinal) + 7)..]
                    : content)
                : Encoding.UTF8.GetBytes(content);
            return (200, mime, body);
        }
        catch { return null; }
    }

    /// <summary>proxy1 返回的数组 [status, mime, body, header?, base64Flag?] → (status, mime, bytes)。</summary>
    private static (int, string, byte[]?)? ProxyArrayToTuple(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var arr = doc.RootElement;
            var status = arr.GetArrayLength() > 0 && arr[0].TryGetInt32(out var st) ? st : 200;
            var mime = arr.GetArrayLength() > 1 && arr[1].ValueKind == JsonValueKind.String
                ? arr[1].GetString()! : "application/octet-stream";
            var bodyRaw = arr.GetArrayLength() > 2 ? arr[2].ToString() : "";
            var isB64 = arr.GetArrayLength() > 4 && arr[4].TryGetInt32(out var f) && f == 1;
            if (bodyRaw is null) return (status, mime, null);
            byte[]? body = isB64
                ? Convert.FromBase64String(bodyRaw.Contains("base64,")
                    ? bodyRaw[(bodyRaw.IndexOf("base64,", StringComparison.Ordinal) + 7)..]
                    : bodyRaw)
                : Encoding.UTF8.GetBytes(bodyRaw);
            return (status, mime, body);
        }
        catch { return null; }
    }

    // ═══════════════════ 宿主桥注入（Global 注入面） ═══════════════════

    private void InstallGlobalHost(Engine engine, VodSiteInfo site)
    {
        engine.SetValue("__hostLog", new Action<string>(m => Log(m)));
        engine.SetValue("__httpReq", new Func<string, string, string>((u, o) =>
            JsonSerializer.Serialize(BridgeResult(u, o))));
        engine.SetValue("__pdfh", new Func<string, string, string>((h, r) => SpiderDomParser.Pdfh(h, r)));
        engine.SetValue("__pd", new Func<string, string, string>((h, r) =>
            SpiderDomParser.Pdfh(h, r, "")));
        engine.SetValue("__pdfa", new Func<string, string, string>((h, r) =>
            JsonSerializer.Serialize(SpiderDomParser.Pdfa(h, r))));
        engine.SetValue("__pdfl", new Func<string, string, string, string, string, string>((h, p, t, u, a) =>
            JsonSerializer.Serialize(SpiderDomParser.Pdffl(h, p, t, u, a))));
        engine.SetValue("__joinUrl", new Func<string, string, string>((b, p) =>
        {
            if (string.IsNullOrEmpty(b)) return p ?? "";
            if (string.IsNullOrEmpty(p)) return b;
            if (p.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return p;
            try { return new Uri(new Uri(b), p).ToString(); } catch { return p; }
        }));
        engine.SetValue("__aesX", new Func<string, bool, string, bool, string, string?, bool, string>(
            (m, e, i, ib, k, iv, ob) => SpiderCrypto.AesX(m, e, i, ib, k, iv, ob)));
        engine.SetValue("__rsaX", new Func<bool, bool, string, bool, string, bool, string>(
            (p, e, i, ib, k, ob) => SpiderCrypto.RsaX(p, e, i, ib, k, ob)));
        engine.SetValue("__rsaEnc", new Func<string, string, string?, string>((d, k, o) =>
            SpiderCrypto.RsaEncrypt(d, k, ParseJsonOpt(o))));
        engine.SetValue("__rsaDec", new Func<string, string, string?, string>((d, k, o) =>
            SpiderCrypto.RsaDecrypt(d, k, ParseJsonOpt(o))));
        engine.SetValue("__getProxy", new Func<bool, string>(local =>
        {
            var port = _proxyPort?.Invoke() ?? 9978;
            return $"http://127.0.0.1:{port}/proxy?do=js" + (local ? "" : "&host=external");
        }));
        engine.SetValue("__js2Proxy", new Func<JsValue?, JsValue?, string, string, JsValue?, string>((dyn, st, sk, url, hs) =>
        {
            var port = _proxyPort?.Invoke() ?? 9978;
            var headerJson = hs is null || hs.IsUndefined() || hs.IsNull() ? "{}" : hs.ToString();
            var local = dyn is null || dyn.IsUndefined() || (dyn.IsBoolean() && !dyn.AsBoolean());
            var hostPart = local ? "127.0.0.1" : GetLanHost();
            return $"http://{hostPart}:{port}/proxy?do=js&from=catvod&siteType={Uri.EscapeDataString(st?.IsUndefined() == false ? st.ToString() : "0")}" +
                   $"&siteKey={Uri.EscapeDataString(sk)}&header={Uri.EscapeDataString(headerJson)}&url={Uri.EscapeDataString(url)}";
        }));
        engine.SetValue("__localGet", new Func<string, string, string>((r, k) => _local.Get(site.Key, r, k)));
        engine.SetValue("__localSet", new Action<string, string, string>((r, k, v) => _local.Set(site.Key, r, k, v)));
        engine.SetValue("__localDel", new Action<string, string>((r, k) => _local.Delete(site.Key, r, k)));

        // JS 胶水层：把宿主原语装配成 TVBox 源依赖的全局 API
        engine.Execute(BuildGlueScript(site));
    }

    /// <summary>host 回调的 options 解析中间结果（__parseHeaderRaw 的载体）。</summary>
    private static JsonElement? ParseJsonOpt(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch { return null; }
    }

    private static string GetLanHost()
    {
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                    if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        return addr.Address.ToString();
            }
        }
        catch { }
        return "127.0.0.1";
    }

    /// <summary>宿主 HTTP 结果 → {content, contentB64, headers, status, ok} JSON（buffer=1 时带 contentB64）。</summary>
    private static object BridgeResult(string url, string optionsJson)
    {
        var opt = SpiderHttpBridge.ParseOptions(optionsJson);
        var resp = SpiderHttpBridge.Request(url, opt);
        var headersJson = JsonSerializer.Serialize(resp.Headers);
        return resp.Buffer == 1
            ? new { content = "", contentB64 = Convert.ToBase64String(resp.ContentBytes ?? Array.Empty<byte>()), headers = resp.Headers, status = resp.Status, ok = resp.Ok }
            : new { content = resp.Content, contentB64 = (string?)null, headers = resp.Headers, status = resp.Status, ok = resp.Ok };
    }

    /// <summary>
    /// JS 胶水：把宿主原语装配成 TVBox 源依赖的全局 API（对齐 net.js + Global.java 注入面）。
    /// <c>__SITEKEY__</c> 由宿主按站点替换，local 键控与 SpiderLocalStore 三段键对应。
    /// 注意：脚本大括号密集，用普通 raw string + Replace，不用内插字符串。
    /// </summary>
    private const string GlueScript = """
            var console={log:function(m){__hostLog(String(m))},info:function(m){__hostLog(String(m))},warn:function(m){__hostLog(String(m))},error:function(m){__hostLog(String(m))},debug:function(m){__hostLog(String(m))}};
            var print=function(m){__hostLog(String(m))};
            var log=print;
            var window=globalThis, self=globalThis, global=globalThis;
            var navigator={userAgent:'Mozilla/5.0 (Linux; Android 12) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Mobile Safari/537.36'};
            var location={href:''};
            globalThis.SITEKEY=__SITEKEY__;
            // ── base64 → 字节数组（纯 JS，buffer=1 用） ──
            var __B64CH='ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
            globalThis.__b64ToBytes=function(s){
              s=String(s).replace(/[-_]/g,function(c){return c==='-'?'+':'/'}).replace(/[^A-Za-z0-9+\/=]/g,'');
              var out=[],bits=0,acc=0;
              for(var i=0;i<s.length;i++){var c=__B64CH.indexOf(s.charAt(i));if(c<0)continue;
                if(c===64)break;acc=(acc<<6)|c;bits+=6;if(bits>=8){bits-=8;out.push((acc>>bits)&0xFF);}}
              return out;
            };
            globalThis.atob=function(s){return __b64ToBytes(s).map(function(x){return String.fromCharCode(x)}).join('')};
            globalThis.btoa=function(s){return ''}; // 少用，占位
            // ── HTTP：net.js 契约（req 同步态 / http Promise 态） ──
            globalThis.req=function(url,obj){
              obj=obj||{};
              var raw=__httpReq(String(url),JSON.stringify(obj));
              var o;try{o=JSON.parse(raw)}catch(e){o={content:'',headers:{},ok:false,status:0}}
              var h=o.headers||{};
              if(o.contentB64!==undefined&&o.contentB64!==null)
                return {content:__b64ToBytes(o.contentB64),headers:h,status:o.status||0,ok:!!o.ok};
              return {content:o.content===undefined?'':o.content,headers:h,status:o.status||0,ok:!!o.ok};
            };
            globalThis.http=function(url,options){
              options=options||{};
              if(options.async===false) return req(url,options);
              return new Promise(function(resolve){var r=req(url,options);resolve(r);});
            };
            globalThis.fetch=function(u,o){var r=req(u,o||{});return typeof r.content==='string'?r.content:''};
            // ── DOM 规则解析 ──
            globalThis.pdfh=function(h,r){return __pdfh(String(h),String(r))};
            globalThis.pd=function(h,r,u){return __pd(String(h),String(r),u===undefined?'':String(u))};
            globalThis.pdfa=function(h,r){return JSON.parse(__pdfa(String(h),String(r)))};
            globalThis.pdfla=function(h,p,t,u,a){return JSON.parse(__pdfl(String(h),String(p),String(t),String(u),String(a)))};
            globalThis.pdfl=globalThis.pdfla;
            globalThis.joinUrl=function(b,p){return __joinUrl(String(b),String(p))};
            // ── 加解密 ──
            globalThis.aesX=function(m,e,i,ib,k,iv,ob){return __aesX(String(m),!!e,String(i),!!ib,String(k),iv===undefined?null:String(iv),!!ob)};
            globalThis.rsaX=function(p,e,i,ib,k,ob){return __rsaX(!!p,!!e,String(i),!!ib,String(k),!!ob)};
            globalThis.rsaEncrypt=function(d,k,o){return __rsaEnc(String(d),String(k),o===undefined?null:JSON.stringify(o))};
            globalThis.rsaDecrypt=function(d,k,o){return __rsaDec(String(d),String(k),o===undefined?null:JSON.stringify(o))};
            // ── 本地代理 ──
            globalThis.getProxy=function(local){return __getProxy(local===true)};
            globalThis.js2Proxy=function(dynamic,siteType,siteKey,url,headers){
              return __js2Proxy(dynamic===undefined?null:dynamic,
                                siteType===undefined?null:siteType,
                                siteKey===undefined?SITEKEY:String(siteKey),
                                String(url),
                                headers===undefined?null:headers);
            };
            // ── 本地 KV ──
            globalThis.local={
              get:function(a,b){return __localGet(SITEKEY,String(a),String(b))},
              set:function(a,b,v){__localSet(SITEKEY,String(a),String(b),String(v===undefined?'':v))},
              delete:function(a,b){__localDel(SITEKEY,String(a),String(b))}
            };
            // ── 定时器：同步桥下无法跨线程入队，回调在「下一次协议调用」开始时补跑
            //    （对绝大多数源够用：setTimeout 多用于延迟初始化；严格定时依赖型源属阶段4范围）
            var __tmrSeq=0;
            globalThis.__tmrs={};
            globalThis.__drainTimers=function(){var now=Date.now();for(var id in globalThis.__tmrs){var t=globalThis.__tmrs[id];if(t&&t.due<=now){delete globalThis.__tmrs[id];try{t.fn()}catch(e){}}}};
            globalThis.setTimeout=function(fn,delay){var id=++__tmrSeq;globalThis.__tmrs[id]={fn:fn,due:Date.now()+(Number(delay)||0)};return id;};
            globalThis.clearTimeout=function(id){delete globalThis.__tmrs[id];};
            globalThis.setInterval=function(){return 0};
            globalThis.clearInterval=function(){};
            globalThis.s2t=function(s){return String(s===undefined?'':s)};
            globalThis.t2s=function(s){return String(s===undefined?'':s)};
            globalThis.require=function(){return {}};
            globalThis.alert=function(){};
            var module={exports:{}};
            """;

    private static string BuildGlueScript(VodSiteInfo site) =>
        GlueScript.Replace("__SITEKEY__", JsonSerializer.Serialize(site.Key));

    // ═══════════════════ 模块下载与缓存 ═══════════════════

    private static string ResolveModuleUrl(string moduleUrl, string baseDir) =>
        moduleUrl switch
        {
            var u when u.StartsWith("./", StringComparison.Ordinal) => JsModuleAssembler.Join(baseDir, u[2..]),
            var u when u.StartsWith("http", StringComparison.OrdinalIgnoreCase) => u,
            var u when u.StartsWith("../", StringComparison.Ordinal) =>
                JsModuleAssembler.Join(JsModuleAssembler.BaseDir(baseDir.TrimEnd('/')), u[3..]),
            _ => JsModuleAssembler.Join(baseDir, moduleUrl),
        };

    /// <summary>模块/脚本下载（sha256 磁盘缓存），失败返回 null（调用方决定 stub 或抛错）。</summary>
    private async Task<string?> LoadModuleTextAsync(string url, CancellationToken ct)
    {
        var key = Sha256(url);
        if (_fileCache.TryGetValue(key, out var cached))
            return Encoding.UTF8.GetString(cached);

        var localPath = Path.Combine(_cacheDir, key + ".js");
        string text;
        if (File.Exists(localPath))
        {
            text = await File.ReadAllTextAsync(localPath, ct);
        }
        else
        {
            var opt = new SpiderReqOptions { Timeout = 20000, Buffer = 0 };
            var resp = await Task.Run(() => SpiderHttpBridge.Request(url, opt), ct);
            if (!resp.Ok || string.IsNullOrEmpty(resp.Content))
            {
                Log($"下载失败 {url}");
                return null;
            }
            text = resp.Content;
            await File.WriteAllTextAsync(localPath, text, ct);
        }
        _fileCache[key] = Encoding.UTF8.GetBytes(text);
        return text;
    }

    private static string StripHeadImports(string js) =>
        Regex.Replace(js, @"^(?:\s*import\s*[^;\n]+?;)+\s*", "");

    private static string Sha256(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes)[..24].ToLowerInvariant();
    }
}
