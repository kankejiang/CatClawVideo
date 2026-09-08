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
        // 影视源提供者：MacCMS JSON 直连源 + TVBox 订阅解析
        // ═══════════════════════════════════════════════════
        services.AddSingleton<IVodSourceProvider, CatClawVideo.Core.Providers.MacCmsJsonProvider>();
        services.AddSingleton<ISubscriptionManager, CatClawVideo.Core.Providers.TvBoxSubscriptionManager>();

        // ═══════════════════════════════════════════════════
        // ViewModels
        // ═══════════════════════════════════════════════════
        services.AddSingleton<MainViewModel>();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<FavoritesViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<VideoPlayerViewModel>();

        // ═══════════════════════════════════════════════════
        // Pages / Shell
        // ═══════════════════════════════════════════════════
        services.AddSingleton<AppShell>();
        services.AddSingleton<Pages.MainPage>();
        services.AddTransient<Pages.HomePage>();
        services.AddTransient<Pages.FavoritesPage>();
        services.AddTransient<Pages.SettingsPage>();
        services.AddTransient<Pages.VideoPlayerPage>();
        services.AddTransient<Pages.WatchPage>();
        services.AddTransient<Pages.SearchPage>();
        services.AddTransient<Pages.SourceConfigPage>();
        services.AddTransient<Pages.HistoryPage>();
        services.AddTransient<Pages.LocalMediaPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        var app = builder.Build();
        Services = app.Services;
        return app;
    }
}
