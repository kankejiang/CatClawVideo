namespace CatClawVideo.Maui.Services;

/// <summary>
/// 轨道语言代码 → 中文显示名。两端（Media3 的 Format.Language、WinRT 的 AudioTrack.Language）
/// 给的都是 ISO 代码，且长度不定（chi/zho/zh-cn/zho-hans…），统一按前缀匹配。
/// </summary>
static class TrackLang
{
    static readonly (string Prefix, string Name)[] Table =
    {
        ("zh", "中文"), ("chi", "中文"), ("zho", "中文"), ("yue", "粤语"),
        ("en", "英文"), ("eng", "英文"),
        ("ja", "日文"), ("jpn", "日文"),
        ("ko", "韩文"), ("kor", "韩文"),
        ("fr", "法文"), ("fra", "法文"), ("fre", "法文"),
        ("de", "德文"), ("deu", "德文"), ("ger", "德文"),
        ("ru", "俄文"), ("rus", "俄文"),
        ("es", "西语"), ("spa", "西语"),
        ("th", "泰语"), ("tha", "泰语"),
        ("vi", "越语"), ("vie", "越语"),
        ("it", "意语"), ("ita", "意语"), ("pt", "葡语"), ("por", "葡语"),
        ("und", "未指定"), ("zxx", "无对白"),
    };

    /// <summary>认得就翻成中文，认不得原样返回（不吞掉用户看得懂的语言代码）。</summary>
    public static string NameOrSelf(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return code;
        var c = code.Trim().ToLowerInvariant();
        foreach (var (prefix, name) in Table)
        {
            if (c == prefix || c.StartsWith(prefix + "-") || c.StartsWith(prefix + "_")) return name;
        }
        // 三字母代码没进表：原样显示；两位的也原样显示
        return code;
    }

    /// <summary>声道数 → 惯例叫法（两端轨道列表都会用到）。</summary>
    public static string Channels(int n) => n switch
    {
        1 => "单声道",
        2 => "立体声",
        6 => "5.1",
        8 => "7.1",
        _ => n + " 声道"
    };
}
