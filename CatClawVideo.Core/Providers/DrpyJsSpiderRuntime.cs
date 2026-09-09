using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;
using Jint;
using Jint.Native;

namespace CatClawVideo.Core.Providers;

/// <summary>
/// drpy2 JS 爬虫运行时（Jint）：承载 api=drpy2.min.js、ext=规则脚本 的 TVBox type=3 站点。
/// <para>
/// 引擎宿主契约（实测 drpy2.min.js 依赖面）：
/// - fetch(url, options) → 响应文本（引擎 request() 底层为宿主 req）
/// - req(url, obj) → {content, headers}；相对路径需宿主/包装层用引擎内 joinUrl(HOST,url) 补全
/// - joinUrl(base, path) → 绝对 URL（引擎 init 内直接使用，非引擎自带）
/// - local.{get,set,delete}(rkey, key[, val]) → KV 存储
/// - print/log → 日志；pdfh/pdfa/pd/require 占位
/// </para>
/// <para>
/// drpy2.min.js 是 ESM 打包产物，Jint 不支持 import/export——加载前做受控剥壳
/// （只剥文件头部 import 块与 export 语句），各模块 IIFE 隔离后按 import 顺序装配。
/// </para>
/// </summary>
public class DrpyJsSpiderRuntime : ISpiderRuntime
{
    public string Id => "jint-drpy";
    public bool IsSupported => true;

    private readonly IJsRuntimeService _js;
    private readonly HttpClient _http;
    private readonly Action<string>? _log;

    /// <summary>站点实例：每个站点独立引擎（rule 状态隔离）</summary>
    private sealed class SiteInstance
    {
        public required Engine Engine;
        public required object Lock;
        public bool Initialized;
    }

    private readonly ConcurrentDictionary<string, SiteInstance> _sites = new();
    private readonly ConcurrentDictionary<string, byte[]> _fileCache = new();
    private readonly string _cacheDir;

    public DrpyJsSpiderRuntime(IJsRuntimeService js, string cacheDir, Action<string>? log = null)
    {
        _js = js;
        _cacheDir = cacheDir;
        _log = log;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Linux; Android 12) Chrome/124.0 Mobile Safari/537.36");
        Directory.CreateDirectory(cacheDir);
    }

    private void Log(string msg) => _log?.Invoke("[drpy2] " + msg);

    // ═══════════════════ ISpiderRuntime 协议 ═══════════════════

    public Task<string> HomeContentAsync(VodSiteInfo site, CancellationToken ct = default) =>
        CallAsync(site, "home", "1", ct);

    public Task<string> CategoryContentAsync(VodSiteInfo site, string tid, string pg, CancellationToken ct = default) =>
        CallAsync(site, "category", JsStr(tid), JsStr(pg), "0", "{}", ct);

    public Task<string> DetailContentAsync(VodSiteInfo site, string id, CancellationToken ct = default) =>
        CallAsync(site, "detail", JsStr(id), ct);

    public Task<string> SearchContentAsync(VodSiteInfo site, string keyword, string pg, CancellationToken ct = default) =>
        CallAsync(site, "search", JsStr(keyword), "0", JsStr(pg), ct);

    public Task<string> PlayerContentAsync(VodSiteInfo site, string flag, string id, CancellationToken ct = default) =>
        CallAsync(site, "play", JsStr(flag), JsStr(id), "[]", ct);

    /// <summary>在站点引擎上串行调用协议方法，返回 JSON 字符串</summary>
    private Task<string> CallAsync(VodSiteInfo site, string method, params object[] argsAndCt)
    {
        var ct = argsAndCt[^1] is CancellationToken token ? token : default;
        var args = string.Join(",", argsAndCt.Take(argsAndCt.Length - 1));
        return CallAsync(site, method, args, ct);
    }

    private async Task<string> CallAsync(VodSiteInfo site, string method, string argsExpr, CancellationToken ct)
    {
        var inst = await EnsureSiteAsync(site, ct);
        lock (inst.Lock)
        {
            // drpy2 协议方法有的返回对象、有的已返回 JSON 文本（如 home 部分引擎版本）；
            // 统一 stringify 一次，C# 侧若发现是双重编码字符串则解包一层。
            var raw = inst.Engine.Evaluate(
                $"try{{JSON.stringify(globalThis.drpy.{method}({argsExpr}))}}catch(e){{'{{\"__error\":'+JSON.stringify(String(e&&e.message||e))+'}}'}}").AsString();
            if (raw.Length > 1 && raw[0] == '"')
            {
                try
                {
                    raw = System.Text.Json.JsonDocument.Parse(raw).RootElement.GetString() ?? raw;
                }
                catch { }
            }
            return raw;
        }
    }

    private static string JsStr(string s) =>
        System.Text.Json.JsonSerializer.Serialize(s ?? "");

    // ═══════════════════ 引擎装配 ═══════════════════

    /// <summary>确保站点引擎已创建并 init（下载引擎/依赖 → 剥壳装配 → init(站点脚本)）</summary>
    private async Task<SiteInstance> EnsureSiteAsync(VodSiteInfo site, CancellationToken ct)
    {
        if (_sites.TryGetValue(site.Key, out var ready) && ready.Initialized)
            return ready;

        var inst = _sites.GetOrAdd(site.Key, _ => new SiteInstance
        { Engine = _js.CreateEngine(TimeSpan.FromSeconds(60)), Lock = new object() });

        lock (inst.Lock)
        {
            if (inst.Initialized) return inst;

            // 1. 宿主桥（全局）
            InstallGlobalHost(inst.Engine, site);

            // 2. 下载 drpy2 引擎脚本（site.Api）并解析依赖
            var engineJs = DownloadTextAsync(site.Api, ct).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"drpy2 引擎脚本下载失败: {site.Api}");
            var imports = ParseImports(engineJs);
            Log($"引擎 {imports.Count} 个依赖");

            // 3. 按 import 顺序装配依赖
            var engineBaseDir = BaseDir(site.Api);
            inst.Engine.Execute("globalThis.__M={};");
            foreach (var (identifier, moduleUrl) in imports)
            {
                var js = LoadModule(moduleUrl, engineBaseDir, ct).GetAwaiter().GetResult();
                var wrapped = WrapModule(js, identifier, out var mode);
                inst.Engine.Execute(wrapped);
                BindModuleGlobals(inst.Engine, identifier, mode);
            }

            // 4. 引擎主体（剥壳 + 宿主变量注入 + IIFE）
            var engineBody = StripHeadImports(engineJs);
            engineBody = engineBody.Replace(
                "var fetch;var print;var log;",
                "var fetch=globalThis.__host.fetch,print=globalThis.__host.print,log=globalThis.__host.log,local=globalThis.__host.local;");
            engineBody = StripDefaultExportTail(engineBody, "drpy");
            var engineWrap = new StringBuilder();
            engineWrap.Append("globalThis.__M[\"drpy\"]=globalThis.__M[\"drpy\"]||{};(function(){\n");
            engineWrap.Append("var fetch=globalThis.__host.fetch,print=globalThis.__host.print,log=globalThis.__host.log,local=globalThis.__host.local;\n");
            engineWrap.Append("var req=function(u,o){try{if(!/^https?:/i.test(String(u))&&typeof HOST!=='undefined'&&HOST){u=joinUrl(HOST,u)}}catch(e){}return globalThis.__host.req(u,o)};\n");
            engineWrap.Append(engineBody).Append("\n})();");
            inst.Engine.Execute(engineWrap.ToString());
            inst.Engine.Execute("globalThis.drpy=globalThis.__M['drpy']&&(globalThis.__M['drpy']['default']||globalThis.__M['drpy']);");

            var ready0 = inst.Engine.Evaluate(
                "typeof globalThis.drpy==='object'&&typeof globalThis.drpy.home==='function'").AsBoolean();
            if (!ready0)
                throw new InvalidOperationException("drpy2 引擎装配失败（home 协议缺失）");

            // 5. init(站点脚本 URL)：引擎内部 request 拉规则脚本并 eval
            var extExpr = JsStr(string.IsNullOrEmpty(site.Ext) ? site.Api : site.Ext);
            inst.Engine.Evaluate(
                $"try{{globalThis.drpy.init({extExpr})}}catch(e){{globalThis.__host.print('init: '+e)}}");

            var title = inst.Engine.Evaluate(
                "try{String(globalThis.drpy.getRule&&globalThis.drpy.getRule().title||'')}catch(e){''}").AsString();
            Log($"站点 {site.Key} init 完成: {title}");
            inst.Initialized = true;
            return inst;
        }
    }

    // ── 宿主桥 ──
    private void InstallGlobalHost(Engine engine, VodSiteInfo site)
    {
        engine.SetValue("__httpFetch", new Func<string, JsValue, string>((u, o) => HttpFetch(u, o)));
        engine.SetValue("__joinUrl", new Func<string, string, string>((b, p) =>
        {
            if (string.IsNullOrEmpty(b)) return p ?? "";
            if (string.IsNullOrEmpty(p)) return b;
            if (p.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return p;
            try { return new Uri(new Uri(b), p).ToString(); } catch { return p; }
        }));
        engine.SetValue("__hostLog", new Action<string>(m => Log(m)));

        engine.Execute("""
            var console={log:function(m){__hostLog(String(m))},info:function(m){__hostLog(String(m))},warn:function(m){__hostLog(String(m))},error:function(m){__hostLog(String(m))},debug:function(m){__hostLog(String(m))}};
            var print=function(m){__hostLog(String(m))};
            var log=print;
            var window=globalThis, self=globalThis, global=globalThis;
            var navigator={userAgent:'Mozilla/5.0 (Linux; Android 12) Chrome/124.0 Mobile Safari/537.36'};
            var __localStore={};
            var local={
              set:function(k,i,v){__localStore[k+'|'+i]=String(v);},
              get:function(k,i){var s=__localStore[k+'|'+i];return s===undefined?'':s;},
              delete:function(k,i){delete __localStore[k+'|'+i];}
            };
            var module={exports:{}};
            globalThis.pdfh=function(){return ''};
            globalThis.pdfa=function(){return []};
            globalThis.pd=function(){return ''};
            globalThis.require=function(){return {}};
            globalThis.fetch=function(u,o){return __httpFetch(u,o===undefined?null:o)};
            // drpy2 引擎的 request() 底层调宿主 req(url,obj)；req 在引擎闭包内包装（joinUrl 补全相对路径）
            globalThis.req=function(url,obj){
              obj=obj||{};
              var res=fetch(url,obj);
              return {content:res===undefined?'':res, headers:{}};
            };
            globalThis.joinUrl=function(b,p){return __joinUrl(b,p)};
            globalThis.__host={
              fetch:function(u,o){return fetch(u,o===undefined?null:o)},
              print:print, log:log, local:local, req:req
            };
            globalThis.__M={};
        """);
    }

    /// <summary>HTTP GET/POST（同步，Jint 调用线程内完成）</summary>
    private string HttpFetch(string url, JsValue? opts)
    {
        try
        {
            var method = "GET";
            string? body = null;
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (opts is not null && opts.IsObject())
            {
                var o = opts.AsObject();
                if (o.TryGetValue("method", out var m) && m.IsString())
                    method = m.AsString().ToUpperInvariant();
                if (o.TryGetValue("headers", out var h) && h.IsObject())
                    foreach (var p in h.AsObject().GetOwnProperties())
                        req.Headers.TryAddWithoutValidation(p.Key.ToString(), p.Value.Value.AsString());
                if (o.TryGetValue("body", out var b) && b.IsString())
                    body = b.AsString();
            }
            req.Method = new HttpMethod(method);
            if (body is not null)
                req.Content = new StringContent(body, Encoding.UTF8);
            using var resp = _http.SendAsync(req, CancellationToken.None).GetAwaiter().GetResult();
            var text = resp.Content.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult();
            Log($"{method} {url[..Math.Min(url.Length, 80)]} -> {(int)resp.StatusCode} len={text.Length}");
            return text;
        }
        catch (Exception ex)
        {
            Log($"fetch {url} 失败: {ex.Message}");
            return "";
        }
    }

    // ── 模块解析 / 下载 / 剥壳 ──
    private sealed record ImportDecl(string Identifier, string Url);

    /// <summary>解析 drpy2 引擎头部 import 块 → (标识符, 模块 URL) 列表（保序）</summary>
    private static List<ImportDecl> ParseImports(string engineJs)
    {
        var list = new List<ImportDecl>();
        var head = engineJs.Length > 8000 ? engineJs[..8000] : engineJs;
        var matches = Regex.Matches(head, @"import\s*(?:([\w$]+)\s+from\s*|\{([\w$]+)\s*\}\s*from\s*)?[""']([^""']+)[""']");
        foreach (Match m in matches)
        {
            var ident = m.Groups[1].Success ? m.Groups[1].Value
                      : m.Groups[2].Success ? m.Groups[2].Value
                      : "_" + list.Count;
            list.Add(new ImportDecl(ident, m.Groups[3].Value));
        }
        return list;
    }

    private static string BaseDir(string url)
    {
        var i = url.LastIndexOf('/');
        return i > 0 ? url[..(i + 1)] : url;
    }

    /// <summary>模块 URL → 脚本文本（assets:// → dr_py 仓库 libs；相对路径 → 引擎同目录；本地磁盘缓存）</summary>
    private async Task<string> LoadModule(string moduleUrl, string engineBaseDir, CancellationToken ct)
    {
        var resolved = ResolveModuleUrl(moduleUrl, engineBaseDir);
        var key = Sha256(resolved);
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
            text = await DownloadTextAsync(resolved, ct)
                ?? throw new InvalidOperationException($"drpy2 依赖下载失败: {resolved}");
            await File.WriteAllTextAsync(localPath, text, ct);
        }
        _fileCache[key] = Encoding.UTF8.GetBytes(text);
        return text;
    }

    private static string ResolveModuleUrl(string moduleUrl, string engineBaseDir) =>
        moduleUrl switch
        {
            // assets:// → dr_py 仓库 libs（raw 直连常被墙/502，走订阅源同款 gh 代理域名）
            var u when u.StartsWith("assets://js/lib/", StringComparison.OrdinalIgnoreCase)
                => "https://git.yylx.win/https://raw.githubusercontent.com/hjdhnx/dr_py/main/libs/" + u["assets://js/lib/".Length..],
            var u when u.StartsWith("./", StringComparison.Ordinal) => Join(engineBaseDir, u[2..]),
            var u when u.StartsWith("http", StringComparison.OrdinalIgnoreCase) => u,
            _ => Join(engineBaseDir, moduleUrl),
        };

    private static string Join(string baseDir, string file) =>
        baseDir.EndsWith("/") ? baseDir + file : baseDir + "/" + file;

    private static string Sha256(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes)[..24].ToLowerInvariant();
    }

    /// <summary>模块剥壳 + IIFE 包装。mode: plain/cjs/tail-named/tail-default/export-named</summary>
    private string WrapModule(string js, string modId, out string mode)
    {
        mode = DetectModuleMode(js, modId);
        var pending = new List<string>();
        js = StripHeadImports(js);

        switch (mode)
        {
            case "cjs":
                js = "var module={exports:{}};(function(module,exports){" + js + "\n})(module,module.exports);" +
                     $"globalThis.__M[\"{modId}\"]=module.exports;";
                break;
            case "tail-named":
                js = StripNamedExportTail(js, modId);
                break;
            case "tail-default":
                js = StripDefaultExportTail(js, modId);
                break;
            case "export-named":
                foreach (var name in Regex.Matches(js, @"export\s+(?:function|const|let|class|var)\s+([\w$]+)")
                             .Cast<Match>().Select(m => m.Groups[1].Value))
                    pending.Add(name);
                js = Regex.Replace(js, @"export\s+(function|const|let|class|var)\s+", "$1 ");
                break;
        }

        var sb = new StringBuilder();
        sb.Append($"globalThis.__M[\"{modId}\"]=globalThis.__M[\"{modId}\"]||{{}};(function(){{\n");
        sb.Append(js).Append('\n');
        foreach (var name in pending)
            sb.Append($"globalThis.__M[\"{modId}\"][\"{name}\"]={name};\n");
        sb.Append("})();");
        return sb.ToString();
    }

    private static string DetectModuleMode(string js, string modId)
    {
        if (Regex.IsMatch(js, @"module\.exports")) return "cjs";
        var lastExport = js.LastIndexOf("export", StringComparison.Ordinal);
        if (lastExport >= 0)
        {
            var rest = js[(lastExport + 6)..].TrimStart();
            if (rest.StartsWith("{") && js[lastExport..].Contains(" as ")) return "tail-named";
            if (rest.StartsWith("default")) return "tail-default";
        }
        if (Regex.IsMatch(js, @"export\s+(?:function|const|let|class|var)\s")) return "export-named";
        return "plain";
    }

    // ── 剥壳工具（与 spike 一致：只动头部 import 块与 export 语句）──
    private static string StripHeadImports(string js) =>
        Regex.Replace(js, @"^(?:\s*import\s*[^;\n]+?;)+\s*", "");

    private static string StripNamedExportTail(string js, string modId)
    {
        var i = js.LastIndexOf("export", StringComparison.Ordinal);
        if (i < 0 || i + 6 >= js.Length) return js;
        var rest = js[(i + 6)..].TrimStart();
        if (!rest.StartsWith("{")) return js;
        var end = js.IndexOf('}', i);
        if (end < 0) return js;
        var braceStart = i + 6 + (rest.Length - rest.TrimStart().Length);
        var inner = js[(braceStart + 1)..end].Trim();
        inner = Regex.Replace(inner, @"([\w$]+)\s+as\s+([\w$]+)", "$2:$1");
        return js[..i] + $"globalThis.__M[\"{modId}\"]=Object.assign(globalThis.__M[\"{modId}\"]||{{}},{{{inner}}});" + js[(end + 1)..];
    }

    private static string StripDefaultExportTail(string js, string modId)
    {
        var i = js.LastIndexOf("export default", StringComparison.Ordinal);
        if (i < 0) return js;
        return js[..i] + $"globalThis.__M[\"{modId}\"]= " + js[(i + 14)..];
    }

    private static void BindModuleGlobals(Engine engine, string identifier, string mode)
    {
        // import 标识符 → 全局别名（default 导出优先；副作用模块无需绑定）
        if (identifier.StartsWith("_")) return;
        engine.Execute(
            $"try{{globalThis.{identifier}=globalThis.__M[\"{identifier}\"]&&(globalThis.__M[\"{identifier}\"][\"default\"]||globalThis.__M[\"{identifier}\"])}}catch(e){{}}");
    }

    private async Task<string?> DownloadTextAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            Log($"下载失败 {url}: {ex.Message}");
            return null;
        }
    }
}
