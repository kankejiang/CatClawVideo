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
    /// <param name="cap">卡片高度上限（移动端默认 182：卡片略小给片名留出两行空间）。桌面端调用方传 252/260。</param>
    public static void Apply(CollectionView? grid, double width, double height, double cap = 182)
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

            // 间距取**模板实际值**：各页模板的 HorizontalItemSpacing 不同
            // （首页/历史/收藏 14、搜索页 10），原先按 12 硬写会让列数算偏。
            var layout = (GridItemsLayout)grid.ItemsLayout;
            var spacing = layout.HorizontalItemSpacing;

            var columns = Math.Clamp((int)Math.Floor((width + spacing) / (cardW + spacing)), 3, 16);

            // 再验一次：按真实列数回推格宽，卡片比格宽就减列。
            // 卡片宽是固定的（模板里 WidthRequest 写死），超出格子的部分会被 CollectionView
            // 的 item 容器**在右侧裁掉** —— 海报图看不出来，但焦点环画在卡片边界，
            // 右半边一被裁就是「焦点缺一边」，右列还容易串到下一格（2026-09-19 用户截图）。
            while (columns > 3 && cardW > CellWidth(width, columns, spacing))
                columns--;

            if (layout.Span != columns)
                layout.Span = columns;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PosterLayout] 自适应失败: {ex.Message}");
        }
    }

    /// <summary>按列数回推每格可用宽度：格宽 = (总宽 − 间隔和) ÷ 列数。</summary>
    private static double CellWidth(double width, int columns, double spacing) =>
        (width - (columns - 1) * spacing) / columns;
}
