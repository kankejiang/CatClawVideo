using CatClawVideo.Maui.Services;
using CatClawVideo.Maui.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CatClawVideo.Maui;

public static class MauiProgram
{
    /// <summary>全局服务定位器（平台层/页面无构造注入时的入口）</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            })
            .ConfigureMauiHandlers(handlers =>
            {
                // 跨平台视频播放器（Android: Media3 ExoPlayer / Windows: WinUI MediaPlayer）
#if ANDROID
                handlers.AddHandler(typeof(Controls.VideoPlayerView),
                    typeof(Platforms.Android.VideoPlayerViewHandler));
#endif
#if WINDOWS
                handlers.AddHandler(typeof(Controls.VideoPlayerView),
                    typeof(Platforms.Windows.VideoPlayerViewHandler));
#endif
            });

        var services = builder.Services;

        // ═══════════════════════════════════════════════════
        // Database（单连接单例，初始化放后台不阻塞首帧）
        // ═══════════════════════════════════════════════════
        // Debug/Release 数据隔离（见 Core.AppPaths）：首次从旧的共享位置（MAUI AppDataDirectory）拷一份 → 订阅/历史不丢
        CatClawVideo.Core.AppPaths.SeedFile("catclawvideo.db", FileSystem.AppDataDirectory);
        var dbPath = CatClawVideo.Core.AppPaths.Of("catclawvideo.db");
        var db = new VideoDatabase(dbPath);
        _ = Task.Run(async () =>
        {
            try { await db.EnsureInitializedAsync(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"数据库初始化失败: {ex.Message}"); }
        });
        services.AddSingleton(db);

        // ═══════════════════════════════════════════════════
        // Platform / Infrastructure services
        // ═══════════════════════════════════════════════════
        services.AddSingleton<IThemeService, Services.ThemeService>();
        services.AddSingleton<VideoPlaybackManager>();

        // ═══════════════════════════════════════════════════
        // 影视源提供者：MacCMS JSON 直连 + TVBox spider 爬虫运行时 + 聚合路由
        // ═══════════════════════════════════════════════════
        services.AddSingleton<ISubscriptionManager, CatClawVideo.Core.Providers.TvBoxSubscriptionManager>();

        // spider 运行时：JS（drpy2，Jint 纯托管，双端可用）+ jar/dex（Android DexClassLoader，仅 Android）
        services.AddSingleton<CatClawVideo.Core.Interfaces.IJsRuntimeService, CatClawVideo.Core.Services.JsRuntimeService>();
        var jsRuntime = new CatClawVideo.Core.Providers.DrpyJsSpiderRuntime(
            new CatClawVideo.Core.Services.JsRuntimeService(),
            cacheDir: Path.Combine(FileSystem.CacheDirectory, "drpy2"),
            log: m => System.Diagnostics.Debug.WriteLine(m));

        // BT 流式引擎（磁力边下边播）：双端同一实现，仅缓存路径/内存预算按平台调参
        // 设置与 tracker 列表：BtSettings（设置页可改，保存后 RecreateEngineAsync 生效）
        //                + BtTrackerSource（ngosang 拉取 + BEP15 可达性过滤 + 每日更新，替代硬编码）
        var btSettings = CatClawVideo.Core.Services.BtSettings.Load(FileSystem.AppDataDirectory);
        var trackerSource = new CatClawVideo.Core.Services.BtTrackerSource(FileSystem.AppDataDirectory, BtFileLog.Write);
#if ANDROID
        var btCacheRoot = Path.Combine(FileSystem.CacheDirectory, "btcache");
        // ⚠️ 日志必须走 BtFileLog（落盘 files/logs/bt.log），不要用 System.Diagnostics.Debug.WriteLine：
        // · Debug.WriteLine 带 [Conditional("DEBUG")]，Release 包里整行被编译掉 → 真机零日志；
        // · Debug 包里它走 stdout，被 logd 按进程名打 tag 且受块缓冲影响，adb logcat 抓不稳。
        // 这两点叠加导致「安卓端磁力线路加载失败」在真机上完全拿不到引擎侧证据（2026-09-14 实测）。
        var btService = new CatClawVideo.Core.Services.BtStreamService(btCacheRoot, BtFileLog.Write, trackerSource, btSettings)
        {
            MemoryCacheBytes = 32 * 1024 * 1024,
            MaxConnections = 120,          // 移动端连接数略降（省电/省流），仍远高于旧值 50
            MaxHalfOpenConnections = 40,
        };
#else
        var btCacheRoot = CatClawVideo.Core.AppPaths.Sub("btcache");
        var btService = new CatClawVideo.Core.Services.BtStreamService(btCacheRoot, BtFileLog.Write, trackerSource, btSettings);

        // PC「迅雷磁力播放」双引擎（链式：前者失败才试后者，全部失败回落内置 BT）：
        //  ① QEMU 本地迅雷引擎（首选）：ARM64 Android 迅雷 SDK 跑在 QEMU 里，走 P2SP 私有网络，
        //     公共磁力也能满速边下边播；无需登录。运行时随包分发在 ThunderRuntime/
        //     （缺失/启动失败判未就绪、自动跳过）。链路与移植说明：JavaBridge/qemu-src/README.md。
        //  ② 迅雷网盘 API（兜底）：云添加 → 迅雷服务器下载 → 取直链；需登录，未登录判未就绪。
        // 为什么不是直接用迅雷下载 SDK：那套安卓 SDK 在 PC 上跑不起来（引导域名被沉 127.0.0.2），
        // 所以 ① 用 QEMU 承载原生跑；② 走官方网盘 API。
        var qemuThunder = new CatClawVideo.Core.Services.QemuThunder.QemuThunderEngine(
            Path.Combine(AppContext.BaseDirectory, "ThunderRuntime"), BtFileLog.Write);
        CatClawVideo.Core.Interfaces.MagnetEngines.Thunder = new CatClawVideo.Core.Providers.ChainedMagnetEngine(
            qemuThunder, new CatClawVideo.Core.Providers.ThunderPanEngine());
#endif
        services.AddSingleton(btSettings);
        services.AddSingleton(trackerSource);
        services.AddSingleton(btService);
        // 后台预热 tracker 列表（不阻塞启动首帧）
        _ = Task.Run(async () => { try { await btService.WarmUpTrackersAsync(); } catch { } });
#if ANDROID
        var jarRuntime = new Platforms.Android.DexSpiderRuntime(
            Path.Combine(FileSystem.CacheDirectory, "spider"),
            m => System.Diagnostics.Debug.WriteLine(m));

        // 荐片（csp_Jianpian）宿主侧 P2P：
        //  ① libp2p.so + com.p2p.P2PClass 起本地 httpd（实测端口 8087+）；
        //  ② 在 spider 扫描的 9978…9999 整段起反代指向该 httpd；
        //  ③ /proxy?do=… 回调爬虫自己的 proxy(Map) 生成响应（对齐 TVBox ApiConfig.proxyLocal）。
        // 必须在我们解析播放地址前就绪，否则 spider 的 adjustPort 握手失败、拼出空端口地址。
        // 启动会阻塞到 httpd 就绪，故放后台线程预热；兜底 await 在 SpiderVodProvider 里。
        var jpP2p = new Platforms.Android.JianpianP2P(
            Path.Combine(FileSystem.CacheDirectory, "p2p"), BtFileLog.Write)
        {
            ProxyHandler = jarRuntime.ProxyAsync,
        };
        CatClawVideo.Core.Interfaces.JpP2PSupport.Current = jpP2p;
        _ = Task.Run(async () => { try { await jpP2p.EnsureReadyAsync(); } catch { } });

        // 迅雷下载引擎（磁力优先）：libxl_thunder_sdk.so + libxl_stat.so + thunder.jar。
        // 起不来（ABI 不符 / appKey 失效 / 服务端变更）不影响任何现有能力 —— 所有调用点都会回落内置 BT。
        var thunder = new Platforms.Android.ThunderP2P(
            Path.Combine(FileSystem.CacheDirectory, "thunder"), BtFileLog.Write);
        CatClawVideo.Core.Interfaces.MagnetEngines.Thunder = thunder;
        _ = Task.Run(async () => { try { await thunder.EnsureReadyAsync(); } catch { } });

        // 手机端「解析节点」：把 spider 能力经局域网借给 PC。
        // PC 上的 Guard jar 跑不了（解密器是 ARM Android native，且加固把解密产物写完即删），
        // 而手机原生就能跑 Guard —— 让 PC 借手机的解析能力。
        // 手机只做取页 + 解析 JSON/HTML 的轻活（不搬媒体流），因此不会发烫。
        var nodeServer = new Platforms.Android.SpiderApiServer(
            8899,
            handler: async (siteKey, method, args) =>
            {                var site = CatClawVideo.Core.Models.SiteRegistry.Find(siteKey)
                    ?? throw new InvalidOperationException($"本机未注册站点 {siteKey}");
                CatClawVideo.Core.Interfaces.ISpiderRuntime rt =
                    site.SpiderKind == CatClawVideo.Core.Models.VodSpiderKind.Jar ? jarRuntime : jsRuntime;

                string Arg(string k) => args.TryGetValue(k, out var v) ? v : "";
                var pg = Arg("pg");
                if (string.IsNullOrEmpty(pg)) pg = "1";

                return method switch
                {
                    "home" => await rt.HomeContentAsync(site),
                    "category" => await rt.CategoryContentAsync(site, Arg("tid"), pg),
                    "detail" => await rt.DetailContentAsync(site, Arg("id")),
                    "search" => await rt.SearchContentAsync(site, Arg("wd"), pg),
                    "player" => await rt.PlayerContentAsync(site, Arg("flag"), Arg("id")),
                    _ => throw new InvalidOperationException($"未知 method: {method}"),
                };
            },
            token: NodeToken(),
            log: BtFileLog.Write);
        nodeServer.Start();
        CatClawVideo.Core.Providers.RemoteSpiderNode.IsNodeHost = true;
        BtFileLog.Write($"[节点] 口令 /node-token = {NodeToken()}（PC 端手填或扫码自动带上）");
#else
        // 桌面 JVM 桥：JavaBridge 目录 + 系统 java.exe（缺一则不可用）
        var bridgeDir = CatClawVideo.Core.Providers.JavaSpiderRuntime.FindBridgeDir();
        var javaExe = CatClawVideo.Core.Providers.JavaSpiderRuntime.FindJavaExe();
        CatClawVideo.Core.Interfaces.ISpiderRuntime jarRuntime = bridgeDir != null && javaExe != null
            ? new CatClawVideo.Core.Providers.JavaSpiderRuntime(bridgeDir, javaExe, m =>
            {
                // Windows 桌面没有控制台，Debug.WriteLine 不挂调试器就抓不到 →
                // 同时落 %APPDATA%\CatClawVideo\home-debug.log，排障 Guard 解壳/桥加载要看这段
                System.Diagnostics.Debug.WriteLine(m);
                DiagLog.Write(m);
            })
            : new CatClawVideo.Core.Providers.NullSpiderRuntime("jvm-dex");

        // 「猫爪互联」本机服务：PC 首页在「没有可用源」时展示配对二维码，
        // 手机扫码 → POST /pair → 把手机登记为解析节点。
        // （Guard 加固源本机已可用 unidbg 解壳，但解壳器缺失/失败时仍需手机兜底；
        // 后续的遥控播放与播放记录同步也复用这条通道。）
        var linkServer = new CatClawVideo.Core.Services.LinkServer(
            CatClawVideo.Core.Services.LinkServer.DefaultPort,
            BtFileLog.Write,
            deviceName: () => Environment.MachineName,
            onPaired: (node, _) =>
            {
                BtFileLog.Write($"[互联] 解析节点已切换为 {node}");
                // 解析路径变了，通知首页/搜索页刷新（订阅集合本身没变）
                CatClawVideo.Core.Models.SiteRegistry.NotifyChanged();
            });
        linkServer.Start();
#endif
        CatClawVideo.Core.Models.SiteRegistry.JsSpiderAvailable = jsRuntime.IsSupported;
        CatClawVideo.Core.Models.SiteRegistry.JarSpiderAvailable = jarRuntime.IsSupported;

        // 「源看不到」类问题的第一现场：订阅解析完/站点集合一变就记一行
        // （可播 = type1 MacCMS + 运行时就绪的 spider 源；jar 桥可用性单独打印）
        CatClawVideo.Core.Models.SiteRegistry.Changed += () =>
            DiagLog.Write($"[源] 站点合计={CatClawVideo.Core.Models.SiteRegistry.Sites.Count} " +
                          $"可播={CatClawVideo.Core.Models.SiteRegistry.Playable.Count()} " +
                          $"jar桥={CatClawVideo.Core.Models.SiteRegistry.JarSpiderAvailable} " +
                          $"js={CatClawVideo.Core.Models.SiteRegistry.JsSpiderAvailable} " +
                          $"桥目录={CatClawVideo.Core.Providers.JavaSpiderRuntime.FindBridgeDir() ?? "(未找到)"}");

        services.AddSingleton(btService);

        // 下载管理器（复刻猫爪音乐）：HTTP 直链 + BT 磁力整包下载
        // BT 引擎借用 BtStreamService 的 ClientEngine（共享 DHT 路由表，下载启动即受益于播放会话焐热的节点表）；
        // 同磁力"流式会话 vs 下载任务"的注册冲突由 CloseStreamingSessionAsync 接管解决
        services.AddSingleton<BitTorrentDownloadService>(sp =>
            new BitTorrentDownloadService(sp.GetRequiredService<Core.Services.BtStreamService>(), BtFileLog.Write));
        services.AddSingleton(sp => new DownloadManager(btFactory: () => sp.GetService<BitTorrentDownloadService>()));

        // 平台嗅探器：Android WebView 拦截 / Windows WebView2 拦截（TVBox parse=1 页面解析）
#if ANDROID
        CatClawVideo.Core.Interfaces.IWebSniffer sniffer = new Platforms.Android.AndroidWebSniffer();
#elif WINDOWS
        CatClawVideo.Core.Interfaces.IWebSniffer sniffer = new Platforms.Windows.WindowsWebSniffer();
#else
        CatClawVideo.Core.Interfaces.IWebSniffer sniffer = new CatClawVideo.Core.Providers.NullWebSniffer();
#endif

        // 手机解析节点地址/口令（设置页与扫码配对都写这里）。
        // 用 Loader 延迟读取 —— MauiProgram 早期平台未初始化，直接调 Preferences 会抛。
        CatClawVideo.Core.Providers.RemoteSpiderNode.Loader =
            () => Preferences.Default.Get("remote_spider_node", "");
        CatClawVideo.Core.Providers.RemoteSpiderNode.TokenLoader =
            () => Preferences.Default.Get("remote_spider_token", "");
        CatClawVideo.Core.Providers.RemoteSpiderNode.Saver = (url, token) =>
        {
            Preferences.Default.Set("remote_spider_node", url ?? "");
            Preferences.Default.Set("remote_spider_token", token ?? "");
        };
        var vodProvider = new CatClawVideo.Core.Providers.CompositeVodSourceProvider(            new IVodSourceProvider[]
            {
                new CatClawVideo.Core.Providers.CatClawSourceProvider(btService),
                new CatClawVideo.Core.Providers.MacCmsJsonProvider(btService),
                new CatClawVideo.Core.Providers.SpiderVodProvider(jsRuntime, jarRuntime, sniffer, btService, BtFileLog.Write),
            });
        services.AddSingleton<IVodSourceProvider>(vodProvider);

        // ═══════════════════════════════════════════════════
        // 封面获取与兜底
        //   源封面取不到（防盗链 / CDN 失效 / JS 盾 / 站点资源损坏，如毒舌电影）时按序兜底：
        //     ① 跨源检索：用**用户自己订阅的可搜索源**按片名找同名片封面
        //        （实测一次站内搜索即拿到详情链接+封面，无第三方限流）
        //     ② 豆瓣海报（会限流，作为补充）
        //     ③ 返回 null → 界面显示本地渲染的占位海报（绝不空白）
        //   内置磁盘缓存 + 并发上限 + 超时 + 失败负缓存 + 主机熔断，避免慢站把列表拖死。
        // ═══════════════════════════════════════════════════
        services.AddSingleton(new CatClawVideo.Core.Services.CoverImageService(
            FileSystem.CacheDirectory,
            DiagLog.Write,
            crossSourceCover: async (title, ct) =>
            {
                // 只打「声明了站内搜索接口」的站：没有搜索接口的站会退化成扫分类页（一次十几个请求），
                // 不适合做封面兜底。命中要求标题归一后一致，避免借到同名的别的片子。
                var target = CatClawVideo.Core.Services.CoverImageService.NormalizeTitle(title);
                if (target.Length < 2) return null;

                foreach (var site in SiteRegistry.Playable)
                {
                    if (!site.DeclaredSearch) continue;
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var items = await vodProvider.SearchAsync(site, title, ct);
                        var hit = items.FirstOrDefault(i =>
                            CatClawVideo.Core.Services.CoverImageService.NormalizeTitle(i.Title) == target &&
                            !string.IsNullOrWhiteSpace(i.Cover));
                        if (hit?.Cover is { Length: > 0 } cover)
                        {
                            DiagLog.Write($"[cover] 跨源命中「{title}」← {site.Name}");
                            return cover;
                        }
                    }
                    catch
                    {
                        // 单站失败继续下一站
                    }
                }
                return null;
            }));

        // ═══════════════════════════════════════════════════
        // ViewModels
        // ═══════════════════════════════════════════════════
        services.AddSingleton<CatClawVideo.Core.Services.IUpdateService, UpdateService>();
        services.AddSingleton<MainViewModel>();
        services.AddTransient<MainViewModel>();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<FavoritesViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<VideoPlayerViewModel>();
        services.AddTransient<DownloadsViewModel>();
        services.AddTransient<AboutViewModel>();

        // ═══════════════════════════════════════════════════
        // Pages / Shell
        // ═══════════════════════════════════════════════════
        services.AddSingleton<AppShell>();
        services.AddSingleton<Pages.MainPage>();
        services.AddTransient<Pages.HomePage>();
        services.AddTransient<Pages.FavoritesPage>();
        // 下载管理：现在是顶部 tab（"下载"）的内容，由 MainPage 注入常驻复用
        services.AddTransient<Pages.DownloadsPage>();
        services.AddTransient<Pages.SettingsPage>();
        services.AddTransient<Pages.BtSettingsPage>();
        services.AddTransient<Pages.DownloadDetailPage>();
        services.AddTransient<Pages.VideoPlayerPage>();
        services.AddTransient<Pages.WatchPage>();
        services.AddTransient<Pages.SearchPage>();
        services.AddTransient<Pages.SourceConfigPage>();
        services.AddTransient<Pages.HistoryPage>();
        services.AddTransient<Pages.LocalMediaPage>();
        services.AddTransient<Pages.AboutPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        var app = builder.Build();

        // Core 层日志 → DiagLog（Android 上同时进 logcat，tag=CatClawDiag）
        CatClawVideo.Core.Providers.CatClawLog.Sink = DiagLog.Write;
        Services = app.Services;

        // ═══════════════════════════════════════════════════
        // 启动后台恢复订阅源：解析已保存订阅 → 填充 SiteRegistry
        // （失败静默不阻塞首帧；多订阅取第一个成功者）
        // ═══════════════════════════════════════════════════
        _ = Task.Run(async () =>
        {
            try
            {
                var database = app.Services.GetRequiredService<VideoDatabase>();
                var subscriptionManager = app.Services.GetRequiredService<ISubscriptionManager>();
                foreach (var sub in await database.GetSubscriptionsAsync())
                {
                    try
                    {
                        var sites = await subscriptionManager.LoadSubscriptionAsync(sub.SourceUrl);
                        SiteRegistry.Replace(sites);
                        DiagLog.Write($"[启动] 订阅恢复成功: {sub.Name} ({sub.SourceUrl}) → {sites.Count} 站点");
                        System.Diagnostics.Debug.WriteLine($"[启动] 订阅恢复成功: {sub.Name} ({sub.SourceUrl})");
                        return;
                    }
                    catch (Exception ex)
                    {
                        // 落文件：Windows 无控制台，Debug.WriteLine 抓不到，否则「源没反应」查不出原因
                        DiagLog.Write($"[启动] 订阅恢复失败 {sub.Name} ({sub.SourceUrl}): {ex.Message}");
                        System.Diagnostics.Debug.WriteLine($"[启动] 订阅恢复失败 {sub.SourceUrl}: {ex.Message}");
                    }
                }
                DiagLog.Write("[启动] 所有订阅均恢复失败，站点列表为空");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[启动] 订阅恢复异常: {ex.Message}");
            }
        });

        return app;
    }

#if ANDROID
    /// <summary>
    /// 本机作为「解析节点」对外的共享口令：首次随机生成并持久化。
    /// PC 端可手填，也会随二维码下发；手机端日志同样打印，便于手动配对。
    /// </summary>
    private static string NodeToken()
    {
        try
        {
            var t = Preferences.Default.Get("node_token", "");
            if (!string.IsNullOrEmpty(t)) return t;
            t = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            Preferences.Default.Set("node_token", t);
            return t;
        }
        catch
        {
            return "";
        }
    }
#endif
}

