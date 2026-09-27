using System.Text;

namespace CatClawVideo.Core.Providers;

/// <summary>
/// JS 爬虫的字面量小工具：把 C# 值安全地嵌进「要 eval 的 JS 调用串」里。
/// <para>这里的转义不是洁癖 —— 筛选值来自站点返回的 JSON（年份/地区名可能带引号、反斜杠、换行），
/// 拼进 <c>__SPIDER__.category('1','1',true,{...})</c> 这种待求值字符串时，
/// 一个未转义的引号就会让整次调用语法报错，表现为「点筛选没反应」。</para>
/// </summary>
internal static class SpiderJsUtil
{
    /// <summary>JS 字符串字面量（含首尾单引号）。null/空 → <c>''</c>。</summary>
    public static string Str(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "''";
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('\'');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\'': sb.Append("\\'"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '<': sb.Append("\\u003c"); break;   // 防 </script> 之类被外层包装切走
                default: sb.Append(c); break;
            }
        }
        sb.Append('\'');
        return sb.ToString();
    }

    /// <summary>把筛选键值表渲染成 JS 对象字面量；空表 → <c>{}</c>。</summary>
    public static string ObjectLiteral(IReadOnlyDictionary<string, string>? filter)
    {
        if (filter is null or { Count: 0 }) return "{}";
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var (k, v) in filter)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(Str(k)).Append(':').Append(Str(v));
        }
        sb.Append('}');
        return sb.ToString();
    }
}
