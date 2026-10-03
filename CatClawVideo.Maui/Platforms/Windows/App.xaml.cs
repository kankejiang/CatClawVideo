using Microsoft.UI.Xaml;
using Microsoft.Maui.Hosting;

namespace CatClawVideo.Maui.WinUI;

public partial class App : MauiWinUIApplication
{
    public App()
    {
        InitializeComponent();
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        UnhandledException += OnApplicationUnhandledException;
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var logPath = Path.Combine(Path.GetTempPath(), "catclawvideo_startup.log");
            File.AppendAllText(logPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] CRASH[{source}]: {ex}\n");
        }
        catch { /* 日志写入失败不影响主流程 */ }
    }

    private void OnApplicationUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogCrash("Application.UnhandledException", e.Exception);

        // 兜底（2026-10-03）：MAUI WebView2 代理在 CoreWebView2 尚未初始化完成时回调
        // LoadHtml → NullReferenceException，抛在 WinUI 异步回调里（try/catch 抓不到），
        // stowed exception 直接把进程打死（用户：点「登入自己云盘」必闪退）。
        // 页面侧已改为「挂树后再设 Source」修根因；这里再兜一层：命中这条已知栈就标记为已处理，
        // 宁可那个对话框渲染空白，也不要整个应用消失。
        if (e.Exception is NullReferenceException
            && (e.Exception.StackTrace?.Contains("WebView2Proxy", StringComparison.Ordinal) ?? false))
        {
            LogCrash("已知 MAUI WebView2 缺陷 → 已吞掉，避免闪退", e.Exception);
            e.Handled = true;
        }
    }

    private void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        LogCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogCrash("TaskScheduler.UnobservedTaskException", e.Exception);
        e.SetObserved();
    }
}
