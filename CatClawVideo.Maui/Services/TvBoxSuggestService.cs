using System.Text.Json;

namespace CatClawVideo.Maui.Services;

/// <summary>
/// 拼音联想（**照抄 TVBox 的实现**，见 TVBoxOSC <c>SearchActivity.loadRec()</c>）。
///
/// <para><b>为什么用在线接口而不是本地方案</b>：中文「拼音 → 汉字」的反查需要完整拼音库 +
/// 候选词表，TVBox 也没自己做（它的 <c>PinyinAdapter</c> 只是个字符串列表 adapter，
/// 名字骗人、没有任何拼音逻辑）。它把这件事交给腾讯的智能搜索接口，接口内部有拼音库。
/// 我们自己造轮子（本地索引 + 预热）既做不到这个覆盖面，还要为主动抓取承担风控风险
/// （2026-09-19 用户指出）。所以照抄。</para>
///
/// <para><b>接口</b>：<c>tv.aiseet.atianqi.com/i-tvbin/qtv_video/search/get_search_smart_box</c>，
/// 参数 <c>format=json&amp;page_num=0&amp;page_size=20&amp;key=&lt;输入&gt;</c>。
/// 实测输入 <c>frx</c> 第一条即「《凡人修仙传》」，单字母 <c>f</c> 也能命中。</para>
///
/// <para><b>容错</b>：接口不可用（网络/风控/结构变更）时返回空列表，调用方退回
/// 本地索引与默认热词，绝不因此让搜索页不可用。</para>
/// </summary>
public static class TvBoxSuggestService
{
    /// <summary>与 TVBox 一致的联想接口地址。</summary>
    private const string Endpoint =
        "https://tv.aiseet.atianqi.com/i-tvbin/qtv_video/search/get_search_smart_box";

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        // 用 Android TV 的 UA：TVBox 是 Android 应用，服务端对 TV 端 UA 更友好
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Linux; Android 11; MIBOX4 Build/RP1A.201005.006) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/90.0.4430.91 Mobile Safari/537.36");
        return client;
    }

    /// <summary>会话级缓存：同一个 key 在一次运行内只请求一次（TVBox 每次按键都打，我们略省）。</summary>
    private static readonly Dictionary<string, List<string>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock Sync = new();

    /// <summary>
    /// 按输入取联想词（照抄 TVBox 的解析路径）。
    /// <para>返回已按 TVBox 规则清洗（去 <c>&lt;&gt;《》-</c>、取空格前第一段）的词列表；
    /// 失败返回空列表。</para>
    /// </summary>
    public static async Task<List<string>> SuggestAsync(string key)
    {
        var k = (key ?? string.Empty).Trim();
        if (k.Length == 0) return [];

        lock (Sync)
        {
            if (Cache.TryGetValue(k, out var hit)) return hit;
        }

        var words = await FetchAsync(k).ConfigureAwait(false);

        // 只在成功（非空）时缓存：失败下次可重试（网络抖动不该被永久记住）
        lock (Sync)
        {
            if (words.Count > 0) Cache[k] = words;
        }
        return words;
    }

    private static async Task<List<string>> FetchAsync(string key)
    {
        var words = new List<string>();
        try
        {
            var url = $"{Endpoint}?format=json&page_num=0&page_size=20&key={Uri.EscapeDataString(key)}";
            var body = await Http.GetStringAsync(url).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body)) return words;

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return words;
            if (!data.TryGetProperty("search_data", out var sd)) return words;
            if (!sd.TryGetProperty("vecGroupData", out var vec) || vec.GetArrayLength() == 0) return words;
            if (!vec[0].TryGetProperty("group_data", out var groups)) return words;

            foreach (var g in groups.EnumerateArray())
            {
                // TVBox 的取值路径：dtReportInfo.reportData.keyword_txt
                if (!g.TryGetProperty("dtReportInfo", out var info)) continue;
                if (!info.TryGetProperty("reportData", out var rd)) continue;
                if (!rd.TryGetProperty("keyword_txt", out var kwEl)) continue;

                var word = CleanHotWord(kwEl.GetString());
                if (word.Length > 0 && !words.Contains(word)) words.Add(word);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[suggest] 「{key}」联想失败: {ex.Message}");
        }
        return words;
    }

    /// <summary>
    /// 联想词清洗（**照抄 TVBox 的 <c>cleanHotWord</c>**）：
    /// 去掉 <c>&lt; &gt; 《 》 -</c>，再取空格分隔的第一段。
    /// <para>「《凡人修仙传》」→「凡人修仙传」；「令人心动的offer 第7季」→「令人心动的offer」。</para>
    /// </summary>
    public static string CleanHotWord(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var t = title.Trim().Replace("<", "").Replace(">", "")
                          .Replace("《", "").Replace("》", "").Replace("-", "");
        var i = t.IndexOf(' ');
        return (i > 0 ? t[..i] : t).Trim();
    }

    /// <summary>
    /// 接口不可用时的**默认热词**（照抄 TVBox 的 <c>DEFAULT_HOT_WORDS</c>）。
    /// 保证搜索页在完全离线时也有可用入口，而不是空白。
    /// </summary>
    public static readonly string[] DefaultHotWords =
    [
        "家业", "主角", "低智商犯罪", "苏超", "书卷一梦",
        "美人余", "藏海传", "长安的荔枝", "庆余年", "凡人修仙传",
    ];
}
