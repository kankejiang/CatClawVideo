namespace CatClawVideo.Core.Network;

/// <summary>网络协议类型（对应猫爪音乐 ConnectionProfile.Protocol，当前接入 WebDAV，其余预留）。</summary>
public enum NetworkProtocol
{
    /// <summary>WebDAV 协议（NAS / Alist / OpenList / 坚果云等）</summary>
    WebDAV = 0,
}

/// <summary>
/// WebDAV 服务器类型（影响 PROPFIND 深度、重定向策略与是否走 REST API）。
/// 移植自猫爪音乐（实测 2026-09）：OpenList/Alist 的 WebDAV 端点在 <c>/dav</c> 下且
/// GET 会 302 到 CDN，CDN 拒绝带 Basic Auth 的请求 —— 需要走 <c>/api/fs/*</c> 直链。
/// </summary>
public enum WebDavServerType
{
    /// <summary>标准 WebDAV（NAS、Apache、IIS 等）</summary>
    Standard = 0,

    /// <summary>OpenList / Alist</summary>
    OpenList = 1,
}

/// <summary>
/// 网络媒体连接配置（对应猫爪音乐 <c>ConnectionProfile</c>，裁掉 SMB/Navidrome 专用字段）。
/// 持久化为 <c>AppPaths/Sub("webdav")/profiles.json</c>（条目量小，JSON 足够，省一张库表）。
/// </summary>
public class ConnectionProfile
{
    /// <summary>本地唯一 Id（持久化时分配，代理 URL 用它定位连接）</summary>
    public int Id { get; set; }

    /// <summary>显示名称</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>协议类型（当前恒为 WebDAV）</summary>
    public NetworkProtocol Protocol { get; set; } = NetworkProtocol.WebDAV;

    /// <summary>主机地址（可带 scheme/端口，BuildUrlForProfile 会归一化）</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>端口</summary>
    public int Port { get; set; } = 5005;

    /// <summary>用户名（空 = 匿名）</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>密码 / Token</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>基础路径（WebDAV 根目录，如 <c>/dav</c>）</summary>
    public string BasePath { get; set; } = "/";

    /// <summary>是否启用</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>是否使用 HTTPS</summary>
    public bool UseHttps { get; set; }

    /// <summary>WebDAV 服务器类型（0=标准, 1=OpenList/Alist；连接测试时自动探测回填）</summary>
    public int ServerType { get; set; }

    /// <summary>上次浏览到的目录（下次进入直接回到该目录）</summary>
    public string LastPath { get; set; } = "/";

    /// <summary>上次使用时间（列表按新在前排序）</summary>
    public DateTime LastUsedAt { get; set; }

    /// <summary>便捷方法：构建完整 Base URL（scheme://host[:port][/path]）</summary>
    public string GetBaseUrl()
    {
        var scheme = UseHttps ? "https" : "http";
        var path = BasePath.TrimEnd('/');
        if (!string.IsNullOrEmpty(path) && path != "/")
            return $"{scheme}://{Host}:{Port}{path}";
        return $"{scheme}://{Host}:{Port}";
    }
}

/// <summary>远程文件信息（对应猫爪音乐 <c>RemoteFile</c>）。</summary>
public class RemoteFile
{
    /// <summary>文件名</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>文件完整路径（以 / 开头的 WebDAV 路径）</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>是否为目录</summary>
    public bool IsDirectory { get; set; }

    /// <summary>文件大小（字节）</summary>
    public long Size { get; set; }

    /// <summary>最后修改时间（Unix 时间戳）</summary>
    public long LastModified { get; set; }
}
