using System.Net.Http;

namespace CatClawVideo.Maui.Platforms.Windows.Mpv;

/// <summary>
/// 杜比视界片源探测：mp4 在 <c>stsd</c> 里携带 <c>dvcc/dvv/dvcC</c> 配置盒（Dolby Vision config），
/// 这些四字码只出现在相关 box 的 type 字段，直接在文件头部字节里搜 ASCII 即可命中。
/// MKV 的 DV 在 Block Additional Mapping 里（头部搜不到），误报率极低、漏报走原路径无损害。
/// 命中 → 走 libmpv 后端（DV RPU tone-map）；未命中 → 维持 FFmpegInteropX。
/// </summary>
public static class DolbyVisionDetector
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(5),
    })
    { Timeout = TimeSpan.FromSeconds(10) };

    private static readonly System.Text.Encoding Ascii = System.Text.Encoding.ASCII;

    /// <summary>同步快速判定：URL/标题含明显 DV 字样时不再探测。</summary>
    public static bool QuickMatch(string url, string? title = null)
    {
        var text = $"{url} {title}";
        return text.Contains("dolby", StringComparison.OrdinalIgnoreCase)
            || text.Contains("杜比", StringComparison.Ordinal)
            || text.Contains("DoVi", StringComparison.OrdinalIgnoreCase)
            || text.Contains(" profile5", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 探测远端 mp4 头部是否带 DV 配置盒。拉前 768KB（moov 通常在前部；拿不满就用手头的）。
    /// 网络失败不抛（返回 false = 按非 DV 走原路径，探测只是加速选择，不能挡播放）。
    /// </summary>
    public static async Task<bool> ProbeAsync(string url)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 768 * 1024 - 1);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode && (int)resp.StatusCode != 206) return false;
            await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var buf = new byte[768 * 1024];
            var read = 0;
            int n;
            while (read < buf.Length && (n = await stream.ReadAsync(buf.AsMemory(read, buf.Length - read)).ConfigureAwait(false)) > 0)
                read += n;

            return ContainsDolbyVisionBox(buf.AsSpan(0, read));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>本地文件版（下载完成后的本地播放路径）。</summary>
    public static bool ProbeFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buf = new byte[768 * 1024];
            var read = fs.Read(buf, 0, buf.Length);
            return ContainsDolbyVisionBox(buf.AsSpan(0, read));
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsDolbyVisionBox(ReadOnlySpan<byte> data)
    {
        return IndexOf(data, "dvcc"u8) >= 0
            || IndexOf(data, "dvv"u8) >= 0
            || IndexOf(data, "dvcC"u8) >= 0;
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
        => haystack.IndexOf(needle);
}
