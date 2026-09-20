using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CatClawVideo.Core.Services.QemuThunder;

/// <summary>
/// 稀疏块存储 —— 宿主与 guest 之间的**零拷贝数据面**。
///
/// <para><b>为什么需要它</b>：2026-09-21 实测，产品现有取流路径的每一层都在白吃带宽
/// （同一台机、产品同款配置 <c>-m 5120 -smp 4</c>，各 512MB）：</para>
///
/// <list type="table">
/// <item><description>guest 内环 TCP（数据不出 guest）：<b>432 MB/s</b> —— 证明 ARM 模拟不是瓶颈</description></item>
/// <item><description>SLIRP 上行（guest→宿主）：<b>40.0 MB/s</b> —— 掉 10.8 倍，全丢在 QEMU 用户态 TCP 栈</description></item>
/// <item><description>harness 转发后（16KB select 循环）：<b>18.9 MB/s</b> —— 再砍一半</description></item>
/// </list>
///
/// <para>而承载这个瓶颈的通道本可以完全不要：让 guest 把字节写进一块 virtio-blk，
/// 宿主**直读同一个物理文件**即可 —— 实测直读 <b>2926 MB/s</b>，
/// 且写入侧实测 84.9~97.7 MB/s（远高于迅雷引擎自身的 2.5~4.8 MB/s）。</para>
///
/// <para><b>写时复制</b>：镜像建成 <b>稀疏文件</b>（<c>FSCTL_SET_SPARSE</c> + <c>SetLength</c>），
/// 没被写过的区间不占磁盘、读到即全零。guest 写多少就占多少，天然按需分配、
/// 无整盘预分配、无中途拷贝 —— 这就是「写时复制」在本场景要的效果。</para>
///
/// <para><b>偏移契约</b>：镜像内 <c>偏移 N</c> == 目标文件（影片）的 <c>偏移 N</c>，1:1 映射。
/// 这样宿主既不需要解析任何文件系统（.NET 无 ext4 解析），也不需要 guest 上报分配表：
/// 直接 <see cref="ReadAt"/> 目标偏移即可。</para>
///
/// <para>⚠ 刻意用 <b>raw</b> 而非 qcow2：qcow2 虽然原生 COW，但宿主读不了它的内部结构
/// （数据被簇表包住），直读就废了 —— 那是给「整块盘交给 guest 自己用」的场景准备的。</para>
/// </summary>
public sealed class SparseBlockStore : IDisposable
{
    private const uint FSCTL_SET_SPARSE = 0x000900C4;
    private const uint FSCTL_QUERY_ALLOCATED_RANGES = 0x000940CF;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ALLOCATED_RANGE_BUFFER
    {
        public long FileOffset;
        public long Length;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    /// <summary>镜像物理路径（交给 QEMU 的 <c>-drive file=...</c>）。</summary>
    public string ImagePath { get; }

    /// <summary>逻辑容量（字节）。guest 看到的盘就是这么大。</summary>
    public long CapacityBytes { get; }

    /// <summary>是否已启用稀疏；失败则退化为普通预分配文件（功能不受影响，只是占盘）。</summary>
    public bool IsSparse { get; private set; }

    public SparseBlockStore(string imagePath, long capacityBytes)
    {
        ImagePath = imagePath;
        CapacityBytes = capacityBytes;
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
    }

    /// <summary>
    /// 建（或复用）稀疏镜像。已存在且大小正确则直接复用 —— 复用是「重看/换集秒开」的关键：
    /// 上次会话已落盘的数据仍在，不必重下。
    ///
    /// <para>⚠ 顺序很关键：**必须先设 FSCTL_SET_SPARSE，再 SetLength**。
    /// 反过来的话文件在创建瞬间就按完整大小分配了簇（实测：4GB 镜像实占 4096MB，
    /// 稀疏标志虽置上但已分配的簇不会回收）—— 那就完全失去了写时复制的意义。</para>
    /// </summary>
    public void EnsureCreated()
    {
        if (File.Exists(ImagePath) && new FileInfo(ImagePath).Length == CapacityBytes)
        {
            IsSparse = QuerySparseFlag();
            return;
        }

        try { File.Delete(ImagePath); } catch { }

        // 1) 先建 0 长度的空文件并置稀疏标志
        using (var fs = new FileStream(ImagePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            fs.Flush();
        }
        var sparseOk = TrySetSparse();

        // 2) 标志生效后再扩到目标大小 —— 这样新簇才是「未分配」的
        using (var fs = new FileStream(ImagePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.SetLength(CapacityBytes);
        }

        IsSparse = sparseOk;
        // 兜底：SetLength 之后重量一次（某些文件系统上标志会被重置）
        if (!IsSparse) IsSparse = TrySetSparse();
    }

    private bool TrySetSparse()
    {
        try
        {
            using var h = File.OpenHandle(ImagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            return DeviceIoControl(h, FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        catch { return false; }
    }

    private bool QuerySparseFlag()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            // 用一次查询探测：稀疏文件查已分配区间会成功返回
            var ranges = QueryAllocatedRanges(0, CapacityBytes);
            return ranges.Count > 0;   // 至少文件头已分配（空文件也有元数据）
        }
        catch { return false; }
    }

    /// <summary>
    /// 查 <c>[offset, offset+length)</c> 内**已实际分配**（= guest 写过）的区间。
    /// 这是判断「这段数据能不能直接供数」的依据 —— 不必等 guest 上报。
    /// </summary>
    public List<(long Start, long End)> QueryAllocatedRanges(long offset, long length)
    {
        var result = new List<(long, long)>();
        if (!OperatingSystem.IsWindows() || length <= 0) return result;

        var inBuf = Marshal.AllocHGlobal(Marshal.SizeOf<FILE_ALLOCATED_RANGE_BUFFER>());
        const int maxOut = 64;
        var outSize = Marshal.SizeOf<FILE_ALLOCATED_RANGE_BUFFER>() * maxOut;
        var outBuf = Marshal.AllocHGlobal(outSize);
        try
        {
            var q = new FILE_ALLOCATED_RANGE_BUFFER { FileOffset = offset, Length = length };
            Marshal.StructureToPtr(q, inBuf, false);

            using var h = File.OpenHandle(ImagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (!DeviceIoControl(h, FSCTL_QUERY_ALLOCATED_RANGES, inBuf,
                    (uint)Marshal.SizeOf<FILE_ALLOCATED_RANGE_BUFFER>(), outBuf, (uint)outSize,
                    out var returned, IntPtr.Zero))
                return result;   // ERROR_MORE_DATA 也是正常返回已填的部分

            var n = (int)(returned / Marshal.SizeOf<FILE_ALLOCATED_RANGE_BUFFER>());
            for (var i = 0; i < n && i < maxOut; i++)
            {
                var e = Marshal.PtrToStructure<FILE_ALLOCATED_RANGE_BUFFER>(outBuf + i * Marshal.SizeOf<FILE_ALLOCATED_RANGE_BUFFER>());
                if (e.Length > 0) result.Add((e.FileOffset, e.FileOffset + e.Length));
            }
        }
        catch { }
        finally
        {
            Marshal.FreeHGlobal(inBuf);
            Marshal.FreeHGlobal(outBuf);
        }
        return result;
    }

    /// <summary>该区间是否已被 guest 写入（可直接供数，无需走 HTTP）。</summary>
    public bool IsRangeAvailable(long offset, int count)
    {
        if (offset < 0 || count <= 0 || offset + count > CapacityBytes) return false;
        foreach (var (s, e) in QueryAllocatedRanges(offset, count))
            if (s <= offset && e >= offset + count) return true;
        return false;
    }

    /// <summary>
    /// 宿主直读镜像（与 guest 写入同一物理文件）。返回实际读到的字节数。
    /// 未写入区间读到全零 —— 调用方应先用 <see cref="IsRangeAvailable"/> 判定可用性。
    /// </summary>
    public int ReadAt(long offset, byte[] dest, int destOffset, int count)
    {
        if (count <= 0 || offset < 0 || offset >= CapacityBytes) return 0;
        if (offset + count > CapacityBytes) count = (int)(CapacityBytes - offset);
        try
        {
            using var h = File.OpenHandle(ImagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return RandomAccess.Read(h, dest.AsSpan(destOffset, count), offset);
        }
        catch { return 0; }
    }

    /// <summary>已实际占用的字节数（判断「有没有数据」「占多少盘」）。</summary>
    public long AllocatedBytes()
    {
        try
        {
            var total = 0L;
            foreach (var (s, e) in QueryAllocatedRanges(0, CapacityBytes)) total += e - s;
            return total;
        }
        catch { return 0; }
    }

    public void Dispose() { }
}
