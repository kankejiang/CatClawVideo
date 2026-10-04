using CatClaw.Qemu;

namespace CatClawVideo.Maui.Services;

/// <summary>环境检测项的严重级别（与套件的 <see cref="QemuEnvLevel"/> 对齐，另加纯信息档）。</summary>
public enum EnvCheckLevel { Ok, Warn, Fail, Info }

/// <summary>
/// 一个可点的处置动作。只声明语义 id 与按钮文案，具体怎么执行由宿主 UI 决定
/// （这样检测服务不依赖 MAUI，也能在无 UI 场景下复用）。
/// </summary>
public sealed record EnvAction(string Id, string Text);

/// <summary>一条环境检测结果。<paramref name="Hint"/> 是文字建议，<paramref name="Actions"/> 是可点的一键处置。</summary>
public sealed record EnvCheckItem(string Title, EnvCheckLevel Level, string Detail, string? Hint = null,
    IReadOnlyList<EnvAction>? Actions = null);

/// <summary>
/// 启动环境检测（Windows）：把「虚拟机套件探测」与「宿主侧条件」合成一份清单，
/// 由首页冷启动画面在引擎预热期间直接展示 —— 排查「磁力不能播 / 杜比视界发灰 /
/// 引擎慢得离谱」这类问题，用户第一眼就能看到根因与处置办法，不用去翻日志。
///
/// <para>全部为本地探测（文件/磁盘/内存/PATH），无网络、无外部进程，毫秒级；
/// 任何一项抛异常都只影响该项（降级成 Info），不会阻断启动。</para>
/// </summary>
public static class StartupEnvCheck
{
    /// <summary>磁盘剩余空间的告警阈值（流缓存默认上限 10GB，块设备按需增长）。</summary>
    private const long DiskWarnBytes = 5L * 1024 * 1024 * 1024;

    /// <summary>物理内存告警阈值（QEMU 默认 2~3GB + 宿主自身）。</summary>
    private const ulong MemoryWarnBytes = 4UL * 1024 * 1024 * 1024;

    /// <summary>执行全部检测。非 Windows 平台返回空表（调用方据此隐藏面板）。</summary>
    public static IReadOnlyList<EnvCheckItem> Run()
    {
        var items = new List<EnvCheckItem>();
#if WINDOWS
        var baseDir = AppContext.BaseDirectory;

        // ① 虚拟机套件：WHPX / QEMU 引擎 / ART 镜像（探测逻辑在套件仓库，这里只映射级别）
        try
        {
            foreach (var q in QemuEnvCheck.Probe(Path.Combine(baseDir, "QemuGuest")))
            {
                // 按「探测项 + 级别」挂一键处置：虚拟化平台可提权直接开，载荷缺失可自动取件
                IReadOnlyList<EnvAction>? actions = (q.Id, q.Level) switch
                {
                    ("whpx", QemuEnvLevel.Warn or QemuEnvLevel.Fail) =>
                    [
                        new EnvAction("enable-whpx", "一键启用虚拟化"),
                        new EnvAction("howto-whpx", "详细步骤"),
                        new EnvAction("open-optional-features", "手动打开 Windows 功能"),
                    ],
                    ("qemu" or "guest", QemuEnvLevel.Fail) =>
                    [
                        new EnvAction("fetch-assets", "自动取件"),
                        new EnvAction("open-release-vm", "下载运行时"),
                    ],
                    _ => null,
                };

                items.Add(new EnvCheckItem(q.Title, q.Level switch
                {
                    QemuEnvLevel.Ok => EnvCheckLevel.Ok,
                    QemuEnvLevel.Warn => EnvCheckLevel.Warn,
                    QemuEnvLevel.Fail => EnvCheckLevel.Fail,
                    _ => EnvCheckLevel.Info,
                }, q.Detail, q.Hint, actions));
            }
        }
        catch (Exception ex)
        {
            items.Add(new EnvCheckItem("虚拟机套件", EnvCheckLevel.Info, $"探测失败：{ex.GetType().Name}"));
        }

        // ④ 磁盘剩余空间
        try
        {
            var root = Path.GetPathRoot(CatClawVideo.Core.AppPaths.LocalRoot);
            var free = root is null ? 0 : new DriveInfo(root).AvailableFreeSpace;
            items.Add(free >= DiskWarnBytes
                ? new EnvCheckItem("磁盘空间", EnvCheckLevel.Ok, $"{free / 1024.0 / 1024 / 1024:0.#} GB 可用")
                : new EnvCheckItem("磁盘空间", EnvCheckLevel.Warn, $"{free / 1024.0 / 1024 / 1024:0.#} GB 可用",
                    "建议 ≥ 5 GB：流缓存默认上限 10GB（设置页可调小），空间不足会让块设备/缓存写入失败"));
        }
        catch { }

        // ⑤ 物理内存
        try
        {
            var total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            var gb = total / 1024.0 / 1024 / 1024;
            items.Add(total >= (long)MemoryWarnBytes
                ? new EnvCheckItem("内存", EnvCheckLevel.Ok, $"{gb:0.#} GB")
                : new EnvCheckItem("内存", EnvCheckLevel.Warn, $"{gb:0.#} GB",
                    "建议 ≥ 4 GB：guest 默认占 2~3GB，余量不足时宿主频繁换页、播放卡顿"));
        }
        catch { }

        // ⑥ 系统与运行时（信息项：报障时先看这一行）
        //
        // ⚠ 这里**不检测**系统 Java：随包 JRE 已于 2026-09-29 全退役（宿主不再常驻 Java 进程），
        // 而残留的 FindJavaExe() 只服务「纯 .class jar 源的 d8 预转换」这一条边缘路径 ——
        // 它除 java.exe 外还要 Android SDK build-tools 的 d8.jar（同样不随包），
        // 只报 java 缺失会让用户去装 JDK 却依然不可用（误导）。该路径缺件时由
        // JavaSpiderRuntime.EnsureDexJarAsync 明确报错并给出替代方案（换含 classes.dex 的源）。
        try
        {
            items.Add(new EnvCheckItem("系统", EnvCheckLevel.Info,
                $"{WindowsVersionText()} · " +
                $"{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture} · .NET {Environment.Version.Major}"));
        }
        catch { }
#endif
        return items;
    }

    /// <summary>
    /// Windows 版本描述，形如「Windows 11 Pro · 26200.9457」。
    ///
    /// <para><b>为什么不能直接读注册表 ProductName</b>：微软从 Windows 11 21H2 起就没再更新过那个键 ——
    /// 实测本机（Build 26200 / 25H2，实打实的 Windows 11）注册表里仍写着
    /// <c>ProductName = "Windows 10 Pro"</c>，照样认错。</para>
    ///
    /// <para><b>也不能用 Environment.OSVersion</b>：它在 Win11 上谎报 <c>10.0.x</c>
    /// （兼容性谎报），本机实测 <c>10.0.26200.0</c>。</para>
    ///
    /// <para><b>可靠判据只有 Build 号</b>：≥22000 即 Windows 11（微软官方分界），
    /// ≥20348 是 Server 2022，≥19041 是 Server / Win10 21H1 一线。Build 号本身是真实的。</para>
    /// </summary>
    private static string WindowsVersionText()
    {
        var build = 0; var ubr = 0;
        try { build = Environment.OSVersion.Version.Build; } catch { }

        var product = ""; var display = "";
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is not null)
            {
                product = key.GetValue("ProductName") as string ?? "";
                display = key.GetValue("DisplayVersion") as string ?? "";
                ubr = Convert.ToInt32(key.GetValue("UBR") ?? 0);
            }
        }
        catch { }

        // 版别（Pro / Home / Enterprise…）从 ProductName 里剥掉系统名，只留版别
        var edition = product.Replace("Windows 10", "").Replace("Windows 11", "")
                            .Replace("Windows 11 ", "").Replace("Windows 10 ", "").Trim();
        var isServer = product.Contains("Server", StringComparison.OrdinalIgnoreCase);

        var sysName = isServer
            ? "Windows Server"
            : (build >= 22000 ? "Windows 11" : "Windows 10");

        var name = edition.Length > 0 ? $"{sysName} {edition}" : sysName;
        var buildText = build > 0
            ? (ubr > 0 ? $"{build}.{ubr}" : build.ToString())
            : (product.Length > 0 ? product : "未知版本");
        return $"{name} · {buildText}" + (display.Length > 0 ? $"（{display}）" : "");
    }
}
