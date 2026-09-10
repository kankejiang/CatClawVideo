using System.Text.Json;
using System.Text.Json.Serialization;

namespace CatClawVideo.Core.Services;

/// <summary>
/// BT/下载全局设置（照搬 Motrix 设置项；RPC 端口/密钥与迅雷协议为 aria2 专属，内置引擎不适用）。
/// 持久化到 {baseDir}/bt-settings.json，引擎与下载管理器构造时读取。
/// </summary>
public sealed class BtSettings
{
    // ─── 传输设置 ───
    /// <summary>上传限速 KB/s（0 = 不限）——BT 互惠：上传过低会被 peer choke，进而拖慢下载</summary>
    public int UploadLimitKBps { get; set; } = 0;
    /// <summary>下载限速 KB/s（0 = 不限）</summary>
    public int DownloadLimitKBps { get; set; } = 0;

    // ─── BT 设置 ───
    /// <summary>保存磁力链接元数据为种子文件（MonoTorrent AutoSaveLoadMagnetLinkMetadata）</summary>
    public bool SaveMagnetMetadata { get; set; } = true;
    /// <summary>自动开始下载磁力链接/种子</summary>
    public bool AutoStartDownload { get; set; } = true;
    /// <summary>BT 强制加密（优先加密连接）</summary>
    public bool ForceEncryption { get; set; } = false;
    /// <summary>持续做种，直到手动停止</summary>
    public bool SeedForever { get; set; } = false;
    /// <summary>做种分享率（SeedForever 关闭时生效）</summary>
    public int SeedRatio { get; set; } = 1;
    /// <summary>做种时间（分钟，SeedForever 关闭时生效）</summary>
    public int SeedMinutes { get; set; } = 60;

    // ─── 任务管理 ───
    /// <summary>同时下载的最大任务数</summary>
    public int MaxConcurrentTasks { get; set; } = 5;
    /// <summary>每个服务器（tracker/peer 主机）最大连接数——映射为单 torrent 连接上限</summary>
    public int MaxConnPerServer { get; set; } = 64;
    /// <summary>断点续传（HTTP 任务；BT 天然支持）</summary>
    public bool ResumeSupport { get; set; } = true;
    /// <summary>新建任务后自动跳转到下载页面</summary>
    public bool AutoJumpToDownloads { get; set; } = true;
    /// <summary>下载完成后通知</summary>
    public bool NotifyOnComplete { get; set; } = true;
    /// <summary>删除任务前需确认</summary>
    public bool ConfirmBeforeDelete { get; set; } = true;

    // ─── Tracker 服务器 ───
    /// <summary>每天自动更新 Tracker 服务器列表</summary>
    public bool AutoUpdateTrackers { get; set; } = true;
    /// <summary>上次更新时间（Unix 秒，0 = 从未）</summary>
    public long TrackerListUpdatedAt { get; set; }

    // ─── 监听端口 ───
    /// <summary>UPnP/NAT-PMP 端口映射</summary>
    public bool UpnpNatPmp { get; set; } = true;
    /// <summary>BT 监听端口（TCP peer 连接）</summary>
    public int BtListenPort { get; set; } = 21301;
    /// <summary>DHT 监听端口（UDP）</summary>
    public int DhtListenPort { get; set; } = 26701;

    // ─── 应用/网络 ───
    /// <summary>模拟 User-Agent（HTTP 下载与 tracker 请求）</summary>
    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/111.0.0.0 Safari/537.36";

    // ─── 持久化 ───
    [JsonIgnore] private string _path = "";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>从 {baseDir}/bt-settings.json 加载（缺失/损坏时返回默认值）</summary>
    public static BtSettings Load(string baseDir)
    {
        var path = Path.Combine(baseDir, "bt-settings.json");
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<BtSettings>(File.ReadAllText(path), JsonOpts);
                if (s != null) { s._path = path; s.Normalize(); return s; }
            }
        }
        catch { }
        var fresh = new BtSettings { _path = path };
        return fresh;
    }

    /// <summary>保存到磁盘（失败静默，设置不阻塞业务）</summary>
    public void Save()
    {
        try
        {
            if (_path.Length == 0) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { }
    }

    private void Normalize()
    {
        if (MaxConcurrentTasks is < 1 or > 20) MaxConcurrentTasks = 5;
        if (MaxConnPerServer is < 8 or > 500) MaxConnPerServer = 64;
        if (BtListenPort is < 1 or > 65535) BtListenPort = 21301;
        if (DhtListenPort is < 1 or > 65535) DhtListenPort = 26701;
        if (SeedRatio < 0) SeedRatio = 1;
        if (SeedMinutes < 0) SeedMinutes = 60;
        if (string.IsNullOrWhiteSpace(UserAgent)) UserAgent =
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/111.0.0.0 Safari/537.36";
    }

    // 便捷换算
    [JsonIgnore] public int UploadLimitBytesPerSec => UploadLimitKBps <= 0 ? 0 : UploadLimitKBps * 1024;
    [JsonIgnore] public int DownloadLimitBytesPerSec => DownloadLimitKBps <= 0 ? 0 : DownloadLimitKBps * 1024;
}
