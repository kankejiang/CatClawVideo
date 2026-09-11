namespace CatClawVideo.Maui.Pages;

/// <summary>
/// Android 启动页：窗口先落启动页，布局稳定后由 App 切换到主界面（AppShell）。
/// 目的：复刻「进二级页再返回」触发的完整重排——直接以主界面冷启动时，
/// MAUI 的窗口 inset 重置竞态会让底部留一块空白（2026-09-11 真机实测）。
/// </summary>
public partial class SplashPage : ContentPage
{
    public SplashPage()
    {
        InitializeComponent();
    }
}
