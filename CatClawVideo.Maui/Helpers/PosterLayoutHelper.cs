namespace CatClawVideo.Maui;

/// <summary>
/// 海报墙自适应（首页 / 历史 / 收藏共用）：卡片高按可视高夹取，列数按海报 2:3 反推。
/// 卡片高写入「应用级」资源 PosterCardHeight——页级 DynamicResource 的更新在
/// DataTemplate 里不生效（HomePage 注释里的踩坑实录），应用级字典才会传播。
/// </summary>
public static class PosterLayoutHelper
{
    /// <param name="grid">海报墙 CollectionView（ItemsLayout 须为 GridItemsLayout）</param>
    /// <param name="width">海报墙当前宽</param>
    /// <param name="height">海报墙当前高</param>
    /// <param name="cap">卡片高度上限。桌面端可视区大，可放宽（如 300）让海报更大。</param>
    public static void Apply(CollectionView? grid, double width, double height, double cap = 190)
    {
        try
        {
            if (grid is null || width <= 0 || height <= 0) return;
            if (grid.ItemsLayout is not GridItemsLayout) return;

            // 固定卡片尺寸（高=cap，宽=高×2/3），只有列数随窗口宽度自适应：
            // 卡片大小恒定、比例恒定 2:3、间隔恒定 12——不再随窗口忽大忽小
            // （随窗口缩放的两种尝试都被否：撑满列格=间隔失控，限宽居中=留白巨大）。
            var cardH = Math.Clamp(cap, 64, 600);
            var cardW = cardH * 2.0 / 3.0;

            if (Application.Current is not null)
            {
                Application.Current.Resources["PosterCardHeight"] = cardH;
                Application.Current.Resources["PosterCardWidth"] = cardW;
            }

            var columns = Math.Clamp((int)Math.Floor((width + 12) / (cardW + 12)), 3, 16);
            if (grid.ItemsLayout is GridItemsLayout g && g.Span != columns)
                g.Span = columns;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PosterLayout] 自适应失败: {ex.Message}");
        }
    }
}
