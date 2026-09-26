namespace CatClawVideo.Core.Live;

/// <summary>
/// 直播频道（对应 TVBox <c>bean/LiveChannelItem.java</c>），字段与行为逐一对齐。
/// 一个频道可携带多条线路（urls + 线路名），支持环形切换；epg/ua/header/referer 等
/// 元数据由直播源解析阶段填充（TXT 的 <c>ua=</c> 行、M3U 的 <c>#EXTVLCOPT</c> 等）。
/// </summary>
public class LiveChannelItem
{
    /// <summary>组内下标（从 0）</summary>
    public int ChannelIndex { get; set; }

    /// <summary>全局连续频道号（从 1，电视端数字键换台用）</summary>
    public int ChannelNum { get; set; }

    public string ChannelName { get; set; } = "";

    /// <summary>台标（可空）</summary>
    public string ChannelLogo { get; set; } = "";

    /// <summary>EPG 名称标记（对应源里的 "epg" 字段）</summary>
    public string ChannelEpg { get; set; } = "";

    /// <summary>该频道专属 UA</summary>
    public string ChannelUa { get; set; } = "";

    /// <summary>click 标记（对应源里的 "click"，暂未消费）</summary>
    public string ChannelClick { get; set; } = "";

    /// <summary>流格式提示（hls/dash…，注入播放器头 "TVBox-Format"）</summary>
    public string ChannelFormat { get; set; } = "";

    public string ChannelOrigin { get; set; } = "";

    public string ChannelReferer { get; set; } = "";

    public string ChannelTvgId { get; set; } = "";

    public string ChannelTvgName { get; set; } = "";

    /// <summary>catchup 类型（default/append…）</summary>
    public string CatchupType { get; set; } = "";

    /// <summary>catchup 模板（回看地址）</summary>
    public string CatchupSource { get; set; } = "";

    /// <summary>catchup 替换规则（"pattern,replacement"）</summary>
    public string CatchupReplace { get; set; } = "";

    /// <summary>频道级自定义请求头</summary>
    public Dictionary<string, string> ChannelHeader { get; set; } = new();

    /// <summary>parse 标记（对应源里的 "parse"，暂未消费）</summary>
    public int ChannelParse { get; set; }

    /// <summary>线路名（与 <see cref="SourceUrls"/> 一一对应；无名时自动"源N"）</summary>
    public List<string> SourceNames { get; set; } = new();

    /// <summary>线路地址列表</summary>
    public List<string> SourceUrls { get; set; } = new();

    /// <summary>当前线路下标</summary>
    public int SourceIndex { get; set; }

    /// <summary>线路总数</summary>
    public int SourceNum => SourceUrls.Count;

    /// <summary>该频道能否回看（由 canCurrentChannelCatchup 判定后写入）</summary>
    public bool IncludeBack { get; set; }

    /// <summary>是否有 catchup 配置</summary>
    public bool HasCatchup => !string.IsNullOrEmpty(CatchupSource) || !string.IsNullOrEmpty(CatchupType);

    /// <summary>
    /// 频道级 catchup 配置对象（给 <see cref="LiveCatchup"/> 用）。
    /// <para>只有 <c>source</c> 非空才算「有配置」—— 与 TVBox <c>hasCatchupSource</c> 一致，
    /// 光有 type 没有模板是没法拼地址的。</para>
    /// </summary>
    public LiveCatchup.Config? CatchupConfig => string.IsNullOrEmpty(CatchupSource)
        ? null
        : new LiveCatchup.Config { Type = CatchupType, Source = CatchupSource, Replace = CatchupReplace };

    /// <summary>当前线路地址（越界时回落到 0 并返回空串）</summary>
    public string GetUrl()
    {
        if (SourceUrls.Count == 0) return "";
        if (SourceIndex < 0 || SourceIndex >= SourceUrls.Count) SourceIndex = 0;
        return SourceUrls[SourceIndex];
    }

    /// <summary>当前线路名（越界时回落到第一条）</summary>
    public string GetSourceName()
    {
        if (SourceNames.Count == 0) return "";
        if (SourceIndex < 0 || SourceIndex >= SourceNames.Count) SourceIndex = 0;
        return SourceNames[SourceIndex];
    }

    /// <summary>环形上一条线路</summary>
    public void PreSource()
    {
        if (SourceNum == 0) return;
        SourceIndex--;
        if (SourceIndex < 0) SourceIndex = SourceNum - 1;
    }

    /// <summary>环形下一条线路</summary>
    public void NextSource()
    {
        if (SourceNum == 0) return;
        SourceIndex++;
        if (SourceIndex == SourceNum) SourceIndex = 0;
    }

    /// <summary>给播放器的最终请求头：频道 header + UA/Origin/Referer（对应 getHeaders）</summary>
    public Dictionary<string, string> GetHeaders()
    {
        var headers = new Dictionary<string, string>(ChannelHeader);
        if (!string.IsNullOrEmpty(ChannelUa)) headers["User-Agent"] = ChannelUa;
        if (!string.IsNullOrEmpty(ChannelOrigin)) headers["Origin"] = ChannelOrigin;
        if (!string.IsNullOrEmpty(ChannelReferer)) headers["Referer"] = ChannelReferer;
        return headers;
    }
}

/// <summary>
/// 直播分组（对应 TVBox <c>bean/LiveChannelGroup.java</c>）。
/// 组名支持 <c>组名_密码</c> 约定：密码组在电视端需要输入密码才能进入。
/// </summary>
public class LiveChannelGroup
{
    public int GroupIndex { get; set; }

    public string GroupName { get; set; } = "";

    /// <summary>组密码（空 = 不需密码）</summary>
    public string GroupPassword { get; set; } = "";

    public List<LiveChannelItem> Channels { get; set; } = new();

    public bool NeedsPassword => !string.IsNullOrEmpty(GroupPassword);
}

/// <summary>EPG 节目（对应 TVBox <c>bean/Epginfo.java</c> 的展示语义，时间统一按 GMT+8 解析）。</summary>
public class LiveProgram
{
    public string Title { get; set; } = "";

    public DateTimeOffset Start { get; set; }

    public DateTimeOffset End { get; set; }

    /// <summary>"HH:mm"</summary>
    public string StartText => Start.ToString("HH:mm");

    /// <summary>"HH:mm"</summary>
    public string EndText => End.ToString("HH:mm");

    /// <summary>当前时刻是否落在节目区间内</summary>
    public bool IsNow(DateTimeOffset now) => now >= Start && now < End;
}

/// <summary>
/// TVBox 订阅 JSON 中 <c>lives</c> 数组的单条直播源（含 TVBoxLive 之外的全字段：
/// api / header / ext / jar，便于忠实解析多源配置）。
/// </summary>
public class LiveLivesEntry
{
    public string Name { get; set; } = "";

    /// <summary>0=文本/m3u/txt 直链，3=spider（py/js/jar）</summary>
    public int Type { get; set; }

    /// <summary>文本型源的地址（TVBox 里优先 url，回退 api）</summary>
    public string Url { get; set; } = "";

    /// <summary>spider api 地址（.py/.js）</summary>
    public string Api { get; set; } = "";

    public string Jar { get; set; } = "";

    public string Epg { get; set; } = "";

    public string Ua { get; set; } = "";

    public int TimeoutSeconds { get; set; }

    public Dictionary<string, string> Header { get; set; } = new();

    /// <summary>
    /// 订阅级 catchup（<c>lives[]</c> 条目上的 <c>catchup</c> 对象）。
    /// 频道自己没带 catchup-source 时回退到它 —— TVBox 的 <c>currentCatchup()</c> 就是这个优先级。
    /// </summary>
    public LiveCatchup.Config? Catchup { get; set; }

    /// <summary>是否为 spider 直播源（v1 未接入，加载时给出明确提示）</summary>
    public bool IsSpider => Type == 3 || Api.Contains(".py", StringComparison.OrdinalIgnoreCase)
        || Api.Contains(".js", StringComparison.OrdinalIgnoreCase);
}

/// <summary>直播模块的持久化偏好（存 <c>AppPaths/Sub("live")/settings.json</c>）。</summary>
public class LiveSourcePrefs
{
    /// <summary>当前直播源地址（URL / 本地文件路径 / 内联内容）</summary>
    public string ApiUrl { get; set; } = "";

    /// <summary>EPG 地址模板（可空；空则用默认 51zmt 模板）</summary>
    public string EpgUrl { get; set; } = "";

    /// <summary>拉取直播源 / EPG 使用的 UA（可空）</summary>
    public string Ua { get; set; } = "";

    /// <summary>历史直播源（最多 30 条，新的在前）</summary>
    public List<string> History { get; set; } = new();

    /// <summary>多源(lives)下标（按源地址分别记忆）</summary>
    public Dictionary<string, int> LivesIndexByApi { get; set; } = new();

    /// <summary>超时换源：连接等待秒数（5..30）</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>显示时钟</summary>
    public bool ShowTime { get; set; } = true;

    /// <summary>显示网速（v1 未接入，保留）</summary>
    public bool ShowNetSpeed { get; set; } = true;

    /// <summary>上下换台反向（v1 由 UI 按钮顺序体现，保留）</summary>
    public bool ReverseKeys { get; set; }

    /// <summary>跨分组换台</summary>
    public bool CrossGroup { get; set; } = true;

    /// <summary>源级附加请求头</summary>
    public Dictionary<string, string> WebHeaders { get; set; } = new();

    /// <summary>上次播放的频道名（进入直播间时恢复）</summary>
    public string LastChannelName { get; set; } = "";
}

/// <summary>直播源加载结果。</summary>
public class LiveLoadResult
{
    public List<LiveChannelGroup> Groups { get; set; } = new();

    /// <summary>多源列表（源配置含 lives 数组时非空）</summary>
    public List<LiveLivesEntry> Lives { get; set; } = new();

    /// <summary>当前选中的多源下标</summary>
    public int LivesIndex { get; set; }

    /// <summary>EPG 地址（来自源配置，可空）</summary>
    public string EpgUrl { get; set; } = "";

    /// <summary>源级 UA（来自 lives 条目，可空）</summary>
    public string Ua { get; set; } = "";

    /// <summary>加载失败时的原因（成功为空）</summary>
    public string Error { get; set; } = "";

    public bool Ok => string.IsNullOrEmpty(Error) && Groups.Count > 0;

    /// <summary>频道总数</summary>
    public int TotalChannels => Groups.Sum(g => g.Channels.Count);
}
