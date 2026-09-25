namespace CatClawVideo.Maui;

public partial class AppShell : Shell
{
    public AppShell(IServiceProvider services)
    {
        InitializeComponent();

        // 主页面从 DI 注入（Singleton，横竖屏切换复用）
        MainShellContent.Content = services.GetRequiredService<Pages.MainPage>();

        // 播放页路由（参数：title / url）
        Routing.RegisterRoute("player", typeof(Pages.VideoPlayerPage));

        // 网页展示页（Guard 系云盘配置等 HTML 交互页；参数：title / url）
        Routing.RegisterRoute("webpage", typeof(Pages.WebPagePage));

        // 观看页路由（详情+播放合并页；参数：title）
        Routing.RegisterRoute("watch", typeof(Pages.WatchPage));

        // 搜索页（顶栏搜索框进入）
        Routing.RegisterRoute("search", typeof(Pages.SearchPage));

        // 源配置页（设置 → 站点与源配置）
        Routing.RegisterRoute("sourceconfig", typeof(Pages.SourceConfigPage));

        // 直播间（顶栏「直播」进入；TVBox LivePlayActivity 移植）
        Routing.RegisterRoute("live", typeof(Pages.LivePage));

        // 直播源配置页（直播间空白态「配置直播源」/ 设置面板进入）
        Routing.RegisterRoute("livesource", typeof(Pages.LiveSourcePage));

        // 网络媒体页（本地媒体 tab「网络媒体」进入；WebDAV 连接管理 + 远程目录浏览）
        Routing.RegisterRoute("networkmedia", typeof(Pages.NetworkMediaPage));

        // 下载设置页（下载 tab 右上角 ⚙ 进入；照搬 Motrix 设置项）

        // 关于页（设置 → 关于）
        Routing.RegisterRoute("about", typeof(Pages.AboutPage));

        // 诊断日志页（设置 → 诊断日志；开关开启后可查看/导出 Debug 级日志）
        Routing.RegisterRoute("diagnosticlog", typeof(Pages.DiagnosticLogPage));

        // 任务详情页（下载卡片 ℹ 进入；照搬 Motrix 任务详情）
        Routing.RegisterRoute("downloaddetail", typeof(Pages.DownloadDetailPage));
    }
}
