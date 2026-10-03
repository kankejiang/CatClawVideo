namespace CatClawVideo.Maui.Services;

/// <summary>
/// 远程投屏的连接参数：可配 + 记住"最近一次<b>成功</b>的连接"（失败不覆盖，免得把用户带进死地址）。
/// </summary>
public static class RemoteStreamPrefs
{
    private const string KeyHost = "remote.host";
    private const string KeyPort = "remote.port";
    private const string KeyCodec = "remote.codec";       // auto | jpeg | h264
    private const string KeyLastHost = "remote.last_ok.host";
    private const string KeyLastPort = "remote.last_ok.port";

    public const string DefaultHost = "10.0.0.108";
    public const int DefaultPort = 27383;

    /// <summary>
    /// 主机名兜底清洗：这个字段是用户手输的，一旦被粘进带空格/中文/多地址的串（实测把
    /// "10.0.0.108" 和 "127.0.0.1" 连成一串），DNS 只会报"不知道这样的主机"，
    /// 而提示里带的还是这串脏值，用户看不出自己填错了什么 ⇒ 非法值直接退回默认地址。
    /// </summary>
    private static string Sanitize(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return DefaultHost;
        var h = host.Trim();
        foreach (var c in h)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '.' || c == '-' || c == '_' || c == ':')) return DefaultHost;
        }
        // "10.0.0.108127.0.0.1" 全是合法字符，只有点分结构能认出它是脏值
        if (h.All(c => char.IsDigit(c) || c == '.') && !LooksLikeIpv4(h)) return DefaultHost;
        return h.Length == 0 ? DefaultHost : h;
    }

    private static bool LooksLikeIpv4(string h)
    {
        var parts = h.Split('.');
        if (parts.Length != 4) return false;
        foreach (var p in parts)
        {
            if (p.Length == 0 || p.Length > 3 || !int.TryParse(p, out var v) || v < 0 || v > 255) return false;
        }
        return true;
    }

    public static string Host => Sanitize(Preferences.Default.Get(KeyHost, DefaultHost));
    public static int Port => Preferences.Default.Get(KeyPort, DefaultPort);
    public static string Codec => Preferences.Default.Get(KeyCodec, "auto");

    public static void Save(string host, int port, string codec)
    {
        Preferences.Default.Set(KeyHost, host);
        Preferences.Default.Set(KeyPort, port);
        Preferences.Default.Set(KeyCodec, codec);
    }

    /// <summary>握手成功才算"最近一次成功连接"，存下来给下次开机直接用。</summary>
    public static void SaveSuccess(string host, int port)
    {
        Preferences.Default.Set(KeyLastHost, host);
        Preferences.Default.Set(KeyLastPort, port);
    }

    public static (string Host, int Port)? LastSuccess
    {
        get
        {
            var host = Sanitize(Preferences.Default.Get(KeyLastHost, ""));
            var port = Preferences.Default.Get(KeyLastPort, 0);
            return port <= 0 || host == DefaultHost && !Preferences.Default.ContainsKey(KeyLastHost)
                ? null
                : (host, port);
        }
    }
}
