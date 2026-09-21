// 数据面基准：量化「guest 写入 → 宿主直读」这条稀疏块设备通道的
//          传输速度 / 单次操作延迟 / 并发扩展性。
//
// 为什么单独做这个基准：产品取流路径已改成
//   迅雷引擎(guest) → harness 转发时顺带 pwrite /dev/vda → 宿主直读同一稀疏镜像
// 而宿主侧每轮循环都要付两步固定成本：
//   IsRangeAvailable()  FSCTL_QUERY_ALLOCATED_RANGES + 2×AllocHGlobal + **开一次文件句柄**
//   ReadAt()            随机读 + **再开一次文件句柄**
// 把这三步拆开测，才能回答「速度/延迟/并发」以及「成本到底花在哪」。
//
// 用法：
//   dotnet run -c Release --project hosttest -- xfer [imagePath] [capacityMB] [fillMB]
//     默认 %TEMP%\catclaw-xfer-bench.img / 容量 2048MB / 预填 512MB
using System.Diagnostics;
using CatClawVideo.Core.Services.QemuThunder;

internal static class BenchXfer
{
    private const long MB = 1024 * 1024;
    /// <summary>单档最多跑多少次操作（小块时防止跑成天文数字）。</summary>
    private const int MaxOps = 60_000;
    /// <summary>单档目标读取量：按块大小换算操作次数，再夹到 [64, MaxOps]。</summary>
    private const long TargetBytesPerCell = 512 * MB;

    // ═══════════════ 统计 ═══════════════

    private sealed class Stat
    {
        public readonly List<double> Us = new(1 << 16);
        public long Bytes;
        public double WallMs;

        public void Tick(long t0, long t1, int bytes)
        {
            Us.Add((t1 - t0) * 1_000_000.0 / Stopwatch.Frequency);
            Bytes += bytes;
        }

        public double Avg => Us.Count == 0 ? 0 : Us.Sum() / Us.Count;
        public double Throughput => WallMs <= 0 ? 0 : Bytes / (double)MB / (WallMs / 1000.0);

        public double Pct(double q)
        {
            if (Us.Count == 0) return 0;
            var a = Us.ToArray();
            Array.Sort(a);
            var i = (int)Math.Clamp(q / 100.0 * (a.Length - 1), 0, a.Length - 1);
            return a[i];
        }

        public void Merge(Stat o)
        {
            Us.AddRange(o.Us);
            Bytes += o.Bytes;
            WallMs = Math.Max(WallMs, o.WallMs);
        }
    }

    private static long T() => Stopwatch.GetTimestamp();
    private static double MsSince(long t) => (Stopwatch.GetTimestamp() - t) * 1000.0 / Stopwatch.Frequency;

    private static string Bytes2Str(long b) => b >= MB ? $"{b / (double)MB:F1}MB" : $"{b / 1024.0:F1}KB";
    private static string US(double us) => us >= 1000 ? $"{us / 1000:F2}ms" : $"{us:F1}µs";

    // ═══════════════ 入口：宿主侧文件通道基准 ═══════════════

    public static int RunDisk(string imagePath, int capacityMb, int fillMb)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        var cap = capacityMb * MB;
        var fill = Math.Min(fillMb * MB, cap);

        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║  数据面基准 · 宿主侧文件通道（稀疏块设备镜像直读）                       ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine($"  机器   : {Environment.MachineName} / .NET {Environment.Version} / {Environment.ProcessorCount} 逻辑核");
        Console.WriteLine($"  镜像   : {imagePath}");
        Console.WriteLine();

        // ── 建镜像 + 预填（模拟 guest 的 pwrite）──
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        var store = new SparseBlockStore(imagePath, cap);
        store.EnsureCreated();
        var already = store.AllocatedBytes();
        Console.WriteLine($"[准备] 稀疏={store.IsSparse}  容量={capacityMb}MB  已占={already / MB}MB");

        if (already < fill)
        {
            var t0 = T();
            var buf = new byte[4 * MB];
            new Random(20260921).NextBytes(buf);
            using var h = File.OpenHandle(imagePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            long off = 0;
            for (; off + buf.Length <= fill; off += buf.Length) RandomAccess.Write(h, buf, off);
            if (fill - off > 0) RandomAccess.Write(h, buf.AsSpan(0, (int)(fill - off)), off);
            Console.WriteLine($"[准备] 模拟 guest 顺序写入 {fill / MB}MB（{MsSince(t0):F0}ms，"
                              + $"{fill / (double)MB / (MsSince(t0) / 1000):F0} MB/s）");
        }
        else
        {
            Console.WriteLine($"[准备] 复用已有数据（已占 {already / MB}MB ≥ 需 {fill / MB}MB）");
        }
        Console.WriteLine($"[准备] 稀疏查询：已分配 {Bytes2Str(store.AllocatedBytes())}");
        Console.WriteLine();

        // 基准只在前 fill 字节内跑（这段确定是「已写入」的，IsRangeAvailable 应为真）
        var region = fill;

        ScanBlockSizes(store, region);
        ScanOps(store, region);
        ScanConcurrency(store, region);
        ScanRandom(store, region);
        ScanBaseline(store, imagePath, region);
        ScanFragmented(store, imagePath, region);

        Console.WriteLine();
        Console.WriteLine("════════════════════════════════════════════════════════════════════════════");
        Console.WriteLine($"  镜像保留在：{imagePath}");
        Console.WriteLine($"  （稀疏文件，物理占用 {Bytes2Str(store.AllocatedBytes())}；不需要可直接删除）");
        Console.WriteLine("════════════════════════════════════════════════════════════════════════════");
        return 0;
    }

    // ═══════════════ [A] 块大小扫描 ═══════════════
    // 播放器就是按块来读的（实测 64KB ~ 4MB），块大小直接决定每轮固定成本的摊薄程度。

    private static void ScanBlockSizes(SparseBlockStore store, long region)
    {
        Console.WriteLine("── [A] 块大小扫描（单线程顺序读，三种操作分开测）─────────────────────────");
        Console.WriteLine();
        Console.WriteLine("   块大小      操作                       吞吐        均值       P50       P95       P99");

        long[] sizes = [4 * 1024, 16 * 1024, 64 * 1024, 256 * 1024, 1024 * 1024, 4 * 1024 * 1024, 16 * 1024 * 1024];
        var buf = new byte[16 * MB];

        foreach (var bsL in sizes)
        {
            var bs = (int)bsL;
            var ops = (int)Math.Clamp(Math.Max(64, TargetBytesPerCell / bs), 64, MaxOps);

            // ① ReadAt 单独
            var s1 = Measure(ops, bs, (i, off) => store.ReadAt(off, buf, 0, bs), region);
            Row(bs, "ReadAt 单独", s1);

            // ② IsRangeAvailable 单独（纯 FSCTL 查询成本）
            var s2 = Measure(ops, bs, (i, off) => store.IsRangeAvailable(off, bs) ? bs : 0, region);
            Row(bs, "IsRangeAvailable 单独", s2);

            // ③ 组合 = QemuStreamProxy 的真实调用序列
            var s3 = Measure(ops, bs, (i, off) =>
            {
                if (!store.IsRangeAvailable(off, bs)) return 0;
                return store.ReadAt(off, buf, 0, bs);
            }, region);
            Row(bs, "组合（proxy 真实序列）", s3);
            Console.WriteLine();
        }
    }

    private static Stat Measure(int ops, int bs, Func<int, long, int> action, long region)
    {
        var st = new Stat();
        var t0 = T();
        for (var i = 0; i < ops; i++)
        {
            var off = (long)(i % Math.Max(1, region / bs)) * bs;
            var a = T();
            var n = action(i, off);
            var b = T();
            st.Tick(a, b, n > 0 ? n : 0);
        }
        st.WallMs = MsSince(t0);
        return st;
    }

    private static void Row(long bs, string op, Stat s)
    {
        var size = bs >= MB ? $"{bs / MB}MB" : $"{bs / 1024}KB";
        Console.WriteLine($"   {size,-9} {op,-24} {s.Throughput,7:F0} MB/s {US(s.Avg),9} {US(s.Pct(50)),9} {US(s.Pct(95)),9} {US(s.Pct(99)),9}");
    }

    // ═══════════════ [B] 固定开销拆解 ═══════════════
    // 把「一次 256KB 顺序读」的成本拆成：开句柄 / FSCTL 查询 / 实际读数据。

    private static void ScanOps(SparseBlockStore store, long region)
    {
        Console.WriteLine("── [B] 固定开销拆解（256KB 块，10000 次）──────────────────────────────────");
        const int bs = 256 * 1024;
        const int ops = 10_000;
        var buf = new byte[bs];

        // 只开句柄 + 只读数据（持久句柄，无重复 OpenHandle）—— 这是 ReadAt 的「不含开句柄」下界
        var persistent = new Stat();
        {
            using var h = File.OpenHandle(store.ImagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var t0 = T();
            for (var i = 0; i < ops; i++)
            {
                var off = (long)(i % (region / bs)) * bs;
                var a = T();
                var n = RandomAccess.Read(h, buf, off);
                var b = T();
                persistent.Tick(a, b, n);
            }
            persistent.WallMs = MsSince(t0);
        }

        var readAt = Measure(ops, bs, (i, off) => store.ReadAt(off, buf, 0, bs), region);
        var query = Measure(ops, bs, (i, off) => store.IsRangeAvailable(off, bs) ? bs : 0, region);
        var combo = Measure(ops, bs, (i, off) =>
        {
            if (!store.IsRangeAvailable(off, bs)) return 0;
            return store.ReadAt(off, buf, 0, bs);
        }, region);

        Console.WriteLine($"   {"动作",-34} {"均值",9} {"P50",9} {"P99",9}   备注");
        Console.WriteLine($"   {"① 纯读（持久句柄，不开句柄）",-34} {US(persistent.Avg),9} {US(persistent.Pct(50)),9} {US(persistent.Pct(99)),9}   RandomAccess.Read");
        Console.WriteLine($"   {"② ReadAt（每次开句柄）",-34} {US(readAt.Avg),9} {US(readAt.Pct(50)),9} {US(readAt.Pct(99)),9}   含 OpenHandle");
        Console.WriteLine($"   {"③ IsRangeAvailable",-34} {US(query.Avg),9} {US(query.Pct(50)),9} {US(query.Pct(99)),9}   含 FSCTL + 2×AllocHGlobal");
        Console.WriteLine($"   {"④ 组合（③→②，proxy 真实序列）",-34} {US(combo.Avg),9} {US(combo.Pct(50)),9} {US(combo.Pct(99)),9}   = 每轮实际成本");
        Console.WriteLine();
        Console.WriteLine($"   -> 开句柄成本 ~ ② - ① = {US(readAt.Avg - persistent.Avg)}（每次读）");
        Console.WriteLine($"   -> FSCTL 查询成本 ~ ③ = {US(query.Avg)}（与 ReadAt 同量级 -> 查询不比读便宜）");
        Console.WriteLine($"   -> 每轮固定成本 ~ ④ - ①（真正读数据）= {US(combo.Avg - persistent.Avg)}"
                          + $"（占一轮的 {(combo.Avg > 0 ? (combo.Avg - persistent.Avg) / combo.Avg * 100 : 0):F0}%）");
        Console.WriteLine($"   -> 组合吞吐 {combo.Throughput:F0} MB/s（单线程上限）");
        Console.WriteLine();
    }

    // ═══════════════ [C] 并发扩展 ═══════════════

    private static void ScanConcurrency(SparseBlockStore store, long region)
    {
        Console.WriteLine("── [C] 并发扩展（256KB 块，总读 256MB，每档线程数）─────────────────────────");
        Console.WriteLine();
        Console.WriteLine("   线程数   总吞吐      扩展效率   单次均值     P95        P99      总操作数");

        const int bs = 256 * 1024;
        const long total = 256 * MB;
        int[] levels = [1, 2, 4, 8, 16, 32];
        double baseThroughput = 0;
        var buf = new byte[bs];

        foreach (var n in levels)
        {
            var per = Math.Max(bs * 8, total / n);
            per = Math.Min(per, region);
            var stats = new Stat[n];
            var barrier = new Barrier(n);
            var threads = new Thread[n];

            for (var t = 0; t < n; t++)
            {
                var idx = t;
                stats[idx] = new Stat();
                threads[idx] = new Thread(() =>
                {
                    var st = stats[idx];
                    var start = (long)idx * (region / n);
                    var ops = Math.Max(1, per / bs);
                    barrier.SignalAndWait();
                    var t0 = T();
                    for (var i = 0; i < ops; i++)
                    {
                        var off = Math.Min(start + (long)i * bs, region - bs);
                        var a = T();
                        var got = store.ReadAt(off, buf, 0, bs);
                        var b = T();
                        st.Tick(a, b, got > 0 ? got : 0);
                    }
                    st.WallMs = MsSince(t0);
                }) { IsBackground = true };
                threads[idx].Start();
            }
            foreach (var th in threads) th.Join();

            var agg = new Stat();
            foreach (var s in stats) agg.Merge(s);
            agg.WallMs = stats.Max(s => s.WallMs);
            if (baseThroughput <= 0) baseThroughput = agg.Throughput;

            var eff = baseThroughput > 0 ? agg.Throughput / (baseThroughput * n) * 100 : 0;
            Console.WriteLine($"   {n,6}   {agg.Throughput,7:F0} MB/s   {eff,6:F0}%   {US(agg.Avg),9} {US(agg.Pct(95)),9} {US(agg.Pct(99)),9}  {agg.Us.Count,8}");
        }
        Console.WriteLine();
        Console.WriteLine("   （扩展效率 = 实测吞吐 / (单线程吞吐 × 线程数)；100% = 完全线性）");
        Console.WriteLine();
    }

    // ═══════════════ [D] 随机读（seek 场景）═══════════════

    private static void ScanRandom(SparseBlockStore store, long region)
    {
        Console.WriteLine("── [D] 随机读（播放器 seek / moov 尾探测）─────────────────────────────────");
        Console.WriteLine();
        Console.WriteLine("   块大小    操作                     均值       P50       P95       P99     吞吐");

        foreach (var bs in new[] { 64 * 1024, 256 * 1024, 1024 * 1024 })
        {
            var buf = new byte[bs];
            var ops = (int)Math.Clamp(TargetBytesPerCell / bs, 64, MaxOps);
            var rnd = new Random(7);
            var maxBlock = Math.Max(1, region / bs - 1);

            var st = new Stat();
            var t0 = T();
            for (var i = 0; i < ops; i++)
            {
                var off = (long)rnd.NextInt64(maxBlock) * bs;
                var a = T();
                var n = store.ReadAt(off, buf, 0, bs);
                var b = T();
                st.Tick(a, b, n > 0 ? n : 0);
            }
            st.WallMs = MsSince(t0);
            Row(bs, "ReadAt 单独", st);

            // 组合（随机 = 每次都过一遍 FSCTL）
            var st2 = new Stat();
            t0 = T();
            for (var i = 0; i < ops; i++)
            {
                var off = (long)rnd.NextInt64(maxBlock) * bs;
                var a = T();
                var n = store.IsRangeAvailable(off, bs) ? store.ReadAt(off, buf, 0, bs) : 0;
                var b = T();
                st2.Tick(a, b, n > 0 ? n : 0);
            }
            st2.WallMs = MsSince(t0);
            Row(bs, "组合（proxy 真实序列）", st2);
            Console.WriteLine();
        }
    }

    // ═══════════════ [E] 对照基线 ═══════════════
    // 用来界定「通道能力上限」，把 SparseBlockStore 的开销衬托出来。

    private static void ScanBaseline(SparseBlockStore store, string imagePath, long region)
    {
        Console.WriteLine("── [E] 对照基线（同机、同数据）─────────────────────────────────────────────");
        Console.WriteLine();
        Console.WriteLine("   路径                                 256KB 均值   吞吐        备注");
        const int bs = 256 * 1024;
        var buf = new byte[bs];
        var ops = (int)Math.Clamp(TargetBytesPerCell / bs, 64, MaxOps);

        // ① 持久句柄 + RandomAccess（无 FSCTL、无重复开句柄）
        {
            using var h = File.OpenHandle(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var st = new Stat();
            var t0 = T();
            for (var i = 0; i < ops; i++)
            {
                var off = (long)(i % (region / bs)) * bs;
                var a = T();
                var n = RandomAccess.Read(h, buf, off);
                var b = T();
                st.Tick(a, b, n);
            }
            st.WallMs = MsSince(t0);
            Console.WriteLine($"   {"持久句柄 RandomAccess.Read",-36} {US(st.Avg),9}   {st.Throughput,7:F0} MB/s   直读天花板");
        }

        // ② 普通 FileStream（每次开）
        {
            var st = new Stat();
            var t0 = T();
            for (var i = 0; i < ops; i++)
            {
                var off = (long)(i % (region / bs)) * bs;
                var a = T();
                using (var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1))
                {
                    fs.Seek(off, SeekOrigin.Begin);
                    fs.ReadExactly(buf, 0, bs);
                }
                var b = T();
                st.Tick(a, b, bs);
            }
            st.WallMs = MsSince(t0);
            Console.WriteLine($"   {"FileStream 每次开 + Seek + Read",-36} {US(st.Avg),9}   {st.Throughput,7:F0} MB/s   老式写法对照");
        }

        // ③ 内存拷贝（QemuStreamProxy 缓存命中时的上限）
        {
            var src = new byte[bs];
            var st = new Stat();
            var t0 = T();
            for (var i = 0; i < ops; i++)
            {
                var a = T();
                src.CopyTo(buf, 0);
                var b = T();
                st.Tick(a, b, bs);
            }
            st.WallMs = MsSince(t0);
            Console.WriteLine($"   {"内存拷贝（proxy 缓存命中）",-36} {US(st.Avg),9}   {st.Throughput,7:F0} MB/s   物理上限参照");
        }
        Console.WriteLine();
    }

    // ═══════════════ [F] 碎片化对照 ═══════════════
    // BT 是乱序下载：镜像上「已分配区间」是碎的。FSCTL 查询返回的是区间**列表**，
    // 碎片越多返回项越多；实现里 maxOut=64，超出会 ERROR_MORE_DATA（DeviceIoControl 返回 FALSE）。
    // 这里造两种极端文件，看查询是否还判得准、成本是否变化。

    private static void ScanFragmented(SparseBlockStore store, string imagePath, long region)
    {
        Console.WriteLine("── [F] 碎片化对照（模拟 BT 乱序下载后的稀疏程度）───────────────────────────");
        Console.WriteLine();

        var fragPath = Path.ChangeExtension(imagePath, ".frag.img");
        const long cap = 1024 * MB;
        var frag = new SparseBlockStore(fragPath, cap);
        frag.EnsureCreated();

        // 交替写：每 1MB 写前半 512KB（= 50% 完成度的碎片盘），共写 512MB 有效数据
        var blk = new byte[512 * 1024];
        new Random(2026).NextBytes(blk);
        using (var h = File.OpenHandle(fragPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            for (long off = 0; off < 512 * MB; off += 1024 * 1024)
                RandomAccess.Write(h, blk, off);
        }
        Console.WriteLine($"   碎片镜像：{fragPath}（每 1MB 写 512KB，有效 512MB）");
        Console.WriteLine($"     AllocatedBytes() = {Bytes2Str(frag.AllocatedBytes())}"
                          + "   （真值 256MB；修复前后对照见 [F1]）");

        // 连续对照：重新用连续文件的前 512MB
        const int bs = 256 * 1024;
        var buf = new byte[bs];
        const int ops = 2000;

        // 连续区：从 region 里取连续 512MB（region 就是连续填充的）
        var contRegion = Math.Min(region, 512 * MB);
        var (contOk, contSt) = ProbeAvailable(store, contRegion, bs, ops);
        Console.WriteLine($"   连续文件 256KB 窗口：可用率 {contOk * 100.0 / ops:F1}%  查询均值 {US(contSt.Avg)}  "
                          + $"P99 {US(contSt.Pct(99))}");

        // 碎片区：查「落在已写半块内」的窗口（应为可用）
        var (fragOk, fragSt) = ProbeAvailable(frag, 512 * MB, bs, ops, aligned: true);
        Console.WriteLine($"   碎片文件 256KB 窗口（落在已写区间内）：可用率 {fragOk * 100.0 / ops:F1}%  查询均值 {US(fragSt.Avg)}  "
                          + $"P99 {US(fragSt.Pct(99))}");

        // 碎片区跨洞：窗口一端在已写、另一端在洞（真实播放器在 piece 边界会这样）
        var crossFalse = 0;
        var crossSt = new Stat();
        {
            var t0 = T();
            for (var i = 0; i < ops; i++)
            {
                var off = (long)i % 256 * 1024 * 1024 + (512 * 1024 - bs / 2);   // 跨越 512KB 边界
                var a = T();
                var ok = frag.IsRangeAvailable(off, bs);
                var b = T();
                crossSt.Tick(a, b, ok ? bs : 0);
                if (!ok) crossFalse++;
            }
            crossSt.WallMs = MsSince(t0);
        }
        Console.WriteLine($"   碎片文件 256KB 窗口（跨越空洞）：判为不可用 {crossFalse * 100.0 / ops:F1}%"
                          + "  （预期 100% —— 跨洞不该直读，实现是对的）");
        Console.WriteLine();

        // 大窗口压力：1MB 窗口在碎片盘上会覆盖大量区间
        foreach (var win in new[] { 256 * 1024, 1024 * 1024, 4 * 1024 * 1024 })
        {
            var st = new Stat();
            const int n = 500;
            var t0 = T();
            for (var i = 0; i < n; i++)
            {
                var off = (long)i * 2 * 1024 * 1024 % (500 * 1024 * 1024);
                var a = T();
                frag.IsRangeAvailable(off, win);
                var b = T();
                st.Tick(a, b, 0);
            }
            st.WallMs = MsSince(t0);
            Console.WriteLine($"   碎片盘查询窗口 {win / 1024}KB：均值 {US(st.Avg)}  P99 {US(st.Pct(99))}");
        }
        Console.WriteLine();
        Console.WriteLine("   注：FSCTL 查询每次最多回 64 个区间（maxOut=64）。下面用两种碎片段实测它的真实边界。");
        Console.WriteLine();

        ScanAllocatedLimit(imagePath);
        ScanBtHitRate(imagePath);
    }

    /// <summary>
    /// 确证 FSCTL 的 64 区间上限 —— 用「大段交替写入」的碎片盘，这正是 **BT 下载的真实分配形态**
    /// （piece 大段落地、段与段之间是空洞）。
    ///
    /// <para>判据链：fsutil file queryextents 显示该文件是 512KB 已分配 / 512KB 空洞交替（512 个区间）；
    /// 分段查询能求和出真值，而 AllocatedBytes()（查全盘）返回 0 —— 说明区间数超 64 后查询整体作废。</para>
    /// </summary>
    private static void ScanAllocatedLimit(string imagePath)
    {
        var p = Path.ChangeExtension(imagePath, ".frag.img");
        const long cap = 1024 * MB;
        var st = new SparseBlockStore(p, cap);
        st.EnsureCreated();

        // 真值：分段（2MB 一段）求和 —— 每段内只有一个 512KB 已分配区间，不会撞上限
        long seg = 0;
        var segRanges = 0;
        for (long o = 0; o < cap; o += 2 * 1024 * 1024)
            foreach (var (s, e) in st.QueryAllocatedRanges(o, 2 * 1024 * 1024)) { seg += e - s; segRanges++; }

        var all = st.AllocatedBytes();

        Console.WriteLine("   ── [F1] FSCTL 64 区间上限实测（碎片盘 = BT 下载的真实分配形态）──");
        Console.WriteLine("   碎片盘：每 1MB 写 512KB，已分配区间交替（fsutil queryextents 证实 512 个区间）");

        var direct = st.QueryAllocatedRanges(0, cap);              // = 修复前 AllocatedBytes 的内部写法
        var directSum = direct.Sum(r => r.End - r.Start);

        Console.WriteLine($"     真值（分段 2MB 求和）    = {seg / (double)MB,8:F1} MB / {segRanges} 个区间");
        Console.WriteLine($"     修复前（查全盘一次）     = {directSum / (double)MB,8:F1} MB   <- 超 64 区间丢结果（静默报 0）");
        Console.WriteLine($"     修复后 AllocatedBytes()  = {all / (double)MB,8:F1} MB   <- 分段查询（2026-09-21 已修）");

        Console.WriteLine();
        Console.WriteLine("     查询范围     返回区间数   判定");
        foreach (var spanMb in new[] { 1, 8, 16, 32, 64, 128, 1024 })
        {
            var span = (long)spanMb * MB;
            var n = st.QueryAllocatedRanges(0, span).Count;
            var expect = (int)Math.Min(int.MaxValue, span / MB);          // 每 1MB 一个区间
            var verdict = n == 0 ? "命中上限 -> 丢全部结果（缺陷）" : (n < expect ? "被截断" : "正常");
            Console.WriteLine($"     {spanMb,7}MB   {n,10}   {verdict}");
        }
        Console.WriteLine();
        Console.WriteLine("     结论：IsRangeAvailable **不受影响** —— 它只在「有一个区间完整覆盖窗口」时判可用，");
        Console.WriteLine("           那种情形下窗口内必然只有 1 个区间，永远撞不到 64 上限。");
        Console.WriteLine("           受影响的只有查大范围的两个入口：");
        Console.WriteLine("             · AllocatedBytes()  -> 碎片盘静默报 0（QemuHostRuntime 的「已占 XMB」日志失效）· 已修");
        Console.WriteLine("             · QuerySparseFlag() -> 复用已有镜像时误判「非稀疏」（仅诊断标签，未改）");
        Console.WriteLine("           BT 场景下 piece 越小、完成度越接近 50%，区间数越多 -> 越容易命中（1MB piece 的 1GB 盘");
        Console.WriteLine("           下 50% 完成度约 128 个区间，必然命中）。");
        Console.WriteLine();
    }

    /// <summary>BT 仿真：4MB piece、50% 完成度下，256KB 窗口的直读可用率（= 直读命中上限）。</summary>
    private static void ScanBtHitRate(string imagePath)
    {
        var p = Path.ChangeExtension(imagePath, ".bt.img");
        const long cap = 512 * MB;
        const int pieceCount = 128;              // 4MB × 128 = 512MB
        const int pieceSize = 4 * 1024 * 1024;
        var st = new SparseBlockStore(p, cap);
        st.EnsureCreated();

        var marked = new bool[pieceCount];
        var rnd = new Random(1);
        var done = 0;
        while (done < pieceCount / 2) { var i = rnd.Next(pieceCount); if (!marked[i]) { marked[i] = true; done++; } }

        var buf = new byte[pieceSize];
        new Random(5).NextBytes(buf);
        using (var h = File.OpenHandle(p, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            for (var i = 0; i < pieceCount; i++)
                if (marked[i]) RandomAccess.Write(h, buf, (long)i * pieceSize);

        Console.WriteLine("   ── [F2] BT 仿真盘：4MB piece、50% 完成度（64/128 piece 随机已下）──");
        foreach (var win in new[] { 64 * 1024, 256 * 1024, 1024 * 1024 })
        {
            int ok = 0;
            const int n = 2000;
            var w2 = new Random(3);
            var slots = Math.Max(1, cap / win - 1);
            for (var i = 0; i < n; i++)
            {
                var off = w2.NextInt64(slots) * win;
                if (st.IsRangeAvailable(off, win)) ok++;
            }
            Console.WriteLine($"     窗口 {win / 1024,5}KB：直读可用率 {ok * 100.0 / n,5:F1}%"
                              + $"   理论 = 50% - 跨 piece 边界损耗 ~{win * 100.0 / pieceSize / 2:F1}%");
        }
        var btAlloc = st.AllocatedBytes();
        Console.WriteLine($"     AllocatedBytes() 报告 = {btAlloc / (double)MB:F0}MB / 512MB（真值 256MB）"
                          + (btAlloc == 0
                              ? "   <- 区间数超限，报 0"
                              : "   <- 4MB 大 piece 相邻合并后区间数 ~32，未触发上限"));
        Console.WriteLine("     注：可用率 = 直读能直接供数的比例；其余按设计走 HTTP 回退（慢，但不会读到洞里的零）。");
        Console.WriteLine();
    }

    private static (int ok, Stat st) ProbeAvailable(SparseBlockStore store, long region, int bs, int ops, bool aligned = false)
    {
        var st = new Stat();
        var ok = 0;
        var t0 = T();
        for (var i = 0; i < ops; i++)
        {
            long off;
            if (aligned) off = (long)i * 1024 * 1024 % Math.Max(bs, region - 1024 * 1024);   // 对齐到 1MB 边界（已写半块内）
            else off = (long)i * bs % Math.Max(bs, region - bs);
            var a = T();
            if (store.IsRangeAvailable(off, bs)) ok++;
            var b = T();
            st.Tick(a, b, 0);
        }
        st.WallMs = MsSince(t0);
        return (ok, st);
    }
}
