using Microsoft.UI.Xaml;
using Microsoft.Maui.Hosting;

namespace CatClawVideo.Maui.WinUI;

public partial class App : MauiWinUIApplication
{
    public App()
    {
        // WebView2 的用户数据目录必须**可写**：默认落在 exe 同级，装到 C:\Program Files 后
        // 不可写 → CoreWebView2 初始化失败 → MAUI 的 WebView 代理在回调里空引用，
        // stowed exception 直接把进程打死（2026-10-03 用户实测：发行版点「登入自己云盘」闪退，
        // 而 Debug 在 D 盘可写目录下正常）。必须在任何 WebView2 创建之前设置。
        try
        {
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER",
                CatClawVideo.Core.AppPaths.LocalSub("webview2-mini"));
        }
        catch { /* 设不上也不该拦启动 */ }

        InitializeComponent();
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        UnhandledException += OnApplicationUnhandledException;

        // 崩溃取证（2026-10-03）：WinUI 的 stowed exception（0xc000027b）只保留错误信息，
        // 原始异常的调用栈已经弹出 —— WinDbg 的 !pe / !clrstack 在崩溃点都看不到它
        //（实测「no current managed exception on this thread」）。
        // 而 FirstChanceException 在**抛出瞬间**就能拿到异常与完整调用栈，
        // 只记 COMException（E_FAIL 80004005 / E_ABORT 80004004 正是这一类），
        // 避免被正常业务异常淹没。
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            if (e.Exception is System.Runtime.InteropServices.COMException ce)
            {
                LogCrash("FirstChance.COMException", ce);
                // FirstChance 触发时异常自身的 StackTrace 还没赋值（只剩 throw 点一帧，实测），
                // 用 Environment.StackTrace 记录**当前**调用栈 —— 此刻尚未 unwound，最接近抛出点，
                // 这是我们唯一能拿到"到底是哪一行调用了会返回失败 HRESULT 的 WinRT API"的机会。
                try
                {
                    var log = Path.Combine(Path.GetTempPath(), "catclawvideo_startup.log");
                    var frames = (Environment.StackTrace ?? string.Empty).Split('\n');
                    File.AppendAllText(log,
                        $"[{DateTime.Now:HH:mm:ss.fff}] FIRSTCHANCE 0x{ce.HResult:X8} {ce.Message}\n"
                        + string.Join("\n", frames.Take(26)) + "\n\n");
                }
                catch { /* 记日志失败不影响主流程 */ }
            }
        };
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

        // 已知 MAUI 缺陷（2026-10-03 WinDbg 取证）：WindowRootView.UpdateTitleBarContentSize() 抛
        // ArgumentException(E_INVALIDARG 0x80070057)，且抛在 WinRT 事件回调里。未处理就会以
        // stowed exception(0xc000027b) 在 CoreMessagingXP!DispatcherQueue::DeferInvokeCallback
        // fail-fast，进程直接消失（托管日志只留一条 UnhandledException）。这里标记为已处理避免闪退；
        // 触发源（反复设置 ExtendsContentIntoTitleBar）已在 WindowDragHelper 侧修掉，此处为兜底。
        if (e.Exception is ArgumentException
            && (e.Exception.StackTrace?.Contains("UpdateTitleBarContentSize", StringComparison.Ordinal) ?? false))
        {
            LogCrash("已知 MAUI 标题栏缺陷 → 已吞掉，避免闪退", e.Exception);
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
