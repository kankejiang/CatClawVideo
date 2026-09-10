using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CatClawVideo.Core.Services;

/// <summary>
/// 本地 BT 流代理：极简 HTTP/1.1 服务（仅 127.0.0.1，GET/HEAD + Range）。
/// 播放器（ExoPlayer / WinUI MediaPlayer）把磁力当普通网络视频：
///   GET /stream/{infoHash}   → 200/206，Range 透传到流式 Stream（阻塞至 piece 到货）。
/// 起播闸门自然形成：首个 Read 需等首尾 piece 预取完成才返回数据。
/// </summary>
public sealed class BtHttpProxy
{
    private readonly BtStreamService _bt;
    private readonly Action<string>? _log;
    private TcpListener? _listener;

    public int Port { get; private set; }
    public string Prefix => $"http://127.0.0.1:{Port}";

    public BtHttpProxy(BtStreamService bt, Action<string>? log = null)
    {
        _bt = bt;
        _log = log;
    }

    private void Log(string m) => _log?.Invoke("[bt-proxy] " + m);

    /// <summary>绑定 127.0.0.1 随机端口并开始接受连接（同步返回，Accept 在后台）</summary>
    public void Start(int port = 0)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener is { } listener)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(); }
            catch { break; } // listener 已关闭
            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        try
        {
            using var _ = client;
            client.NoDelay = true;
            using var stream = client.GetStream();

            var request = await ReadRequestAsync(stream);
            if (request == null) return;
            var (method, path, rangeHeader) = request.Value;

            // 路由：/stream/{infoHash}
            var seg = path.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (seg.Length != 2 || !seg[0].Equals("stream", StringComparison.OrdinalIgnoreCase))
            {
                await WriteErrorAsync(stream, 404, "Not Found");
                return;
            }
            var infoHex = seg[1];
            var session = _bt.FindSession(infoHex);
            if (session == null)
            {
                await WriteErrorAsync(stream, 404, "BT session not ready");
                return;
            }

            // Range 解析：bytes=start-end / bytes=start- / bytes=-suffix
            long start = 0, endIncl = session.FileLength - 1;
            bool isRange = false;
            if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                var spec = rangeHeader["bytes=".Length..].Trim();
                var dash = spec.IndexOf('-');
                var left = dash > 0 ? spec[..dash].Trim() : "";
                var right = dash >= 0 && dash < spec.Length - 1 ? spec[(dash + 1)..].Trim() : "";
                if (left.Length > 0 && long.TryParse(left, out var s0))
                {
                    start = s0;
                    if (right.Length > 0 && long.TryParse(right, out var e0)) endIncl = Math.Min(e0, session.FileLength - 1);
                    isRange = true;
                }
                else if (right.Length > 0 && long.TryParse(right, out var suffix))
                {
                    start = Math.Max(0, session.FileLength - suffix); // 尾部 N 字节
                    isRange = true;
                }
            }

            if (start >= session.FileLength || start > endIncl)
            {
                await WriteErrorAsync(stream, 416, $"Requested Range Not Satisfiable (len={session.FileLength})");
                return;
            }

            var total = endIncl - start + 1;
            var contentType = GuessContentType(session.FileName);
            var head = isRange
                ? $"HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {start}-{endIncl}/{session.FileLength}\r\n"
                : "HTTP/1.1 200 OK\r\n";
            head += $"Accept-Ranges: bytes\r\nContent-Type: {contentType}\r\n" +
                    $"Content-Length: {total}\r\nConnection: close\r\n\r\n";

            if (method == "HEAD")
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                return;
            }

            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));

            // 数据泵：ReadAsync 阻塞至 piece 到货（起播闸门在此自然形成）
            var buf = new byte[64 * 1024];
            long offset = start, remaining = total;
            while (remaining > 0)
            {
                var n = await _bt.ReadAsync(infoHex, offset, buf, (int)Math.Min(buf.Length, remaining), CancellationToken.None);
                if (n <= 0) break; // EOF（长度与元数据不符时防御）
                await stream.WriteAsync(buf.AsMemory(0, n));
                offset += n;
                remaining -= n;
            }
            Log($"GET {infoHex[..12]}… range={start}+{total - remaining}/{total} 完成");
        }
        catch (Exception ex)
        {
            Log($"连接处理结束: {ex.Message}");
        }
    }

    /// <summary>读请求头（至空行，上限 16KB），返回 (方法, 路径, Range)</summary>
    private static async Task<(string Method, string Path, string? Range)?> ReadRequestAsync(NetworkStream stream)
    {
        var buf = new byte[16 * 1024];
        var received = 0;
        while (received < buf.Length)
        {
            var n = await stream.ReadAsync(buf.AsMemory(received));
            if (n <= 0) return null;
            received += n;
            var text = Encoding.ASCII.GetString(buf, 0, received);
            var idx = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (idx < 0) continue;

            var head = text[..idx];
            var lines = head.Split("\r\n");
            var requestLine = lines[0].Split(' ');
            if (requestLine.Length < 2) return null;
            string? range = null;
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon > 0 &&
                    line[..colon].Trim().Equals("Range", StringComparison.OrdinalIgnoreCase))
                    range = line[(colon + 1)..].Trim();
            }
            return (requestLine[0], requestLine[1], range);
        }
        return null;
    }

    private static async Task WriteErrorAsync(NetworkStream stream, int code, string message)
    {
        var body = Encoding.UTF8.GetBytes(message);
        var head = $"HTTP/1.1 {code} {message}\r\nContent-Type: text/plain; charset=utf-8\r\n" +
                   $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(body);
    }

    private static string GuessContentType(string fileName)
    {
        var ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".mp4" or ".m4v" or ".mov" => "video/mp4",
            ".mkv" => "video/x-matroska",
            ".webm" => "video/webm",
            ".avi" => "video/x-msvideo",
            ".ts" or ".m2ts" => "video/mp2t",
            ".flv" => "video/x-flv",
            ".wmv" => "video/x-ms-wmv",
            _ => "application/octet-stream",
        };
    }
}
