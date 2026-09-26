using CatClawVideo.Core.Live;
using CatClawVideo.Core.Services;
using CatClawVideo.Data;
using CatClawVideo.Maui.Controls;

namespace CatClawVideo.Maui.Services;

/// <summary>
/// 配置包导出 / 导入（对位 TVBox <c>BackupDialog</c>）+ 设置项**登记表**。
///
/// <para><b>为什么是登记表而不是「扫一遍 Preferences」</b>：MAUI 的 <c>Preferences</c> 没有列举全部键的 API
/// （实测 net11 预览版的 <c>Preferences</c> 只有 Get/Set/Remove/ContainsKey），而扫平台存储文件又各端一套。
/// 所以新增设置时必须在这里补一行 —— 漏了不会崩，只是那一项不会被带走，
/// 这比「静默把用户设置改坏」可控，也让「备份覆盖了哪些项」是一个能直接读出来的清单。</para>
///
/// <para><b>写回走各自的访问器</b>（<see cref="HistoryCap"/>/<see cref="DecoderModePrefs"/> …），
/// 不重复写键名字面量：键名改了这里会跟着编译失败，而不是备份悄悄失灵。</para>
/// </summary>
public static class SettingsBackup
{
    readonly record struct Item(string Key, Func<string> Read, Action<string> Write);

    static readonly List<Item> Catalog = new()
    {
        new("m3u8_purify",
            () => M3u8Purifier.Enabled ? "1" : "0",
            v => { M3u8Purifier.Enabled = v == "1"; Preferences.Default.Set("m3u8_purify", v == "1"); }),

        new("stream_cache_gb",
            () => Core.Services.QemuThunder.StreamCachePrefs.CapGb + "",
            v => { if (long.TryParse(v, out var gb)) Core.Services.QemuThunder.StreamCachePrefs.SetGb(gb); }),

        new(HistoryKey,
            () => HistoryCap.Load() + "",
            v => { if (int.TryParse(v, out var n)) HistoryCap.Save(n); }),

        new(DecoderKey,
            () => DecoderModePrefs.Load().ToString(),
            v => { if (Enum.TryParse<VideoDecoderMode>(v, out var m)) DecoderModePrefs.Save(m); }),

        new(AspectKey,
            () => AspectPrefs.Load().ToString(),
            v => { if (Enum.TryParse<VideoAspect>(v, out var a)) AspectPrefs.Save(a); }),

        // 倍速存的是档位值而不是档位下标：Save 内部会映射回下标，读回来才不会有浮点串漂移
        new(SpeedKey,
            () => SpeedPrefs.Load() + "",
            v => { if (double.TryParse(v, out var d)) SpeedPrefs.Save(d); }),

        new("search_history",
            () => Preferences.Default.Get("search_history", string.Empty),
            v => Preferences.Default.Set("search_history", v)),

        new("source_history",
            () => Preferences.Default.Get("source_history", string.Empty),
            v => Preferences.Default.Set("source_history", v)),
    };

    // 与上面各访问器内部键名保持一致（这些是 Preferences 键，不是访问器名）
    const string HistoryKey = "history_max";
    const string DecoderKey = "decoder_mode";
    const string AspectKey = "default_aspect";
    const string SpeedKey = "default_speed_index";

    /// <summary>随配置包带走的独立配置文件（直播设置 / 搜索源勾选）。</summary>
    static readonly (string Name, Func<string> Path)[] Files =
    [
        ("live-settings.json", () => LiveSourceService.SettingsFilePath),
        ("search-sources.json", () => SearchSourceStore.FilePath),
    ];

    public static string FileName => $"catclaw-backup-{DateTime.Now:yyyyMMdd-HHmmss}.json";

    /// <summary>导出一份完整配置包文本（不写文件，交给调用方决定落点/分享）。</summary>
    public static async Task<string> ExportAsync(VideoDatabase db)
    {
        var bundle = new BackupBundle
        {
            ExportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };
        foreach (var s in await db.GetAllSubscriptionsAsync())
            bundle.Subscriptions.Add(new BackupSubscription
            {
                Name = s.Name,
                Url = s.SourceUrl,
                Kind = s.Kind,
                Enabled = s.Enabled,
                SortOrder = s.SortOrder,
            });
        foreach (var item in Catalog)
            try { bundle.Settings[item.Key] = item.Read(); } catch { }
        foreach (var (name, path) in Files)
            try
            {
                var p = path();
                if (File.Exists(p)) bundle.Files[name] = File.ReadAllText(p);
            }
            catch { }
        return bundle.ToJson();
    }

    public sealed class Result
    {
        public int SubsAdded { get; set; }
        public int SubsSkipped { get; set; }
        public int SettingsApplied { get; set; }
        public int FilesRestored { get; set; }
        public string Summary =>
            $"订阅 新增 {SubsAdded} / 已存在 {SubsSkipped}，设置项 {SettingsApplied} 项，配置文件 {FilesRestored} 个";
    }

    /// <summary>
    /// 导入（合并式，不清空现有数据 —— 用户手上有多份备份时，「导入即覆盖一切」太容易出事）。
    /// <para>返回的 <see cref="Result"/> 供 UI 直接播报，用户要看得见到底动了什么。</para>
    /// </summary>
    public static async Task<Result> ImportAsync(VideoDatabase db, string json)
    {
        var bundle = BackupBundle.Parse(json, out var error);
        if (bundle is null) throw new InvalidDataException(error ?? "备份包无法解析");
        var r = new Result();

        foreach (var s in bundle.Subscriptions)
        {
            if (string.IsNullOrWhiteSpace(s.Url)) continue;
            if (await db.FindSubscriptionAsync(s.Url) is not null) { r.SubsSkipped++; continue; }
            await db.AddSubscriptionAsync(new VodSubscription
            {
                Name = string.IsNullOrWhiteSpace(s.Name) ? SafeHost(s.Url) : s.Name,
                SourceUrl = s.Url,
                Kind = string.IsNullOrWhiteSpace(s.Kind) ? "tvbox" : s.Kind,
                Enabled = s.Enabled,
                SortOrder = s.SortOrder,
            });
            r.SubsAdded++;
        }

        foreach (var item in Catalog)
        {
            if (!bundle.Settings.TryGetValue(item.Key, out var value)) continue;
            try { item.Write(value); r.SettingsApplied++; } catch { }
        }

        foreach (var (name, path) in Files)
        {
            if (!bundle.Files.TryGetValue(name, out var text)) continue;
            try
            {
                var target = path();
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, text);
                r.FilesRestored++;
            }
            catch { }
        }
        return r;
    }

    static string SafeHost(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.Length > 0) return u.Host;
        }
        catch { }
        return "导入的订阅";
    }
}
