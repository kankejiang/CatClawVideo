using System.Collections.ObjectModel;
using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;
using Microsoft.Maui.Controls.Shapes;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 源配置页：订阅源管理（增删，真解析）+ 站点列表（开关）。
/// TVBox 明文 JSON 真解析（TvBoxSubscriptionManager）；加密源（饭太硬等）返回明确错误提示；
/// csp spider 源标记「需播放器内核」暂不可播；type=1 MacCMS 源可直接播放。
/// </summary>
public partial class SourceConfigPage : ContentPage
{
    private readonly ISubscriptionManager _subscriptionManager;

    private sealed class SubRow
    {
        public string Name { get; }
        public string Url { get; }
        public SubRow(string name, string url) { Name = name; Url = url; }
    }

    private readonly ObservableCollection<SubRow> _subs = new()
    {
        new("量子资源（内置）", "https://cj.lziapi.com/api.php/provide/vod"),
        new("非凡资源（内置）", "http://cj.ffzyapi.com/api.php/provide/vod"),
    };

    private sealed class SiteRow
    {
        public string Name { get; }
        public string Type { get; }
        public bool Playable { get; }
        public bool Enabled { get; set; } = true;
        public SiteRow(string name, string type, bool playable)
        {
            Name = name; Type = type; Playable = playable;
        }
    }

    private readonly List<SiteRow> _sites = [];

    public SourceConfigPage(ISubscriptionManager subscriptionManager)
    {
        InitializeComponent();
        _subscriptionManager = subscriptionManager;
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
            left.Add(new Label
            {
                Text = captured.Name + (captured.Playable ? "" : "  · 需播放器内核"),
                FontSize = 13,
                TextColor = captured.Playable
                    ? (Color)Application.Current!.Resources["TextPrimaryColor"]
                    : (Color)Application.Current!.Resources["TextHintColor"],
            });
            left.Add(new Label
            {
                Text = captured.Type,
                FontSize = 10.5,
                TextColor = Application.Current?.Resources["TextHintColor"] as Color,
            });
            row.Add(left, 0);

            var sw = new Switch { IsToggled = captured.Enabled, HorizontalOptions = LayoutOptions.End, VerticalOptions = LayoutOptions.Center };
            sw.Toggled += (_, e) => captured.Enabled = e.Value;
            row.Add(sw, 1);

            SiteList.Children.Add(row);
        }
    }

    /// <summary>添加订阅：拉取并解析 TVBox 配置 → 站点列表真数据</summary>
    private async void OnAddSubClicked(object? sender, EventArgs e)
    {
        var url = SubEntry.Text?.Trim();
        if (string.IsNullOrEmpty(url)) return;

        try
        {
            var sites = await _subscriptionManager.LoadSubscriptionAsync(url);

            // 合并去重（按站点名）
            var existing = _sites.Select(s => s.Name).ToHashSet();
            int added = 0, skipped = 0;
            foreach (var s in sites)
            {
                if (!existing.Add(s.Name)) continue;
                var typeName = s.Type switch
                {
                    0 => "xml",
                    1 => "MacCMS json",
                    3 => s.Api.StartsWith("csp_") ? "spider (需内核)" : "spider",
                    _ => $"type {s.Type}",
                };
                _sites.Add(new SiteRow(s.Name, typeName, s.Playable));
                added++;
            }
            skipped = sites.Count - added;
            RebuildSites();
            SubEntry.Text = "";
            await DisplayAlertAsync("订阅已添加",
                $"解析到 {sites.Count} 个站点，新增 {added} 个" +
                (skipped > 0 ? $"（跳过重复 {skipped} 个）" : "") + "。", "确定");
        }
        catch (NotSupportedException ex)
        {
            await DisplayAlertAsync("暂不支持该订阅", ex.Message, "确定");
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("订阅添加失败", $"无法拉取或解析该地址：{ex.Message}", "确定");
        }
    }
}
