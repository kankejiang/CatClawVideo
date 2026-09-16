using System.Text.Json;
using System.Text.Json.Serialization;

namespace CatClawVideo.Core.Models;

/// <summary>
/// 站点列表的本地缓存（<c>sites-cache.json</c>，随 Debug/Release 各自一份，见 <see cref="AppPaths"/>）。
///
/// <para><b>为什么</b>：启动时的订阅恢复是**异步联网**的（要拉订阅地址 + 解析），
/// 首屏渲染时站点还没到位 → 每次都闪一下「还没有可用的源 + 扫码配对」引导页
/// （用户实测：每次启动都有；并误以为数据没持久化）。</para>
///
/// <para><b>做法</b>：每次解析成功就把站点列表落盘；启动时**同步**读缓存先灌进
/// <see cref="SiteRegistry"/>（毫秒级、离线可用），再后台联网刷新替换。</para>
/// </summary>
public static class SiteCache
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string FilePath => AppPaths.Of("sites-cache.json");

    /// <summary>落盘（失败静默：缓存不该影响主流程）。</summary>
    public static void Save(IReadOnlyCollection<VodSiteInfo> sites)
    {
        try
        {
            if (sites.Count == 0) return;
            File.WriteAllText(FilePath, JsonSerializer.Serialize(sites, Options));
        }
        catch { }
    }

    /// <summary>读缓存；没有或损坏返回 null。</summary>
    public static List<VodSiteInfo>? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var list = JsonSerializer.Deserialize<List<VodSiteInfo>>(File.ReadAllText(FilePath), Options);
            return list is { Count: > 0 } ? list : null;
        }
        catch { return null; }
    }

    /// <summary>缓存文件路径（排障用）。</summary>
    public static string Path => FilePath;
}
