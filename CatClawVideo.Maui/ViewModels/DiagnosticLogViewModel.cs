using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CatClawVideo.Maui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CatClawVideo.Maui.ViewModels;

/// <summary>日志级别（对应文件里的 D/I/W/E）</summary>
public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>单条日志条目</summary>
public partial class LogEntryViewModel : ObservableObject
{
    /// <summary>时间戳 HH:mm:ss.fff</summary>
    [ObservableProperty] private string _timestamp = "";
    /// <summary>级别</summary>
    [ObservableProperty] private LogLevel _level;
    /// <summary>模块标签</summary>
    [ObservableProperty] private string _tag = "";
    /// <summary>日志正文</summary>
    [ObservableProperty] private string _message = "";

    /// <summary>级别简称（d/i/w/e），供筛选比对</summary>
    public string LevelCode => Level switch
    {
        LogLevel.Error => "e",
        LogLevel.Warn => "w",
        LogLevel.Info => "i",
        _ => "d"
    };

    /// <summary>级别文本（DEBUG/INFO/WARN/ERROR）</summary>
    public string LevelText => Level switch
    {
        LogLevel.Error => "ERROR",
        LogLevel.Warn => "WARN",
        LogLevel.Info => "INFO",
        _ => "DEBUG"
    };

    /// <summary>级别配色（直接暴露 Color，省掉一层 Converter）</summary>
    public Color LevelColor => Level switch
    {
        LogLevel.Error => Color.FromArgb("#FF6B6B"),
        LogLevel.Warn => Color.FromArgb("#FFC107"),
        LogLevel.Info => Color.FromArgb("#55D6FF"),
        _ => Color.FromArgb("#8A8A9E")
    };

    partial void OnLevelChanged(LogLevel value)
    {
        OnPropertyChanged(nameof(LevelCode));
        OnPropertyChanged(nameof(LevelText));
        OnPropertyChanged(nameof(LevelColor));
    }
}

/// <summary>
/// 诊断日志查看页 ViewModel：读取 <c>logs/debug.log</c>，解析成结构化条目，
/// 支持级别/模块/关键字筛选、统计、复制、清空与导出诊断包。
/// 结构与猫爪音乐 <c>LogViewModel</c> 对齐。
/// </summary>
public partial class DiagnosticLogViewModel : ObservableObject
{
    /// <summary>最多渲染的条目数（避免上万条时列表卡顿）</summary>
    private const int RenderLimit = 3000;

    /// <summary>最多加载的日志行数：8MB 文件理论上可达十万行，全量渲染会卡死列表，只取最近的部分</summary>
    private const int MaxEntries = 20000;

    private readonly string _logFilePath;

    /// <summary>当前加载的全部条目（筛选前）</summary>
    private readonly List<LogEntryViewModel> _allEntries = new();

    private string _levelFilter = "all";
    private string _tagFilter = "all";
    private string _searchQuery = "";

    /// <summary>筛选后的条目（绑定 CollectionView）</summary>
    public ObservableCollection<LogEntryViewModel> FilteredEntries { get; } = new();

    /// <summary>可选的模块标签（从日志动态提取）</summary>
    public ObservableCollection<string> AvailableTags { get; } = new();

    [ObservableProperty] private string _deviceModel = "";
    [ObservableProperty] private string _deviceOs = "";
    [ObservableProperty] private string _appVersion = "";
    [ObservableProperty] private string _buildNumber = "";
    [ObservableProperty] private string _installDate = "";
    [ObservableProperty] private string _logPath = "";
    [ObservableProperty] private string _sizeText = "0 KB";

    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private int _warnCount;
    [ObservableProperty] private int _infoCount;
    [ObservableProperty] private int _debugCount;

    /// <summary>是否为空状态（无匹配日志）</summary>
    [ObservableProperty] private bool _isEmpty;
    /// <summary>是否正在加载</summary>
    [ObservableProperty] private bool _isBusy;
    /// <summary>操作反馈（复制/清空/导出结果）</summary>
    [ObservableProperty] private string _statusText = "";
    /// <summary>截断提示（只加载了最近 N 条时显示）</summary>
    [ObservableProperty] private string _truncateNote = "";
    /// <summary>是否有截断提示（控制提示行是否占位）</summary>
    [ObservableProperty] private bool _hasTruncateNote;
    /// <summary>筛选结果过多时的提示（只渲染最近 N 条）</summary>
    [ObservableProperty] private string _filterNote = "";
    /// <summary>当前选中的级别筛选（all/e/w/i/d），供 XAML 高亮</summary>
    [ObservableProperty] private string _selectedLevel = "all";

    public DiagnosticLogViewModel()
    {
        _logFilePath = string.IsNullOrEmpty(DiagnosticLog.LogFilePath)
            ? Path.Combine(Core.AppPaths.Sub("logs"), "debug.log")
            : DiagnosticLog.LogFilePath;

        LogPath = _logFilePath;
        LoadDeviceInfo();
    }

    /// <summary>设备与版本信息（诊断包要带上的现场信息）</summary>
    private void LoadDeviceInfo()
    {
        try
        {
            DeviceModel = DeviceInfo.Current.Model;
            var apiLevel = 0;
#if ANDROID
            apiLevel = (int)global::Android.OS.Build.VERSION.SdkInt;
#endif
            DeviceOs = $"{DeviceInfo.Current.Platform} {DeviceInfo.Current.VersionString}" +
                       (apiLevel > 0 ? $" (API {apiLevel})" : "");
        }
        catch { DeviceModel = "未知"; DeviceOs = "未知"; }

        try
        {
            AppVersion = AppInfo.Current?.VersionString ?? "0.0.0";
            BuildNumber = AppInfo.Current?.BuildString ?? "";
        }
        catch { AppVersion = "0.0.0"; }

#if ANDROID
        try
        {
            var ctx = global::Android.App.Application.Context;
            if (ctx?.PackageManager?.GetPackageInfo(ctx.PackageName, 0) is { } pi)
                InstallDate = DateTimeOffset.FromUnixTimeMilliseconds(pi.FirstInstallTime)
                                      .LocalDateTime.ToString("yyyy-MM-dd");
        }
        catch { }
#endif
    }

    /// <summary>加载并解析日志文件（页面 OnAppearing / 点刷新时调用）</summary>
    [RelayCommand]
    public async Task LoadLogsAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "";
        try
        {
            _allEntries.Clear();
            AvailableTags.Clear();
            AvailableTags.Add("全部模块");
            TruncateNote = "";
            HasTruncateNote = false;

            if (!File.Exists(_logFilePath))
            {
                UpdateStats();
                ApplyFilter();
                return;
            }

            try
            {
                var fi = new FileInfo(_logFilePath);
                SizeText = fi.Length < 1024
                    ? $"{fi.Length} B"
                    : fi.Length < 1024 * 1024
                        ? $"{fi.Length / 1024.0:F1} KB"
                        : $"{fi.Length / 1048576.0:F1} MB";
            }
            catch { SizeText = "—"; }

            var lines = await File.ReadAllLinesAsync(_logFilePath).ConfigureAwait(true);
            if (lines.Length > MaxEntries)
            {
                lines = lines[^MaxEntries..];
                TruncateNote = $"日志较大，仅显示最近 {MaxEntries} 条（完整内容见导出包）";
                HasTruncateNote = true;
            }

            var seenTags = new HashSet<string>();
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var m = LineRegex().Match(line);
                if (!m.Success)
                {
                    // 老格式/异常行：原样保留，级别按 INFO、无标签
                    _allEntries.Add(new LogEntryViewModel { Timestamp = "", Level = LogLevel.Info, Message = line });
                    continue;
                }

                var level = m.Groups[2].Value switch
                {
                    "E" => LogLevel.Error,
                    "W" => LogLevel.Warn,
                    "I" => LogLevel.Info,
                    _ => LogLevel.Debug
                };
                var tag = m.Groups[3].Value;

                _allEntries.Add(new LogEntryViewModel
                {
                    Timestamp = m.Groups[1].Value,
                    Level = level,
                    Tag = tag,
                    Message = m.Groups[4].Value
                });

                if (!string.IsNullOrEmpty(tag) && seenTags.Add(tag))
                    AvailableTags.Add(tag);
            }
        }
        catch (Exception ex)
        {
            _allEntries.Add(new LogEntryViewModel
            {
                Timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
                Level = LogLevel.Error,
                Tag = "UI",
                Message = $"读取日志失败：{ex.GetType().Name}: {ex.Message}"
            });
        }
        finally
        {
            UpdateStats();
            ApplyFilter();
            IsBusy = false;
        }
    }

    private void UpdateStats()
    {
        TotalCount = _allEntries.Count;
        ErrorCount = _allEntries.Count(e => e.Level == LogLevel.Error);
        WarnCount = _allEntries.Count(e => e.Level == LogLevel.Warn);
        InfoCount = _allEntries.Count(e => e.Level == LogLevel.Info);
        DebugCount = _allEntries.Count(e => e.Level == LogLevel.Debug);
    }

    private void ApplyFilter()
    {
        FilteredEntries.Clear();
        var q = SearchQuery?.Trim() ?? "";
        var matched = new List<LogEntryViewModel>();

        foreach (var e in _allEntries)
        {
            if (_levelFilter != "all" && e.LevelCode != _levelFilter) continue;
            if (_tagFilter != "all" && e.Tag != _tagFilter) continue;
            if (q.Length > 0 &&
                !e.Message.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !e.Tag.Contains(q, StringComparison.OrdinalIgnoreCase))
                continue;

            matched.Add(e);
        }

        // 渲染上限：CollectionView 需要量测全部条目才能算出滚动范围，
        // 上万条会明显卡顿。只渲染最近的 RenderLimit 条，其余靠筛选/搜索收敛。
        var skipped = 0;
        if (matched.Count > RenderLimit)
        {
            skipped = matched.Count - RenderLimit;
            matched = matched[^RenderLimit..];
        }

        foreach (var e in matched) FilteredEntries.Add(e);

        FilterNote = skipped > 0 ? $"结果较多，仅显示最近 {RenderLimit} 条（已省略更早 {skipped} 条）" : "";
        IsEmpty = FilteredEntries.Count == 0;
    }

    /// <summary>搜索关键字（双向绑定，改动即过滤）</summary>
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (_searchQuery == value) return;
            _searchQuery = value;
            OnPropertyChanged();
            ApplyFilter();
        }
    }

    /// <summary>设置级别筛选（all/e/w/i/d）</summary>
    public void SetLevelFilter(string level)
    {
        _levelFilter = level;
        SelectedLevel = level;
        ApplyFilter();
    }

    /// <summary>设置模块筛选（"全部模块" 或不带前缀的标签名）</summary>
    public void SetTagFilter(string tag)
    {
        _tagFilter = tag == "全部模块" ? "all" : tag;
        ApplyFilter();
    }

    /// <summary>复制当前筛选结果到剪贴板</summary>
    [RelayCommand]
    public async Task CopyLogsAsync()
    {
        try
        {
            var text = string.Join(Environment.NewLine, FilteredEntries.Select(e =>
                $"{e.Timestamp}\t[{e.LevelText}][{e.Tag}] {e.Message}"));
            if (text.Length == 0) { StatusText = "没有可复制的内容"; return; }

            await Clipboard.Default.SetTextAsync(text).ConfigureAwait(true);
            StatusText = $"已复制 {FilteredEntries.Count} 条到剪贴板";
        }
        catch (Exception ex)
        {
            StatusText = $"复制失败：{ex.Message}";
        }
    }

    /// <summary>清空日志文件（含备份）</summary>
    [RelayCommand]
    public async Task ClearLogsAsync()
    {
        try
        {
            DiagnosticLog.Instance?.Flush();
            if (File.Exists(_logFilePath)) await File.WriteAllTextAsync(_logFilePath, "").ConfigureAwait(true);

            var bak = Path.Combine(Path.GetDirectoryName(_logFilePath) ?? ".", "debug.1.log");
            try { if (File.Exists(bak)) File.Delete(bak); } catch { }

            _allEntries.Clear();
            UpdateStats();
            ApplyFilter();
            SizeText = "0 B";
            TruncateNote = "";
            HasTruncateNote = false;
            StatusText = "日志已清空";
        }
        catch (Exception ex)
        {
            StatusText = $"清空失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 导出诊断包（<c>debug.log</c> + <c>debug.1.log</c> + 设备信息 → zip）。
    /// Android 交系统分享；Windows 打开所在文件夹并选中文件。
    /// </summary>
    [RelayCommand]
    public async Task ExportAsync()
    {
        try
        {
            IsBusy = true;
            DiagnosticLog.Instance?.Flush();

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var tmpDir = Path.Combine(FileSystem.CacheDirectory, $"diag_{stamp}");
            Directory.CreateDirectory(tmpDir);

            var logDir = Path.GetDirectoryName(_logFilePath) ?? ".";
            foreach (var name in new[] { "debug.log", "debug.1.log" })
            {
                var src = Path.Combine(logDir, name);
                if (File.Exists(src))
                {
                    try
                    {
                        await Task.Run(() => File.Copy(src, Path.Combine(tmpDir, name), true)).ConfigureAwait(true);
                    }
                    catch { }
                }
            }

            var info = $"""
                应用: 猫爪影视 {AppVersion} (build {BuildNumber})
                设备: {DeviceModel}
                系统: {DeviceOs}
                首次安装: {InstallDate}
                日志条数: {TotalCount}（错误 {ErrorCount} / 警告 {WarnCount} / 信息 {InfoCount} / 调试 {DebugCount}）
                日志文件: {_logFilePath}（{SizeText}）
                导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}
                """;
            await File.WriteAllTextAsync(Path.Combine(tmpDir, "device.txt"), info).ConfigureAwait(true);

            var zipPath = Path.Combine(FileSystem.CacheDirectory, $"catclaw_video_diag_{stamp}.zip");
            await Task.Run(() =>
            {
                if (File.Exists(zipPath)) File.Delete(zipPath);
                System.IO.Compression.ZipFile.CreateFromDirectory(tmpDir, zipPath);
            }).ConfigureAwait(true);

            try { Directory.Delete(tmpDir, true); } catch { }

#if ANDROID
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = "猫爪影视诊断包",
                File = new ShareFile(zipPath)
            }).ConfigureAwait(true);
            StatusText = $"诊断包已生成：{Path.GetFileName(zipPath)}";
#elif WINDOWS
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{zipPath}\"",
                    UseShellExecute = true
                });
                StatusText = $"诊断包已生成并打开所在文件夹：{zipPath}";
            }
            catch
            {
                StatusText = $"诊断包已生成：{zipPath}";
            }
#else
            StatusText = $"诊断包已生成：{zipPath}";
#endif
        }
        catch (Exception ex)
        {
            StatusText = $"导出失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>行格式：HH:mm:ss.fff⇥[L][tag] 消息</summary>
    [GeneratedRegex(@"^(\d{2}:\d{2}:\d{2}\.\d{3})\s*\[(D|I|W|E)\]\s*\[([^\]]+)\]\s?(.*)$")]
    private static partial Regex LineRegex();
}
