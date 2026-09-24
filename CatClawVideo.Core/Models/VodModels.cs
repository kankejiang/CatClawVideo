using System.ComponentModel;

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

    /// <summary>
    /// 站点是否**声明了**站内搜索接口（猫爪 web 源的 rules.searchUrl；MacCMS/爬虫源天然有标准搜索 API）。
    /// 与 <see cref="Searchable"/> 的区别：Searchable 为 true 也可能只是走「扫分类页 + 标题过滤」的
    /// 慢速回退——那种一次要打十几个请求，**不适合用来做封面兜底**。
    /// 跨源封面检索只打 DeclaredSearch 的站点。
    /// </summary>
    public bool DeclaredSearch { get; set; }

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
public class VodItem : INotifyPropertyChanged
{
    /// <summary>影片 ID（源站点原生 ID）</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>所属站点 Key</summary>
    public string SourceKey { get; set; } = string.Empty;

    /// <summary>片名</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>封面图 URL（源站原始地址；写历史/收藏用，勿改写成本地路径）</summary>
    public string? Cover { get; set; }

    private string? _coverDisplay;

    /// <summary>
    /// 封面**展示**源：由 <see cref="Services.CoverImageService"/> 解析出的本地缓存文件路径。
    /// null = 无可展示封面（界面应显示占位海报，勿留白）。
    /// 与 <see cref="Cover"/> 分离的原因：本地路径不该被写进播放历史/收藏（换机器即失效）。
    /// 列表在解析完成后回填此属性（INotifyPropertyChanged 驱动界面刷新）。
    /// </summary>
    public string? CoverDisplay
    {
        get => _coverDisplay;
        set
        {
            if (string.Equals(_coverDisplay, value, StringComparison.Ordinal)) return;
            _coverDisplay = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverDisplay)));
        }
    }

    private bool _isFocused;

    /// <summary>
    /// 遥控器 / 键盘焦点（仅界面态，不持久化）。
    /// 海报墙用 DataTrigger 绑定它绘制焦点环 —— 列表项本身不可聚焦控件，
    /// 焦点由页面的 <c>IRemoteKeyHandler</c> 显式驱动。
    /// </summary>
    public bool IsFocused
    {
        get => _isFocused;
        set
        {
            if (_isFocused == value) return;
            _isFocused = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFocused)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

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

    /// <summary>
    /// 卡片自带的 TVBox <c>action</c>（原样 JSON 文本，如 <c>{"do":2,"key":"..."}</c>）。
    /// <para>非空说明这张卡不是影片而是**操作入口**：点击应调 <c>spider.action(json)</c>
    /// （Guard 系网盘的「登入自己网盘」→ 原生对话框/扫码就是这么弹的），
    /// 而不是走 detailContent → playerContent 那条兜底路。</para>
    /// </summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>
    /// 卡片类型标记（协议 <c>vod_tag</c>）。网盘源用它表达「这是一层目录」：
    /// <c>folder</c> = 点进去要用本条目的 <see cref="Id"/> 当分类 ID 重新拉列表，
    /// <c>cover</c> = 同样重拉但换封面式排版。都不是影片，绝不能走 detailContent。
    /// （TVBox <c>GridFragment.onItemClick</c> 里 <c>video.tag</c> 的语义。）
    /// </summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>更新说明（如「更新至12集」「HD」）</summary>
    public string? Remarks { get; set; }

    /// <summary>角标可见性（首页 XAML 绑定用，与历史/收藏卡片的 HasRemark 对齐）</summary>
    public bool HasRemark => !string.IsNullOrEmpty(Remarks);

    /// <summary>评分（MacCMS vod_score；部分源列表接口返回 0）</summary>
    public double Score { get; set; }

    /// <summary>
    /// 来源**站点名**（跨站聚合列表上屏用，如搜索结果同时命中多个站时标明出处）。
    /// 仅界面展示，不参与落库/持久化（历史、收藏按 SourceKey 还原站点）。
    /// </summary>
    public string? SiteName { get; set; }

    /// <summary>站点角标可见性（XAML 无 null 判断，直接绑定布尔）</summary>
    public bool HasSiteName => !string.IsNullOrEmpty(SiteName);
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

    /// <summary>所属线路的 flag（vod_play_from 的值，playerContent 第一参数；spider 常按它分支解析逻辑）</summary>
    public string? Flag { get; set; }
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

    /// <summary>
    /// 爬虫给的失败原因（协议 <c>msg</c> / <c>errMsg</c>）。
    /// <para>网盘源尤其重要：像「夸克盘容量不足,请购买会员扩容或清理空间」这类是**用户自己的盘**
    /// 的状态,`url` 会返回空。此时绝不能把剧集 id 当地址喂给播放器 —— 那只会得到一句
    /// 毫无信息量的「播放失败: Source error」（2026-09-24 真机实测）。</para>
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>请求播放地址时需要的 Referer（TVBox 源常见防盗链要求）</summary>
    public string? Referer { get; set; }

    /// <summary>请求播放地址时需要的 User-Agent</summary>
    public string? UserAgent { get; set; }

    /// <summary>完整请求头（含 Referer/UA 之外的 Origin/Cookie 等；TVBox spider header 全量透传）</summary>
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>是否需要网页嗅探（spider playerContent parse=1 或站点 parse 未解析时为 true）</summary>
    public bool NeedsSniff { get; set; }

    /// <summary>
    /// HTML 交互页标记：Url 不是视频流而是宿主本地 proxy 提供的网页配置页
    /// （Guard 系网盘源「云盘配置」卡片，如 csp_MyDriveGuard 的 itemId=0000 登入入口），
    /// 播放器无法消费，须用 WebView 打开（对齐 TVBox 嗅探后渲染网页的行为）。
    /// </summary>
    public bool IsHtmlPage { get; set; }

    /// <summary>
    /// spider playerContent 的 danmaku 字段。Guard 系网盘源把它用作**宿主钩子**：
    /// 指向本地 proxy 的 do=danmu&url=&lt;vod_id&gt;，GET 该 URL 会回调 jar 的 proxy(Map)，
    /// 触发网盘配置对话框（TVBox 由弹幕加载隐式触发同一 URL）。
    /// </summary>
    public string? DanmakuUrl { get; set; }
}
