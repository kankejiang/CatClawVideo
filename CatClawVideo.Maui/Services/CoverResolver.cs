using CatClawVideo.Core.Models;
using CatClawVideo.Core.Services;
using CatClawVideo.Maui.ViewModels;

namespace CatClawVideo.Maui.Services;

/// <summary>
/// 封面解析接线：把海报墙条目交给 <see cref="CoverImageService"/> 解析，
/// 完成后在 UI 线程回填 <c>CoverDisplay</c>（本地文件路径；解析不到则为 null → 显示占位海报）。
///
/// <para>
/// 设计要点：
/// <list type="bullet">
/// <item>**先出列表、后填封面**：条目立刻可见，封面异步补齐，绝不因为封面慢而卡住整页。</item>
/// <item>只回填展示属性，不动 <c>Cover</c>（源站 URL）——历史/收藏落库用的还是原始 URL。</item>
/// <item>并发由服务内部的信号量统一限流，这里不额外排队，避免多页叠加放大请求量。</item>
/// </list>
/// </para>
/// </summary>
public static class CoverResolver
{
    /// <summary>首页 / 搜索页（VodItem 列表）</summary>
    public static void Attach(CoverImageService service, IEnumerable<VodItem> items)
    {
        foreach (var item in items)
            _ = ResolveAsync(service, item.Cover, item.Title, path => item.CoverDisplay = path);
    }

    /// <summary>历史 / 收藏页（WallCard 列表）</summary>
    public static void Attach(CoverImageService service, IEnumerable<WallCard> cards)
    {
        foreach (var card in cards)
            _ = ResolveAsync(service, card.Cover, card.Title, path => card.CoverDisplay = path);
    }

    /// <summary>单条解析（观看页等单张封面场景）</summary>
    public static void Resolve(CoverImageService service, string? coverUrl, string title, Action<string?> apply)
        => _ = ResolveAsync(service, coverUrl, title, apply);

    private static async Task ResolveAsync(
        CoverImageService service, string? coverUrl, string title, Action<string?> apply)
    {
        try
        {
            var path = await service.GetCoverAsync(coverUrl, title).ConfigureAwait(false);
            // 绑定通知必须在 UI 线程发（后台线程改属性不刷界面）
            if (MainThread.IsMainThread) apply(path);
            else MainThread.BeginInvokeOnMainThread(() => apply(path));
        }
        catch
        {
            // 解析失败保持 null → 占位海报，不影响列表其余部分
        }
    }
}
