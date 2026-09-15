using CatClawVideo.Core.Interfaces;

namespace CatClawVideo.Core.Providers;

/// <summary>
/// 磁力优先引擎链：按顺序尝试，任一命中即返回，全部失败返回 null（回落内置 BT）。
///
/// <para>PC 上的组合 = [QEMU 本地迅雷引擎, 迅雷网盘引擎]：
/// 本地 P2SP 优先（无需账号、公共磁力即可、确定性强）；网盘（需登录，云添加 → 取直链）兜底。</para>
/// </summary>
public sealed class ChainedMagnetEngine : IPreferredMagnetEngine
{
    private readonly IPreferredMagnetEngine[] _engines;

    public ChainedMagnetEngine(params IPreferredMagnetEngine[] engines) => _engines = engines;

    public string Name => string.Join("+", _engines.Select(e => e.Name));

    public bool IsReady => _engines.Any(e => e.IsReady);

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
        foreach (var e in _engines)
        {
            if (!e.IsReady) continue;
            try
            {
                var hit = await e.TryOpenAsync(magnet, preferName, ct).ConfigureAwait(false);
                if (hit is not null) return hit;
            }
            catch { }
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
}
