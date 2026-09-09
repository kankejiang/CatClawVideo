using System.Text.Json.Serialization;

namespace CatClawVideo.Core.Models;

/// <summary>
/// 猫爪源协议（CatClaw Source）v1 模型。
/// <para>
/// 设计原则：单文件纯静态 JSON（可托管任意静态空间：GitHub Pages / NAS / 对象存储），
/// 无服务端逻辑、无爬虫运行时依赖，PC / Android 原生可播。
/// 播放地址为 m3u8 / mp4 直链，可选择性附带 UA / Referer。
/// </para>
/// <para>
/// 文件结构示例：
/// <code>
/// {
///   "magic": "catclaw-source", "version": 1,
///   "name": "源名称", "updated": "2026-09-09T22:00:00",
///   "categories": [ { "id": "movie", "name": "电影" } ],
///   "items": [ { "id": "v001", "title": "片名", "cover": "...", "category": "movie",
///                "sources": [ { "name": "线路一", "episodes": [ { "name": "HD", "url": "https://.../1.m3u8" } ] } ] } ]
/// }
/// </code>
/// </para>
/// </summary>
public class CatClawSourceDoc
{
    /// <summary>协议标识，固定 "catclaw-source"</summary>
    [JsonPropertyName("magic")]
    public string Magic { get; set; } = string.Empty;

    /// <summary>协议版本，当前 1</summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary>源名称（首页站点条显示）</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>最近更新时间（可选，ISO 8601）</summary>
    [JsonPropertyName("updated")]
    public string? Updated { get; set; }

    /// <summary>分类目录</summary>
    [JsonPropertyName("categories")]
    public List<CatClawSourceCategory> Categories { get; set; } = [];

    /// <summary>影片全集（内嵌播放线路；大源后续版本支持分片）</summary>
    [JsonPropertyName("items")]
    public List<CatClawSourceItem> Items { get; set; } = [];

    /// <summary>本机加载时间（缓存 TTL 用；运行时元数据，不参与协议序列化）</summary>
    [JsonIgnore]
    public DateTime LoadedAt { get; set; }

    // ═══════════════ 协议常量 ═══════════════

    /// <summary>协议 magic 标识</summary>
    public const string ProtocolMagic = "catclaw-source";

    /// <summary>当前支持的协议版本</summary>
    public const int ProtocolVersion = 1;

    /// <summary>站点内部类型标记（VodSiteInfo.Type；避开 TVBox 0/1/3 段位）</summary>
    public const int SiteType = 100;
}

/// <summary>猫爪源分类</summary>
public class CatClawSourceCategory
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

/// <summary>猫爪源影片（播放线路内嵌，列表页即可直接播放）</summary>
public class CatClawSourceItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("cover")]
    public string? Cover { get; set; }

    /// <summary>所属分类 id（对应 categories[].id）</summary>
    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("year")]
    public string? Year { get; set; }

    [JsonPropertyName("area")]
    public string? Area { get; set; }

    /// <summary>更新说明（如「HD」「更新至12集」，海报角标）</summary>
    [JsonPropertyName("remarks")]
    public string? Remarks { get; set; }

    [JsonPropertyName("score")]
    public double Score { get; set; }

    [JsonPropertyName("actors")]
    public string? Actors { get; set; }

    [JsonPropertyName("director")]
    public string? Director { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>播放线路（多线路并列，各线路内为剧集序列）</summary>
    [JsonPropertyName("sources")]
    public List<CatClawSourceGroup> Sources { get; set; } = [];
}

/// <summary>猫爪源播放线路</summary>
public class CatClawSourceGroup
{
    /// <summary>线路名称（如「线路一」「蓝光」）</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("episodes")]
    public List<CatClawSourceEpisode> Episodes { get; set; } = [];
}

/// <summary>猫爪源剧集（直链 url 与待解析页面链接 resolve 二选一；resolve 优先）</summary>
public class CatClawSourceEpisode
{
    /// <summary>集名（如「第01集」「HD」）</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>播放直链（m3u8 / mp4，稳定直链源用）</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>
    /// 待解析页面链接（v1.1）：如站点的播放页/播放器页地址，含时效签名也无需担心——
    /// App 播放时经通用嗅探器（WebProbeResolver）实时解析出当下有效的直链。
    /// 与 url 二选一，resolve 优先。
    /// </summary>
    [JsonPropertyName("resolve")]
    public string? Resolve { get; set; }

    /// <summary>可选：播放请求 UA（防盗链直链用）</summary>
    [JsonPropertyName("ua")]
    public string? Ua { get; set; }

    /// <summary>可选：播放请求 Referer</summary>
    [JsonPropertyName("referer")]
    public string? Referer { get; set; }
}
