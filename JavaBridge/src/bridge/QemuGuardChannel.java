package bridge;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.util.Base64;

/**
 * Guard QEMU 解密通道（2026-09-24 用户拍板架构）：把 DexNative 的解密/签名/proxyInvoke
 * 转发到独立 Guard VM（QEMU）里的 ftyguard so。
 *
 * <p>宿主（C#）在 Guard 源加载时启动 Guard VM 并经 load op 下发解密服务端口（本类静态持有）。
 * guest 内 harness 监听 <c>0.0.0.0:&lt;port&gt;</c>，宿主 hostfwd 把 127.0.0.1:同号 直通 guest ——
 * 本类就是那个 TCP 客户端。so 弹的对话框/二维码由 harness 经控制口上行宿主渲染，不经本类。</p>
 *
 * <p>行式协议（一条请求一个短连接，串行天然安全）：
 * <pre>
 * → DECRYPT &lt;b64&gt;                              ← OK &lt;b64&gt; / ERR &lt;msg&gt;
 * → PROXY &lt;np&gt; k v ... &lt;nm&gt; k v ...（全 b64）   ← OK3 &lt;status&gt; &lt;mimeB64&gt; &lt;bodyB64|-&gt;
 * </pre></p>
 */
public final class QemuGuardChannel {

    private static volatile int port = 0;

    private QemuGuardChannel() { }

    /** 宿主 load op 下发的解密服务端口（0 = 未启用 → GuardSession 走 unidbg 会话）。 */
    public static void setPort(int p) {
        port = p;
        System.err.println("[guard] QEMU 解密通道端口 = " + p);
    }

    public static boolean enabled() {
        return port > 0;
    }

    /** 单次调用（短连接）。失败抛 IOException —— 调用方回落 unidbg。 */
    public static String call(String line, int timeoutSec) throws IOException {
        // 连接目标可覆盖：spider 运行时整体进 QEMU guest 后，桥在 guest 里连宿主的
    // Guard VM 要走 slirp 网关 10.0.2.2（-Dcatclaw.guard.host 覆盖，默认本机）。
    String guardHost = System.getProperty("catclaw.guard.host", "127.0.0.1");
    try (Socket s = new Socket(guardHost, port)) {
            s.setSoTimeout(timeoutSec * 1000);
            OutputStream os = s.getOutputStream();
            os.write((line + "\n").getBytes(StandardCharsets.UTF_8));
            os.flush();
            BufferedReader in = new BufferedReader(
                    new InputStreamReader(s.getInputStream(), StandardCharsets.UTF_8));
            String resp = in.readLine();
            if (resp == null) throw new IOException("guard 服务空响应");
            return resp.trim();
        }
    }

    public static String b64(String s) {
        // ⚠ 空值必须占位：空串 b64 = 空 token，会把行式协议的配对挤错位
        //   （2026-09-24 实测：act="" 挤掉 do=danmu 的配对 → so 查不到 do）
        if (s == null || s.isEmpty()) return ".";
        return Base64.getEncoder().encodeToString(s.getBytes(StandardCharsets.UTF_8));
    }

    public static String unb64(String s) {
        if (s == null || s.isEmpty() || ".".equals(s)) return "";
        return new String(Base64.getDecoder().decode(s), StandardCharsets.UTF_8);
    }
}
