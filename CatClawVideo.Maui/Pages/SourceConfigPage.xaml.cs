using System.Collections.ObjectModel;
using Microsoft.Maui.Controls.Shapes;
using CatClawVideo.Core.Models;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 源配置页：订阅源管理（增删）+ 站点列表（开关）。
/// 订阅解析（ISubscriptionManager）与站点拉取接入前用假数据撑起 UI；数据模型对齐 VodSiteInfo。
/// </summary>
public partial class SourceConfigPage : ContentPage
{
    private sealed class SubRow
    {
        public string Name { get; }
        public string Url { get; }
        public SubRow(string name, string url) { Name = name; Url = url; }
    }

    private readonly ObservableCollection<SubRow> _subs = new()
    {
        new("暴风资源站", "https://github.com/xx/baoFeng.json"),
        new("量子资源库", "https://tvbox.xxx.com/liangzi.json"),
        new("非凡影视", "http://xx/feifan.json"),
    };

    private sealed class SiteRow
    {
        public string Name { get; }
        public string Type { get; }
        public bool Enabled { get; set; } = true;
        public SiteRow(string name, string type) { Name = name; Type = type; }
    }

    private readonly List<SiteRow> _sites =
    [
        new("暴风资源", "mac_cms json"),
        new("量子资源", "mac_cms json"),
        new("非凡影视", "mac_cms xml"),
        new("多多资源", "mac_cms json"),
        new("豆瓣官方", "csp_Douban (spider)"),
    ];

    public SourceConfigPage()
    {
        InitializeComponent();
        RebuildSubs();
        RebuildSites();
    }

    private void OnBackTapped(object? sender, TappedEventArgs e) => Shell.Current.GoToAsync("..");

    private void RebuildSubs()
    {
        SubList.Children.Clear();
        foreach (var sub in _subs)
        {
            var row = new Grid { ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)] };
            var left = new VerticalStackLayout { Spacing = 2 };
            left.Add(new Label { Text = sub.Name, FontSize = 13, TextColor = Application.Current?.Resources["TextPrimaryColor"] as Color });
            left.Add(new Label { Text = sub.Url, FontSize = 10.5, TextColor = Application.Current?.Resources["TextHintColor"] as Color, LineBreakMode = LineBreakMode.TailTruncation });
            row.Add(left, 0);
            var del = new Label { Text = "删除", FontSize = 11.5, TextColor = Color.FromArgb("#c0392b"), VerticalOptions = LayoutOptions.Center };
            var captured = sub;
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) =>
            {
                _subs.Remove(captured);
                RebuildSubs();
            };
            del.GestureRecognizers.Add(tap);
            row.Add(del, 1);
            SubList.Children.Add(row);
        }
    }

    private void RebuildSites()
    {
        SiteList.Children.Clear();
        foreach (var site in _sites)
        {
            var captured = site;
            var row = new Grid
            {
                ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
                Padding = new Thickness(0, 8),
            };
            var left = new VerticalStackLayout { Spacing = 2 };
            left.Add(new Label { Text = captured.Name, FontSize = 13, TextColor = Application.Current?.Resources["TextPrimaryColor"] as Color });
            left.Add(new Label { Text = captured.Type, FontSize = 10.5, TextColor = Application.Current?.Resources["TextHintColor"] as Color });
            row.Add(left, 0);

            var sw = new Switch { IsToggled = captured.Enabled, HorizontalOptions = LayoutOptions.End, VerticalOptions = LayoutOptions.Center };
            sw.Toggled += (_, e) => captured.Enabled = e.Value;
            row.Add(sw, 1);

            SiteList.Children.Add(row);
        }
    }

    private void OnAddSubClicked(object? sender, EventArgs e)
    {
        var url = SubEntry.Text?.Trim();
        if (string.IsNullOrEmpty(url)) return;
        _subs.Add(new SubRow($"订阅 {_subs.Count + 1}", url));
        SubEntry.Text = "";
        RebuildSubs();
        // 订阅解析（LoadSubscriptionAsync）接入后：拉取 → 站点列表 → 持久化
    }
}
