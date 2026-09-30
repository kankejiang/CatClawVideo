package bridge;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.InputStream;
import java.net.InetAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.net.URLDecoder;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.Enumeration;
import java.util.List;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;

/**
 * Guard 资源控制口（guest 内 18090）：给本机 harness 下发 GLOAD 并供其 so/dex 条目。
 *
 * <p><b>为什么在 guest 内</b>：spider 运行时整体进 QEMU guest 后，Guard 解密走 guest 内
 * 原生 harness（ARM64 SO 原生执行，无 QEMU 通道/无宿主 Guard VM 依赖）。harness 启动后
 * 每秒 {@code GET /task} 拉命令、按需 {@code GET /res?jar=&name=} 取 raw jar 条目、
 * {@code GET /report} 回报——与宿主 QemuControlServer 的协议逐字节一致。</p>
 *
 * <p>GLOAD 流程：首个 /task 轮询时下发 {@code GLOAD <hash>}（hash = /spider/converted/
 * raw-*.jar 的 SHA256 前 24 位）→ harness 经 /res 取 __so__（aarch64 so + DT_NEEDED
 * 补丁）与其余条目 → so 加载 → DECRYPT/ENCRYPT 服务在本机 18481 就绪 → 桥的
 * QemuGuardChannel（catclaw.guard.host=127.0.0.1）直连。</p>
 */
public final class GuardCtrl {

    private static final String CONVERTED_DIR = "/spider/converted";
    private static volatile boolean gloadSent;

    private GuardCtrl() { }

    /** 启动控制口监听（guest 内 0.0.0.0:18090；失败不影响桥主流程）。 */
    public static void start() {
        Thread t = new Thread(() -> {
            try (ServerSocket ss = new ServerSocket(18090, 64, InetAddress.getByName("0.0.0.0"))) {
                System.err.println("[guardctrl] 18090 就绪（GLOAD + 资源服务）");
                while (true) {
                    Socket s = ss.accept();
                    handle(s);
                }
            } catch (Throwable th1) {
                System.err.println("[guardctrl] 退出: " + th1);
            }
        }, "guard-ctrl");
        t.setDaemon(true);
        t.start();
    }

    private static void handle(Socket s) {
        try (s) {
            s.setSoTimeout(15000);
            byte[] buf = new byte[8192];
            int len = 0, n;
            while (len < buf.length && (n = s.getInputStream().read(buf, len, buf.length - len)) > 0) {
                len += n;
                if (new String(buf, 0, len, StandardCharsets.ISO_8859_1).contains("\r\n\r\n")) break;
            }
            String head = new String(buf, 0, len, StandardCharsets.ISO_8859_1);
            int sp1 = head.indexOf(' ');
            int sp2 = head.indexOf(' ', sp1 + 1);
            if (sp1 < 0 || sp2 < 0) return;
            String target = head.substring(sp1 + 1, sp2);
            int q = target.indexOf('?');
            String path = q < 0 ? target : target.substring(0, q);
            String query = q < 0 ? "" : target.substring(q + 1);

            switch (path) {
                case "/task":
                    respond(s, "200 OK", gloadCommand());
                    return;
                case "/res":
                    String jar = get(query, "jar");
                    String name = get(query, "name");
                    byte[] data = resolve(jar, name);
                    if (data == null) {
                        System.err.println("[guardctrl] /res 未命中: " + jar + "/" + name);
                        respond(s, "404 Not Found", "no entry");
                    } else {
                        System.err.println("[guardctrl] /res 命中: " + name + "（" + data.length / 1024 + "KB）");
                        respondBytes(s, data);
                    }
                    return;
                case "/report":
                    respond(s, "200 OK", "ok");
                    return;
                default:
                    respond(s, "404 Not Found", "?");
            }
        } catch (Throwable th2) {
            // 单连接异常不影响服务
        }
    }

    /** GLOAD 命令：首次轮询下发一次（hash = raw jar 的 SHA256 前 24 位）。 */
    private static String gloadCommand() {
        if (gloadSent) return "NONE";
        File jar = findRawJar();
        if (jar == null) return "NONE";
        try {
            byte[] bytes = java.nio.file.Files.readAllBytes(jar.toPath());
            byte[] dig = MessageDigest.getInstance("SHA-256").digest(bytes);
            StringBuilder hex = new StringBuilder();
            for (byte b : dig) hex.append(String.format("%02x", b));
            String hash = hex.substring(0, 24);
            gloadSent = true;
            System.err.println("[guardctrl] 下发 GLOAD " + hash + "（" + bytes.length / 1024 + "KB）");
            return "GLOAD " + hash;
        } catch (Throwable t) {
            System.err.println("[guardctrl] GLOAD 组装失败: " + t);
            return "NONE";
        }
    }

    private static File findRawJar() {
        File dir = new File(CONVERTED_DIR);
        File[] files = dir.listFiles((d, n) -> n.startsWith("raw-") && n.endsWith(".jar"));
        if (files == null || files.length == 0) return null;
        return files[0];
    }

    /** 与宿主 QemuGuardEngine.ResolveResource 同语义：__so__ = aarch64 so（DT_NEEDED 补丁），其余 zip 条目名。 */
    private static byte[] resolve(String jarHash, String name) throws Exception {
        File jar = findRawJar();
        if (jar == null) return null;
        try (ZipFile zf = new ZipFile(jar)) {
            if ("__so__".equals(name)) {
                for (Enumeration<? extends ZipEntry> e = zf.entries(); e.hasMoreElements(); ) {
                    ZipEntry ze = e.nextElement();
                    if (!ze.getName().endsWith(".so")) continue;
                    byte[] bytes = read(zf.getInputStream(ze));
                    // ELF e_machine 偏移 18-19 小端 = 0xB7（aarch64）
                    if (bytes.length > 20 && bytes[0] == 0x7F && bytes[1] == 'E'
                            && bytes[2] == 'L' && bytes[3] == 'F'
                            && ((bytes[18] & 0xFF) | (bytes[19] << 8)) == 0xB7) {
                        return patchGuardSo(bytes);
                    }
                }
                return null;
            }
            for (String cand : new String[]{name, "assets/" + name}) {
                ZipEntry ze = zf.getEntry(cand);
                if (ze != null) return read(zf.getInputStream(ze));
            }
            for (Enumeration<? extends ZipEntry> e = zf.entries(); e.hasMoreElements(); ) {
                ZipEntry ze = e.nextElement();
                if (ze.getName().endsWith(name)) return read(zf.getInputStream(ze));
            }
            return null;
        }
    }

    /** DT_NEEDED 重定向：libandroid.so → libxl_stat.so（等长替换，符号由 base initramfs 的引擎库提供）。 */
    private static byte[] patchGuardSo(byte[] so) {
        byte[] from = "libandroid.so\0".getBytes(StandardCharsets.ISO_8859_1);
        byte[] to = "libxl_stat.so\0".getBytes(StandardCharsets.ISO_8859_1);
        byte[] out = so.clone();
        int count = 0, i;
        List<Integer> hits = new ArrayList<>();
        while ((i = indexOf(out, from)) >= 0) {
            System.arraycopy(to, 0, out, i, to.length);
            count++;
            if (count > 64) break;
            i += to.length;
            byte[] rest = new byte[out.length - i];
            System.arraycopy(out, i, rest, 0, rest.length);
            int next = indexOf(rest, from);
            if (next < 0) break;
            hits.add(i + next);
            i = 0;
        }
        System.err.println("[guardctrl] __so__ DT_NEEDED 补丁 " + count + " 处");
        return out;
    }

    private static int indexOf(byte[] hay, byte[] needle) {
        for (int i = 0; i + needle.length <= hay.length; i++) {
            boolean ok = true;
            for (int j = 0; j < needle.length; j++) {
                if (hay[i + j] != needle[j]) { ok = false; break; }
            }
            if (ok) return i;
        }
        return -1;
    }

    private static byte[] read(InputStream in) throws Exception {
        ByteArrayOutputStream bos = new ByteArrayOutputStream();
        byte[] buf = new byte[16384];
        int n;
        while ((n = in.read(buf)) > 0) bos.write(buf, 0, n);
        in.close();
        return bos.toByteArray();
    }

    private static void respond(Socket s, String status, String body) throws Exception {
        byte[] b = body.getBytes(StandardCharsets.UTF_8);
        String head = "HTTP/1.0 " + status + "\r\nContent-Length: " + b.length
                + "\r\nConnection: close\r\n\r\n";
        s.getOutputStream().write(head.getBytes(StandardCharsets.ISO_8859_1));
        s.getOutputStream().write(b);
        s.getOutputStream().flush();
    }

    private static void respondBytes(Socket s, byte[] b) throws Exception {
        String head = "HTTP/1.0 200 OK\r\nContent-Length: " + b.length
                + "\r\nConnection: close\r\n\r\n";
        s.getOutputStream().write(head.getBytes(StandardCharsets.ISO_8859_1));
        s.getOutputStream().write(b);
        s.getOutputStream().flush();
    }

    private static String get(String query, String key) {
        for (String kv : query.split("&")) {
            int eq = kv.indexOf('=');
            if (eq > 0 && kv.substring(0, eq).equals(key)) {
                try { return URLDecoder.decode(kv.substring(eq + 1), "UTF-8"); }
                catch (Exception e) { return kv.substring(eq + 1); }
            }
        }
        return "";
    }
}
