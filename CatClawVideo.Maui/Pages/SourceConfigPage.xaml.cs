using System.Collections.ObjectModel;
using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;
using CatClawVideo.Core.Providers;
using CatClawVideo.Data;
using Microsoft.Maui.Controls.Shapes;
namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 源配置页：订阅源管理（增删，真解析，持久化到数据库）+ 站点列表（开关）。
/// TVBox 明文 JSON 真解析（TvBoxSubscriptionManager）；加密源（饭太硬等）返回明确错误提示；
/// type=3 spider 源按 jar/脚本细分并标注所需运行时；type=1 MacCMS 源可直接播放。
/// </summary>
public partial class SourceConfigPage : ContentPage
{
    private readonly ISubscriptionManager _subscriptionManager;
    private readonly VideoDatabase _db;

    /// <summary>订阅行（携带数据库记录，删除时同步删库）</summary>
    private sealed class SubRow
    {
        public VodSubscription Sub { get; }
        public string Name => Sub.Name;
        public string Url => Sub.SourceUrl;
        public SubRow(VodSubscription sub) { Sub = sub; }
    }

    /// <summary>订阅列表（进入页面时从数据库加载）</summary>
    private readonly ObservableCollection<SubRow> _subs = new();

    private sealed class SiteRow
    {
        public string Name { get; }
        public string Type { get; }
        /// <summary>不可播原因（可播为 null）</summary>
        public string? Note { get; }
        /// <summary>原始站点信息（账号认证判定用）</summary>
        public VodSiteInfo Site { get; }
        public bool Enabled { get; set; } = true;
        public SiteRow(string name, string type, string? note, VodSiteInfo site)
        {
            Name = name; Type = type; Note = note; Site = site;
        }
    }

    private readonly List<SiteRow> _sites = [];

    public SourceConfigPage(ISubscriptionManager subscriptionManager, VideoDatabase db)
    {
        InitializeComponent();
        _subscriptionManager = subscriptionManager;
        _db = db;
        RebuildSites();
    }

    /// <summary>进入页面即聚焦订阅地址输入框（键盘/遥控直接输入，回车即添加），并从数据库加载订阅列表</summary>
    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = LoadSubsFromDbAsync();
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            await Task.Delay(120);
            SubEntry.Focus();
        });
    }

    /// <summary>从数据库加载订阅列表（数据库可能仍在后台建表，失败静默下次再载）</summary>
    private async Task LoadSubsFromDbAsync()
    {
        try
        {
            var subs = await _db.GetSubscriptionsAsync();
            _subs.Clear();
            foreach (var s in subs) _subs.Add(new SubRow(s));
            RebuildSubs();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[源配置] 订阅列表加载失败: {ex.Message}"); }
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
            tap.Tapped += (_, _) => _ = DeleteSubAsync(captured);
            del.GestureRecognizers.Add(tap);
            row.Add(del, 1);
            SubList.Children.Add(row);
        }
    }

    /// <summary>删除订阅：UI 移除 + 同步删库（失败不回滚 UI，下次进入以库为准）</summary>
    private async Task DeleteSubAsync(SubRow row)
    {
        _subs.Remove(row);
        RebuildSubs();
        try { await _db.DeleteSubscriptionAsync(row.Sub); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[源配置] 订阅删除失败: {ex.Message}"); }
    }

    private void RebuildSites()
    {
        SiteList.Children.Clear();
        foreach (var site in _sites)
        {
            var captured = site;
            var row = new Grid
            {
                ColumnDefinitions =
                [
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto),
                ],
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

            // 需账号认证的站点：显示「账号」入口（点击录入/修改凭据）
            if (site.Site.NeedsCredentials && site.Site.CredentialServers.Count > 0)
            {
                var saved = SpiderCredentials.Get(site.Site.CredentialServers[0]) is not null;
                var credLabel = new Label
                {
                    Text = saved ? "账号✓" : "账号",
                    FontSize = 12,
                    FontFamily = "OpenSansSemibold",
                    TextColor = (Color)Application.Current!.Resources["PrimaryColor"],
                    VerticalOptions = LayoutOptions.Center,
                    Margin = new Thickness(0, 0, 14, 0),
                };
                var credTap = new TapGestureRecognizer();
                credTap.Tapped += async (_, _) =>
                {
                    var dlg = new CredentialsDialogPage(site.Site.Name, site.Site.CredentialServers[0]);
                    await Navigation.PushModalAsync(dlg);
                    if (dlg.Saved) RebuildSites(); // 保存后刷新「账号✓」状态
                };
                credLabel.GestureRecognizers.Add(credTap);
                row.Add(credLabel, 1);
            }

            var sw = new Switch { IsToggled = captured.Enabled, HorizontalOptions = LayoutOptions.End, VerticalOptions = LayoutOptions.Center };
            sw.Toggled += (_, e) => captured.Enabled = e.Value;
            row.Add(sw, 2);

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
                        CatClawSourceDoc.SiteType => "猫爪源",
                        CatClawSourceWeb.WebSiteType => "猫爪源(web)",
                        _ => $"type {s.Type}",
                    },
                };
                _sites.Add(new SiteRow(s.Name, typeName,
                    s.SpiderKind == VodSpiderKind.Script && CatClawVideo.Core.Models.SiteRegistry.JsSpiderAvailable
                        ? null
                        : s.StatusNote, s));
                added++;
            }
            skipped = sites.Count - added;

            // 写入站点仓库（首页/搜索从这里取可播站点）
            SiteRegistry.Replace(sites);

            // 订阅入库（按地址去重，重复添加只刷新站点）
            var name = new Uri(url).Host;
            if (await _db.FindSubscriptionAsync(url) is null)
                await _db.AddSubscriptionAsync(new VodSubscription { Name = name, SourceUrl = url, Kind = "tvbox" });
            if (_subs.All(s => !string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase)))
                _subs.Add(new SubRow(new VodSubscription { Name = name, SourceUrl = url, Kind = "tvbox" }));

            RebuildSites();
            SubEntry.Text = "";

            // 需要账号认证的站点（alist 类）：逐个弹窗录入凭据，无凭据无法观看
            var credSite = sites.FirstOrDefault(x => x.NeedsCredentials);
            foreach (var server in SpiderCredentials.MissingServers(sites))
            {
                var dlg = new CredentialsDialogPage(credSite?.Name ?? "站点", server);
                await Navigation.PushModalAsync(dlg);
            }

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
