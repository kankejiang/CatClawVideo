namespace CatClawVideo.Core.Models;

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

    /// <summary>是否支持快速搜索</summary>
    public bool QuickSearch { get; set; } = true;

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
