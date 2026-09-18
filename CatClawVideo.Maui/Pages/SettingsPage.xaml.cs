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
    private readonly MainViewModel _mainVm;

    public SettingsPage(SettingsViewModel vm, IThemeService theme,
        ISubscriptionManager subscriptionManager, VideoDatabase db, MainViewModel mainVm)
    {
        InitializeComponent();
        _vm = vm;
        _theme = theme;
        _subscriptionManager = subscriptionManager;
        _db = db;
        _mainVm = mainVm;
        BindingContext = _vm;
    }

    public Task OnTabShownAsync()
    {
        // 主题锁定蓝色 + 深色（外观 UI 已移除，进入设置页即纠正历史存储值）
        _vm.SelectedTheme = Core.Interfaces.AppTheme.Blue;
        _vm.SelectedDarkMode = DarkModeSetting.Dark;
        // 版本号动态填充（避免硬编码过期）
        try { AboutVersionLabel.Text = $"猫爪影视 {AppInfo.Current?.VersionString ?? "0.0.0"}"; } catch { }
        LoadNodeSettings();
        LoadNodeHostSettings();
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

    // ═══════════ 本机作为解析节点（手机端）═══════════

    /// <summary>
    /// 手机端：显示本机节点地址/口令/二维码，并可一键配对到电脑。
    /// PC 端则相反 —— 显示「填手机地址」的卡片。两平台共用同一份 XAML，这里按平台切换可见性。
    /// </summary>
    private void LoadNodeHostSettings()
    {
        bool isHost = CatClawVideo.Core.Providers.RemoteSpiderNode.IsNodeHost;
        NodeHostCard.IsVisible = isHost;
        NodeClientCard.IsVisible = !isHost;
        if (!isHost) return;

        try
        {
            var ip = CatClawVideo.Core.Services.LanInfo.PrimaryIPv4();
            const int port = 8899;
            var token = Preferences.Default.Get("node_token", "");

            NodeAddressLabel.Text = $"本机节点：http://{ip}:{port}";
            NodeTokenLabel.Text = string.IsNullOrEmpty(token) ? "（口令未生成）" : $"口令：{token}";

            if (NodeQrImage.Source is null)
            {
                var payload = $"http://{ip}:{port}" + (string.IsNullOrEmpty(token) ? "" : $"?token={token}");
                NodeQrImage.Source = ImageSource.FromStream(() => new MemoryStream(Services.PairQr.Png(payload, 5)));
            }
        }
        catch (Exception ex)
        {
            NodeTokenLabel.Text = $"初始化失败：{ex.Message}";
        }
    }

    /// <summary>把本机节点登记到电脑（POST /pair 到 PC 的 LinkServer）</summary>
    private async void OnPairToPcClicked(object? sender, EventArgs e)
    {
        var raw = PcAddressEntry.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(raw))
        {
            PairStatusLabel.Text = "请先填电脑上显示的地址（形如 192.168.1.5:8900）";
            return;
        }

        // 容错：允许只填 IP、填 http://、末尾带 /
        var hostPort = raw.Replace("http://", "").Replace("https://", "").TrimEnd('/');
        var url = $"http://{hostPort}/pair";

        PairButton.IsEnabled = false;
        PairStatusLabel.Text = "正在配对…";
        try
        {
            var ip = CatClawVideo.Core.Services.LanInfo.PrimaryIPv4();
            var token = Preferences.Default.Get("node_token", "");
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                node = $"http://{ip}:8899",
                token,
                name = Services.PairQr.DeviceName(),
            });

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var body = await (await http.PostAsync(url, content)).Content.ReadAsStringAsync();

            PairStatusLabel.Text = body.Contains("\"ok\":true")
                ? $"✅ 配对成功，电脑已记住本机节点（{ip}:8899）"
                : $"❌ 电脑返回：{Trim(body)}";
        }
        catch (Exception ex)
        {
            PairStatusLabel.Text = $"❌ 连不上电脑：{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            PairButton.IsEnabled = true;
        }
    }

    // ═══════════ 手机解析节点 ═══════════

    /// <summary>回填当前节点配置（扫码配对后也会走到这里刷新）</summary>
    private void LoadNodeSettings()
    {
        try
        {
            NodeEntry.Text = CatClawVideo.Core.Providers.RemoteSpiderNode.BaseUrl ?? "";
            NodeTokenEntry.Text = CatClawVideo.Core.Providers.RemoteSpiderNode.Token ?? "";
            var url = CatClawVideo.Core.Providers.RemoteSpiderNode.BaseUrl;
            NodeStatusLabel.Text = string.IsNullOrEmpty(url)
                ? "未配置 —— Guard 加固源在本机不可用（其余源不受影响）"
                : $"已配置：{url}";
        }
        catch { }
    }

    /// <summary>测试节点连通性：GET /ping</summary>
    private async void OnTestNodeClicked(object? sender, EventArgs e)
    {
        var url = NodeEntry.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(url))
        {
            NodeStatusLabel.Text = "请先填写手机端显示的地址";
            return;
        }

        NodeTestButton.IsEnabled = false;
        NodeStatusLabel.Text = "正在测试…";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var baseUrl = url.TrimEnd('/');
            var token = NodeTokenEntry.Text?.Trim();
            var probe = string.IsNullOrEmpty(token) ? "/ping" : $"/ping?token={Uri.EscapeDataString(token)}";
            var body = await http.GetStringAsync(baseUrl + probe);

            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var ok = doc.RootElement.TryGetProperty("ok", out var o) && o.ValueKind == System.Text.Json.JsonValueKind.True;
            NodeStatusLabel.Text = ok
                ? "✅ 连接成功。点「保存」生效（Guard 站点将走该节点解析）"
                : $"❌ 节点返回异常：{Trim(body)}";
        }
        catch (Exception ex)
        {
            NodeStatusLabel.Text = $"❌ 连不上：{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            NodeTestButton.IsEnabled = true;
        }
    }

    private void OnSaveNodeClicked(object? sender, EventArgs e)
    {
        var url = NodeEntry.Text?.Trim();
        if (string.IsNullOrEmpty(url))
        {
            NodeStatusLabel.Text = "地址为空，如需关闭请点「清除」";
            return;
        }
        CatClawVideo.Core.Providers.RemoteSpiderNode.Set(url, NodeTokenEntry.Text);
        LoadNodeSettings();
        NodeStatusLabel.Text = $"✅ 已保存：{CatClawVideo.Core.Providers.RemoteSpiderNode.BaseUrl}";
    }

    private void OnClearNodeClicked(object? sender, EventArgs e)
    {
        CatClawVideo.Core.Providers.RemoteSpiderNode.Clear();
        LoadNodeSettings();
        NodeStatusLabel.Text = "已清除，全部源改回本机解析";
    }

    private static string Trim(string s) => s.Length <= 200 ? s : s[..200] + "…";

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

    /// <summary>切到顶部「下载」tab（下载管理已从独立 Shell 页面改为 tab，索引见 MainViewModel.Tabs）</summary>
    private void OnOpenDownloads(object? sender, EventArgs e) => _mainVm.SelectTab(3);

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
