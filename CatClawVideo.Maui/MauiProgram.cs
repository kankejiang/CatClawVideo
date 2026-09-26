using System.Text;
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
        // 片名首字母索引：注入数据库，之后每次列表上屏（CoverResolver.Attach）都会被动积累。
        // 用完全限定名：本方法参数名 services 会遮蔽 Services 命名空间。
        CatClawVideo.Maui.Services.SearchIndex.Initialize(db);
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
        // GBK 等代码页：部分采集站/网盘源响应为 GBK，须在首次 GetEncoding 前注册（最前！）
        Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        services.AddSingleton<ISubscriptionManager, CatClawVideo.Core.Providers.TvBoxSubscriptionManager>();

        // ═══════════════════════════════════════════════════
        // 直播（TVBox LivePlayActivity 移植）：源加载 + EPG 均为进程内单例
        //   —— 直播间与直播源配置页共用同一份偏好与已解析频道，换页不重复拉源
        // ═══════════════════════════════════════════════════
        services.AddSingleton<CatClawVideo.Core.Live.LiveSourceService>();
        services.AddSingleton<CatClawVideo.Core.Live.EpgService>();

        // ═══════════════════════════════════════════════════
        // 网络媒体（猫爪音乐 WebDAV 能力移植）：连接仓库 + WebDAV 客户端 + 本地流代理
        //   —— 代理把带 Basic Auth / OpenList 重定向的远程文件变成 127.0.0.1 普通 HTTP 流，
        //      双平台播放器统一按本地地址消费（Windows 播放器不支持自定义请求头）
        // ═══════════════════════════════════════════════════
        services.AddSingleton<CatClawVideo.Core.Network.WebDavProfileStore>();
        services.AddSingleton<CatClawVideo.Core.Network.WebDavService>();
        services.AddSingleton<CatClawVideo.Core.Network.WebDavStreamProxy>();

        // spider 运行时：JS（drpy2，Jint 纯托管，双端可用）+ jar/dex（Android DexClassLoader，仅 Android）
        services.AddSingleton<CatClawVideo.Core.Interfaces.IJsRuntimeService, CatClawVideo.Core.Services.JsRuntimeService>();
        var jsRuntime = new CatClawVideo.Core.Providers.DrpyJsSpiderRuntime(
            new CatClawVideo.Core.Services.JsRuntimeService(),
            cacheDir: CatClawVideo.Core.AppPaths.LocalSub("drpy2"),
            log: m => System.Diagnostics.Debug.WriteLine(m));
        // 磁力下载引擎（Windows 下方赋值；Android 恒 null → 磁力下载任务提示不支持）
        CatClawVideo.Core.Services.QemuThunder.QemuThunderEngine? magnetDownloadEngine = null;

#if !ANDROID

        // PC「迅雷磁力播放」双引擎（链式：前者失败才试后者，全部失败回落内置 BT）：
        //  ① QEMU 本地迅雷引擎（首选）：ARM64 Android 迅雷 SDK 跑在 QEMU 里，走 P2SP 私有网络，
        //     公共磁力也能满速边下边播；无需登录。运行时随包分发在 ThunderRuntime/
        //     （缺失/启动失败判未就绪、自动跳过）。链路与移植说明：JavaBridge/qemu-src/README.md。
        //  ② 迅雷网盘 API（兜底）：云添加 → 迅雷服务器下载 → 取直链；需登录，未登录判未就绪。
        // 为什么不是直接用迅雷下载 SDK：那套安卓 SDK 在 PC 上跑不起来（引导域名被沉 127.0.0.2），
        // 所以 ① 用 QEMU 承载原生跑；② 走官方网盘 API。
        var qemuThunder = new CatClawVideo.Core.Services.QemuThunder.QemuThunderEngine(
            Path.Combine(AppContext.BaseDirectory, "ThunderRuntime"), BtFileLog.Write);
        // 磁力点播磁盘缓存：播放数据 4MB 分块落盘 + LRU 超限清理，已看区间重进/换集直接磁盘秒供。
        // 上限由设置页控制（默认 20GB，档位 5/10/20/30/50），持久化在 Preferences；
        // 启动时灌进 Core 的 StreamCachePrefs，引擎在超限清理时实时读取（改完即时生效，无需重启）。
        var cacheGbPref = Preferences.Default.Get("stream_cache_gb", (int)CatClawVideo.Core.Services.QemuThunder.StreamCachePrefs.DefaultGb);
        CatClawVideo.Core.Services.QemuThunder.StreamCachePrefs.CapGb = cacheGbPref;
        qemuThunder.StreamCacheRoot = CatClawVideo.Core.AppPaths.Sub("btcache");
        // 数据面块设备：guest 把引擎吐出的字节按偏移写进宿主镜像，供数时直读同一文件
        //（实测 2454~2926 MB/s，绕开 SLIRP 的 40MB/s 与 harness 转发的 18.9MB/s）。
        // 稀疏镜像，写多少占多少；环境异常时引擎会自动退化为纯 HTTP 通道。
        qemuThunder.BlockDeviceRoot = CatClawVideo.Core.AppPaths.Sub("btcache/hub");
        BtFileLog.Write($"[缓存] 上限 {CatClawVideo.Core.Services.QemuThunder.StreamCachePrefs.CapGb}GB（设置页可调）");
        CatClawVideo.Core.Interfaces.MagnetEngines.Thunder = new CatClawVideo.Core.Providers.ChainedMagnetEngine(
            qemuThunder, new CatClawVideo.Core.Providers.ThunderPanEngine());
        // 磁力下载也走同一个迅雷引擎（下载管理页的磁力任务：引擎独占下载 → 媒体口导出本机）
        magnetDownloadEngine = qemuThunder;

        // ★ VM 预热：QEMU 冷启动实测 11s（39.4s 起进程 → 50.5s 就绪），而它发生在**用户点开
        //   磁力片的那一刻**，直接叠进「海报 → 选集」的等待里。放到启动后的后台线程先跑起来，
        //   用户浏览首页/找片的时间足够 VM 就绪，点磁力时省掉这 11s。
        //   延迟 3s 起，避让启动首屏的 UI 与订阅解析；失败无害（首次播放时仍会懒启动重试）。
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                await qemuThunder.EnsureReadyAsync().ConfigureAwait(false);
                BtFileLog.Write("[qemu] 预热完成（VM 已就绪，磁力点播无需冷启动等待）");
            }
            catch (Exception ex) { BtFileLog.Write($"[qemu] 预热失败（不影响后续懒启动）：{ex.Message}"); }
        });

        // Guard 解密 VM（2026-09-24 用户拍板架构）：Guard 网盘源的解密/签名/proxyInvoke（ARM
        // ftyguard so）跑在独立 QEMU 实例里，桥进程经 hostfwd 直连；so 弹的对话框/二维码经
        // 控制口上行由 SpiderUiHost 渲染（jar 框架全权负责登录 UX，宿主只做 UI 接入）。
        // 不预热（首个 Guard 站点加载时懒启动），运行时缺失则 Guard 解密通道不可用（ARM 调用明确报错）。
        var qemuGuard = new CatClawVideo.Core.Services.QemuThunder.QemuGuardEngine(
            Path.Combine(AppContext.BaseDirectory, "ThunderRuntime"), BtFileLog.Write);
        CatClawVideo.Core.Services.QemuThunder.GuardRuntime.Attach(qemuGuard);
#endif

        // TVBox 系爬虫（ProxyOrigin 等）会把播放地址拼成 http://127.0.0.1:<port>/proxy?...
        // 端口来自爬虫自己的 drivePort()：在 6677–6999 逐端口探测 GET /proxy?do=ck。
        // 宿主不提供该服务 → 端口缓存非法 → 地址端口为空 → .NET 抛
        // 「Invalid URI: Invalid port specified.」→ 播放页「播放失败：加载失败」。
        // 必须早于任何播放地址解析，故放启动最前；监听失败不影响其他能力（Start 返回 false 只记日志）。
        var spiderProxy = new CatClawVideo.Core.Services.SpiderProxyServer { Log = BtFileLog.Write };
        // /cache?do=get|set|del —— jar/JS 爬虫的跨启动 KV（对位 TVBox CacheRequestProcess + Hawk）。
        // 不接的话爬虫的 set 静默丢失，表现为「登录/配置存了，重启又没了」。
        spiderProxy.CacheStore = CatClawVideo.Core.Services.SpiderLocalStore
            .For(CatClawVideo.Core.AppPaths.LocalSub("spider-kv"));
        // m3u8 去广告：默认关（与 TVBox HawkConfig.M3U8_PURIFY 一致），设置页可开
        CatClawVideo.Core.Services.M3u8Purifier.Enabled = Preferences.Default.Get("m3u8_purify", false);
        CatClawVideo.Maui.Services.HistoryCap.Load();   // 播放历史条数上限（Data 层不读 Preferences，靠这里回填）
        // DoH：Core 不碰 Preferences，所以这里回填选择并在变化时存回去
        CatClawVideo.Core.Services.Doh.Selector = CatClawVideo.Maui.Services.DohPrefs.Load();
        CatClawVideo.Core.Services.Doh.Changed = () => CatClawVideo.Maui.Services.DohPrefs.Save(
            CatClawVideo.Core.Services.Doh.Selector);
        // 局域网遥控（对位 TVBox RemoteServer）：token 持久化 —— 换一次就要在手机上重抄一次太难用。
        var rcToken = Preferences.Default.Get("rc_token", "");
        if (rcToken.Length == 0)
        {
            rcToken = Guid.NewGuid().ToString("N")[..16];
            Preferences.Default.Set("rc_token", rcToken);
        }
        CatClawVideo.Core.Services.RemoteControlHub.Token = rcToken;
        try { CatClawVideo.Core.Services.RemoteControlHub.DeviceName = "猫爪影视 · " + DeviceInfo.Current.Name; }
        catch { }
        CatClawVideo.Core.Services.GoLiveProxy.Log = m => { System.Diagnostics.Debug.WriteLine(m); DiagLog.Write(m); };
        // 订阅 rules 里的正则是任意用户串，编译失败的要能被看见（作废该组，不影响其余判定）
        CatClawVideo.Core.Providers.TvBoxParseEngine.Log = m => { System.Diagnostics.Debug.WriteLine(m); DiagLog.Write(m); };
        CatClawVideo.Core.Providers.TvBoxConfigStore.Log = m => { System.Diagnostics.Debug.WriteLine(m); DiagLog.Write(m); };
        spiderProxy.Start();
        services.AddSingleton(spiderProxy);

#if ANDROID
        // proxy 端口上报给 Java 侧 SpiderApi.hostProxyPort（Guard 系网盘源用
        // spiderApi.getAddress/getPort 拼「云盘配置」页 URL，返回空则配置入口失效）
        Platforms.Android.TvBoxCompatBridge.SetProxyPort(spiderProxy.Port);
#endif

        // TVBox 社区 JS Spider 运行时（Jint）：csp_ + .js spider 包的 type=3 站点（含网盘聚合源）。
        // proxyPort 用懒访问器——此处 spiderProxy 已构造，端口在首请求时才真正读取。
        var tvboxJsRuntime = new CatClawVideo.Core.Providers.TvBoxJsSpiderRuntime(
            new CatClawVideo.Core.Services.JsRuntimeService(),
            cacheDir: CatClawVideo.Core.AppPaths.LocalSub("tvbox-js"),
            proxyPort: () => spiderProxy.Port,
            log: m => { System.Diagnostics.Debug.WriteLine(m); DiagLog.Write(m); });
#if ANDROID
        var jarRuntime = new Platforms.Android.DexSpiderRuntime(
            Path.Combine(FileSystem.CacheDirectory, "spider"),
            m => System.Diagnostics.Debug.WriteLine(m),
            // Guard 系网盘源弹「云盘配置」对话框需要前台 Activity（Alert 需要 Activity token）
            currentActivity: () => Microsoft.Maui.ApplicationModel.Platform.CurrentActivity);

        // 荐片（csp_Jianpian）宿主侧 P2P：
        //  ① libp2p.so + com.p2p.P2PClass 起本地 httpd（实测端口 8087+）；
        //  ② 在 spider 扫描的 9978…9999 整段起反代指向该 httpd；
        //  ③ /proxy?do=… 回调爬虫自己的 proxy(Map) 生成响应（对齐 TVBox ApiConfig.proxyLocal）。
        // 必须在我们解析播放地址前就绪，否则 spider 的 adjustPort 握手失败、拼出空端口地址。
        // 启动会阻塞到 httpd 就绪，故放后台线程预热；兜底 await 在 SpiderVodProvider 里。
        var jpP2p = new Platforms.Android.JianpianP2P(
            Path.Combine(FileSystem.CacheDirectory, "p2p"), BtFileLog.Write)
        {
            ProxyHandler = (q, ct) => jarRuntime.ProxyAsync(new Dictionary<string, string>(q), ct),
        };
        CatClawVideo.Core.Interfaces.JpP2PSupport.Current = jpP2p;
        _ = Task.Run(async () => { try { await jpP2p.EnsureReadyAsync(); } catch { } });

        // 迅雷下载引擎（磁力优先）：libxl_thunder_sdk.so + libxl_stat.so + thunder.jar。
        // 起不来（ABI 不符 / appKey 失效 / 服务端变更）不影响任何现有能力 —— 所有调用点都会回落内置 BT。
        var thunder = new Platforms.Android.ThunderP2P(
            Path.Combine(FileSystem.CacheDirectory, "thunder"), BtFileLog.Write);
        CatClawVideo.Core.Interfaces.MagnetEngines.Thunder = thunder;
        _ = Task.Run(async () => { try { await thunder.EnsureReadyAsync(); } catch { } });

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
            },
            // 桥进程启动后下发 proxy 端口：Guard 系网盘源（MDrive 等）靠 SpiderApi.getPort()
            // 拼「云盘配置」数据端点（对齐 Android 的 TvBoxCompatBridge.SetProxyPort）
            proxyPort: () => spiderProxy.Port)
            : new CatClawVideo.Core.Providers.NullSpiderRuntime("jvm-dex");

        // 桥爬虫 UI 事件 → MAUI 对话框/二维码（对齐 TVBox：点「登入自己网盘」弹
        // 「已登录+启用中」列表 + 扫码登录，而非回落网页）
        if (jarRuntime is CatClawVideo.Core.Providers.JavaSpiderRuntime desktopJar)
            CatClawVideo.Maui.Services.SpiderUiHost.Attach(desktopJar);

#endif
        // spider 型直播源（TVBox LivePlayActivity 的 liveContent 链）：Core.Live 不依赖爬虫层，
        // 由这里在运行时都造好之后把取列表的口子注进去。
        CatClawVideo.Core.Live.LiveSpiderBridge.Fetcher = async (entry, ct) =>
        {
            var target = entry.Url.Length > 0 ? entry.Url : entry.Api;
            if (target.Length == 0) return null;
            var isJs = target.Contains(".js", StringComparison.OrdinalIgnoreCase);
            var site = new CatClawVideo.Core.Models.VodSiteInfo
            {
                Key = "live#" + (entry.Name.Length > 0 ? entry.Name : target),
                Name = entry.Name,
                Api = target,
                Type = 3,
                Jar = entry.Jar.Length > 0 ? entry.Jar : null,
                SubscriptionName = "live",
                SpiderKind = isJs ? CatClawVideo.Core.Models.VodSpiderKind.Script
                                  : CatClawVideo.Core.Models.VodSpiderKind.Jar,
            };
            var rt = isJs ? (object)tvboxJsRuntime : jarRuntime;
            if (rt is not CatClawVideo.Core.Interfaces.ISpiderLiveRuntime live) return null;
            var txt = await live.LiveContentAsync(site, target, ct);
            // 运行时把「无结果」规范成 "{}"，这里当空处理让上层给出「未返回频道列表」
            return string.IsNullOrWhiteSpace(txt) || txt.Length < 3 ? "" : txt;
        };

        CatClawVideo.Core.Models.SiteRegistry.JsSpiderAvailable = jsRuntime.IsSupported || tvboxJsRuntime.IsSupported;
        CatClawVideo.Core.Models.SiteRegistry.JarSpiderAvailable = jarRuntime.IsSupported;

        // js2Proxy 回环代理路由：按 siteKey 查站点 → 按 SpiderKind 分派 JS/Java 爬虫运行时
        spiderProxy.JsProxyHandler = (query, ct) =>
        {
            var key = query.GetValueOrDefault("siteKey");
            var site = key is null ? null : CatClawVideo.Core.Models.SiteRegistry.Find(key);
            if (site is not null)
            {
                CatClawVideo.Core.Interfaces.ISpiderRuntime? rt = site.SpiderKind switch
                {
                    CatClawVideo.Core.Models.VodSpiderKind.Jar => jarRuntime,
                    CatClawVideo.Core.Models.VodSpiderKind.Script => tvboxJsRuntime,
                    _ => null,
                };
                return rt is CatClawVideo.Core.Interfaces.ISpiderProxyRuntime pr
                    ? pr.ProxyAsync(query, ct)
                    : Task.FromResult<(int Status, string Mime, byte[]? Body)?>(null);
            }

            // spider 自发的 proxy 请求（如 Guard 系网盘源 detailContent 内部的 do=config）
            // 不携带 siteKey → 依次尝试各支持回调的运行时（jar → js），谁接住用谁；
            // 都不接则回 null（调用方 502）。
            foreach (var rt in new CatClawVideo.Core.Interfaces.ISpiderRuntime[] { jarRuntime, tvboxJsRuntime })
            {
                if (rt is CatClawVideo.Core.Interfaces.ISpiderProxyRuntime pr)
                    return pr.ProxyAsync(query, ct);
            }
            return Task.FromResult<(int Status, string Mime, byte[]? Body)?>(null);
        };

        // 「源看不到」类问题的第一现场：订阅解析完/站点集合一变就记一行
        // （可播 = type1 MacCMS + 运行时就绪的 spider 源；jar 桥可用性单独打印）
        CatClawVideo.Core.Models.SiteRegistry.Changed += () =>
            DiagLog.Write($"[源] 站点合计={CatClawVideo.Core.Models.SiteRegistry.Sites.Count} " +
                          $"可播={CatClawVideo.Core.Models.SiteRegistry.Playable.Count()} " +
                          $"jar桥={CatClawVideo.Core.Models.SiteRegistry.JarSpiderAvailable} " +
                          $"js={CatClawVideo.Core.Models.SiteRegistry.JsSpiderAvailable} " +
                          $"桥目录={CatClawVideo.Core.Providers.JavaSpiderRuntime.FindBridgeDir() ?? "(未找到)"}");


        // 下载管理器：HTTP 直链 + 磁力下载（Windows 注入迅雷引擎；Android 磁力暂不支持）
        services.AddSingleton(sp => new DownloadManager(magnetDownloadEngine));

        // 平台嗅探器：Android WebView 拦截 / Windows WebView2 拦截（TVBox parse=1 页面解析）
#if ANDROID
        CatClawVideo.Core.Interfaces.IWebSniffer sniffer = new Platforms.Android.AndroidWebSniffer();
#elif WINDOWS
        CatClawVideo.Core.Interfaces.IWebSniffer sniffer = new Platforms.Windows.WindowsWebSniffer();
#else
        CatClawVideo.Core.Interfaces.IWebSniffer sniffer = new CatClawVideo.Core.Providers.NullWebSniffer();
#endif

        var vodProvider = new CatClawVideo.Core.Providers.CompositeVodSourceProvider(            new IVodSourceProvider[]
            {
                new CatClawVideo.Core.Providers.CatClawSourceProvider(),
                new CatClawVideo.Core.Providers.MacCmsJsonProvider(sniffer),
                new CatClawVideo.Core.Providers.SpiderVodProvider(jsRuntime, jarRuntime, sniffer, log: BtFileLog.Write,
                    tvboxJsRuntime: tvboxJsRuntime),
            });
        services.AddSingleton<IVodSourceProvider>(vodProvider);

        // /rc/search 的处理器：跨源搜一遍再回 json（对位 TVBox /action?do=search）。
        // 限制站点数是因为这是给电脑浏览器用的，等 40 个站不如先出前几个站的结果。
        CatClawVideo.Core.Services.RemoteControlHub.SearchHandler = async (text, ct) =>
        {
            var hits = new List<Dictionary<string, string>>();
            foreach (var site in CatClawVideo.Core.Models.SiteRegistry.Playable
                         .Where(x => x.Searchable).Take(8).ToList())
            {
                try
                {
                    var items = await vodProvider.SearchAsync(site, text, ct).ConfigureAwait(false);
                    foreach (var it in items.Take(15))
                        hits.Add(new Dictionary<string, string>
                        {
                            ["site"] = site.Name,
                            ["siteName"] = site.Name,
                            ["sourceKey"] = site.Key,
                            ["name"] = it.Title,
                            ["id"] = it.Id,
                            ["cover"] = it.Cover ?? "",
                        });
                }
                catch { /* 单站失败不该拖垮整个搜索 */ }
            }
            return System.Text.Json.JsonSerializer.Serialize(hits);
        };

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
            CatClawVideo.Core.AppPaths.CacheRoot,
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
        services.AddTransient<Pages.SettingsPage>();
        services.AddTransient<Pages.DownloadDetailPage>();
        services.AddTransient<Pages.VideoPlayerPage>();
        services.AddTransient<Pages.WatchPage>();
        services.AddTransient<Pages.SearchPage>();
        services.AddTransient<Pages.SourceConfigPage>();
        services.AddTransient<Pages.HistoryPage>();
        services.AddTransient<Pages.LocalMediaPage>();
        services.AddTransient<Pages.AboutPage>();
        services.AddTransient<Pages.DiagnosticLogPage>();
        services.AddTransient<Pages.LivePage>();
        services.AddTransient<Pages.LiveSourcePage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        var app = builder.Build();

        // Core 层日志 → DiagLog（Android 上同时进 logcat，tag=CatClawDiag）
        CatClawVideo.Core.Providers.CatClawLog.Sink = DiagLog.Write;

        // 诊断日志服务：构造即注册进 Core.Log 门面（设置页开关控制，默认关闭、关闭时零开销）。
        // 必须在 builder.Build() 之后构造 —— 它读 Preferences 与 AppPaths，依赖已初始化的平台层。
        _ = new Services.DiagnosticLog();
        Services = app.Services;

        // ═══════════════════════════════════════════════════
        // 启动后台恢复订阅源：解析已保存订阅 → 填充 SiteRegistry
        // （失败静默不阻塞首帧；多订阅取第一个成功者）
        // ═══════════════════════════════════════════════════
        // ★ 启动先把**上次解析成功的站点缓存**同步灌进注册表（毫秒级、离线可用）：
        //   否则首屏渲染时异步恢复还没回来 → 每次启动都闪「还没有可用的源 + 扫码配对」引导页
        //   （2026-09-16 用户实测：以为数据没持久化）。联网刷新在下面后台照常进行并覆盖缓存。
        try
        {
            var cachedSites = CatClawVideo.Core.Models.SiteCache.Load();
            if (cachedSites is not null)
            {
                SiteRegistry.Replace(cachedSites);
                DiagLog.Write($"[启动] 站点缓存命中 {cachedSites.Count} 个（后台联网刷新中）");
            }
        }
        catch { }

        _ = Task.Run(async () =>
        {
            try
            {
                var database = app.Services.GetRequiredService<VideoDatabase>();
                var subscriptionManager = app.Services.GetRequiredService<ISubscriptionManager>();
                // 单选启用（2026-09-26 用户定案）：一次只用一个订阅源，行内开关切换。
                               var subs = await database.GetSubscriptionsAsync();
                // 全停是合法状态（用户主动停用）：尊重之，站点表空 → 首页回引导态
                var active = subs.FirstOrDefault(s => s.Enabled);
                if (active is null)
                {
                    SiteRegistry.Replace([]);
                    DiagLog.Write("[启动] 没有启用中的订阅源（可在订阅管理页启用），站点列表为空");
                    return;
                }
                var sites = await subscriptionManager.LoadSubscriptionAsync(active.SourceUrl);
                SiteRegistry.Replace(sites);
                CatClawVideo.Core.Models.SiteCache.Save(sites);   // 供下次启动秒读
                DiagLog.Write($"[启动] 订阅恢复成功: {active.Name}（启用源）→ {sites.Count} 站点");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[启动] 订阅恢复异常: {ex.Message}");
            }
        });

        return app;
    }

}

