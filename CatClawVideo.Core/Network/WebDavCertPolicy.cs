using System.Net.Security;
using CatClawVideo.Core.Logging;

namespace CatClawVideo.Core.Network;

/// <summary>
/// 统一 TLS 证书策略（全局开关 + 回调工厂）。
/// NAS / Alist 场景大量自签证书，默认信任全部证书保可用；开关留给设置页以后接严格校验。
/// （移植自猫爪音乐 <c>WebDavCertPolicy</c>，网络模块内所有 HttpClient 的证书策略统一走此入口。）
/// </summary>
public static class WebDavCertPolicy
{
    /// <summary>
    /// 是否信任所有 TLS 证书（默认 true，保持局域网/自签 NAS 的可用性）。
    /// </summary>
    public static bool TrustAllCertificates { get; set; } = true;

    /// <summary>
    /// 创建统一的 TLS 证书校验回调：有效证书直接通过；无效证书按 <see cref="TrustAllCertificates"/>
    /// 决定接受（记录中间人风险告警）或拒绝（严格模式）。
    /// </summary>
    /// <param name="host">服务器主机名，仅用于日志定位。</param>
    public static RemoteCertificateValidationCallback CreateCertValidationCallback(string host)
    {
        return (_, _, _, sslErrors) =>
        {
            if (sslErrors == SslPolicyErrors.None)
                return true;
            if (!TrustAllCertificates)
            {
                Log.Warn("WebDavCertPolicy", $"[WebDAV] 严格校验：拒绝服务器 {host} 的无效 TLS 证书（{sslErrors}）。");
                return false;
            }
            Log.Warn("WebDavCertPolicy",
                $"[WebDAV] 已接受服务器 {host} 的无效 TLS 证书（{sslErrors}），存在中间人攻击风险。" +
                $"建议配置可信证书。");
            return true;
        };
    }
}
