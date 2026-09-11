using System.Text.Json.Serialization;

namespace CatClawVideo.Core.Models;

/// <summary>
/// 猫爪源 v2「web 规则模式」模型：源文件只存站点入口 + 声明式解析规则（几 KB），
/// 分类/列表/详情/播放全部由 App 引擎按需实时抓取，源永不携带内容数据。
/// <para>
/// 规则 = 命名捕获组的正则（(?&lt;url&gt;...) (?&lt;title&gt;...) 等），捕获组名约定映射字段。
/// 分页模板 {path}/{page} 占位符；详情相对路径拼 site。
/// </para>
/// </summary>
public class CatClawSourceWeb
{
    [JsonPropertyName("magic")]
    public string Magic { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public int Version { get; set; } = 2;

    /// <summary>源模式："web"（规则实时抓取）；v1 整包为 "static"</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "web";

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>站点入口（所有相对路径的基准）</summary>
    [JsonPropertyName("site")]
    public string Site { get; set; } = string.Empty;

    [JsonPropertyName("updated")]
    public string? Updated { get; set; }

    /// <summary>分类声明（静态列出，无需请求）</summary>
    [JsonPropertyName("categories")]
    public List<CatClawSourceWebCategory> Categories { get; set; } = [];

    [JsonPropertyName("rules")]
    public CatClawSourceWebRules Rules { get; set; } = new();

    /// <summary>
    /// 多站点模式（v2.1）：一个源文件聚合多个站点（如「奥特影视 + 6V电影」合一）。
    /// 存在时顶层 name/site/categories/rules 被忽略，按 sites 逐个注册站点，
    /// 站点 Key = "catclaw#&lt;id&gt;"。缺省为单站点模式（向后兼容）。
    /// </summary>
    [JsonPropertyName("sites")]
    public List<CatClawSourceWebSite>? Sites { get; set; }

    /// <summary>本站点 id（多站点模式下由 SelectSite 填充；单站点为空）</summary>
    [JsonIgnore]
    public string? Id { get; set; }

    /// <summary>本机加载时间（缓存 TTL 用，不参与序列化）</summary>
    [JsonIgnore]
    public DateTime LoadedAt { get; set; }

    // ═══════════════ 协议常量 ═══════════════

    public const string ProtocolMagic = "catclaw-source";
    public const int ProtocolVersion = 2;

    /// <summary>web 规则源站点类型标记（VodSiteInfo.Type）</summary>
    public const int WebSiteType = 101;
}

/// <summary>猫爪源 web 模式分类（path 为相对站点入口的栏目路径）</summary>
public class CatClawSourceWebCategory
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>栏目路径（如 /juqingpian/）</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;
}

/// <summary>多站点模式下的单站点定义（v2.1；结构与顶层单站点一致）</summary>
public class CatClawSourceWebSite
{
    /// <summary>站点 id（站点 Key 后缀，需在文件内唯一）</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("site")]
    public string Site { get; set; } = string.Empty;

    [JsonPropertyName("categories")]
    public List<CatClawSourceWebCategory> Categories { get; set; } = [];

    [JsonPropertyName("rules")]
    public CatClawSourceWebRules Rules { get; set; } = new();
}

/// <summary>
/// web 规则集：全部为命名捕获组正则，缺一条则跳过对应能力（如无 detailMagnet 则无磁力线路）。
/// 捕获组名约定：url / title / cover / name / year / area / desc / score。
/// </summary>
public class CatClawSourceWebRules
{
    // ═══ 列表 ═══

    /// <summary>第 1 页地址模板（占位符 {path}；缺省 "{path}"）</summary>
    [JsonPropertyName("listPageFirst")]
    public string? ListPageFirst { get; set; }

    /// <summary>第 N 页地址模板（{path} / {page}，page 从 2 起）</summary>
    [JsonPropertyName("listPage")]
    public string? ListPage { get; set; }

    /// <summary>列表条目正则（捕获组：url / title / cover 可选 / year 可选）</summary>
    [JsonPropertyName("listItem")]
    public string? ListItem { get; set; }

    // ═══ 详情 ═══

    /// <summary>详情标题正则（捕获组：title；缺省用列表 title）</summary>
    [JsonPropertyName("detailTitle")]
    public string? DetailTitle { get; set; }

    /// <summary>在线播放入口正则（捕获组：url / name 可选；url 为播放页相对/绝对地址）</summary>
    [JsonPropertyName("detailPlay")]
    public string? DetailPlay { get; set; }

    /// <summary>磁力链接正则（捕获组：url / name 可选）</summary>
    [JsonPropertyName("detailMagnet")]
    public string? DetailMagnet { get; set; }

    /// <summary>简介正则（捕获组：desc；单行匹配 + 去标签）</summary>
    [JsonPropertyName("detailDesc")]
    public string? DetailDesc { get; set; }

    /// <summary>年份正则（捕获组：year）</summary>
    [JsonPropertyName("detailYear")]
    public string? DetailYear { get; set; }

    /// <summary>产地正则（捕获组：area）</summary>
    [JsonPropertyName("detailArea")]
    public string? DetailArea { get; set; }

    // ═══ 播放解析（DownSys 类两级跳转；直链页可省 playIframe） ═══

    /// <summary>播放页内播放器 iframe 正则（捕获组：url；省略则不跟 iframe）</summary>
    [JsonPropertyName("playIframe")]
    public string? PlayIframe { get; set; }

    /// <summary>直链提取正则（捕获组：url；在播放页或 iframe 页内找 m3u8/mp4）</summary>
    [JsonPropertyName("playDirect")]
    public string? PlayDirect { get; set; }

    /// <summary>直链页集名正则（捕获组：name 可选，如 iframe 页 title）</summary>
    [JsonPropertyName("playName")]
    public string? PlayName { get; set; }

    /// <summary>站点搜索地址模板（{kw} 关键词；GET。可选，缺省不支持搜索）</summary>
    [JsonPropertyName("searchUrl")]
    public string? SearchUrl { get; set; }

    /// <summary>搜索结果条目正则（缺省用 listItem）</summary>
    [JsonPropertyName("searchItem")]
    public string? SearchItem { get; set; }
}
