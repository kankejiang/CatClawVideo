using CatClawVideo.Core.Interfaces;
using CatClawVideo.Data;
using CatClawVideo.Maui.ViewModels;
using Microsoft.Maui.Controls.Shapes;

namespace CatClawVideo.Maui.Pages;

/// <summary>设置页：订阅源添加 / 关于（主题已锁定蓝色+深色，外观设置项已移除）。</summary>
public partial class SettingsPage : ContentView, ITabView
{
    private readonly SettingsViewModel _vm;
    private readonly IThemeService _theme;
    private readonly ISubscriptionManager _subscriptionManager;
    private readonly VideoDatabase _db;

    public SettingsPage(SettingsViewModel vm, IThemeService theme,
        ISubscriptionManager subscriptionManager, VideoDatabase db)
    {
        InitializeComponent();
        _vm = vm;
        _theme = theme;
        _subscriptionManager = subscriptionManager;
        _db = db;
        BindingContext = _vm;
    }

    public Task OnTabShownAsync()
    {
        // 主题锁定蓝色 + 深色（外观 UI 已移除，进入设置页即纠正历史存储值）
        _vm.SelectedTheme = Core.Interfaces.AppTheme.Blue;
        _vm.SelectedDarkMode = DarkModeSetting.Dark;
        // 版本号动态填充（避免硬编码过期）
        try { AboutVersionLabel.Text = $"猫爪影视 {AppInfo.Current?.VersionString ?? "0.0.0"}"; } catch { }
        LoadCacheCapSetting();
        LoadDiagnosticLogSetting();
        return Task.CompletedTask;
    }

    // ═══════════ 诊断日志（2026-09-18 用户要求：对齐猫爪音乐，可抓 Debug 级日志）═══════════

    /// <summary>回填开关状态时抑制 Toggled 回写（否则打开设置页会把默认关误写成开）</summary>
    private bool _diagLogLoading;

    private void LoadDiagnosticLogSetting()
    {
        _diagLogLoading = true;
        try
        {
            DiagnosticLogSwitch.IsToggled = Services.DiagnosticLog.Instance?.IsEnabled ?? false;
        }
        catch { }
        finally { _diagLogLoading = false; }
    }

    /// <summary>开关：开启即开始记录；关闭时立即刷盘（最后几行往往是关键现场）</summary>
    private void OnDiagnosticLogToggled(object? sender, ToggledEventArgs e)
    {
        if (_diagLogLoading) return;
        try
        {
            if (Services.DiagnosticLog.Instance is not { } log) return;
            log.IsEnabled = e.Value;

            if (e.Value)
            {
                var logDir = Core.AppPaths.Sub("logs");
                Core.Logging.Log.Info("Settings", "诊断日志已开启（" + logDir + "）");
                log.Flush();
            }
        }
        catch { }
    }

    /// <summary>进查看页（筛选/导出诊断包）</summary>
    private async void OnDiagnosticLogClicked(object? sender, TappedEventArgs e)
    {
        try { await Shell.Current.GoToAsync("diagnosticlog"); } catch { }
    }

    // ═══════════ 磁力缓存上限（2026-09-17 用户要求：5~15GB 偏小，默认提到 20GB 且可调）═══════════

    private bool _cacheCapLoading;

    private void LoadCacheCapSetting()
    {
        _cacheCapLoading = true;
        try
        {
            var opts = CatClawVideo.Core.Services.QemuThunder.StreamCachePrefs.OptionsGb;
            CacheCapPicker.ItemsSource = opts.Select(g => $"{g} GB").ToList();
            var cur = (long)Preferences.Default.Get("stream_cache_gb",
                (int)CatClawVideo.Core.Services.QemuThunder.StreamCachePrefs.DefaultGb);
            var idx = Array.IndexOf(opts, cur);
            CacheCapPicker.SelectedIndex = idx >= 0
                ? idx
                : Array.IndexOf(opts, CatClawVideo.Core.Services.QemuThunder.StreamCachePrefs.DefaultGb);
        }
        catch { }
        finally { _cacheCapLoading = false; }
    }

    private void OnCacheCapChanged(object? sender, EventArgs e)
    {
        if (_cacheCapLoading || CacheCapPicker.SelectedIndex < 0) return;
        try
        {
            var gb = CatClawVideo.Core.Services.QemuThunder.StreamCachePrefs.OptionsGb[CacheCapPicker.SelectedIndex];
            Preferences.Default.Set("stream_cache_gb", (int)gb);
            // 实时生效：引擎下一次超限清理即按新值判，无需重启
            CatClawVideo.Core.Services.QemuThunder.StreamCachePrefs.SetGb(gb);
            DiagLog.Write($"[缓存] 上限改为 {gb}GB");
        }
        catch { }
    }

    /// <summary>跳转关于页（品牌信息 / 免责声明 / 开源协议 / 检查更新）</summary>
    private async void OnAboutClicked(object? sender, TappedEventArgs e)
    {
        try { await Shell.Current.GoToAsync("about"); } catch { }
    }

    /// <summary>跳转源配置页（订阅源/站点完整管理）</summary>
    private async void OnOpenSourceConfig(object? sender, TappedEventArgs e)
    {
        try { await Shell.Current.GoToAsync("sourceconfig"); } catch { }
    }

    /// <summary>添加订阅：拉取解析 TVBox 配置 → 写库 → 站点仓库立即生效</summary>
    private async void OnAddSubClicked(object? sender, EventArgs e)
    {
        var url = SubEntry.Text?.Trim();
        if (string.IsNullOrEmpty(url)) return;

        var isLocal = File.Exists(url) || url.StartsWith("file://", StringComparison.OrdinalIgnoreCase);
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !isLocal)
        {
            await Shell.Current.DisplayAlertAsync("无效地址", "请输入 http(s) 订阅地址，或使用「📂 文件」导入本地源文件。", "确定");
            return;
        }

        await AddSubscriptionCoreAsync(url);
    }

    /// <summary>导入本地源文件（猫爪源 ccs / TVBox json）：文件对话框 → 同链路解析入库</summary>
    private async void OnImportFileClicked(object? sender, EventArgs e)
    {
        try
        {
#if WINDOWS
            // Win32 原生对话框：未打包环境下 WinRT FilePicker 可能抛 COM 异常，桌面端直接用 comdlg32
            var path = PickFileWin32();
#else
            var picked = await FilePicker.PickAsync(new PickOptions
            {
                PickerTitle = "选择猫爪源 / TVBox 订阅文件",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    [DevicePlatform.Android] = new[] { "application/json", "text/plain", "application/octet-stream" },
                }),
            });
            var path = picked?.FullPath;
#endif
            if (string.IsNullOrEmpty(path)) return;

            await AddSubscriptionCoreAsync(path);
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlertAsync("导入失败", ex.Message, "确定");
        }
    }

#if WINDOWS
    [System.Runtime.InteropServices.DllImport("comdlg32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool GetOpenFileNameW(ref OpenFileNameW ofn);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct OpenFileNameW
    {
        public uint lStructSize;
        public nint hwndOwner;
        public nint hInstance;
        public string lpstrFilter;
        public nint lpstrCustomFilter;
        public uint nMaxCustFilter;
        public uint nFilterIndex;
        public nint lpstrFile;
        public uint nMaxFile;
        public nint lpstrFileTitle;
        public uint nMaxFileTitle;
        public string lpstrInitialDir;
        public string lpstrTitle;
        public uint Flags;
        public ushort nFileOffset;
        public ushort nFileExtension;
        public string lpstrDefExt;
        public nint lCustData;
        public nint lpfnHook;
        public string lpTemplateName;
        public nint pvReserved;
        public uint dwReserved;
        public uint FlagsEx;
    }

    /// <summary>Win32 打开文件对话框（返回所选完整路径；取消返回 null）</summary>
    private static string? PickFileWin32()
    {
        const uint OFN_PATHMUSTEXIST = 0x800, OFN_FILEMUSTEXIST = 0x1000, OFN_HIDEREADONLY = 0x4;

        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(65536);
        try
        {
            // 写入空终止（缓冲区清零）
            System.Runtime.InteropServices.Marshal.WriteInt16(buffer, 0);

            var ofn = new OpenFileNameW
            {
                lStructSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<OpenFileNameW>(),
                hwndOwner = App.MainWindowHwnd,
                lpstrFilter = "源文件 (*.json;*.ccs;*.txt)\0*.json;*.ccs;*.txt\0所有文件 (*.*)\0*.*\0",
                lpstrFile = buffer,
                nMaxFile = 32768,
                lpstrTitle = "选择猫爪源 / TVBox 订阅文件",
                Flags = OFN_PATHMUSTEXIST | OFN_FILEMUSTEXIST | OFN_HIDEREADONLY,
            };

            if (!GetOpenFileNameW(ref ofn)) return null; // 用户取消
            return System.Runtime.InteropServices.Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }
#endif

    /// <summary>订阅添加核心流程（地址/本地文件共用）：解析 → 站点仓库生效 → 入库去重 → 认证弹窗 → 结果反馈</summary>
    private async Task AddSubscriptionCoreAsync(string urlOrPath)
    {
        try
        {
            AddSubButton.IsEnabled = false;
            ImportFileButton.IsEnabled = false;
            var sites = await _subscriptionManager.LoadSubscriptionAsync(urlOrPath);

            // 站点仓库立即生效（首页/搜索事件刷新）
            SiteRegistry.Replace(sites);

            // 订阅入库（按地址去重；本地文件存绝对路径，重启后仍可恢复）
            var name = urlOrPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? new Uri(urlOrPath).Host
                : System.IO.Path.GetFileNameWithoutExtension(urlOrPath);
            if (await _db.FindSubscriptionAsync(urlOrPath) is null)
                await _db.AddSubscriptionAsync(new VodSubscription { Name = name, SourceUrl = urlOrPath, Kind = "tvbox" });

            SubEntry.Text = "";

            // 需要账号认证的站点（alist 类）：逐个弹窗录入凭据，无凭据无法观看
            foreach (var server in Core.Providers.SpiderCredentials.MissingServers(sites))
            {
                var dlg = new CredentialsDialogPage(name, server);
                await Shell.Current.Navigation.PushModalAsync(dlg);
            }

            // 计数与 SiteRegistry.Playable 口径一致：spider 站在运行时就绪时也算可播（此前只数 type1，误导）
            var regPlayable = Core.Models.SiteRegistry.Playable.Select(s => s.Key).ToHashSet();
            var playableCount = sites.Count(s => s.Playable || regPlayable.Contains(s.Key));
            await Shell.Current.DisplayAlertAsync("订阅已添加",
                $"解析到 {sites.Count} 个站点，其中可播 {playableCount} 个。", "确定");
        }
        catch (NotSupportedException ex)
        {
            await Shell.Current.DisplayAlertAsync("暂不支持该订阅", ex.Message, "确定");
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlertAsync("订阅添加失败", $"无法拉取或解析该地址：{ex.Message}", "确定");
        }
        finally
        {
            AddSubButton.IsEnabled = true;
            ImportFileButton.IsEnabled = true;
        }
    }
}
