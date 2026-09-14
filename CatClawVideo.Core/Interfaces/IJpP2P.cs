namespace CatClawVideo.Core.Interfaces;

/// <summary>
/// 荐片（csp_Jianpian）宿主侧 P2P 支持。
///
/// <para><b>为什么需要</b>：荐片爬虫自己不下载。它的 <c>playerContent</c> 会逐个探测
/// <c>127.0.0.1:9978…9999</c> 寻找宿主提供的本地 P2P HTTP 服务；探不到就拼出端口位为空的地址，
/// 播放器随即报 <c>Source error</c>（底层 <c>MalformedURLException: invalid port: -1</c>）。
/// 2026-09-14 真机实测：该源列表/详情/线路全部正常，只有点播必炸，换线路也一样。</para>
///
/// <para><b>平台实现</b>：Android 由 <c>libp2p.so</c> + <c>com.p2p.P2PClass</c> 提供
/// （见 <c>Platforms/Android/JianpianP2P.cs</c> + <c>Platforms/Android/com/catclaw/video/JpP2P.java</c>）；
/// Windows 暂无实现 → <see cref="Current"/> 为 null，走原逻辑。</para>
/// </summary>
public interface IJpP2P
{
    /// <summary>本地 P2P httpd 是否已就绪（未就绪时不要调用 <see cref="Decode"/>）</summary>
    bool IsReady { get; }

    /// <summary>
    /// 确保本地 httpd 已启动并返回是否可用。幂等、只真正尝试一次（失败不会反复重试）。
    /// <para>爬虫在 <c>playerContent</c> 里就会探测该服务，所以**必须在解析前 await 一次**：
    /// 探不到即失败，与返回的播放地址形态无关。</para>
    /// </summary>
    Task<bool> EnsureReadyAsync();

    /// <summary>是否为荐片私有地址（<c>tvbox-xg:</c> 前缀，或含 <c>gbl.114s</c> 的 ftp 地址）</summary>
    bool IsJpUrl(string url);

    /// <summary>
    /// 解码荐片地址并投递给本地 P2P 引擎，返回可播的本地 http 地址。
    /// 失败返回 null（调用方保留原地址并给出可读提示）。
    /// </summary>
    string? Decode(string url);

    /// <summary>停播时释放当前 P2P 任务。</summary>
    void Finish();
}

/// <summary>
/// <see cref="IJpP2P"/> 的静态注册点。
/// <para>用静态注册而不是 DI：各 Provider 在 Core 内被 <c>new</c> 出来，拿不到 Maui 的
/// ServiceProvider；且只有 Android 有实现，“未注册 = 不可用”这个语义最省事。</para>
/// </summary>
public static class JpP2PSupport
{
    /// <summary>当前生效的实现（平台在启动时写入；未注册为 null）</summary>
    public static IJpP2P? Current { get; set; }
}
