namespace CatClawVideo.Core.Services;

/// <summary>
/// 随机 UA（对位 TVBox <c>util/UA.java</c>）。用在**没有源级 UA 可用**的公开请求上 ——
/// TVBox 的两个真实调用点是 EPG 抓取（<c>EpgNameFuzzyMatch:55</c>）与 gitcode 文件下载
/// （<c>FileUtils:412</c>），不是给爬虫/直播链路用的（那些链路自己有 ua 字段，不该被随机值覆盖）。
///
/// <para><b>与 TVBox 的偏差（刻意的）</b>：它的 <c>uas[]</c> 有 5,407 条字面量，但去重后只剩
/// 564 条 —— 重复项本身就是权重（最高一条出现 1,286 次 ≈ 24%）。这里取**按出现次数排序的前 12 条**
/// （覆盖原表 70% 的抽样概率）均匀抽：字符串仍是 TVBox 自己的值，但不再为了伪装权重塞 5K 行字面量。
/// 另一份 <c>assets/ua.db</c>（657KB 二进制跳读）不搬：那是同一批 UA 的另一种存法，不是另一批数据。</para>
/// </summary>
public static class UserAgents
{
    /// <summary>TVBox 原表里出现频次最高的 12 条（值取自 <c>UA.java</c>，未改写）。</summary>
    public static readonly string[] Table =
    [
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.114 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.114 Safari/537.36",
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.114 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/14.1.1 Safari/605.1.15",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36 Edg/91.0.864.64",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.114 Safari/537.36 Edg/91.0.864.59",
        "Mozilla/5.0 (iPhone; CPU iPhone OS 14_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/14.1.1 Mobile/15E148 Safari/604.1",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_14_6) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.114 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.77 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.106 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.106 Safari/537.36",
    ];

    /// <summary>TVBox 读不到 ua.db 时的兜底值（保持一致，别造第三种默认）。</summary>
    public const string Fallback =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.114 Safari/537.36";

    /// <summary>随机取一条。同一个进程内多次调用会给不同值（这正是它存在的理由）。</summary>
    public static string Random() => Table[System.Random.Shared.Next(Table.Length)];
}
