using CatClaw.Qemu;
using CatClawVideo.Core.Interfaces;

namespace CatClawVideo.Core.Providers;

/// <summary>
/// 把虚拟机套件（独立仓库 <c>CatClaw.Qemu</c>）里的迅雷引擎适配成应用层的
/// <see cref="IPreferredMagnetEngine"/> / <see cref="IPlaybackSessionLease"/>。
///
/// <para><b>为什么需要这一层</b>：套件不能依赖应用（否则依赖方向颠倒：应用 → 套件 → 应用），
/// 所以它对外用库内 DTO（<c>QemuMagnetFile</c>/<c>QemuPlayback</c>）；应用接口用的是自己的
/// <c>MagnetFile</c>/<c>MagnetPlayback</c>。两边只在适配器里做一次字段映射。</para>
///
/// <para>需要套件引擎原生 API（<c>DownloadToFileExAsync</c>、<c>LastFailureReason</c>、
/// <c>ExternalVmProvider</c> 等）的地方，用 <see cref="Inner"/> 直接拿引擎。</para>
/// </summary>
public sealed class QemuMagnetEngine : IPreferredMagnetEngine, IPlaybackSessionLease
{
    private readonly QemuGuestEngine _engine;

    public QemuMagnetEngine(QemuGuestEngine engine) => _engine = engine;

    /// <summary>被包裹的套件引擎（下载管理 / 诊断用）。</summary>
    public QemuGuestEngine Inner => _engine;

    public string Name => _engine.Name;

    public bool IsReady => _engine.IsReady;

    public bool IsBusy => _engine.IsBusy;

    public Task<bool> EnsureReadyAsync() => _engine.EnsureReadyAsync();

    public async Task<List<MagnetFile>?> ListFilesAsync(string magnet, string? preferName = null,
        CancellationToken ct = default)
    {
        var files = await _engine.ListFilesAsync(magnet, preferName, ct).ConfigureAwait(false);
        return files?.Select(f => new MagnetFile(f.Index, f.Size, f.Name)).ToList();
    }

    public async Task<MagnetPlayback?> TryOpenAsync(string magnet, string? preferName = null,
        CancellationToken ct = default)
    {
        var r = await _engine.TryOpenAsync(magnet, preferName, ct).ConfigureAwait(false);
        return r is null
            ? null
            : new MagnetPlayback(r.InfoHashHex, r.FileIndex, r.FileLength, r.FileName, r.Url);
    }

    public void Stop() => _engine.Stop();

    public void ReleasePlaybackSession(string? playedUrl) => _engine.ReleasePlaybackSession(playedUrl);
}
