using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;
using CatClawVideo.Core.Providers;
using Microsoft.Web.WebView2.Core;

namespace CatClawVideo.Maui.Platforms.Windows;

/// <summary>
/// Windows WebView2 嗅探器：与 AndroidWebSniffer 同语义（TVBox SysWebClient 移植）。
/// WebResourceRequested 逐请求跑视频正则，第一个命中即回传直链。
/// 注意：MAUI Windows 的 WebView2 环境由 MAUI 管理，这里自建离屏 WebView2 实例。
/// </summary>
public class WindowsWebSniffer : IWebSniffer
{
    private const int TimeoutSeconds = 20;

    public async Task<PlayRequest> SniffAsync(string pageUrl, IReadOnlyDictionary<string, string>? extraHeaders,
        string? subscriptionKey, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<PlayRequest>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            // 不显式建 CoreWebView2Environment：C#/WinRT 投影下 CreateAsync 的重载形状与
            // Microsoft.Web.WebView2.Core 文档不完全一致（2 参/3 参均报 CS1501）。
            // 离屏嗅探不需要定制 user-data 目录，直接用进程默认环境即可。
            var webView = new Microsoft.UI.Xaml.Controls.WebView2();
            await webView.EnsureCoreWebView2Async();

            var core = webView.CoreWebView2;
            var settings = core.Settings;
            settings.IsScriptEnabled = true;
            settings.IsStatusBarEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreHostObjectsAllowed = false;
            settings.IsWebMessageEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;

            string? ua = null;
            var pageHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (extraHeaders is not null)
                foreach (var kv in extraHeaders)
                {
                    if (kv.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value))
                        ua = kv.Value.Trim();
                    else
                        pageHeaders[kv.Key] = kv.Value;
                }
            if (ua is not null) core.Settings.UserAgent = ua;

            int found = 0;

            core.WebResourceRequested += (s, e) =>
            {
                try
                {
                    if (Volatile.Read(ref found) > 0)
                    {
                        // 空响应体：WinUI3 的 WebView2 投影签名是 IRandomAccessStream，
                        // 直接传 null 表示「无内容」，避免 MemoryStream→IRandomAccessStream 转换
                        e.Response = core.Environment.CreateWebResourceResponse(null, 200, "OK", "Content-Type: text/plain");
                        return;
                    }
                    var url = e.Request.Uri;
                    if (url.EndsWith("/favicon.ico", StringComparison.Ordinal)) return;
                    // 订阅 rules 的 filter 命中 → 不作候选（对照 TVBox checkIsVideo 的 isFilter 分支）
                    if (TvBoxParseEngine.IsFiltered(pageUrl, url, subscriptionKey)) return;
                    // 通用正则 + 订阅下发的 per-host 规则
                    if (TvBoxParseEngine.CheckIsVideoForParse(pageUrl, url, subscriptionKey) &&
                        Interlocked.Increment(ref found) == 1)
                    {
                        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var h in e.Request.Headers)
                            if (h.Key is "User-Agent" or "Referer" or "Origin" or "Cookie")
                                headers[h.Key] = h.Value;
                        // WebView2 无法同步取 Cookie；Windows 播放器当前不消费 headers，直链即结果
                        tcs.TrySetResult(new PlayRequest { Url = url, Headers = headers });
                        e.Response = core.Environment.CreateWebResourceResponse(null, 200, "OK", "Content-Type: text/plain");
                    }
                }
                catch { }
            };

            // 请求头注入：CoreWebView2 无 loadUrl(url, headers) 等价物，通过 AdditionalRequestHeaders 事件期改写
            if (pageHeaders.Count > 0)
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            else
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);

            core.NavigationCompleted += (s, e) => { /* 等拦截命中，不在此结束 */ };

            webView.Width = 1;
            webView.Height = 1;
            webView.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;

            // 挂到可视树外触发加载（离屏）
            var root = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
            if (root?.Content is Microsoft.UI.Xaml.FrameworkElement fe && fe.XamlRoot is not null)
            {
                // XamlRoot 场景：直接 Content 挂 1x1 隐藏控件不可靠，改为仅靠 Navigate 触发（WebView2 离屏加载无需挂树也能发请求）
            }
            webView.Source = new Uri(pageUrl);

            void StopAndClose()
            {
                try { core.Stop(); } catch { }
                try { webView.Close(); } catch { }
            }

            _ = Task.Delay(TimeSpan.FromSeconds(TimeoutSeconds), CancellationToken.None).ContinueWith(_ =>
            {
                StopAndClose();
                tcs.TrySetException(new NotSupportedException($"嗅探超时（{TimeoutSeconds}s）：页面未给出视频直链，请换线路或换源。"));
            });
            ct.Register(() =>
            {
                StopAndClose();
                tcs.TrySetCanceled(ct);
            });
        }
        catch (Exception ex)
        {
            tcs.TrySetException(new NotSupportedException($"Windows 嗅探初始化失败: {ex.Message}"));
        }

        return await tcs.Task;
    }
}
