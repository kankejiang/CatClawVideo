using CatClawVideo.Core.Models;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 首页数据源选择弹窗（参考影视仓「请选择首页数据源」）：
/// 半透明遮罩 + 居中卡片，两列站点按钮，当前站点主题色描边高亮；
/// 点击站点回调 onSelected 并关闭。
/// </summary>
public partial class SitePickerDialogPage : ContentPage
{
    public SitePickerDialogPage(IEnumerable<VodSiteInfo> sites, string? currentKey, Action<VodSiteInfo> onSelected)
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
        stack.Add(new Label
        {
            Text = "请选择首页数据源",
            FontSize = 17,
            FontFamily = "OpenSansSemibold",
            TextColor = (Color)res["TextPrimaryColor"],
            HorizontalOptions = LayoutOptions.Center,
        });

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

            var btn = new Border
            {
                StrokeThickness = current ? 2 : 0,
                Stroke = current ? (Color)res["PrimaryColor"] : Colors.Transparent,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
                BackgroundColor = (Color)res["ChipInactiveColor"],
                Padding = new Thickness(10, 12),
            };
            btn.Content = new Label
            {
                Text = site.Name,
                FontSize = 13.5,
                MaxLines = 1,
                LineBreakMode = LineBreakMode.TailTruncation,
                HorizontalTextAlignment = TextAlignment.Center,
                TextColor = current ? (Color)res["PrimaryColor"] : (Color)res["TextPrimaryColor"],
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

        Content = new Grid
        {
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Children = { card },
        };
    }
}
