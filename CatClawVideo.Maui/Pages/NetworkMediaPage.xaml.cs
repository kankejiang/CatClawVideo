using CatClawVideo.Core.Network;
using Microsoft.Maui.Controls.Shapes;

// MAUI 的 Connectivity API 里有同名类型（Microsoft.Maui.Networking.ConnectionProfile），显式消歧
using ConnectionProfile = CatClawVideo.Core.Network.ConnectionProfile;
// Microsoft.Maui.Controls.Shapes.Path 与 System.IO.Path 同名
using IOPath = System.IO.Path;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 网络媒体页：WebDAV 连接管理 + 远程目录浏览 + 点击播放。
///
/// <para>三态面板（同页切换，返回键逐级退出）：</para>
/// <list type="number">
/// <item>连接列表 —— 增删改、点卡片即连接；进入页面自动恢复上次使用的连接</item>
/// <item>连接编辑 —— 主机/端口/HTTPS/账号密码/根路径，测试连接给出探测结论</item>
/// <item>目录浏览 —— 文件夹优先排列，点媒体文件经本地流代理进全屏播放器</item>
/// </list>
///
/// <para>播放链路：媒体文件 → <see cref="WebDavStreamProxy"/> 生成本地 URL
/// （Basic Auth / OpenList 重定向都在代理内部消化）→ <c>player</c> 路由。</para>
/// </summary>
public partial class NetworkMediaPage : ContentPage
{
    private readonly WebDavProfileStore _store;
    private readonly WebDavService _webDav;
    private readonly WebDavStreamProxy _proxy;

    private enum Panel { Profiles, Editor, Browser }

    private Panel _panel = Panel.Profiles;
    private ConnectionProfile? _editingProfile;   // 编辑中（null = 新建）
    private ConnectionProfile? _activeProfile;    // 正在浏览的连接
    private string _currentPath = "/";
    private bool _busy;
    private int _toastToken;

    /// <summary>可播放的媒体扩展名（视频为主，音频顺带——播放内核都能放）。</summary>
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mkv", ".avi", ".mov", ".flv", ".webm", ".ts", ".m2ts", ".mts",
        ".wmv", ".mpg", ".mpeg", ".vob", ".rmvb", ".rm", ".3gp", ".m3u8",
        ".mp3", ".flac", ".m4a", ".aac", ".wav", ".ape", ".ogg", ".wma",
    };

    public NetworkMediaPage(WebDavProfileStore store, WebDavService webDav, WebDavStreamProxy proxy)
    {
        InitializeComponent();
#if ANDROID
        // Edge-to-Edge：推入式页面必须自己补顶部安全区，否则顶栏压状态栏（同 SourceConfigPage）
        SafeAreaHelper.ApplyPageTopInset(this);
#endif
        _store = store;
        _webDav = webDav;
        _proxy = proxy;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // 从播放器返回时保持浏览现场，不做任何重载
        if (_panel == Panel.Browser) return;

        BuildProfileList();

        // 首次进入：自动恢复上次使用的连接
        if (_panel == Panel.Profiles && _activeProfile == null)
        {
            var last = _store.Find(_store.Prefs.LastProfileId);
            if (last != null) await ConnectAsync(last);
        }
    }

    // ═══════════════════ 面板切换 ═══════════════════

    private void ShowPanel(Panel panel)
    {
        _panel = panel;
        ProfilesPanel.IsVisible = panel == Panel.Profiles;
        EditorPanel.IsVisible = panel == Panel.Editor;
        BrowserPanel.IsVisible = panel == Panel.Browser;
    }

    private void GoBack()
    {
        switch (_panel)
        {
            case Panel.Editor:
            case Panel.Browser:
                ShowPanel(Panel.Profiles);
                BuildProfileList();
                break;
            default:
                _ = Shell.Current.GoToAsync("..");
                break;
        }
    }

    protected override bool OnBackButtonPressed()
    {
        if (_panel == Panel.Profiles)
        {
            _ = Shell.Current.GoToAsync("..");
            return true;
        }
        GoBack();
        return true;
    }

    private void OnBackTapped(object? sender, TappedEventArgs e) => GoBack();

    // ═══════════════════ 连接列表 ═══════════════════

    private void BuildProfileList()
    {
        ProfileList.Children.Clear();
        var profiles = _store.Prefs.Profiles;
        ProfileEmpty.IsVisible = profiles.Count == 0;

        foreach (var profile in profiles)
        {
            ProfileList.Children.Add(BuildProfileCard(profile));
        }
    }

    private View BuildProfileCard(ConnectionProfile profile)
    {
        var title = new Label
        {
            Text = profile.Name,
            FontSize = 14,
            FontFamily = "OpenSansSemibold",
            TextColor = (Color)Application.Current!.Resources["TextPrimaryColor"],
            LineBreakMode = LineBreakMode.TailTruncation,
        };
        var sub = new Label
        {
            Text = $"webdav://{profile.Host}:{profile.Port}{profile.BasePath}" +
                   (profile.ServerType == (int)WebDavServerType.OpenList ? "（OpenList/Alist）" : ""),
            FontSize = 11,
            TextColor = (Color)Application.Current!.Resources["TextHintColor"],
            LineBreakMode = LineBreakMode.MiddleTruncation,
        };
        var info = new VerticalStackLayout { Spacing = 3, VerticalOptions = LayoutOptions.Center };
        info.Add(title);
        info.Add(sub);

        var edit = ActionLabel("编辑", (Color)Application.Current!.Resources["PrimaryColor"], () => OpenEditor(profile));
        var del = ActionLabel("删除", Color.FromArgb("#c0392b"), () =>
        {
            _store.Remove(profile.Id);
            if (_activeProfile?.Id == profile.Id) _activeProfile = null;
            BuildProfileList();
        });

        var actions = new HorizontalStackLayout { Spacing = 14, VerticalOptions = LayoutOptions.Center };
        actions.Add(edit);
        actions.Add(del);

        var grid = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
        };
        grid.Add(info, 0);
        grid.Add(actions, 1);

        var border = new Border
        {
            Content = grid,
            Padding = new Thickness(16, 12),
            StrokeThickness = 1,
            Stroke = (Color)Application.Current!.Resources["DividerColor"],
            BackgroundColor = (Color)Application.Current!.Resources["CardBackgroundColor"],
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(12) },
        };
        border.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() => _ = ConnectAsync(profile)),
        });
        return border;
    }

    private void OnAddProfileClicked(object? sender, EventArgs e) => OpenEditor(null);

    // ═══════════════════ 连接编辑 ═══════════════════

    private void OpenEditor(ConnectionProfile? profile)
    {
        _editingProfile = profile;
        EditorTitle.Text = profile == null ? "添加 WebDAV 连接" : "编辑连接";
        NameEntry.Text = profile?.Name ?? "";
        HostEntry.Text = profile?.Host ?? "";
        PortEntry.Text = profile is { Port: > 0 and not 80 and not 443 } ? profile.Port.ToString() : "";
        HttpsSwitch.IsToggled = profile?.UseHttps ?? false;
        UserEntry.Text = profile?.UserName ?? "";
        PasswordEntry.Text = profile?.Password ?? "";
        BasePathEntry.Text = profile?.BasePath is { Length: > 0 } bp ? bp : "/";
        EditorStatus.Text = "";
        ShowPanel(Panel.Editor);
    }

    /// <summary>从表单收集连接（校验主机非空；端口缺省按 80/443）。</summary>
    private ConnectionProfile? CollectProfile()
    {
        var host = (HostEntry.Text ?? "").Trim();
        if (host.Length == 0)
        {
            EditorStatus.Text = "请填写主机地址";
            return null;
        }

        var useHttps = HttpsSwitch.IsToggled;
        var port = 80;
        if (int.TryParse((PortEntry.Text ?? "").Trim(), out var parsed) && parsed is > 0 and <= 65535)
            port = parsed;
        else
            port = useHttps ? 443 : 80;

        var basePath = (BasePathEntry.Text ?? "").Trim();
        if (basePath.Length == 0) basePath = "/";
        if (!basePath.StartsWith('/')) basePath = "/" + basePath;

        var profile = _editingProfile ?? new ConnectionProfile { Id = _store.NextId() };
        profile.Name = (NameEntry.Text ?? "").Trim();
        if (profile.Name.Length == 0) profile.Name = host;
        profile.Host = host;
        profile.Port = port;
        profile.UseHttps = useHttps;
        profile.UserName = (UserEntry.Text ?? "").Trim();
        profile.Password = (PasswordEntry.Text ?? "").Trim();
        profile.BasePath = basePath;
        return profile;
    }

    private async void OnTestClicked(object? sender, EventArgs e)
    {
        if (_busy) return;
        var profile = CollectProfile();
        if (profile == null) return;

        _busy = true;
        EditorStatus.Text = "测试中…";
        EditorStatus.TextColor = (Color)Application.Current!.Resources["TextSecondaryColor"];
        try
        {
            var (ok, message) = await _webDav.TestConnectionAsync(profile);
            if (ok)
            {
                profile.ServerType = (int)_webDav.DetectedServerType;
                EditorStatus.TextColor = Colors.White;
                EditorStatus.Text = "✓ " + message.Replace("\n", "  ");
            }
            else
            {
                EditorStatus.TextColor = Color.FromArgb("#c0392b");
                EditorStatus.Text = "✗ " + message;
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        if (_busy) return;
        var profile = CollectProfile();
        if (profile == null) return;

        _busy = true;
        try
        {
            // 保存时顺带完成一次连接测试：成功才落库并直接进入浏览（坏配置不留列表）
            EditorStatus.Text = "验证连接中…";
            var (ok, message) = await _webDav.TestConnectionAsync(profile);
            if (!ok)
            {
                EditorStatus.TextColor = Color.FromArgb("#c0392b");
                EditorStatus.Text = message;
                return;
            }

            profile.ServerType = (int)_webDav.DetectedServerType;
            _store.Upsert(profile);
            ShowToast("已保存，正在连接…");
            // ConnectAsync 自带忙判定：先释放本方法的 _busy，否则会被守卫直接拦掉
            _busy = false;
            await ConnectAsync(profile);
        }
        finally
        {
            _busy = false;
        }
    }

    // ═══════════════════ 目录浏览 ═══════════════════

    private async Task ConnectAsync(ConnectionProfile profile)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            _activeProfile = profile;
            _store.MarkUsed(profile);

            var startPath = string.IsNullOrWhiteSpace(profile.LastPath) ? "/" : profile.LastPath;
            ShowPanel(Panel.Browser);
            LblBrowserTitle.Text = profile.Name;
            await BrowseAsync(startPath);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task BrowseAsync(string path)
    {
        if (_activeProfile == null) return;

        FileEmpty.IsVisible = false;
        FileStatus.IsVisible = true;
        FileStatus.Text = "加载中…";
        FileList.Children.Clear();

        _currentPath = path;
        LblBrowserPath.Text = path;

        List<RemoteFile> entries;
        try
        {
            _webDav.Configure(_activeProfile);
            entries = await _webDav.ListFilesAsync(path);
        }
        catch (Exception ex)
        {
            entries = new List<RemoteFile>();
            FileStatus.IsVisible = false;
            FileEmpty.IsVisible = true;
            FileEmpty.Text = "加载失败：" + ex.Message;
            return;
        }

        FileStatus.IsVisible = false;

        if (entries.Count == 0)
        {
            // 空列表区分「目录真的空」与「连不上」：跑一次连接测试拿准确原因
            var (ok, message) = await _webDav.TestConnectionAsync(_activeProfile);
            FileEmpty.IsVisible = true;
            FileEmpty.Text = ok ? "此目录为空" : message.Split('\n')[0];
            return;
        }

        // 记住浏览位置（下次进入直接回到这里）
        _activeProfile.LastPath = path;
        _store.Save();

        var ordered = entries
            .OrderBy(f => f.IsDirectory ? 0 : 1)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var entry in ordered)
        {
            FileList.Children.Add(BuildFileRow(entry));
        }
    }

    private View BuildFileRow(RemoteFile file)
    {
        var isMedia = !file.IsDirectory && MediaExtensions.Contains(IOPath.GetExtension(file.Name));
        var playable = file.IsDirectory || isMedia;

        var icon = file.IsDirectory ? "📁" : isMedia ? "▶" : "·";
        var title = new Label
        {
            Text = $"{icon}  {file.Name}",
            FontSize = 13.5,
            FontFamily = file.IsDirectory ? "OpenSansSemibold" : "OpenSansRegular",
            TextColor = playable
                ? (Color)Application.Current!.Resources["TextPrimaryColor"]
                : (Color)Application.Current!.Resources["TextHintColor"],
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1,
            VerticalOptions = LayoutOptions.Center,
        };

        Grid grid;
        if (file.IsDirectory)
        {
            grid = new Grid { ColumnDefinitions = [new ColumnDefinition(GridLength.Star)] };
            grid.Add(title, 0);
        }
        else
        {
            var size = new Label
            {
                Text = file.Size > 0 ? FormatSize(file.Size) : "",
                FontSize = 11,
                TextColor = (Color)Application.Current!.Resources["TextHintColor"],
                VerticalOptions = LayoutOptions.Center,
            };
            grid = new Grid
            {
                ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
                ColumnSpacing = 10,
            };
            grid.Add(title, 0);
            grid.Add(size, 1);
        }

        var row = new Grid { Padding = new Thickness(6, 9), Children = { grid } };

        if (playable)
        {
            row.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(() => _ = file.IsDirectory ? EnterDirectory(file) : PlayAsync(file)),
            });
        }
        return row;
    }

    private Task EnterDirectory(RemoteFile dir)
    {
        var target = dir.Path.TrimEnd('/');
        if (target.Length == 0) target = "/";
        return BrowseAsync(target);
    }

    private async void OnUpClicked(object? sender, EventArgs e)
    {
        if (_currentPath.Length <= 1) return;
        var parent = _currentPath.TrimEnd('/');
        var slash = parent.LastIndexOf('/');
        parent = slash <= 0 ? "/" : parent[..slash];
        await BrowseAsync(parent);
    }

    private void OnSwitchProfileClicked(object? sender, EventArgs e)
    {
        ShowPanel(Panel.Profiles);
        BuildProfileList();
    }

    // ═══════════════════ 播放 ═══════════════════

    private async Task PlayAsync(RemoteFile file)
    {
        if (_activeProfile == null) return;

        var url = _proxy.BuildLocalUrl(_activeProfile.Id, file.Path);
        if (url == null)
        {
            ShowToast("本地流代理启动失败");
            return;
        }

        var title = IOPath.GetFileNameWithoutExtension(file.Name);
        try
        {
            await Shell.Current.GoToAsync($"player?title={Uri.EscapeDataString(title)}&url={Uri.EscapeDataString(url)}");
        }
        catch (Exception ex)
        {
            ShowToast("打开播放器失败：" + ex.Message);
        }
    }

    // ═══════════════════ 小工具 ═══════════════════

    private static Label ActionLabel(string text, Color color, Action onTap)
    {
        var label = new Label
        {
            Text = text,
            FontSize = 11.5,
            TextColor = color,
            VerticalOptions = LayoutOptions.Center,
        };
        label.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(onTap) });
        return label;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):F1} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):F1} MB";
        if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):F0} KB";
        return $"{bytes} B";
    }

    private void ShowToast(string text, int ms = 2500)
    {
        LblToast.Text = text;
        ToastHost.IsVisible = true;
        _toastToken++;
        var token = _toastToken;
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(ms), () =>
        {
            if (token != _toastToken) return false;
            ToastHost.IsVisible = false;
            return false;
        });
    }
}
