#if WINDOWS
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace CatClawVideo.Maui.Platforms.Windows;

/// <summary>
/// 树渲染专用的原生 WebView2 宿主。
///
/// <para><b>为什么不用 MAUI 的 WebView</b>：MAUI 11 预览版的 WebView2 代理会在
/// <c>WebView2Proxy.OnCoreWebView2Initialized</c> 里对 null 的 <c>CoreWebView2</c> 解引用，
/// 抛出的 NullReferenceException 落在 WinUI 异步回调里（try/catch 抓不到）→ stowed exception
/// 0xc000027b → 进程直接死。实测（2026-10-03）构造期设源、延后到 Loaded 设源都会触发；
/// 事件日志与 %TEMP%\catclawvideo_startup.log 均有该栈。本类直接用 WinUI 控件，
/// 并用**显式可写用户数据目录**建环境（默认目录落在 exe 同级，装到 Program Files 时不可写）。</para>
/// </summary>
public sealed class TreeWebHost : ContentView
{
    private WebView2? _wv;
    private string _html = string.Empty;
    private bool _ready;

    /// <summary>HTML 里的 clsk: 链接（点节点/按钮/取消）——原样回传，解析交给页面。</summary>
    public event Action<string>? LinkClicked;

    /// <summary>诊断日志出口（接到宿主 DiagLog）。</summary>
    public Action<string>? Log { get; set; }

    public TreeWebHost()
    {
        HandlerChanged += (_, _) => Attach();
    }

    /// <summary>换 HTML（整树换新也走这里）。</summary>
    public void SetHtml(string html)
    {
        _html = html ?? string.Empty;
        Apply();
    }

    /// <summary>在页面里执行脚本（jar 就地 setText 后的整树换新）。</summary>
    public void RunScript(string js)
    {
        _ = RunScriptAsync(js);
    }

    private async Task RunScriptAsync(string js)
    {
        try
        {
            if (_wv?.CoreWebView2 is { } core) await core.ExecuteScriptAsync(js);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[tree] ExecuteScript 失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Attach()
    {
        if (_wv is not null) return;
        if (Handler?.PlatformView is not Panel panel) return;

        _wv = new WebView2
        {
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Stretch,
        };
        panel.Children.Add(_wv);
        _wv.CoreWebView2Initialized += (_, e) =>
        {
            if (e.Exception is not null)
                Log?.Invoke($"[tree] WebView2 初始化异常：{e.Exception.GetType().Name}: {e.Exception.Message}");
        };
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            // 用户数据目录由 App.xaml.cs 里的 WEBVIEW2_USER_DATA_FOLDER 统一指定为**可写**目录：
            // 默认落在 exe 同级，装到 C:\Program Files 后不可写 → CoreWebView2 初始化失败
            // → MAUI 的 WebView 代理随即空引用崩进程（2026-10-03 实测根因）。
            // 注：C#/WinRT 投影下 CoreWebView2Environment.CreateAsync 无 (string,string) 重载
            //（2 参/3 参都报 CS1501，嗅探器注释里早有记录），故走环境变量这条路。
            await _wv!.EnsureCoreWebView2Async();
            _wv.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (e.Uri.StartsWith("clsk:", StringComparison.Ordinal))
                {
                    e.Cancel = true;          // 与 MAUI 版语义一致：拦截自定义 scheme，不让它真跳转
                    LinkClicked?.Invoke(e.Uri);
                }
            };
            _ready = true;
            Log?.Invoke("[tree] 原生 WebView2 就绪");
            Apply();
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[tree] WebView2 初始化失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Apply()
    {
        try
        {
            if (_ready && _html.Length > 0 && _wv?.CoreWebView2 is { } core)
                core.NavigateToString(_html);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[tree] NavigateToString 失败：{ex.GetType().Name}: {ex.Message}");
        }
    }
}
#endif
