package android.net.wifi;

/**
 * <code>android.net.wifi.WifiInfo</code> 桩。
 *
 * <p>爬虫（如 TVBox 系的 <code>ProxyOrigin.getInternalIP()</code>）会
 * <code>WifiManager.getConnectionInfo().getIpAddress()</code> 取本机 IPv4，
 * 再拼本地代理地址 <code>http://&lt;ip&gt;:&lt;port&gt;/proxy?...</code>。
 * 之前这里是空类 → 调用时抛 <code>NoSuchMethodError</code> → 本机 IP 取不到。</p>
 *
 * <p>返回 <b>127.0.0.1</b>（Android 用 little-endian 的 int 表示 IPv4）：
 * 本宿主的代理只监听回环，返回局域网 IP 会让爬虫拼出连不上的地址。</p>
 */
public class WifiInfo {

    /** 127.0.0.1 的 little-endian int（0x7F000001 → 0x0100007F）。 */
    private static final int LOOPBACK_IP = 0x0100007F;

    public int getIpAddress() { return LOOPBACK_IP; }

    public String getSSID() { return "\"catclaw\""; }

    public String getBSSID() { return "02:00:00:00:00:00"; }

    public String getMacAddress() { return "02:00:00:00:00:00"; }

    public int getLinkSpeed() { return 100; }

    public int getRssi() { return -50; }

    public int getNetworkId() { return 0; }
}
