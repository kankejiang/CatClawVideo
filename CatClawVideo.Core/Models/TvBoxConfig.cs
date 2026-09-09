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

    /// <summary>
    /// 域名替换表（"a.com=b.com" 形式）。爬虫返回的图片/资源域名在此映射到可用镜像，
    /// 典型如饭太硬源的 img1.wsyzy.org=fan.cloudflare.182682.xyz。
    /// </summary>
    [JsonPropertyName("hosts")]
    public List<string> Hosts { get; set; } = [];

    /// <summary>播放规则列表（m3u8 广告段剔除正则等，按 host 匹配生效）</summary>
    [JsonPropertyName("rules")]
    public List<TvBoxRule> Rules { get; set; } = [];

    /// <summary>站点 logo（影视仓扩展字段）</summary>
    [JsonPropertyName("logo")]
    public string? Logo { get; set; }

    /// <summary>壁纸地址（影视仓扩展字段）</summary>
    [JsonPropertyName("wallpaper")]
    public string? Wallpaper { get; set; }

    /// <summary>首页推荐路由（如 https://xx/home.video 后续版本使用）</summary>
    [JsonPropertyName("homeVideo")]
    public bool? HomeVideo { get; set; }
}

/// <summary>TVBox 播放规则（按 host 匹配的 m3u8 正则处理规则）</summary>
public class TvBoxRule
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>该规则生效的 host 关键字列表</summary>
    [JsonPropertyName("hosts")]
    public List<string> Hosts { get; set; } = [];

    /// <summary>匹配并剔除/改写的正则列表</summary>
    [JsonPropertyName("regex")]
    public List<string> Regex { get; set; } = [];
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
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool? Playable { get; set; }

    [JsonPropertyName("searchable")]
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool? Searchable { get; set; }

    [JsonPropertyName("quickSearch")]
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool? QuickSearch { get; set; }

    /// <summary>是否可换源（影视仓扩展字段，源里写作 0/1）</summary>
    [JsonPropertyName("changeable")]
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool? Changeable { get; set; }

    /// <summary>是否支持分类筛选（源里写作 0/1）</summary>
    [JsonPropertyName("filterable")]
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool? Filterable { get; set; }

    /// <summary>首页取第几组数据（影视仓扩展字段）</summary>
    [JsonPropertyName("indexs")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? Indexs { get; set; }

    /// <summary>播放器类型（1=IJK、2=Exo 等；源中可能写作数字或字符串）</summary>
    [JsonPropertyName("playerType")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? PlayerType { get; set; }

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

    /// <summary>请求直播源时使用的 UA（如 okhttp/3.15）</summary>
    [JsonPropertyName("ua")]
    public string? Ua { get; set; }

    /// <summary>播放器类型（源中可能写作数字或字符串）</summary>
    [JsonPropertyName("playerType")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? PlayerType { get; set; }
}

/// <summary>
/// 布尔字段容错反序列化。TVBox 社区配置的布尔值写法不统一：true/false、1/0、"1"/"0" 都常见
/// （如本订阅源的 searchable/quickSearch 全部是 1/0 数字），直接按 bool 读会抛异常。
/// </summary>
public class FlexibleBoolConverter : JsonConverter<bool?>
{
    public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.True => true,
        JsonTokenType.False => false,
        JsonTokenType.Number => reader.TryGetInt32(out var n) ? n != 0 : null,
        JsonTokenType.String => ParseText(reader.GetString()),
        _ => null,
    };

    private static bool? ParseText(string? text)
    {
        if (bool.TryParse(text, out var b)) return b;
        if (int.TryParse(text, out var n)) return n != 0;
        return null;
    }

    public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteBooleanValue(value.Value);
        else writer.WriteNullValue();
    }
}

/// <summary>整数字段容错反序列化（源中 playerType 等可能写成 "2" 字符串）</summary>
public class FlexibleIntConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.Number => reader.TryGetInt32(out var n) ? n : null,
        JsonTokenType.String => int.TryParse(reader.GetString(), out var s) ? s : null,
        _ => null,
    };

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteNumberValue(value.Value);
        else writer.WriteNullValue();
    }
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

