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
        var dbPath = Path.Combine(FileSystem.AppDataDirectory, "catclawvideo.db");
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
        var btService = new CatClawVideo.Core.Services.BtStreamService(btCacheRoot, m => System.Diagnostics.Debug.WriteLine(m), trackerSource, btSettings)
        {
            MemoryCacheBytes = 32 * 1024 * 1024,
            MaxConnections = 120,          // 移动端连接数略降（省电/省流），仍远高于旧值 50
            MaxHalfOpenConnections = 40,
        };
#else
        var btCacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CatClawVideo", "btcache");
        var btService = new CatClawVideo.Core.Services.BtStreamService(btCacheRoot, BtFileLog.Write, trackerSource, btSettings);
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
#else
        // 桌面 JVM 桥：JavaBridge 目录 + 系统 java.exe（缺一则不可用）
        var bridgeDir = CatClawVideo.Core.Providers.JavaSpiderRuntime.FindBridgeDir();
        var javaExe = CatClawVideo.Core.Providers.JavaSpiderRuntime.FindJavaExe();
        CatClawVideo.Core.Interfaces.ISpiderRuntime jarRuntime = bridgeDir != null && javaExe != null
            ? new CatClawVideo.Core.Providers.JavaSpiderRuntime(bridgeDir, javaExe, m => System.Diagnostics.Debug.WriteLine(m))
            : new CatClawVideo.Core.Providers.NullSpiderRuntime("jvm-dex");
#endif
        CatClawVideo.Core.Models.SiteRegistry.JsSpiderAvailable = jsRuntime.IsSupported;
        CatClawVideo.Core.Models.SiteRegistry.JarSpiderAvailable = jarRuntime.IsSupported;

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

        var vodProvider = new CatClawVideo.Core.Providers.CompositeVodSourceProvider(
            new IVodSourceProvider[]
            {
                new CatClawVideo.Core.Providers.CatClawSourceProvider(btService),
                new CatClawVideo.Core.Providers.MacCmsJsonProvider(btService),
                new CatClawVideo.Core.Providers.SpiderVodProvider(jsRuntime, jarRuntime, sniffer, btService),
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
                        SiteRegistry.Replace(await subscriptionManager.LoadSubscriptionAsync(sub.SourceUrl));
                        System.Diagnostics.Debug.WriteLine($"[启动] 订阅恢复成功: {sub.Name} ({sub.SourceUrl})");
                        return;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[启动] 订阅恢复失败 {sub.SourceUrl}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[启动] 订阅恢复异常: {ex.Message}");
            }
        });

        return app;
    }
}

