using System.Text;
using System.Text.RegularExpressions;

namespace CatClawVideo.Core.Js;

/// <summary>
/// Jint 模块剥壳工具（从 <see cref="CatClawVideo.Core.Providers.DrpyJsSpiderRuntime"/> 移植的静态副本）。
/// Jint 不支持 import/export——加载前做受控剥壳（只动头部 import 块与 export 语句），
/// 各模块 IIFE 隔离后按 import 顺序装配到 <c>globalThis.__M</c>。
/// </summary>
public static class JsModuleAssembler
{
    private sealed record ImportDecl(string Identifier, string Url);

    /// <summary>缺失依赖模块的空 stub 导出清单（对齐 TVBox <c>EMPTY_MODULE_CODE</c>）。</summary>
    public const string EmptyModuleCode =
        "const empty = null;\n" +
        "export default empty;\n" +
        "export const JSEncrypt = empty;\n" +
        "export const NodeRSA = empty;\n" +
        "export const pako = empty;\n" +
        "export const JSON5 = empty;\n" +
        "export const mb = empty;\n" +
        "export const parse = empty;\n" +
        "export const stringify = empty;\n" +
        "export const inflate = empty;\n" +
        "export const deflate = empty;\n" +
        "export const gzip = empty;\n" +
        "export const ungzip = empty;\n" +
        "export const encrypt = empty;\n" +
        "export const decrypt = empty;";

    /// <summary>解析脚本头部 import 块 → (标识符, 模块 URL) 列表（保序）。</summary>
    public static List<(string Identifier, string Url)> ParseImports(string js)
    {
        var list = new List<(string, string)>();
        var head = js.Length > 8000 ? js[..8000] : js;
        var matches = Regex.Matches(head, @"import\s*(?:([\w$]+)\s+from\s*|\{([\w$]+)\s*\}\s*from\s*)?[""']([^""']+)[""']");
        foreach (Match m in matches)
        {
            var ident = m.Groups[1].Success ? m.Groups[1].Value
                      : m.Groups[2].Success ? m.Groups[2].Value
                      : "_" + list.Count;
            list.Add((ident, m.Groups[3].Value));
        }
        return list;
    }

    public static string BaseDir(string url)
    {
        var i = url.LastIndexOf('/');
        return i > 0 ? url[..(i + 1)] : url;
    }

    public static string Join(string baseDir, string file) =>
        baseDir.EndsWith("/") ? baseDir + file : baseDir + "/" + file;

    /// <summary>模块剥壳 + IIFE 包装。mode: plain/cjs/tail-named/tail-default/export-named</summary>
    public static string WrapModule(string js, string modId, out string mode)
    {
        mode = DetectModuleMode(js);
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

    public static string DetectModuleMode(string js)
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

    /// <summary>TVBox 源码里的模块无效判定：HTML 错误页 / 404 / not found。</summary>
    public static bool IsInvalidModuleContent(string? content)
    {
        if (string.IsNullOrEmpty(content)) return true;
        var trim = content.Trim();
        if (trim.StartsWith('\uFEFF')) trim = trim[1..].Trim();
        var lower = trim.ToLowerInvariant();
        return lower.StartsWith("<")
               || lower.StartsWith("{\"code\":404")
               || lower.StartsWith("404")
               || lower.StartsWith("not found");
    }
}
