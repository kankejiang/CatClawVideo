using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CatClawVideo.Core.Logging;

namespace CatClawVideo.Core.Network;

/// <summary>
/// WebDAV 流媒体本地 HTTP 代理：把 WebDAV 远程文件变成 <c>http://127.0.0.1:{port}/wd/{profileId}/{编码路径}</c>，
/// 播放器（ExoPlayer / Windows MediaPlayer+FFmpegInteropX）统一按普通 HTTP 流消费。
///
/// <para><b>为什么需要代理（而不是把带 Basic Auth 的直链交给播放器）</b>：</para>
/// <list type="bullet">
/// <item>Windows 侧 <c>VideoPlayerView</c> 的 Headers 被播放器忽略，Basic Auth 无处可放；</item>
/// <item>ExoPlayer 的 DefaultHttpDataSource 不解析 URL userinfo，需全局 Java Authenticator 兜 401（猫爪音乐的做法），对全局状态有侵入；</item>
/// <item>OpenList/Alist 的 GET 会 302 到 CDN，且 CDN 拒绝带 Basic Auth 的请求 —— 重定向链必须在代理内部消化；</item>
/// <item>视频**必须支持 Range 拖动**：代理逐请求转发 Range 头，两端播放器都能直接 seek。</item>
/// </list>
///
/// <para>代理 URL 含 profileId + 路径、与端口无关于内容，播放历史里保存的本地 URL
/// 在「端口未被占用」的下一次会话中仍然可播（默认端口优先，被占才顺延）。</para>
/// （模式参照猫爪音乐 <c>SmbStreamProxy</c>：HttpListener + 逐请求 Task + Range 转发。）
/// </summary>
public class WebDavStreamProxy : IDisposable
{
    /// <summary>默认端口：固定值让历史记录里的本地 URL 跨会话尽量可用。</summary>
    private const int DefaultPort = 18923;
    private const int PortScanCount = 100;

    /// <summary>请求路由前缀</summary>
    private const string RoutePrefix = "/wd/";

    private readonly WebDavProfileStore _store;

    private HttpListener? _listener;
    private int _port;
    private bool _disposed;
    private CancellationTokenSource? _cts;

    /// <summary>WebDavService 按连接池化：不同连接独立 HttpClient 状态（探测前缀/token 缓存互不串扰）。
    /// 不复用全局单例——连接 A 的流在途时用户切到连接 B，不能让 Configure 互相打断。</summary>
    private readonly Dictionary<int, WebDavService> _services = new();
    private readonly object _serviceLock = new();

    /// <summary>代理是否正在运行</summary>
    public bool IsRunning => _listener?.IsListening ?? false;

    /// <summary>代理监听端口（未启动时为 0）</summary>
    public int Port => _port;

    /// <summary>是否已配置过连接（应用启动时据此决定是否值得预热代理）。</summary>
    public bool HasProfiles => _store.Prefs.Profiles.Count > 0;

    public WebDavStreamProxy(WebDavProfileStore store)
    {
        _store = store;
    }

    /// <summary>取（或创建）某连接专属的 WebDavService。</summary>
    private WebDavService GetService(ConnectionProfile profile)
    {
        lock (_serviceLock)
        {
            if (!_services.TryGetValue(profile.Id, out var svc))
            {
                svc = new WebDavService();
                svc.Configure(profile);
                _services[profile.Id] = svc;
            }
            else
            {
                // 密码等连接信息可能被编辑过：重新 Configure（内部按值变化重建 HttpClient）
                svc.Configure(profile);
            }
            return svc;
        }
    }

    /// <summary>确保代理已启动（幂等；启动失败返回 false）。</summary>
    public bool EnsureStarted()
    {
        if (IsRunning) return true;
        if (_disposed) return false;

        _cts = new CancellationTokenSource();

        for (var port = DefaultPort; port < DefaultPort + PortScanCount; port++)
        {
            var listener = new HttpListener();
            try
            {
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                listener.Start();
                _listener = listener;
                _port = port;
                break;
            }
            catch
            {
                listener.Close();
            }
        }

        if (_listener == null || !_listener.IsListening)
        {
            Log.Warn("WebDavStreamProxy", "[WebDAVProxy] 无法绑定本地端口，代理启动失败");
            return false;
        }

        Log.Debug("WebDavStreamProxy", $"[WebDAVProxy] 代理已启动: http://127.0.0.1:{_port}/");
        _ = Task.Run(() => ListenLoopAsync(_cts.Token));
        return true;
    }

    /// <summary>构建某连接下某远程文件的本地代理 URL（自动启动代理）。</summary>
    public string? BuildLocalUrl(int profileId, string remotePath)
    {
        if (!EnsureStarted()) return null;
        var encoded = Base64UrlEncode(remotePath);
        return $"http://127.0.0.1:{_port}{RoutePrefix}{profileId}/{encoded}";
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        if (listener == null) return;

        try
        {
            while (!ct.IsCancellationRequested && listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync().WaitAsync(ct);
                }
                catch (OperationCanceledException) { break; }
                _ = Task.Run(() => HandleRequestAsync(context), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn("WebDavStreamProxy", $"[WebDAVProxy] 监听循环退出: {ex.Message}");
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            var rawUrl = request.RawUrl ?? "";
            if (!rawUrl.StartsWith(RoutePrefix, StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = 404;
                response.Close();
                return;
            }

            // /wd/{profileId}/{base64url(path)}
            var rest = rawUrl[RoutePrefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash <= 0 || !int.TryParse(rest[..slash], out var profileId))
            {
                response.StatusCode = 400;
                response.Close();
                return;
            }
            var encodedPath = rest[(slash + 1)..];
            var qIdx = encodedPath.IndexOf('?');
            if (qIdx >= 0) encodedPath = encodedPath[..qIdx];

            var remotePath = Base64UrlDecode(encodedPath);
            if (string.IsNullOrEmpty(remotePath))
            {
                response.StatusCode = 400;
                response.Close();
                return;
            }

            var profile = _store.Find(profileId);
            if (profile == null || !profile.IsEnabled)
            {
                Log.Warn("WebDavStreamProxy", $"[WebDAVProxy] 找不到连接 #{profileId}（可能已被删除）");
                response.StatusCode = 404;
                response.Close();
                return;
            }

            var service = GetService(profile);

            if (request.HttpMethod == "HEAD")
            {
                // 部分播放器先探头部：只回存在性（避免完整 PROPFIND 拖慢起播）
                response.StatusCode = 200;
                response.ContentType = "application/octet-stream";
                response.Headers["Accept-Ranges"] = "bytes";
                response.Close();
                return;
            }

            // 解析播放器的 Range 头（拖动/起播分段），原样转发上游
            RangeHeaderValue? range = null;
            var rangeHeader = request.Headers["Range"];
            if (!string.IsNullOrEmpty(rangeHeader))
            {
                try { range = RangeHeaderValue.Parse(rangeHeader); }
                catch { range = null; }
            }

            HttpResponseMessage upstream;
            try
            {
                upstream = await service.OpenReadResponseAsync(remotePath, range)
                    ?? throw new HttpRequestException("上游无响应");
            }
            catch (Exception ex)
            {
                Log.Debug("WebDavStreamProxy", $"[WebDAVProxy] 上游拉取失败 {remotePath}: {ex.Message}");
                response.StatusCode = 502;
                response.Close();
                return;
            }

            using (upstream)
            {
                response.StatusCode = (int)upstream.StatusCode;   // 200 / 206 原样透传
                response.Headers["Accept-Ranges"] = "bytes";
                response.SendChunked = false;
                response.KeepAlive = true;

                var length = upstream.Content.Headers.ContentLength;
                if (length.HasValue)
                {
                    response.ContentLength64 = length.Value;
                }
                else
                {
                    // 上游未给长度（分块/chunked）：本地也用 chunked，否则 HttpListener 会按 ContentLength64=0 截断
                    response.SendChunked = true;
                }

                var contentRange = upstream.Content.Headers.ContentRange;
                if (contentRange != null) response.Headers["Content-Range"] = contentRange.ToString();

                response.ContentType = upstream.Content.Headers.ContentType?.ToString()
                    ?? GuessMimeType(remotePath);

                await using var source = await upstream.Content.ReadAsStreamAsync();
                var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                try
                {
                    // 不带取消 token：客户端断开会在下一次 Write 抛异常退出循环，
                    // using 会随即释放上游响应、断开远端连接（读挂死场景同样被覆盖）。
                    int read;
                    while ((read = await source.ReadAsync(buffer)) > 0)
                    {
                        await response.OutputStream.WriteAsync(buffer.AsMemory(0, read));
                    }
                    await response.OutputStream.FlushAsync();
                }
                catch (Exception ex)
                {
                    // 播放器中断连接（拖动换段、退出播放）属正常路径
                    Log.Debug("WebDavStreamProxy", $"[WebDAVProxy] 传输中断: {ex.Message}");
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            response.Close();
        }
        catch (Exception ex)
        {
            Log.Warn("WebDavStreamProxy", $"[WebDAVProxy] 请求处理异常: {ex.Message}");
            try
            {
                response.StatusCode = 500;
                response.Close();
            }
            catch { }
        }
    }

    /// <summary>按扩展名猜 MIME（FFmpeg 会自己嗅探，此值主要帮部分播放器起播判断）。</summary>
    private static string GuessMimeType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".mp4" or ".m4v" or ".m4a" => "video/mp4",
            ".mkv" => "video/x-matroska",
            ".ts" or ".m2ts" => "video/mp2t",
            ".avi" => "video/x-msvideo",
            ".mov" => "video/quicktime",
            ".flv" => "video/x-flv",
            ".webm" => "video/webm",
            ".wmv" => "video/x-ms-wmv",
            ".m3u8" => "application/vnd.apple.mpegurl",
            ".mp3" => "audio/mpeg",
            ".flac" => "audio/flac",
            ".wav" => "audio/wav",
            _ => "application/octet-stream",
        };
    }

    // ── Base64Url（路径编码：中文/空格/斜杠全部安全，且不含 URL 特殊字符） ──

    internal static string Base64UrlEncode(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal static string Base64UrlDecode(string encoded)
    {
        try
        {
            var padded = encoded.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }
            return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        }
        catch
        {
            return "";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts?.Cancel(); } catch { }
        try { _cts?.Dispose(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        lock (_serviceLock)
        {
            foreach (var svc in _services.Values)
            {
                try { svc.Dispose(); } catch { }
            }
            _services.Clear();
        }
    }
}
