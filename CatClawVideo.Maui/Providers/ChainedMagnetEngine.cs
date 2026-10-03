using CatClawVideo.Core.Interfaces;

namespace CatClawVideo.Core.Providers;

/// <summary>
/// 磁力优先引擎链：按顺序尝试，任一命中即返回，全部失败返回 null（回落内置 BT）。
///
/// <para>PC 上的组合 = [QEMU 本地迅雷引擎, 迅雷网盘引擎]：
/// 本地 P2SP 优先（无需账号、公共磁力即可、确定性强）；网盘（需登录，云添加 → 取直链）兜底。</para>
/// </summary>
public sealed class ChainedMagnetEngine : IPreferredMagnetEngine, IPlaybackSessionLease
{
    private readonly IPreferredMagnetEngine[] _engines;

    public ChainedMagnetEngine(params IPreferredMagnetEngine[] engines) => _engines = engines;

    public string Name => string.Join("+", _engines.Select(e => e.Name));

    public bool IsReady => _engines.Any(e => e.IsReady);

    /// <summary>链上任一引擎忙碌即视为忙碌（探测让位判断用）。</summary>
    public bool IsBusy => _engines.Any(e => e.IsBusy);

    /// <summary>
    /// 链上最近一次失败原因。引擎的失败契约是「返回 null」（回落下一家），真实病因只落在
    /// 各家肚子里、只进 bt.log；UI 拿不到就只能对六种死法说一句「资源不存在」，测试清单判不了
    /// 病根（2026-09-30 磁力链路调试报告 §一「spider 返回 magnet 之后没有下文」）。
    /// </summary>
    public string? LastFailureReason { get; private set; }

    public async Task<bool> EnsureReadyAsync()
    {
        var any = false;
        foreach (var e in _engines)
        {
            try { any |= await e.EnsureReadyAsync().ConfigureAwait(false); }
            catch { }
        }
        return any;
    }

    public async Task<List<MagnetFile>?> ListFilesAsync(string magnet, string? preferName = null, CancellationToken ct = default)
    {
        foreach (var e in _engines)
        {
            if (!e.IsReady) continue;
            try
            {
                var files = await e.ListFilesAsync(magnet, preferName, ct).ConfigureAwait(false);
                if (files is { Count: > 0 }) return files;
            }
            catch { }
        }
        return null;
    }

    public async Task<MagnetPlayback?> TryOpenAsync(string magnet, string? preferName = null, CancellationToken ct = default)
    {
        LastFailureReason = null;
        foreach (var e in _engines)
        {
            if (!e.IsReady) continue;
            try
            {
                var hit = await e.TryOpenAsync(magnet, preferName, ct).ConfigureAwait(false);
                if (hit is not null) return hit;
                // QEMU 迅雷引擎现在来自独立套件仓库，链上挂的是适配器 → 从 Inner 取引擎自己的失败原因
                LastFailureReason = (e as CatClawVideo.Core.Providers.QemuMagnetEngine)?.Inner.LastFailureReason
                    ?? LastFailureReason ?? $"{e.Name}：未打开（无原因上报）";
            }
            catch (Exception ex)
            {
                LastFailureReason = $"{e.Name} 异常：{ex.GetType().Name}: {ex.Message}";
            }
        }
        return null;
    }

    public void Stop()
    {
        foreach (var e in _engines)
        {
            try { e.Stop(); } catch { }
        }
    }

    /// <summary>播放页退出：转给链上实现了「可冻结」的引擎（PC 侧是 QEMU 迅雷引擎）。
    /// 未实现该接口的引擎（如网盘 API 兜底）本就无长驻下载，忽略即可。</summary>
    public void ReleasePlaybackSession(string? playedUrl)
    {
        foreach (var e in _engines)
        {
            if (e is not IPlaybackSessionLease lease) continue;
            try { lease.ReleasePlaybackSession(playedUrl); } catch { }
        }
    }
}
