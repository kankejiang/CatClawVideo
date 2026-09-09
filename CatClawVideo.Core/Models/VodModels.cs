namespace CatClawVideo.Core.Models;

/// <summary>
/// spider（爬虫）源依赖的运行时类型。TVBox site.type=3 只说明「是爬虫源」，
/// 具体还要按 api 形态区分：csp_Xxx 走 jar/dex 爬虫、http(s) 脚本地址走 JS 引擎。
/// </summary>
public enum VodSpiderKind
{
    /// <summary>非爬虫源（MacCMS json/xml 等直接按 API 协议取数）</summary>
    None = 0,

    /// <summary>Java jar/dex 爬虫（api 形如 csp_Xxx，依赖订阅全局或站点 jar）</summary>
    Jar = 1,

    /// <summary>脚本爬虫（api 为 http(s) 脚本文件地址，如 .js / drp，依赖 JS 引擎）</summary>
    Script = 2,
}

/// <summary>影视站点信息（订阅源解析后的统一站点描述，跨 TVBox / 影视仓多仓格式）</summary>
public class VodSiteInfo
{
    /// <summary>站点唯一标识（TVBox 源的 key）</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>站点显示名称</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>站点 API 地址（MacCMS json 接口 / csp_ 协议 / spider 等）</summary>
    public string Api { get; set; } = string.Empty;

    /// <summary>源类型标识（TVBox site.type：0=xml、1=json、3=spider；或 mac_cms 等内部类型）</summary>
    public int Type { get; set; }

    /// <summary>扩展配置地址（spider 源的 ext，可为 URL 或内嵌 json 字符串）</summary>
    public string? Ext { get; set; }

    /// <summary>所属订阅（多仓源时区分子仓）</summary>
    public string SubscriptionName { get; set; } = string.Empty;

    /// <summary>是否可播放</summary>
    public bool Playable { get; set; } = true;

    /// <summary>是否可搜索</summary>
    public bool Searchable { get; set; } = true;

    /// <summary>
    /// spider 包地址（type=3 站点）。站点自带 jar 时取站点值，否则回退到订阅的全局 spider。
    /// 常见写法 "url;md5;hash"，也可能是伪装成 .jpg 的 jar。
    /// </summary>
    public string? Jar { get; set; }

    /// <summary>爬虫运行时类型（type=3 站点按 api 形态判定）</summary>
    public VodSpiderKind SpiderKind { get; set; }

    /// <summary>是否支持快速搜索</summary>
    public bool QuickSearch { get; set; } = true;

    /// <summary>请求超时（秒）。来自订阅配置的 timeout 字段，为空时用全局默认。</summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>
    /// 站点状态说明（不可播时给出具体原因，如「jar 爬虫源 · 需 spider 运行时」）。
    /// 可播站点为 null。
    /// </summary>
    public string? StatusNote { get; set; }

    /// <summary>
    /// 站点是否需要账号认证（ext 为 JSON 数组且含 type=global 的 username/password，
    /// 典型如小雅 Alist 源；无凭据时站点无法取数观看）。
    /// </summary>
    public bool NeedsCredentials { get; set; }

    /// <summary>需要认证的服务器地址列表（ext 数组中的 server 字段，凭据按其 authority 存取）</summary>
    public List<string> CredentialServers { get; set; } = [];

    public override string ToString() => Name;
}

/// <summary>影片分类（电影/剧集/综艺/动漫等，源站点返回的分类目录）</summary>
public class VodCategory
{
    /// <summary>分类 ID（源站点原生 ID）</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>分类名称</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>影片条目（列表/搜索结果中的单部影片）</summary>
public class VodItem
{
    /// <summary>影片 ID（源站点原生 ID）</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>所属站点 Key</summary>
    public string SourceKey { get; set; } = string.Empty;

    /// <summary>片名</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>封面图 URL</summary>
    public string? Cover { get; set; }

    /// <summary>分类（如：动作片 / 国产剧）</summary>
    public string? Category { get; set; }

    /// <summary>年份</summary>
    public string? Year { get; set; }

    /// <summary>地区</summary>
    public string? Area { get; set; }

    /// <summary>演员</summary>
    public string? Actors { get; set; }

    /// <summary>导演</summary>
    public string? Director { get; set; }

    /// <summary>简介</summary>
    public string? Description { get; set; }

    /// <summary>更新说明（如「更新至12集」「HD」）</summary>
    public string? Remarks { get; set; }

    /// <summary>评分（MacCMS vod_score；部分源列表接口返回 0）</summary>
    public double Score { get; set; }
}

/// <summary>播放线路（一部影片通常有多条线路，每条线路含全部剧集）</summary>
public class VodPlaySource
{
    /// <summary>线路名称（如「线路一」「蓝光」）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>该线路的剧集列表</summary>
    public List<VodEpisode> Episodes { get; set; } = [];
}

/// <summary>单集</summary>
public class VodEpisode
{
    /// <summary>集名（如「第01集」）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>播放地址（可能是直链 m3u8/mp4，也可能是待解析的页面 URL）</summary>
    public string Url { get; set; } = string.Empty;
}

/// <summary>搜索结果（跨站搜索的聚合容器）</summary>
public class VodSearchResult
{
    /// <summary>搜索的站点</summary>
    public VodSiteInfo Site { get; set; } = new();

    /// <summary>命中的影片列表</summary>
    public List<VodItem> Items { get; set; } = [];
}

/// <summary>播放请求（进入播放页的统一参数）</summary>
public class PlayRequest
{
    /// <summary>展示标题（片名 + 集名）</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>播放地址（直链）</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>请求播放地址时需要的 Referer（TVBox 源常见防盗链要求）</summary>
    public string? Referer { get; set; }

    /// <summary>请求播放地址时需要的 User-Agent</summary>
    public string? UserAgent { get; set; }
}
