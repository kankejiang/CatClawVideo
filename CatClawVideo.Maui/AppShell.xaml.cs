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

        // 观看页路由（详情+播放合并页；参数：title）
        Routing.RegisterRoute("watch", typeof(Pages.WatchPage));
    }
}
