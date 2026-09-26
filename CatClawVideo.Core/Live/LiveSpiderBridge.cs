namespace CatClawVideo.Core.Live;

/// <summary>
/// spider 型直播源的取列表入口（宿主注入）。
///
/// <para><b>为什么要用注入而不是让 <see cref="LiveSourceService"/> 直接依赖爬虫运行时</b>：
/// <c>Core.Live</c> 只管「订阅文本 → 频道分组」，而运行时按平台分叉
/// （Android 是进程内 dex、桌面是常驻 JVM 桥、另有两套 JS），把那条依赖拉进来会让
/// 直播模块编译期绑死爬虫层。这里留一个函数口子，由宿主在运行时构造完成后赋值。</para>
///
/// <para>返回 null = 本机没有能处理该源的运行时；返回空串 = spider 确实没给出频道列表
/// （两者在上层的提示不同：前者说「不支持」，后者说「该源没有频道」）。</para>
/// </summary>
public static class LiveSpiderBridge
{
    public static Func<LiveLivesEntry, CancellationToken, Task<string>?>? Fetcher { get; set; }

    public static bool Available => Fetcher is not null;
}
