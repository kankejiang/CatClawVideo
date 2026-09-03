using System.Text.Json;
using System.Text.Json.Serialization;

namespace CatClawVideo.Core.Models;

/// <summary>
/// TVBox / 影视仓单仓配置文件模型（对应源 JSON 根对象）。
/// 影视仓的多仓源（URL: xx/xx.json 形如 {"urls":[{...}]} 或多仓列表）解析后每个仓
/// 仍是本结构的单仓配置，统一映射到 <see cref="VodSiteInfo"/> 列表。
/// 字段名按 TVBox 社区通用规范（snake_case），反序列化忽略未识别字段。
/// </summary>
public class TvBoxConfig
{
    /// <summary>全局 spider 规则（jar 包地址 + 启动参数）</summary>
    [JsonPropertyName("spider")]
    public string? Spider { get; set; }

    /// <summary>站点列表</summary>
    [JsonPropertyName("sites")]
    public List<TvBoxSite> Sites { get; set; } = [];

    /// <summary>直播源列表（基础架构阶段暂不消费，保留解析）</summary>
    [JsonPropertyName("lives")]
    public List<TvBoxLive> Lives { get; set; } = [];

    /// <summary>嗅探解析规则列表（优酷/腾讯等 web 页面直链嗅探配置）</summary>
    [JsonPropertyName("parses")]
    public List<TvBoxParse> Parses { get; set; } = [];

    /// <summary>壁纸地址（影视仓扩展字段）</summary>
    [JsonPropertyName("wallpaper")]
    public string? Wallpaper { get; set; }

    /// <summary>首页推荐路由（如 https://xx/home.video 后续版本使用）</summary>
    [JsonPropertyName("homeVideo")]
    public bool? HomeVideo { get; set; }
}

/// <summary>TVBox 配置中的单个站点</summary>
public class TvBoxSite
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>0=xml 源、1=json(MacCMS v10) 源、3=spider(JS/Python) 源</summary>
    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("api")]
    public string Api { get; set; } = string.Empty;

    /// <summary>扩展配置（URL 或内嵌 json 对象）</summary>
    [JsonPropertyName("ext")]
    public JsonElement? Ext { get; set; }

    /// <summary>spider 专用 jar 包地址（覆盖全局 spider）</summary>
    [JsonPropertyName("jar")]
    public string? Jar { get; set; }

    [JsonPropertyName("playerable")]
    public bool? Playable { get; set; }

    [JsonPropertyName("searchable")]
    public bool? Searchable { get; set; }

    [JsonPropertyName("quickSearch")]
    public bool? QuickSearch { get; set; }

    /// <summary>超时（秒）</summary>
    [JsonPropertyName("timeout")]
    public int? Timeout { get; set; }

    /// <summary>站点样式/分类过滤（影视仓扩展字段）</summary>
    [JsonPropertyName("style")]
    public JsonElement? Style { get; set; }

    /// <summary>站点分组</summary>
    [JsonPropertyName("group")]
    public string? Group { get; set; }

    /// <summary>ext 为内嵌 json 时序列化为字符串；为 URL 时原样返回</summary>
    public string? ExtAsString()
    {
        if (Ext == null) return null;
        return Ext.Value.ValueKind switch
        {
            JsonValueKind.String => Ext.Value.GetString(),
            _ => Ext.Value.GetRawText(),
        };
    }
}

/// <summary>TVBox 嗅探解析规则</summary>
public class TvBoxParse
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>0=嗅探（web 页面嗅探直链）、1=json 解析、2=扩展解析</summary>
    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("ext")]
    public JsonElement? Ext { get; set; }
}

/// <summary>TVBox 直播源</summary>
public class TvBoxLive
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>0=文本 m3u/txt、1=json 接口</summary>
    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("epg")]
    public string? Epg { get; set; }

    [JsonPropertyName("logo")]
    public string? Logo { get; set; }
}

/// <summary>影视仓多仓源模型（仓库列表 JSON，如 {"urls":[{"url":"...","name":"..."}]}）</summary>
public class TvBoxRepoList
{
    [JsonPropertyName("urls")]
    public List<TvBoxRepo> Repos { get; set; } = [];
}

/// <summary>影视仓多仓源中的单个仓库</summary>
public class TvBoxRepo
{
    /// <summary>仓库名称</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>仓库地址（单仓 TVBox 配置 JSON 的 URL）</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    public override string ToString() => Name;
}

