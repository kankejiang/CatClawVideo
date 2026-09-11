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

            var cardH = Math.Clamp(height - 40, 64, cap);
            if (Application.Current is not null)
                Application.Current.Resources["PosterCardHeight"] = cardH;

            var target = cardH * 2.0 / 3.0 + 12;   // 单元格目标宽（海报 2:3）+ 列间距
            var span = (int)Math.Clamp(Math.Round((width + 12) / target), 3, 16);
            if (grid.ItemsLayout is GridItemsLayout g && g.Span != span)
                g.Span = span;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PosterLayout] 自适应失败: {ex.Message}");
        }
    }
}
