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
#if ANDROID
        // Edge-to-Edge：推入式页面必须自己补顶部安全区，否则顶栏压状态栏（原因见 SafeAreaHelper.ApplyPageTopInset）
        SafeAreaHelper.ApplyPageTopInset(this);
#endif
        _subscriptionManager = subscriptionManager;
        _db = db;
        RebuildSites();
        RebuildHistory();
    }

    // ═══════════════════ 最近添加过的订阅地址（对位 TVBox API_HISTORY + ApiHistoryDialog）═══════════════════

    const string HistoryKey = "source_history";
    const int HistoryLimit = 10;

    static List<string> LoadHistory() =>
        (Microsoft.Maui.Storage.Preferences.Default.Get(HistoryKey, "") ?? "")
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

    /// <summary>成功添加后记一条：去重 + 置顶 + 截断（存的是原样地址，chip 上只显示主机名）。</summary>
    private static void PushHistory(string url)
    {
        var list = LoadHistory();
        list.RemoveAll(x => string.Equals(x, url, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, url);
        if (list.Count > HistoryLimit) list.RemoveRange(HistoryLimit, list.Count - HistoryLimit);
        Microsoft.Maui.Storage.Preferences.Default.Set(HistoryKey, string.Join('\n', list));
    }

    private void RebuildHistory()
    {
        HistoryRow.Children.Clear();
        var list = LoadHistory();
        HistoryLabel.IsVisible = list.Count > 0;
        foreach (var url in list)
        {
            var chip = new Button
            {
                Text = ShortHost(url),
                FontSize = 11.5,
                Padding = new Thickness(10, 4),
                CornerRadius = 8,
                BackgroundColor = Colors.Transparent,
                BorderWidth = 0,
                Margin = new Thickness(0, 0, 6, 0),
            };
            chip.SetDynamicResource(Button.TextColorProperty, "TextSecondaryColor");
            var captured = url;
            // 点一下只是「填回输入框」，不直接添加 —— 换订阅是要看结果的，不该一步到位
            chip.GestureRecognizers.Add(new TapGestureRecognizer
            { Command = new Command(() => SubEntry.Text = captured) });
            HistoryRow.Children.Add(chip);
        }
    }

    static string ShortHost(string url)
    {
        var text = url;
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.Length > 0) text = u.Host;
        }
        catch { }
        return text.Length > 26 ? text[..23] + "…" : text;
    }

    /// <summary>进入页面即聚焦订阅地址输入框（键盘/遥控直接输入，回车即添加），并从数据库加载订阅列表</summary>
    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = LoadSubsFromDbAsync();
        // ⚠ 重启后「站点列表」不能空着（2026-09-16 用户实测）：启动恢复已把订阅站点灌进
        // SiteRegistry，而页面本地 _sites 只在「手动添加订阅」时才会填 → 这里从注册表回填一份。
        SeedSitesFromRegistry();
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            await Task.Delay(120);
            SubEntry.Focus();
        });
    }

    /// <summary>把启动恢复出来的站点回填到页面列表（按 Key 去重，不覆盖手动添加的条目）。</summary>
    private void SeedSitesFromRegistry()
    {
        try
        {
            var existing = _sites.Select(s => s.Site.Key).ToHashSet(StringComparer.Ordinal);
            var added = 0;
            foreach (var site in Core.Models.SiteRegistry.Sites)
            {
                if (!existing.Add(site.Key)) continue;
                _sites.Add(BuildRow(site));
                added++;
            }
            if (added > 0 || _sites.Count > 0) RebuildSites();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[源配置] 站点回填失败: {ex.Message}"); }
    }

    /// <summary>由站点信息构造列表行（类型名 + 备注；手动添加与启动回填共用同一口径）。</summary>
    private static SiteRow BuildRow(VodSiteInfo s)
    {
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
        // 备注只在「运行时真的不可用」时才该显示：
        // 解析期写入的 StatusNote 是按「是否 MacCMS(type=1)」判可播得来的，type=3 站点一律带着
        // 「需 spider 运行时」；若这里不按运行时豁免，jar 源会在**桥明明可用**的机器上照样显示该提示
        // （2026-09-21 用户反馈「显示需要 spider 运行」—— 实测 jar桥=True 仍显示，纯误导）。
        var note = s.SpiderKind switch
        {
            VodSpiderKind.Script when CatClawVideo.Core.Models.SiteRegistry.JsSpiderAvailable => null,
            VodSpiderKind.Jar when CatClawVideo.Core.Models.SiteRegistry.JarSpiderAvailable => null,
            _ => s.StatusNote,
        };
        return new SiteRow(s.Name, typeName, note, s);
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
            var tail = new HorizontalStackLayout { Spacing = 12, VerticalOptions = LayoutOptions.Center };
            var captured = sub;
            var swap = new Label { Text = "换线路", FontSize = 11.5, TextColor = Color.FromArgb("#2b6cb0") };
            swap.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => _ = SwitchLineAsync(captured)) });
            var del = new Label { Text = "删除", FontSize = 11.5, TextColor = Color.FromArgb("#c0392b") };
            del.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => _ = DeleteSubAsync(captured)) });
            tail.Add(swap);
            tail.Add(del);
            row.Add(tail, 1);
            SubList.Children.Add(row);
        }
    }

    /// <summary>
    /// 切换这条订阅用的线路（对位 TVBox 设置页的「线路选择」）。
    /// <para>只改地址后缀与显示名并写库 —— 本会话已经并入 <c>SiteRegistry</c> 的旧线路站点
    /// 不会凭空消失（与「再加一个订阅」的现有行为一致），所以提示里说清「下次拉取按新线路」。</para>
    /// </summary>
    private async Task SwitchLineAsync(SubRow row)
    {
        var (rawUrl, current) = TvBoxSubscriptionManager.SplitLine(row.Url);
        var lines = await _subscriptionManager.ProbeLinesAsync(rawUrl);
        if (lines.Count <= 1)
        {
            await DisplayAlertAsync("切换线路",
                lines.Count == 1 ? "这条订阅只有一条线路，没有可切换的。" : "这条订阅不是「影视仓多仓」地址。", "好");
            return;
        }

        var names = lines.Select(l => l.Name).ToArray();
        var nowIdx = current >= 0 && current < names.Length ? current : 0;
        var pick = await DisplayActionSheetAsync("选择线路（当前：" + names[nowIdx] + "）", "取消", null, names);
        var idx = Array.IndexOf(names, pick);
        if (idx < 0 || idx == nowIdx) return;

        row.Sub.SourceUrl = rawUrl + "#line=" + idx;
        row.Sub.Name = ShortHost(rawUrl) + " · " + names[idx];
        RebuildSubs();
        try { await _db.UpdateSubscriptionAsync(row.Sub); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[源配置] 线路改写失败: {ex.Message}"); }
        await DisplayAlertAsync("已切换线路",
            names[idx] + System.Environment.NewLine + "下次拉取这条订阅时按新线路取站点。", "好");
    }

    /// <summary>删除订阅：UI 移除 + 同步删库（失败不回滚 UI，下次进入以库为准）</summary>
    private async Task DeleteSubAsync(SubRow row)
    {
        _subs.Remove(row);
        RebuildSubs();
        try { await _db.DeleteSubscriptionAsync(row.Sub); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[源配置] 订阅删除失败: {ex.Message}"); }

        // 多订阅并存：被删订阅的站点要从仓库里退场 —— 重载剩余订阅并整体替换
        try
        {
            var rest = await _db.GetSubscriptionsAsync();
            var merged = await _subscriptionManager.LoadAllSubscriptionsAsync(
                rest.Select(s => new CatClawVideo.Core.Interfaces.SubscriptionRef(s.Name, s.SourceUrl)));
            SiteRegistry.Replace(merged);
            Core.Models.SiteCache.Save(merged);
            _sites.Clear();
            foreach (var s in merged) _sites.Add(BuildRow(s));
            RebuildSites();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[源配置] 剩余订阅重载失败: {ex.Message}"); }
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
            // 影视仓「多仓」：顶层只有 urls。先探一下，多于一条线就先问用户要哪一条，
            // 选中结果以 #line=N 后缀写回地址（订阅表因此不用改结构，换线路也只是改这一个字符串）。
            var repoLines = await _subscriptionManager.ProbeLinesAsync(url);
            string? chosenLine = null;
            if (repoLines.Count > 1)
            {
                var names = repoLines.Select(l => l.Name).ToArray();
                var pick = await DisplayActionSheetAsync("这条订阅是多仓，选一条线路", "取消", null, names);
                var li = Array.IndexOf(names, pick);
                if (li < 0) return;         // 取消 = 什么都不添加
                url += "#line=" + li;
                chosenLine = names[li];
            }

            var sites = await _subscriptionManager.LoadSubscriptionAsync(url);

            // 合并去重（按站点名）
            var existing = _sites.Select(s => s.Name).ToHashSet();
            int added = 0, skipped = 0;
            foreach (var s in sites)
            {
                if (!existing.Add(s.Name)) continue;
                _sites.Add(BuildRow(s));   // 类型名/备注口径与启动回填一致
                added++;
            }
            skipped = sites.Count - added;

            // 多订阅并存（2026-09-26）：现注册表快照 + 新订阅站点按 Key 覆盖合并
            // （重加同一订阅 = 刷新其站点；不同订阅的站点共存）。此前 Replace(sites)
            // 会把已装订阅的站点整体顶掉。
            var mergedTable = SiteRegistry.Sites.ToDictionary(s => s.Key, s => s, StringComparer.OrdinalIgnoreCase);
            foreach (var s in sites) mergedTable[s.Key] = s;
            var mergedList = mergedTable.Values.ToList();

            // 写入站点仓库（首页/搜索从这里取可播站点）+ 落盘缓存（下次启动秒读）
            SiteRegistry.Replace(mergedList);
            Core.Models.SiteCache.Save(mergedList);

            // 订阅入库（按地址去重，重复添加只刷新站点）
            var name = chosenLine is null ? new Uri(url).Host : ShortHost(url) + " · " + chosenLine;
            if (await _db.FindSubscriptionAsync(url) is null)
                await _db.AddSubscriptionAsync(new VodSubscription { Name = name, SourceUrl = url, Kind = "tvbox" });
            if (_subs.All(s => !string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase)))
                _subs.Add(new SubRow(new VodSubscription { Name = name, SourceUrl = url, Kind = "tvbox" }));

            RebuildSites();
            SubEntry.Text = "";
            PushHistory(url);
            RebuildHistory();

            // 需要账号认证的站点（alist 类）：逐个弹窗录入凭据，无凭据无法观看
            var credSite = sites.FirstOrDefault(x => x.NeedsCredentials);
            foreach (var server in SpiderCredentials.MissingServers(sites))
            {
                var dlg = new CredentialsDialogPage(credSite?.Name ?? "站点", server);
                await Navigation.PushModalAsync(dlg);
            }

            // 计数与 SiteRegistry.Playable 口径一致：spider 站在运行时就绪时也算可播（此前只数 type1，误导）
            var regPlayable = Core.Models.SiteRegistry.Playable.Select(s => s.Key).ToHashSet();
            var playableCount = sites.Count(s => s.Playable || regPlayable.Contains(s.Key));
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
