using AWebView = global::Android.Webkit.WebView;
using global::Android.Webkit;
using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Providers;
using CatClawVideo.Core.Models;
using Java.Net;

namespace CatClawVideo.Maui.Platforms.Android;

/// <summary>
/// Android WebView 嗅探器（TVBox OSC SysWebClient 移植）：
/// 1x1 隐藏 WebView 加载解析页 → shouldInterceptRequest 逐请求跑视频正则 → 第一个命中即停。
/// WebView 配置对照 configWebViewSys（L2756-2830）：JS/DOM 开、mixedContent 放行、SSL 一律继续、
/// mediaPlaybackRequiresUserGesture=false、blockNetworkImage。
/// </summary>
public class AndroidWebSniffer : IWebSniffer
{
    private const int TimeoutSeconds = 20;   // TVBox MSG_PARSE_TIMEOUT 口径

    public async Task<PlayRequest> SniffAsync(string pageUrl, IReadOnlyDictionary<string, string>? extraHeaders,
        string? subscriptionKey, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<PlayRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        // 主线程调度（MAUI Essentials）
        Action<Action> main = a => Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(a);

        main(() =>
        {
            AWebView? web = null;
            global::Android.App.Activity? activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            if (activity is null)
            {
                tcs.TrySetException(new NotSupportedException("嗅探失败：无前台 Activity"));
                return;
            }

            string? ua = null;
            var pageHeaders = new Dictionary<string, string>();
            if (extraHeaders is not null)
                foreach (var kv in extraHeaders)
                {
                    if (kv.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value))
                        ua = kv.Value.Trim();
                    else
                        pageHeaders[kv.Key] = kv.Value;
                }

            try
            {
                web = new AWebView(activity);
                web.LayoutParameters = new global::Android.Widget.AbsoluteLayout.LayoutParams(1, 1, 0, 0);
                web.Focusable = false;
                web.ClearFocus();

                var settings = web.Settings;
                settings.JavaScriptEnabled = true;
                settings.DomStorageEnabled = true;
                settings.AllowContentAccess = true;
                settings.AllowFileAccess = true;
                settings.MediaPlaybackRequiresUserGesture = false;
                settings.BlockNetworkImage = true;
                settings.JavaScriptCanOpenWindowsAutomatically = true;
                settings.SetSupportMultipleWindows(false);
                settings.MixedContentMode = MixedContentHandling.AlwaysAllow;
                settings.CacheMode = CacheModes.Default;
                settings.DefaultTextEncodingName = "utf-8";
                if (ua is not null) settings.UserAgentString = ua;

                bool settled = false;
                web.SetWebViewClient(new SniffClient(url =>
                {
                    // 命中视频直链：携带嗅探时的 Cookie（对照 L2896-2897）
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var cookie = global::Android.Webkit.CookieManager.Instance?.GetCookie(url);
                    if (!string.IsNullOrEmpty(cookie)) headers["Cookie"] = " " + cookie;
                    tcs.TrySetResult(new PlayRequest { Url = url, Headers = headers });
                }, () => { }, pageUrl, subscriptionKey));

                activity.AddContentView(web, new global::Android.Views.ViewGroup.LayoutParams(1, 1));

                // 页面请求头（loadUrl(url, headerMap) 等价：首请求附加）
                if (pageHeaders.Count > 0)
                    web.LoadUrl(pageUrl, pageHeaders);
                else
                    web.LoadUrl(pageUrl);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }

            // 总超时 + 外部取消：销毁 WebView
            void Cleanup()
            {
                try
                {
                    main(() =>
                    {
                        try
                        {
                            web?.StopLoading();
                            web?.LoadUrl("about:blank");
                            ((global::Android.Views.ViewGroup?)web?.Parent)?.RemoveView(web);
                            web?.Destroy();
                        }
                        catch { }
                    });
                }
                catch { }
            }

            _ = Task.Delay(TimeSpan.FromSeconds(TimeoutSeconds), CancellationToken.None).ContinueWith(_ =>
            {
                Cleanup();
                tcs.TrySetException(new NotSupportedException(
                    $"嗅探超时（{TimeoutSeconds}s）：页面未给出视频直链，请换线路或换源。"));
            });

            ct.Register(() =>
            {
                Cleanup();
                tcs.TrySetCanceled(ct);
            });
        });

        return await tcs.Task;
    }

    /// <summary>拦截客户端：shouldInterceptRequest 逐请求判定（对照 SysWebClient.checkIsVideo）</summary>
    private sealed class SniffClient(Action<string> onVideo, Action onSettled, string pageUrl,
        string? subscriptionKey) : WebViewClient
    {
        private int _found;

        public override WebResourceResponse? ShouldInterceptRequest(AWebView? view, IWebResourceRequest? request)
        {
            var url = request?.Url?.ToString();
            if (url is null) return null;

            // 命中即停：后续请求全部空响应（对照 loadFoundCount>0 → createEmptyResource）
            if (Volatile.Read(ref _found) > 0)
                return EmptyResponse();

            if (url.EndsWith("/favicon.ico", StringComparison.Ordinal)) return null;

            // 订阅 rules 的 filter 命中 → 这个 URL 既不作候选也不空响应（对照 isFilter → return null）
            if (TvBoxParseEngine.IsFiltered(pageUrl, url, subscriptionKey)) return null;

            // 通用正则（DefaultConfig.snifferMatch）+ 订阅下发的 per-host 规则
            if (TvBoxParseEngine.CheckIsVideoForParse(pageUrl, url, subscriptionKey))
            {
                Interlocked.Increment(ref _found);
                onVideo(url);
                return EmptyResponse();
            }
            return null;
        }

        public override void OnReceivedSslError(AWebView? view, SslErrorHandler? handler, global::Android.Net.Http.SslError? error)
        {
            handler?.Proceed();   // TVBox 语义：解析页 SSL 一律继续
        }

        public override bool ShouldOverrideUrlLoading(AWebView? view, string? url) => false;

        private static WebResourceResponse EmptyResponse() =>
            new("text/plain", "utf-8", new System.IO.MemoryStream(Array.Empty<byte>()));
    }
}
