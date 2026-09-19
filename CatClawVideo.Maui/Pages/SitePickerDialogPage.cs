using CatClawVideo.Core.Models;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 首页数据源选择弹窗（参考影视仓「请选择首页数据源」）：
/// 半透明遮罩 + 居中卡片，两列站点按钮，当前站点主题色描边高亮；
/// 切换失败过的站点置灰标注「不可用」（仍可点击重试）；
/// 点击站点回调 onSelected 并关闭。
///
/// <para><b>三种关闭方式</b>（2026-09-19 用户反馈「只能点站点才能关，误触后没法退出」）：
/// 右上角 <b>✕ 关闭按钮</b>、<b>点击遮罩空白处</b>、按 <b>Back / Esc</b>。
/// 之前只有「选中站点」一条退出路径，误触弹出后用户被迫换源。</para>
/// </summary>
public partial class SitePickerDialogPage : ContentPage
{
    public SitePickerDialogPage(IEnumerable<VodSiteInfo> sites, string? currentKey,
        Action<VodSiteInfo> onSelected, IReadOnlySet<string>? failedKeys = null)
    {
        BackgroundColor = Color.FromArgb("#B3000000");

        var res = Application.Current!.Resources;

        var card = new Border
        {
            WidthRequest = 620,
            MaximumHeightRequest = 620,
            BackgroundColor = (Color)res["CardBackgroundColor"],
            Stroke = (Color)res["DividerColor"],
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 18 },
            Padding = 22,
        };

        var stack = new VerticalStackLayout { Spacing = 14 };

        // 标题行：标题居中 + 右上角关闭按钮（标题两侧用同宽占位保证真居中）
        var titleRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(34)),   // 左侧占位，与关闭按钮等宽
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(new GridLength(34)),
            },
        };
        titleRow.Add(new Label
        {
            Text = "请选择首页数据源",
            FontSize = 17,
            FontFamily = "OpenSansSemibold",
            TextColor = (Color)res["TextPrimaryColor"],
            HorizontalOptions = LayoutOptions.Center,
        }, 1, 0);

        var closeBtn = new Border
        {
            WidthRequest = 34,
            HeightRequest = 34,
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 17 },
            BackgroundColor = (Color)res["ChipInactiveColor"],
            Content = new Label
            {
                Text = "✕",
                FontSize = 15,
                TextColor = (Color)res["TextSecondaryColor"],
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
            },
        };
        var closeTap = new TapGestureRecognizer();
        closeTap.Tapped += async (_, _) => await CloseAsync();
        closeBtn.GestureRecognizers.Add(closeTap);
        titleRow.Add(closeBtn, 2, 0);

        stack.Add(titleRow);

        // 两列站点按钮网格（站点多时卡片内滚动）
        var list = sites.ToList();
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
            },
            ColumnSpacing = 12,
            RowSpacing = 12,
        };
        for (int i = 0; i < list.Count; i++)
        {
            var site = list[i];
            bool current = site.Key == currentKey;
            bool failed = failedKeys?.Contains(site.Key) == true;

            var btn = new Border
            {
                StrokeThickness = current ? 2 : 0,
                Stroke = current ? (Color)res["PrimaryColor"] : Colors.Transparent,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
                BackgroundColor = (Color)res["ChipInactiveColor"],
                Padding = new Thickness(10, 12),
                Opacity = failed ? 0.55 : 1,
            };
            btn.Content = new Label
            {
                Text = failed ? site.Name + "（不可用）" : site.Name,
                FontSize = 13.5,
                MaxLines = 1,
                LineBreakMode = LineBreakMode.TailTruncation,
                HorizontalTextAlignment = TextAlignment.Center,
                TextColor = current ? (Color)res["PrimaryColor"]
                    : failed ? (Color)res["TextHintColor"]
                    : (Color)res["TextPrimaryColor"],
            };

            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) =>
            {
                onSelected(site);
                await Navigation.PopModalAsync();
            };
            btn.GestureRecognizers.Add(tap);

            grid.Add(btn, i % 2, i / 2);
        }

        var scroll = new ScrollView { MaximumHeightRequest = 500 };
        scroll.Content = grid;
        stack.Add(scroll);
        card.Content = stack;

        // 根容器铺满整页：承担「点遮罩空白处关闭」。
        // 卡片在自己的范围内会吃掉点击（命中测试不会冒泡到根 Grid），
        // 所以卡片内点站点仍走站点自己的手势，只有点在卡片外才关闭。
        var root = new Grid { Children = { card } };
        var backdrop = new TapGestureRecognizer();
        backdrop.Tapped += async (_, _) => await CloseAsync();
        root.GestureRecognizers.Add(backdrop);

        Content = root;
    }

    /// <summary>关闭弹窗且**不改变**当前数据源（选中站点走的才是 onSelected + 关闭）。</summary>
    private async Task CloseAsync()
    {
        try
        {
            if (Navigation.ModalStack.Count > 0) await Navigation.PopModalAsync();
        }
        catch { }
    }

    /// <summary>
    /// Android 物理返回键 / Windows Esc → 关闭弹窗。
    /// 返回 <c>true</c> 表示已处理，避免继续冒泡把整个页面弹掉。
    /// </summary>
    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync();
        return true;
    }
}
