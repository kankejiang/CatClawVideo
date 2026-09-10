using CatClawVideo.Core.Services;
using CatClawVideo.Maui.Services;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 下载 / BT 设置页（布局照搬 Motrix）：默认下载路径、传输限速、BT 设置、任务管理、
/// Tracker 服务器（动态列表 + 每日更新）、监听端口、User-Agent。
/// <para>差异说明：Motrix 的 RPC 端口/密钥、迅雷链接、默认客户端协议为 aria2 专属，内置 MonoTorrent 引擎不适用，故不提供。</para>
/// </summary>
public partial class BtSettingsPage : ContentPage
{
    private readonly BtSettings _settings;
    private readonly BtTrackerSource _trackers;
    private readonly BtStreamService _bt;
    private readonly DownloadManager _downloads;

    public BtSettingsPage(BtSettings settings, BtTrackerSource trackers, BtStreamService bt, DownloadManager downloads)
    {
        InitializeComponent();
        _settings = settings;
        _trackers = trackers;
        _bt = bt;
        _downloads = downloads;
        LoadFromSettings();
        ShowTrackers();
    }

    private void LoadFromSettings()
    {
        PathEntry.Text = _downloads.DownloadFolderPath;
        UploadLimitEntry.Text = _settings.UploadLimitKBps.ToString();
        DownloadLimitEntry.Text = _settings.DownloadLimitKBps.ToString();
        SaveMetaCheck.IsChecked = _settings.SaveMagnetMetadata;
        AutoStartCheck.IsChecked = _settings.AutoStartDownload;
        SeedForeverCheck.IsChecked = _settings.SeedForever;
        SeedRatioEntry.Text = _settings.SeedRatio.ToString();
        SeedMinutesEntry.Text = _settings.SeedMinutes.ToString();
        MaxTasksEntry.Text = _settings.MaxConcurrentTasks.ToString();
        MaxConnEntry.Text = _settings.MaxConnPerServer.ToString();
        AutoJumpCheck.IsChecked = _settings.AutoJumpToDownloads;
        NotifyCheck.IsChecked = _settings.NotifyOnComplete;
        ConfirmDeleteCheck.IsChecked = _settings.ConfirmBeforeDelete;
        AutoUpdateTrackersCheck.IsChecked = _settings.AutoUpdateTrackers;
        UpnpCheck.IsChecked = _settings.UpnpNatPmp;
        BtPortEntry.Text = _settings.BtListenPort.ToString();
        DhtPortEntry.Text = _settings.DhtListenPort.ToString();
        UserAgentEditor.Text = _settings.UserAgent;
        TrackerSourceLabel.Text = "列表来源：ngosang/trackerslist（best_ip + best，多镜像回退）";
    }

    private void ShowTrackers()
    {
        var list = _bt.TrackerList;
        TrackerListLabel.Text = list.Count == 0 ? "（暂无，点击立即更新）" : string.Join("\n", list);
        TrackerUpdatedAtLabel.Text = _settings.TrackerListUpdatedAt > 0
            ? $"上次更新：{DateTimeOffset.FromUnixTimeSeconds(_settings.TrackerListUpdatedAt).ToLocalTime():yyyy/M/d HH:mm:ss}"
            : "尚未更新过";
    }

    private async void OnBackTapped(object? sender, EventArgs e) => await Shell.Current.GoToAsync("..");

    private async void OnPickFolderClicked(object? sender, EventArgs e)
    {
        try
        {
            var result = await FilePicker.Default.PickAsync();
            if (result != null)
                PathEntry.Text = Path.GetDirectoryName(result.FullPath) ?? PathEntry.Text;
        }
        catch (Exception ex)
        {
            HintLabel.Text = $"选择目录失败：{ex.Message}";
        }
    }

    private async void OnUpdateTrackersClicked(object? sender, EventArgs e)
    {
        UpdateTrackersButton.IsEnabled = false;
        HintLabel.Text = "正在从 ngosang 拉取并探测可达性…";
        try
        {
            var list = await _bt.RefreshTrackersAsync();
            _settings.TrackerListUpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _settings.Save();
            ShowTrackers();
            HintLabel.Text = $"✅ 已更新：{list.Length} 个可达 tracker";
        }
        catch (Exception ex)
        {
            HintLabel.Text = $"更新失败：{ex.Message}（继续沿用现有列表）";
        }
        finally
        {
            UpdateTrackersButton.IsEnabled = true;
        }
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        try
        {
            var needsEngineRebuild =
                _settings.MaxConnPerServer != ParseInt(MaxConnEntry.Text, _settings.MaxConnPerServer)
                || _settings.UploadLimitKBps != ParseInt(UploadLimitEntry.Text, _settings.UploadLimitKBps)
                || _settings.DownloadLimitKBps != ParseInt(DownloadLimitEntry.Text, _settings.DownloadLimitKBps)
                || _settings.BtListenPort != ParseInt(BtPortEntry.Text, _settings.BtListenPort)
                || _settings.DhtListenPort != ParseInt(DhtPortEntry.Text, _settings.DhtListenPort)
                || _settings.UpnpNatPmp != (UpnpCheck.IsChecked == true);

            _downloads.SetDownloadFolderPath(PathEntry.Text ?? "");
            _settings.UploadLimitKBps = ParseInt(UploadLimitEntry.Text, 0);
            _settings.DownloadLimitKBps = ParseInt(DownloadLimitEntry.Text, 0);
            _settings.SaveMagnetMetadata = SaveMetaCheck.IsChecked == true;
            _settings.AutoStartDownload = AutoStartCheck.IsChecked == true;
            _settings.SeedForever = SeedForeverCheck.IsChecked == true;
            _settings.SeedRatio = ParseInt(SeedRatioEntry.Text, 1);
            _settings.SeedMinutes = ParseInt(SeedMinutesEntry.Text, 60);
            _settings.MaxConcurrentTasks = ParseInt(MaxTasksEntry.Text, 5);
            _settings.MaxConnPerServer = ParseInt(MaxConnEntry.Text, 64);
            _settings.AutoJumpToDownloads = AutoJumpCheck.IsChecked == true;
            _settings.NotifyOnComplete = NotifyCheck.IsChecked == true;
            _settings.ConfirmBeforeDelete = ConfirmDeleteCheck.IsChecked == true;
            _settings.AutoUpdateTrackers = AutoUpdateTrackersCheck.IsChecked == true;
            _settings.UpnpNatPmp = UpnpCheck.IsChecked == true;
            _settings.BtListenPort = ParseInt(BtPortEntry.Text, 21301);
            _settings.DhtListenPort = ParseInt(DhtPortEntry.Text, 26701);
            _settings.UserAgent = UserAgentEditor.Text ?? "";
            _settings.Save();

            _downloads.SetConcurrentLimit(_settings.MaxConcurrentTasks);

            if (needsEngineRebuild)
            {
                HintLabel.Text = "正在重建 BT 引擎…";
                await _bt.RecreateEngineAsync();
            }
            HintLabel.Text = "✅ 已保存" + (needsEngineRebuild ? "（引擎已按新设置重建）" : "");
            LoadFromSettings();
        }
        catch (Exception ex)
        {
            HintLabel.Text = $"保存失败：{ex.Message}";
        }
    }

    private static int ParseInt(string? text, int fallback) =>
        int.TryParse((text ?? "").Trim(), out var v) && v >= 0 ? v : fallback;
}
