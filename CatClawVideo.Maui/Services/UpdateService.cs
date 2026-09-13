using System.Net.Http;
using System.Text;
using System.Text.Json;
using CatClawVideo.Core.Services;

namespace CatClawVideo.Maui.Services;

/// <summary>GitHub Release 版本检查服务（跨平台实现）</summary>
public class UpdateService : IUpdateService
{
    private const string GithubApiUrl =
        "https://api.github.com/repos/kankejiang/CatClawVideo/releases/latest";

    private const string ReleasePageUrl =
        "https://github.com/kankejiang/CatClawVideo/releases";

    /// <summary>更新说明截断上限（字符）</summary>
    private const int MaxNotesLength = 400;

    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    static UpdateService()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CatClawVideo");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <inheritdoc />
    public async Task<UpdateCheckResult?> CheckUpdateAsync()
    {
        using var response = await _httpClient.GetAsync(GithubApiUrl);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub API 返回 {(int)response.StatusCode}");

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("tag_name", out var tagProp))
            return null;

        var latestTag = tagProp.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(latestTag))
            return null;

        var latestVersion = latestTag.TrimStart('v');
        if (CompareVersion(latestVersion, GetCurrentVersion()) <= 0)
            return null; // 已是最新版本

        return new UpdateCheckResult(
            LatestVersion: latestVersion,
            ReleaseNotes: CleanNotes(root),
            DownloadUrl: PickAssetUrl(root),
            ReleasePageUrl: ReleasePageUrl);
    }

    /// <summary>
    /// 按当前平台挑选最合适的安装包资源：
    /// Android 优先 .apk；Windows 优先 *Setup*.exe（安装包），其次 *win*.zip（便携版）。
    /// </summary>
    private static string? PickAssetUrl(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        string? apk = null, setupExe = null, winZip = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("browser_download_url", out var urlProp))
                continue;
            var url = urlProp.GetString();
            if (string.IsNullOrWhiteSpace(url))
                continue;

            var name = url;
            if (asset.TryGetProperty("name", out var nameProp))
                name = nameProp.GetString() ?? url;

            var lower = name.ToLowerInvariant();
            if (lower.EndsWith(".apk")) apk ??= url;
            else if (lower.EndsWith(".exe") && lower.Contains("setup")) setupExe ??= url;
            else if (lower.EndsWith(".zip") && lower.Contains("win")) winZip ??= url;
        }

#if WINDOWS
        return setupExe ?? winZip;
#elif ANDROID
        return apk;
#else
        return null;
#endif
    }

    /// <summary>提取并清理 release body：去掉常见 Markdown 标记、压缩空行、截断到 MaxNotesLength</summary>
    private static string? CleanNotes(JsonElement root)
    {
        if (!root.TryGetProperty("body", out var bodyProp) || bodyProp.ValueKind != JsonValueKind.String)
            return null;

        var body = bodyProp.GetString();
        if (string.IsNullOrWhiteSpace(body))
            return null;

        var lines = body.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('#')) line = line.TrimStart('#').Trim();     // 标题
            line = line.Replace("**", "").Replace("`", "");                  // 加粗 / 行内代码
            if (line.StartsWith("---")) continue;                            // 分隔线
            if (line.Length == 0)
            {
                if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
                continue;
            }
            if (sb.Length + line.Length + 1 > MaxNotesLength)
            {
                sb.Append("…");
                break;
            }
            sb.Append(line).Append('\n');
        }

        var notes = sb.ToString().TrimEnd();
        return notes.Length > 0 ? notes : null;
    }

    private static string GetCurrentVersion()
    {
        try
        {
            return AppInfo.Current.VersionString;
        }
        catch
        {
            return "0.0.0";
        }
    }

    /// <summary>比较版本号前 3 段（a &gt; b 返回 1，相等 0，a &lt; b 返回 -1）</summary>
    private static int CompareVersion(string a, string b)
    {
        var pa = ParseVersion(a);
        var pb = ParseVersion(b);
        for (int i = 0; i < 3; i++)
        {
            if (pa[i] > pb[i]) return 1;
            if (pa[i] < pb[i]) return -1;
        }
        return 0;
    }

    private static int[] ParseVersion(string v)
    {
        var parts = (v ?? "0.0.0").Split('.');
        var result = new int[3];
        for (int i = 0; i < 3; i++)
        {
            if (i < parts.Length && int.TryParse(parts[i], out var n))
                result[i] = n;
        }
        return result;
    }
}
