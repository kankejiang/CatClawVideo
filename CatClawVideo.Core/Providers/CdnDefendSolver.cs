using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CatClawVideo.Core.Providers;

/// <summary>
/// cdndefend 浏览器盾通用破盾器（毒舌电影等站点）。
/// 挑战页本质：内嵌 JS 用 SHA1 暴力找一个计数 i，使 sha1(token + i) 的
/// 第 n1 字节 == t1 且第 n1+1 字节 == t2，然后把 cdndefend_js_cookie=token+i
/// 写入 cookie 后刷新页面。服务端只认 cookie——无需真跑 JS，
/// HttpClient 抓到挑战页后在本机算出 cookie 即可放行（毫秒级）。
/// 解析失败（站点改版）返回 null，上层按普通抓取失败处理，不影响其他站点。
/// </summary>
public static class CdnDefendSolver
{
    /// <summary>挑战页识别：含 cdndefend 标记 + 40 位 hex token。</summary>
    public static bool IsChallenge(string? html) =>
        html != null
        && html.Contains("cdndefend", StringComparison.OrdinalIgnoreCase)
        && Regex.IsMatch(html, "'[0-9a-fA-F]{40}'");

    /// <summary>
    /// 解挑战：解析挑战参数 → 算 PoW → 写入共享 CookieContainer → 重取页面。
    /// challengeHtml 传入首次抓到的挑战页（省一次请求）。返回重取结果；
    /// 仍在挑战或任一步失败时返回 null（调用方按失败处理）。
    /// </summary>
    public static async Task<string?> SolveAsync(Uri uri, string challengeHtml)
    {
        try
        {
            var ch = Parse(challengeHtml);
            if (ch == null) return null;
            var i = BruteForce(ch);
            if (i < 0) return null;

            Cookies.Add(new Uri(uri.GetLeftPart(UriPartial.Authority) + "/"),
                new Cookie(ch.CookieName, ch.Token + i.ToString(CultureInfo.InvariantCulture)) { Path = "/" });

            var retry = await Http.GetStringAsync(uri);
            return IsChallenge(retry) ? null : retry;
        }
        catch { return null; }
    }

    // ═══════════════════ 实现 ═══════════════════

    /// <summary>共享 cookie 容器（引擎 HttpClient 的 handler 引用同一实例）。</summary>
    public static readonly CookieContainer Cookies = new();

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient(new HttpClientHandler { CookieContainer = Cookies, UseCookies = true })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/124.0");
        return client;
    }

    private sealed record Challenge(string Token, string CookieName, int N1, byte T1, byte T2);

    /// <summary>
    /// 解析挑战页 JS。脚本形态（变量名可能变，结构固定）：
    /// const arr=['TOKEN','cdndefend_js_cookie=','array'];
    /// (function(a,n){while(--n){a.push(a.shift());}}(arr,0x178));
    /// let c=reader('0x2'); n1=parseInt('0x'+c[0]);
    /// s=sha[methodIdx](c+i); if(s[n1]===0xb0&&s[n1+1]===0xb){cookie=...}
    /// </summary>
    private static Challenge? Parse(string html)
    {
        // 挑战参数数组：必须锚定含 40 位 hex token 的字面量
        //（SHA1 库里还有别的字符串数组，不能按"第一个数组"匹配）
        var arrM = Regex.Match(html, @"([\w$]+)\s*=\s*\[[^\]]*'(?<tok>[0-9a-fA-F]{40})'[^\]]*\]");
        if (!arrM.Success) return null;
        var token = arrM.Groups["tok"].Value;
        var arrayName = arrM.Groups[1].Value;
        var elements = Regex.Matches(arrM.Value, "'([^']*)'")
            .Select(m => m.Groups[1].Value).ToList();
        if (elements.Count == 0) return null;

        // 轮转次数：while(--n){push(shift())} 调用尾 (数组名,0x178) → 左移 N 次取模
        var k = 1;
        var rotM = Regex.Match(html, @"\}\(\s*" + Regex.Escape(arrayName) + @"\s*,\s*(0x[0-9a-fA-F]+|\d+)\s*\)\)");
        if (rotM.Success && int.TryParse(rotM.Groups[1].Value.Replace("0x", "").Replace("0X", ""),
                NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rot))
            k = rot % elements.Count;

        // 读取下标（默认 c=2 / cookie 名=0；实际下标从脚本解析）
        int CIdx() => ReadIndex(@"\bc\s*=\s*[\w$]+\('?(0x[0-9a-fA-F]+|\d+)'?\)", 2);
        int NameIdx() => ReadIndex(@"document\['cookie'\]\s*=\s*[\w$]+\('?(0x[0-9a-fA-F]+|\d+)'?\)", 0);

        int ReadIndex(string pattern, int fallback)
        {
            var m = Regex.Match(html, pattern);
            if (!m.Success) return fallback;
            var v = m.Groups[1].Value;
            return v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? int.TryParse(v[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var x) ? x : fallback
                : int.TryParse(v, out var d) ? d : fallback;
        }

        var c = elements[(CIdx() + k) % elements.Count];
        // 元素存的是 "名=" 全串（JS 里 cookie = 元素 + 值），作 Cookie 名要去掉分隔等号
        var cookieName = elements[(NameIdx() + k) % elements.Count].TrimEnd('=');

        // n1 = parseInt('0x' + c[0])：token 首字符的 hex 值
        if (c.Length == 0 || !Uri.IsHexDigit(c[0])) return null;
        var n1 = int.Parse(c[0].ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (n1 >= 19) return null; // 字节模式取 n1/n1+1，SHA1 摘要仅 20 字节

        // 目标字节 s[n1]===t1 && s[n1+1]===t2
        var t1 = (byte)0xb0;
        var t2 = (byte)0x0b;
        var cond = Regex.Match(html, @"\[n1\]\s*===\s*(0x[0-9a-fA-F]+|\d+)\s*&&");
        if (cond.Success) t1 = ParseNum(cond.Groups[1].Value);
        var cond2 = Regex.Match(html, @"\+\s*0x1\]\s*===\s*(0x[0-9a-fA-F]+|\d+)");
        if (cond2.Success) t2 = ParseNum(cond2.Groups[1].Value);

        return new Challenge(token, cookieName, n1, t1, t2);
    }

    private static byte ParseNum(string s) =>
        s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToByte(s[2..], 16)
            : byte.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>暴力：找最小 i 使 sha1(token+i) 命中两个目标字节（实测 ~15 万次）。</summary>
    private static long BruteForce(Challenge ch)
    {
        using var sha = SHA1.Create();
        var prefix = Encoding.UTF8.GetBytes(ch.Token);
        var buf = new byte[prefix.Length + 12];
        prefix.CopyTo(buf, 0);
        Span<char> digits = stackalloc char[20];
        Span<byte> hash = stackalloc byte[20];

        for (long i = 0; i < 50_000_000; i++)
        {
            i.TryFormat(digits, out var len, null, CultureInfo.InvariantCulture);
            for (var j = 0; j < len; j++)
                buf[prefix.Length + j] = (byte)digits[j];

            sha.TryComputeHash(buf.AsSpan(0, prefix.Length + len), hash, out _);

            if (hash[ch.N1] == ch.T1 && hash[ch.N1 + 1] == ch.T2)
                return i;
        }
        return -1;
    }
}
