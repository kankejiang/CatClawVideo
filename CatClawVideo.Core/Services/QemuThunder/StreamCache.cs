namespace CatClawVideo.Core.Services.QemuThunder;

/// <summary>
/// 磁力点播磁盘缓存上限的**全局偏好**（设置页可调）。
///
/// <para><b>为什么单独抽一个类</b>：上限要在「播放中实时生效」——用户在设置页改成 20GB，
/// 引擎下一次超限清理就要按 20GB 判，不必重启 App。用一个进程级静态量最简单；
/// 放 Core 而不是直接读 MAUI 的 Preferences，是为了不让 Core 依赖平台层
/// （持久化仍由 MAUI 层做，启动时灌进来）。</para>
///
/// <para>默认 20GB（2026-09-17 用户反馈 5~15GB 偏小）。已看区间重进秒开靠它，
/// 太小会导致「刚看过的片段又被 LRU 清掉」。</para>
/// </summary>
public static class StreamCachePrefs
{
    /// <summary>默认上限（GB）</summary>
    public const long DefaultGb = 20;

    /// <summary>设置页下拉的档位（GB）</summary>
    public static readonly long[] OptionsGb = [5, 10, 20, 30, 50];

    private static long _gb = DefaultGb;

    /// <summary>上限（GB）；非法值回落默认</summary>
    public static long CapGb
    {
        get => _gb;
        set => _gb = value is >= 2 and <= 500 ? value : DefaultGb;
    }

    /// <summary>上限（字节）——引擎超限清理直接用这个</summary>
    public static long CapBytes => CapGb * 1024L * 1024 * 1024;

    /// <summary>变化通知（需要立刻重算时用）</summary>
    public static event Action? Changed;

    /// <summary>设置页写入口：改值并通知</summary>
    public static void SetGb(long gb)
    {
        CapGb = gb;
        Changed?.Invoke();
    }
}

/// <summary>
/// 磁力点播的宿主侧磁盘缓存（2026-09-17 用户要求：超限自动删最旧；上限见 <see cref="StreamCachePrefs"/>）。
///
/// <para><b>为什么每次重进都要从头下</b>：此前播放数据只存在于引擎 tmpfs 与代理内存窗，
/// 换集/重开/重启后全部丢失，播放器重新 Range 拉取时引擎要么重新下载、要么供不出数。</para>
///
/// <para><b>设计</b>：按 <c>btih/文件索引</c> 分目录，4MB 一个分块文件（<c>{n}.bin</c>）。
/// 分块**只整块写入**（代理侧攒齐 4MB 才落盘，见 QemuStreamProxy 的 pending 攒写），
/// 因此「文件长度 = 期望长度」即可判定完整——不引入覆盖 bitmap，也不怕稀疏洞。
/// LRU 按文件 mtime：正在播的文件块不断被写 → mtime 最新 → 最后才被清，天然安全。</para>
/// </summary>
public static class StreamCache
{
    /// <summary>分块大小（4MB）：与播放器典型读窗口匹配，单块 SSD 写入 ~10ms 量级。</summary>
    public const int ChunkSize = 4 * 1024 * 1024;

    /// <summary>默认容量上限（兼容保留；实际取 <see cref="StreamCachePrefs.CapBytes"/>）。</summary>
    public const long DefaultCapBytes = StreamCachePrefs.DefaultGb * 1024 * 1024 * 1024;

    /// <summary>某文件某分块的完整落盘目录：<c>{root}/{btih}/{fileIndex}</c>。</summary>
    public static string DirFor(string cacheRoot, string btihHex, int fileIndex)
    {
        var dir = Path.Combine(cacheRoot, Sanitize(btihHex), fileIndex.ToStringInvariant());
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>分块是否已完整落盘（长度 = 期望长度；末块允许短）。</summary>
    public static bool ChunkComplete(string dir, long totalSize, long chunkIndex)
    {
        try
        {
            var path = ChunkPath(dir, chunkIndex);
            if (!File.Exists(path)) return false;
            var expected = ExpectedChunkSize(totalSize, chunkIndex);
            return new FileInfo(path).Length == expected;
        }
        catch { return false; }
    }

    /// <summary>整体写入一个完整分块（调用方攒齐整块数据后才调用，保证无洞）。</summary>
    public static void WriteWholeChunk(string dir, long chunkIndex, byte[] data, int count)
    {
        try
        {
            File.WriteAllBytes(ChunkPath(dir, chunkIndex), data.AsSpan(0, count).ToArray());
        }
        catch { /* 磁盘异常静默：缓存失败只影响下次点播速度，不影响本次播放 */ }
    }

    /// <summary>读一个已完整分块的 <c>[withinChunk, withinChunk+count)</c> 区间到 dest。返回实际读取字节数。</summary>
    public static int ReadChunkData(string dir, long chunkIndex, int withinChunk, byte[] dest, int destOffset, int count)
    {
        try
        {
            using var fs = new FileStream(ChunkPath(dir, chunkIndex), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (withinChunk >= fs.Length) return 0;
            fs.Seek(withinChunk, SeekOrigin.Begin);
            var n = 0;
            while (n < count)
            {
                var r = fs.Read(dest, destOffset + n, count - n);
                if (r <= 0) break;
                n += r;
            }
            return n;
        }
        catch { return 0; }
    }

    /// <summary>LRU 容量清理：按 mtime 从旧到新删分块，直到总占用 ≤ cap。后台调用，不阻塞播放。</summary>
    public static void EnforceCap(string cacheRoot, long capBytes, Action<string>? log = null)
    {
        try
        {
            if (!Directory.Exists(cacheRoot)) return;
            var files = Directory.EnumerateFiles(cacheRoot, "*.bin", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();
            long total = files.Sum(f => f.Length);
            if (total <= capBytes) return;
            var freed = 0L;
            var deleted = 0;
            foreach (var f in files)
            {
                if (total - freed <= capBytes) break;
                try
                {
                    var len = f.Length;
                    f.Delete();
                    freed += len;
                    deleted++;
                }
                catch { /* 被占用（正在写）跳过 */ }
            }
            if (deleted > 0)
                log?.Invoke($"[缓存] 磁盘缓存超限清理：删 {deleted} 块 / 释放 {freed / 1048576.0:F0}MB（剩余 {(total - freed) / 1048576.0:F0}MB / 上限 {capBytes / 1073741824.0:F0}GB）");
        }
        catch (Exception ex)
        {
            log?.Invoke($"[缓存] 清理失败：{ex.Message}");
        }
    }

    private static string ChunkPath(string dir, long chunkIndex) => Path.Combine(dir, $"{chunkIndex}.bin");

    private static long ExpectedChunkSize(long totalSize, long chunkIndex) =>
        Math.Min(ChunkSize, totalSize - chunkIndex * ChunkSize);

    private static string Sanitize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.Length == 0 ? "unknown" : sb.ToString();
    }
}

file static class IntExt
{
    public static string ToStringInvariant(this int v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
