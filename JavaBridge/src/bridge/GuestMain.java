package bridge;

import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.FileDescriptor;
import java.net.ServerSocket;
import java.net.Socket;

/**
 * QEMU guest（真 ART）里的桥入口：把原来 stdin/stdout 的行协议挂到 TCP 上，
 * 宿主经 slirp 端口转发连它，其余协议与 {@link Server} 完全一致。
 *
 * <p>为什么单独一个入口：宿主侧 {@code JavaSpiderRuntime} 一直是"起子进程 + 管道读写"，
 * 而 guest 里没有共享 stdin —— 但 TCP 已经在用了（Guard 解密服务 18481、迅雷控制口 18080），
 * 所以这是最小改动：Server 的循环一行不动，只是把 System.in/out 换成 socket 流。</p>
 */
public final class GuestMain {

    private GuestMain() { }

    public static void main(String[] args) throws Exception {
        int port = args.length > 0 ? Integer.parseInt(args[0].trim()) : 18581;
        String dataDir = System.getenv("CATCLAW_DATA");
        System.setProperty("data.dir", dataDir != null && !dataDir.isEmpty() ? dataDir : "/data/catclaw");
        new File(System.getProperty("data.dir")).mkdirs();
        // 让 Server.main 别把标准流抢回 FileDescriptor.out（那会断掉 socket 通道）
        System.setProperty("bridge.keepStdio", "1");
        if (Art.onArt()) {
            // 真机上 Zygote 会备好主线程 Looper；我们只有 libart，不备则壳的内层 dex（InitOrigin.<clinit>）NPE。
            // 代价：MessageQueue 由我们的 guest 垫片实现（epoll+eventfd），见 proppreload.c。
            android.os.Looper.prepareMainLooper();
            System.err.println("[guest] ART: 主线程 Looper = " + android.os.Looper.getMainLooper());
            // 注：壳的 InitOrigin.getActivity() 走 ActivityThread.currentActivityThread()，本进程不是
            // zygote 起的，那条反射链拿不到（试过 ActivityThread.systemMain()，ART 里直接抛
            // InvocationTargetException，2026-09-26）。→ 不再造伪 ActivityThread 类（boot 覆盖是死路），
            // 改为 Unsafe 造真类伪实例 + 填静态字段，把 currentApplication()/mInitialApplication 指到桥的
            // App 桩；mActivities 留空 —— 壳拿不到 Activity 就走它自己的无 Activity 降级（proxy/HTML）。
            Art.injectActivityThread();
        }
        // 强制走 IPv4 栈：guest 里只配了 v4 地址，双栈绑定会让 slirp 的 v4 目标连不上。
        // ⚠ slirp hostfwd 拨的是 guest 的 eth0 地址（10.0.2.15），**不是**它的回环 ——
        //   所以要给宿主够得着的端口必须绑通配，只绑 127.0.0.1 会被 RST（WinError 10054，2026-09-26 实测）。
        System.setProperty("java.net.preferIPv4Stack", "true");

        try (ServerSocket ss = new ServerSocket(port)) {
            System.out.println("[guest] bridge.GuestMain 监听 :" + port
                    + "  vm=" + System.getProperty("java.vm.name")
                    + " data.dir=" + System.getProperty("data.dir"));
            System.out.flush();
            while (true) {
                System.out.println("[guest] 等 accept…"); System.out.flush();
                Socket s = ss.accept();
                System.out.println("[guest] 收到连接 " + s.getRemoteSocketAddress()
                        + " 本地=" + s.getLocalAddress() + ":" + s.getLocalPort());
                System.out.flush();
                System.setIn(new BufferedInputStream(s.getInputStream()));
                java.io.PrintStream out = new java.io.PrintStream(
                        new BufferedOutputStream(s.getOutputStream()), false, "UTF-8");
                System.setOut(out);
                System.setErr(new java.io.PrintStream(
                        new FileOutputStream(FileDescriptor.err), true, "UTF-8"));
                try {
                    Server.main(new String[0]);          // 读到 EOF 或 op=exit 返回
                } catch (Throwable t) {
                    System.err.println("[guest] 会话异常: " + t);
                } finally {
                    out.flush();
                    try { s.close(); } catch (java.io.IOException ignored) { }
                }
            }
        }
    }
}
