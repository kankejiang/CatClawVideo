namespace CatClawVideo.Core.Interfaces;

/// <summary>磁力播放结果（与 <c>BtStreamService.BtSession</c> 同形，避免 Core 内部耦合）</summary>
public sealed record MagnetPlayback(string InfoHashHex, int FileIndex, long FileLength, string FileName, string Url);

/// <summary>磁力文件条目（种子内单个文件）</summary>
public sealed record MagnetFile(int Index, long Size, string Name);

/// <summary>
/// 优先磁力引擎（如迅雷下载引擎）：先于内置 MonoTorrent 尝试。
///
/// <para><b>为什么要它</b>：新6V 这类站的磁力，公共 BT swarm 极薄甚至已死（实测 0.28 Mbps），
/// 而 TVBox 用迅雷 P2SP 私有网络（中心化种子索引 + 自有节点）能秒出文件列表并流畅播放。
/// 内置引擎只会在「公共 BT 拿得到」时才工作。</para>
///
/// <para><b>回落契约</b>：任何一步失败（引擎没起来 / appKey 失效 / 解析超时 / 起播超时）
/// 都必须返回 null，调用方回落到内置 BT —— 绝不能因为第三方引擎坏掉而让磁力整体不可用。</para>
///
/// <para>⚠️ 平台实现（Android）目前是迅雷 SDK，二进制与 appKey 取自 TVBox 仓库，
/// 属商业闭源 SDK 的未授权使用；用户已知情并选择打包，见 THIRD-PARTY-NOTICES.md。</para>
/// </summary>
public interface IPreferredMagnetEngine
{
    /// <summary>引擎名（日志用，如「迅雷」）</summary>
    string Name { get; }

    /// <summary>是否已就绪（未就绪时调用方不会尝试）</summary>
    bool IsReady { get; }

    /// <summary>初始化引擎（幂等，只真正尝试一次）</summary>
    Task<bool> EnsureReadyAsync();

    /// <summary>解析磁力并返回种子内文件列表；失败返回 null</summary>
    Task<List<MagnetFile>?> ListFilesAsync(string magnet, string? preferName = null, CancellationToken ct = default);

    /// <summary>
    /// 用本引擎起播该磁力：内部挑一个文件（优先匹配 preferName，否则取最大的视频文件）
    /// 并返回本地可播地址；任何一步失败返回 null（调用方回落内置 BT）。
    /// </summary>
    Task<MagnetPlayback?> TryOpenAsync(string magnet, string? preferName = null, CancellationToken ct = default);

    /// <summary>停止当前任务</summary>
    void Stop();
}

/// <summary>
/// 优先磁力引擎的静态注册点。
/// <para>用静态注册而非 DI：各 Provider 需要的只是一个「有没有更优先的引擎」的判断，
/// 且只有 Android 有实现，「未注册 = 无优先引擎」的语义最省事。</para>
/// </summary>
public static class MagnetEngines
{
    /// <summary>迅雷（或同类 P2SP）引擎；未注册 / 未就绪时磁力走内置 BT</summary>
    public static IPreferredMagnetEngine? Thunder { get; set; }
}
