package com.catclaw.video;

import android.net.Uri;
import android.text.TextUtils;

import com.p2p.P2PClass;

import java.io.UnsupportedEncodingException;
import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.NetworkInterface;
import java.net.URLDecoder;
import java.net.URLEncoder;
import java.util.Collections;
import java.util.Enumeration;

/**
 * 荐片（csp_Jianpian）宿主侧桥接。
 *
 * <p>背景：荐片爬虫自己不做下载——它需要宿主提供一个本地 P2P HTTP 服务，
 * 播放地址形如 {@code http://<本机IP>:<P2PClass.port>/<GBK 编码的文件名>}。
 * 缺这个服务时 spider 内部 adjustPort 逐个探测 127.0.0.1:9978…9999 全部 ECONNREFUSED，
 * 最终拼出空端口地址 → 播放器报 Source error / MalformedURLException: invalid port: -1。</p>
 *
 * <p>本类逐语义对齐 TVBox 的
 * {@code com.github.tvbox.osc.util.thunder.Jianpian} + {@code util.LocalIPAddress}，
 * 保留 JVM 内的 GBK / URLDecoder / Uri 语义（在 C# 侧重实现容易在编码上走样）。</p>
 *
 * <p>许可说明：{@code libp2p.so} 与 {@code com.p2p.P2PClass} 取自 TVBox（AGPL-3.0），
 * P2P 引擎为荐片厂商闭源二进制。用户已知悉并选择随包分发。</p>
 */
public final class JpP2P {

    /** 荐片 P2P 引擎单例（构造即启动本地 httpd）。 */
    private static P2PClass sP2p;

    /** 当前正在播放的 ftp 地址（停播时要 P2Pdoxpause + P2Pdoxdel）。 */
    private static String sBurl = "";

    private JpP2P() {
    }

    /**
     * 启动本地 P2P httpd（幂等）。会阻塞到 httpd 就绪，请在后台线程调用。
     *
     * @param cachePath 引擎数据目录（会在其下建 jpali/）
     * @return 实际监听端口；失败返回 -1
     */
    public static synchronized int start(String cachePath) {
        try {
            if (sP2p == null) {
                sP2p = new P2PClass(cachePath);
            }
            return P2PClass.port;
        } catch (Throwable t) {
            return -1;
        }
    }

    /** httpd 是否已启动。 */
    public static synchronized boolean isReady() {
        return sP2p != null && P2PClass.port > 0;
    }

    /** 对齐 TVBox {@code Jianpian.isJpUrl}：识别荐片私有地址。 */
    public static boolean isJpUrl(String url) {
        if (TextUtils.isEmpty(url)) {
            return false;
        }
        return url.startsWith("tvbox-xg:")
                || (url.toLowerCase().startsWith("ftp://") && url.contains("gbl.114s"));
    }

    /**
     * 对齐 TVBox {@code Jianpian.JPUrlDec}：解码荐片地址并投递给本地 P2P 引擎。
     *
     * @return 可播的本地 http 地址；失败返回空串
     */
    public static synchronized String decode(String url) {
        if (sP2p == null) {
            return "";
        }
        try {
            String decode = URLDecoder.decode(url, "UTF-8");
            String[] split = decode.split("\\|");
            String replace = split[0].replace("xg://", "ftp://");
            if (replace.contains("xgplay://")) {
                replace = split[0].replace("xgplay://", "ftp://");
            }
            if (!TextUtils.isEmpty(sBurl)) {
                sP2p.P2Pdoxpause(sBurl.getBytes("GBK"));
                sP2p.P2Pdoxdel(sBurl.getBytes("GBK"));
            }
            sBurl = replace;
            sP2p.P2Pdoxstart(replace.getBytes("GBK"));
            sP2p.P2Pdoxadd(replace.getBytes("GBK"));
            return "http://" + getLocalIp() + ":" + P2PClass.port + "/"
                    + URLEncoder.encode(Uri.parse(replace).getLastPathSegment(), "GBK");
        } catch (Exception e) {
            return e.getLocalizedMessage() == null ? "" : e.getLocalizedMessage();
        }
    }

    /** 对齐 TVBox {@code Jianpian.finish}：停播时释放 P2P 任务。 */
    public static synchronized void finish() {
        if (!TextUtils.isEmpty(sBurl) && sP2p != null) {
            try {
                sP2p.P2Pdoxpause(sBurl.getBytes("GBK"));
                sP2p.P2Pdoxdel(sBurl.getBytes("GBK"));
            } catch (UnsupportedEncodingException ignored) {
            }
            sBurl = "";
        }
    }

    /**
     * 对齐 TVBox {@code LocalIPAddress.getIP} 的网卡枚举分支。
     * 这里不读 WifiManager（避免额外权限/系统服务依赖），直接枚举 eth0/wlan0，
     * 找不到非回环 IPv4 时退回 127.0.0.1（本地 httpd 在 127.0.0.1 同样可达）。
     */
    private static String getLocalIp() {
        try {
            Enumeration<NetworkInterface> nis = NetworkInterface.getNetworkInterfaces();
            for (NetworkInterface ni : Collections.list(nis)) {
                String name = ni.getDisplayName();
                if (!"eth0".equals(name) && !"wlan0".equals(name)) {
                    continue;
                }
                for (InetAddress addr : Collections.list(ni.getInetAddresses())) {
                    if (!addr.isLoopbackAddress() && addr instanceof Inet4Address) {
                        return addr.getHostAddress();
                    }
                }
            }
        } catch (Exception ignored) {
        }
        return "127.0.0.1";
    }
}
