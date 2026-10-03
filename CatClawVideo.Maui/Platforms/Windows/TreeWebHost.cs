#if WINDOWS
using Microsoft.Maui.Handlers;
using Microsoft.UI.Xaml.Controls;

namespace CatClawVideo.Maui.Platforms.Windows;

/// <summary>
/// 树渲染控件（MAUI 侧视图）。真正干活的是 <see cref="TreeWebView2Handler"/>：用原生 WinUI WebView2
/// 渲染 jar 的 View 树。走正规 Handler（不是"往平台面板里塞控件"），尺寸/可见性由框架负责。
///
/// <para><b>为什么不用 MAUI 的 WebView</b>：MAUI 11 预览版的 WebView2 代理在
/// <c>WebView2Proxy.OnCoreWebView2Initialized</c> 里对 null 的 <c>CoreWebView2</c> 解引用，
/// 抛在 WinUI 异步回调里（try/catch 抓不到）→ stowed exception 0xc000027b → 进程直接死。
/// 而 CoreWebView2 为 null 的根因是 WebView2 **默认用户数据目录落在 exe 同级**，
/// 装到 C:\\Program Files 后不可写 → 初始化失败（Debug 在 D 盘则正常，与用户现象一致）。
/// 用户数据目录现由 App.xaml.cs 的 WEBVIEW2_USER_DATA_FOLDER 指向可写目录。</para>
/// </summary>
public class TreeWebView2 : View
{
    public static readonly BindableProperty HtmlProperty =
        BindableProperty.Create(nameof(Html), typeof(string), typeof(TreeWebView2), string.Empty);

    public string Html
    {
        get => (string)GetValue(HtmlProperty);
        set => SetValue(HtmlProperty, value);
    }

    /// <summary>HTML 里的 clsk: 链接（点节点/按钮/取消），原样回传由页面解析。</summary>
    public event Action<string>? LinkClicked;

    /// <summary>诊断日志出口。</summary>
    public Action<string>? Log { get; set; }

    /// <summary>执行脚本（jar 就地 setText 后的整树换新）。</summary>
    public void RunScript(string js) => (Handler as TreeWebView2Handler)?.RunScript(js);

    internal void RaiseLink(string url) => LinkClicked?.Invoke(url);
    internal void RaiseLog(string msg) => Log?.Invoke(msg);
}

/// <summary>把 <see cref="TreeWebView2"/> 映射到原生 WinUI WebView2。</summary>
public class TreeWebView2Handler : ViewHandler<TreeWebView2, WebView2>
{
    public static readonly IPropertyMapper<TreeWebView2, TreeWebView2Handler> Mapper =
        new PropertyMapper<TreeWebView2, TreeWebView2Handler>(ViewMapper)
        {
            [nameof(TreeWebView2.Html)] = MapHtml,
        };

    private bool _ready;

    public TreeWebView2Handler() : base(Mapper) { }

    protected override WebView2 CreatePlatformView() => new WebView2();

    protected override void ConnectHandler(WebView2 platformView)
    {
        base.ConnectHandler(platformView);
        platformView.CoreWebView2Initialized += OnCoreInitialized;
        _ = InitAsync(platformView);
    }

    protected override void DisconnectHandler(WebView2 platformView)
    {
        platformView.CoreWebView2Initialized -= OnCoreInitialized;
        base.DisconnectHandler(platformView);
    }

    private async Task InitAsync(WebView2 pv)
    {
        try
        {
            // 用户数据目录由 WEBVIEW2_USER_DATA_FOLDER 指定为可写目录（见 App.xaml.cs）：
            // C#/WinRT 投影下 CoreWebView2Environment.CreateAsync 无 (string,string) 重载，故走环境变量。
            await pv.EnsureCoreWebView2Async();
            if (pv.CoreWebView2 is { } core)
            {
                core.NavigationStarting += (_, e) =>
                {
                    if (e.Uri.StartsWith("clsk:", StringComparison.Ordinal))
                    {
                        e.Cancel = true;
                        VirtualView?.RaiseLink(e.Uri);
                    }
                };
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                _ready = true;
                VirtualView?.RaiseLog("[tree] 原生 WebView2 就绪");
                Apply();
            }
            else
            {
                VirtualView?.RaiseLog("[tree] WebView2 就绪但 CoreWebView2 为空");
            }
        }
        catch (Exception ex)
        {
            VirtualView?.RaiseLog($"[tree] WebView2 初始化失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnCoreInitialized(WebView2 sender, CoreWebView2InitializedEventArgs args)
    {
        if (args.Exception is not null)
            VirtualView?.RaiseLog($"[tree] CoreWebView2Initialized 异常：{args.Exception.GetType().Name}: {args.Exception.Message}");
    }

    private static void MapHtml(TreeWebView2Handler h, TreeWebView2 v) => h.Apply();

    private void Apply()
    {
        var html = VirtualView?.Html ?? string.Empty;
        if (string.IsNullOrEmpty(html) || !_ready) return;
        try
        {
            PlatformView?.CoreWebView2?.NavigateToString(html);
            VirtualView?.RaiseLog($"[tree] 已注入 HTML（{html.Length} 字符）");
        }
        catch (Exception ex)
        {
            VirtualView?.RaiseLog($"[tree] NavigateToString 失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    public void RunScript(string js) => _ = RunScriptAsync(js);

    private async Task RunScriptAsync(string js)
    {
        try
        {
            if (PlatformView?.CoreWebView2 is { } core) await core.ExecuteScriptAsync(js);
        }
        catch (Exception ex)
        {
            VirtualView?.RaiseLog($"[tree] ExecuteScript 失败：{ex.GetType().Name}: {ex.Message}");
        }
    }
}
#endif
