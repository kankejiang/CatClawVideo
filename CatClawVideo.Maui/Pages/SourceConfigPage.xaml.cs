using System.Collections.ObjectModel;
using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;
using Microsoft.Maui.Controls.Shapes;
namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 源配置页：订阅源管理（增删，真解析）+ 站点列表（开关）。
/// TVBox 明文 JSON 真解析（TvBoxSubscriptionManager）；加密源（饭太硬等）返回明确错误提示；
/// type=3 spider 源按 jar/脚本细分并标注所需运行时（当前均不可播）；type=1 MacCMS 源可直接播放。
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

    /// <summary>订阅列表（不内置任何源，全部由用户添加）</summary>
    private readonly ObservableCollection<SubRow> _subs = new();

    private sealed class SiteRow
    {
        public string Name { get; }
        public string Type { get; }
        /// <summary>不可播原因（可播为 null）</summary>
        public string? Note { get; }
        public bool Enabled { get; set; } = true;
        public SiteRow(string name, string type, string? note)
        {
            Name = name; Type = type; Note = note;
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

    /// <summary>进入页面即聚焦订阅地址输入框（键盘/遥控直接输入，回车即添加）</summary>
    protected override void OnAppearing()
    {
        base.OnAppearing();
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            await Task.Delay(120);
            SubEntry.Focus();
        });
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
                Text = captured.Name + (captured.Note is null ? "" : "  · " + captured.Note),
                FontSize = 13,
                TextColor = captured.Note is null
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
                var typeName = s.SpiderKind switch
                {
                    VodSpiderKind.Jar => "spider jar",
                    VodSpiderKind.Script => "spider 脚本",
                    _ => s.Type switch
                    {
                        0 => "xml",
                        1 => "MacCMS json",
                        _ => $"type {s.Type}",
                    },
                };
                _sites.Add(new SiteRow(s.Name, typeName,
                    s.SpiderKind == VodSpiderKind.Script && CatClawVideo.Core.Models.SiteRegistry.JsSpiderAvailable
                        ? null
                        : s.StatusNote));
                added++;
            }
            skipped = sites.Count - added;

            // 写入站点仓库（首页/搜索从这里取可播站点）
            SiteRegistry.Replace(sites);

            RebuildSites();
            SubEntry.Text = "";
            var playableCount = sites.Count(s => s.Playable);
            await DisplayAlertAsync("订阅已添加",
                $"解析到 {sites.Count} 个站点，新增 {added} 个" +
                (skipped > 0 ? $"（跳过重复 {skipped} 个）" : "") +
                $"，其中可播 {playableCount} 个。", "确定");
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
