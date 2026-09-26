using System.Text.Json;
using System.Text.Json.Serialization;

namespace CatClawVideo.Core.Services;

/// <summary>备份包里的一条订阅（对位 TVBox 备份里的 Hawk+Room 打包）。</summary>
public sealed class BackupSubscription
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Kind { get; set; } = "tvbox";
    public bool Enabled { get; set; } = true;
    public int SortOrder { get; set; }
}

/// <summary>
/// 配置包（对位 TVBox <c>BackupDialog</c> 的 <c>bak_*.json</c>）：换机时把「订阅 + 设置 + 独立配置文件」
/// 一次带走。
///
/// <para><b>不带爬虫账号凭据</b>：<c>SpiderCredentials</c> 存的是 alist 一类站点的账号口令，
/// 明文 json 会被用户随手发到群里 —— TVBox 的备份是全量 Hawk，等于把这类值一起带出去了，
/// 这里刻意不做（导入后按提示重新录一次）。</para>
///
/// <para><b>版本校验是硬失败</b>：<see cref="Parse"/> 认不出 <see cref="AppId"/> 或版本更高时返回错误，
/// 让 UI 明确说「这不是猫爪影视的备份」，而不是把别处的 json 半收下再产生一堆怪状态。</para>
/// </summary>
public sealed class BackupBundle
{
    public const int CurrentVersion = 1;
    public const string AppId = "CatClawVideo";

    [JsonPropertyName("appId")] public string App { get; set; } = AppId;
    [JsonPropertyName("version")] public int Version { get; set; } = CurrentVersion;
    [JsonPropertyName("exportedAt")] public string? ExportedAt { get; set; }

    /// <summary>订阅列表（顺序即备份时的顺序）。</summary>
    [JsonPropertyName("subscriptions")] public List<BackupSubscription> Subscriptions { get; set; } = [];

    /// <summary>设置项：键 → 值。值统一存字符串，跨平台不因 int/bool 编码差异丢设置。</summary>
    [JsonPropertyName("settings")] public Dictionary<string, string> Settings { get; set; } = [];

    /// <summary>独立配置文件：文件名 → 原文（直播设置、搜索源勾选等 json 文件）。</summary>
    [JsonPropertyName("files")] public Dictionary<string, string> Files { get; set; } = [];

    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    /// <summary>解析并校验。<paramref name="error"/> 非空时不要使用返回值。</summary>
    public static BackupBundle? Parse(string? json, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "文件是空的";
            return null;
        }
        BackupBundle? bundle = null;
        try
        {
            bundle = JsonSerializer.Deserialize<BackupBundle>(json);
        }
        catch (Exception ex)
        {
            error = "不是合法的 json：" + ex.Message;
        }
        if (bundle is null)
        {
            error ??= "解析失败";
            return null;
        }
        if (!string.Equals(bundle.App, AppId, StringComparison.OrdinalIgnoreCase))
        {
            error = $"这不是猫爪影视的备份包（appId={bundle.App}）";
            return null;
        }
        if (bundle.Version > CurrentVersion)
        {
            error = $"备份包版本 v{bundle.Version} 比当前程序 v{CurrentVersion} 新，请先升级";
            return null;
        }
        return bundle;
    }
}
