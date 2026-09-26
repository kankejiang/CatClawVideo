package bridge;

import java.io.ByteArrayInputStream;
import java.io.File;
import java.net.URL;
import java.net.URLClassLoader;
import java.util.ArrayList;
import java.util.Base64;
import java.util.List;

/**
 * Guard 壳框架的桌面运行会话（桥进程内常驻）。
 *
 * <p><b>架构（2026-09-25 定案，覆盖 2026-09-24 的"双路"版）</b>：TVBox 客户端只提供 Activity，
 * 网盘管理对话框/扫码/凭据管理全部在 jar 的壳框架（{@code BaseSpiderGuard} + {@code DexNative}）里实现，
 * 所有 Guard 源共用、客户端与源作者均零适配。{@code DexNative} 是 ARM native，
 * **一律在 QEMU guest 里执行**（用性能换兼容性），桥这边只剩转发与注入：</p>
 * <ul>
 *   <li><b>解密/签名/计算</b>：{@code decrypt/encrypt/noxSign/native_ting_md5/calcResult/proxyInvoke}
 *       → {@link QemuGuardChannel} → Guard VM 里的 ftyguard so。</li>
 *   <li><b>真实类注入</b>：{@code getLoader} 不进 ARM ——直接返回桥预建的解壳 jar ClassLoader；
 *       {@code getSpider} 从中实例化真实类（去 Guard 后缀）。</li>
 *   <li><b>UI</b>：壳框架调 AlertDialog/Bitmap → 宿主桩（android.app.* 桥接版）→
 *       UiBridge 事件上行 → 宿主渲染。</li>
 * </ul>
 *
 * <p><b>unidbg 已经不是运行时的执行路径</b>：它自建一套 Android 桩环境，结果可以"看着对而语义不同"，
 * 而 guest/unidbg 两条通道在日志里只差一个词，会被静默换引擎骗过去（实测同一条
 * {@code DECRYPT 10232B} 两种都出现过）。unidbg 现在只剩离线解壳器 {@code bridge.GuardUnpacker}
 * （{@code vendor/unidbg/unpacker.jar}，由宿主单独起进程跑），那是"把壳卸掉"的离线工具，
 * 不参与播放路径。</p>
 */
public final class GuardSession {

    /** 解壳 jar（真实类）的 ClassLoader——由宿主经 op 注入。 */
    private static volatile URLClassLoader realLoader;
    /** 解壳 jar 里的类名映射（MyDriveGuard → MyDrive），C# 侧下发。 */
    private static final java.util.Map<String, String> REAL_NAMES = new java.util.concurrent.ConcurrentHashMap<>();

    private GuardSession() { }

    // ═══════════ DexNative 替身调用的实现（全部转发进 guest）═══════════

    /** 每次 ARM native 调用留一行「谁调的、多长、由哪条通道服务」，用来判定到底是谁在服务。 */
    private static void trace(String api, int inLen, String via, int outLen) {
        System.err.println("[guard] " + api + " " + inLen + "B → " + via + " " + outLen + "B");
    }

    private static int len(String s) { return s == null ? 0 : s.length(); }

    /** guest 是唯一 ARM 执行器：通道没启用就没有可执行的实现，直接报，不猜。 */
    private static void requireGuest(String api) {
        if (!QemuGuardChannel.enabled())
            throw new IllegalStateException("ARM native " + api + " 只能由 QEMU guest 执行，但 Guard 解密服务端口未启用"
                    + "（Guard VM 没起来或 rawJar 缺失）");
    }

    /** guest 调用失败就地抛 —— 运行时已经没有第二个 ARM 引擎可换。 */
    private static IllegalStateException guestFailed(String api, Throwable t) {
        return new IllegalStateException("ARM native " + api + " 在 QEMU guest 里执行失败: " + t, t);
    }

    /** 单参 guest 调用（DECRYPT/ENCRYPT/MD5）。 */
    private static String qemuOne(String op, String s) {
        requireGuest(op);
        try {
            String resp = QemuGuardChannel.call(op + " " + QemuGuardChannel.b64(s), 60);
            if (!resp.startsWith("OK ")) throw new IllegalStateException(resp);
            String out = QemuGuardChannel.unb64(resp.substring(3).trim());
            trace(op, len(s), "QEMU", len(out));
            return out;
        } catch (Throwable t) {
            throw guestFailed(op, t);
        }
    }

    public static String decrypt(String s) { return qemuOne("DECRYPT", s); }

    public static String encrypt(String s) { return qemuOne("ENCRYPT", s); }

    public static String md5(String s) { return qemuOne("MD5", s); }

    public static String noxSign(String a, String b, String c) {
        requireGuest("NOXSIGN");
        try {
            String resp = QemuGuardChannel.call("NOXSIGN " + QemuGuardChannel.b64(a) + " "
                    + QemuGuardChannel.b64(b) + " " + QemuGuardChannel.b64(c), 60);
            if (!resp.startsWith("OK ")) throw new IllegalStateException(resp);
            String out = QemuGuardChannel.unb64(resp.substring(3).trim());
            trace("NOXSIGN", len(a), "QEMU", len(out));
            return out;
        } catch (Throwable t) {
            throw guestFailed("NOXSIGN", t);
        }
    }

    public static int[] calcResult(int[] input) {
        requireGuest("CALC");
        try {
            java.nio.ByteBuffer buf = java.nio.ByteBuffer.allocate(4 * (input == null ? 0 : input.length));
            if (input != null) for (int v : input) buf.putInt(v);
            String resp = QemuGuardChannel.call("CALC " + Base64.getEncoder().encodeToString(buf.array()), 60);
            if (!resp.startsWith("OK ")) throw new IllegalStateException(resp);
            byte[] out = Base64.getDecoder().decode(resp.substring(3).trim());
            java.nio.ByteBuffer rb = java.nio.ByteBuffer.wrap(out);
            int[] r = new int[out.length / 4];
            for (int i = 0; i < r.length; i++) r[i] = rb.getInt();
            trace("CALC", input == null ? 0 : input.length * 4, "QEMU", out.length);
            return r;
        } catch (Throwable t) {
            throw guestFailed("CALC", t);
        }
    }

    /** getLoader：返回预注入的解壳 jar ClassLoader（壳框架拿它 loadClass 真实类）。 */
    public static Object getLoader(Object ctx) {
        return realLoader;
    }

    /** getSpider：从解壳 loader 实例化真实类（壳框架传全限定壳名 → 短名映射/去 Guard 后缀）。 */
    public static Object getSpider(Object loaderObj, String name) {
        try {
            String simple = name.substring(name.lastIndexOf('.') + 1);   // 兼容全限定/短名
            String mapped = REAL_NAMES.getOrDefault(simple, simple);
            String noGuard = mapped.endsWith("Guard") ? mapped.substring(0, mapped.length() - "Guard".length()) : mapped;
            // 优先壳框架传入的 loader，但壳内部自建的 loader（Android 语义）在桌面不可用 →
            // 一律兜底用 realLoader（解壳产物，真实类所在）
            for (ClassLoader cl : new ClassLoader[]{
                    loaderObj instanceof ClassLoader c ? c : null, realLoader}) {
                if (cl == null) continue;
                for (String candidate : new String[]{mapped, noGuard}) {
                    String fq = candidate.contains(".") ? candidate : "com.github.catvod.spider." + candidate;
                    try {
                        Class<?> cls = cl.loadClass(fq);
                        Object inst = cls.getDeclaredConstructor().newInstance();
                        System.err.println("[guard] getSpider: " + name + " → " + fq + "（"
                                + (cl == realLoader ? "realLoader" : "caller-loader") + "）");
                        return inst;
                    } catch (ClassNotFoundException ignored) { }
                }
            }
            System.err.println("[guard] getSpider 找不到真实类: " + name
                    + "（realLoader=" + (realLoader != null ? "ok" : "null") + "）");
            return null;
        } catch (Throwable t) {
            System.err.println("[guard] getSpider 异常: " + t);
            return null;
        }
    }

    /**
     * proxyInvoke：壳框架的 proxy 分发入口（playerContent 内部调 DexNative.proxyInvoke(spider, param)）。
     * <b>QEMU 优先</b>（2026-09-24 用户拍板）：转发到 Guard VM 里的 so proxyInvoke（ARM 原生，
     * 与 TVBox 真机行为一致——「已登录+启用中」对话框/扫码由它弹出，UI 经 harness 上行宿主渲染）；
     * 通道不可用或 so 无响应体时回落真实类的平台分发（Cloud_&lt;平台&gt;.proxy(Map) →
     * 兜底 Pan.proxyInput() 配置页）。返回 Object[]{status, mime, InputStream}。
     */
    public static Object[] proxyInvoke(Object a, Object b) {
        System.err.println("[guard] proxyInvoke a=" + (a == null ? "null" : a.getClass().getName())
                + " b=" + (b == null ? "null" : b.getClass().getName()));
        if (QemuGuardChannel.enabled() && b instanceof java.util.Map) {
            try {
                StringBuilder req = new StringBuilder("PROXY ");
                // prefs 快照随行：so 读 SharedPreferences 判断登录态（夸克/UC Cookie 等）
                List<String> pk = new ArrayList<>(), pv = new ArrayList<>();
                for (var e : android.content.PrefsStore.snapshot().entrySet()) {
                    String vs = e.getValue() == null ? "" : e.getValue().toString();
                    if (!vs.isEmpty()) { pk.add(e.getKey()); pv.add(vs); }
                }
                req.append(pk.size());
                for (int i = 0; i < pk.size(); i++)
                    req.append(' ').append(QemuGuardChannel.b64(pk.get(i)))
                       .append(' ').append(QemuGuardChannel.b64(pv.get(i)));
                java.util.Map<?, ?> m = (java.util.Map<?, ?>) b;
                List<String> mk = new ArrayList<>(), mv = new ArrayList<>();
                for (var e : m.entrySet()) {
                    mk.add(String.valueOf(e.getKey()));
                    mv.add(e.getValue() == null ? "" : String.valueOf(e.getValue()));
                }
                req.append(' ').append(mk.size());
                for (int i = 0; i < mk.size(); i++)
                    req.append(' ').append(QemuGuardChannel.b64(mk.get(i)))
                       .append(' ').append(QemuGuardChannel.b64(mv.get(i)));
                String resp = QemuGuardChannel.call(req.toString(), 120);
                if (resp.startsWith("OK3 ")) {
                    String[] parts = resp.split(" ");
                    int status = Integer.parseInt(parts[1]);
                    String mime = QemuGuardChannel.unb64(parts[2]);
                    if (!"-".equals(parts[3])) {
                        byte[] body = Base64.getDecoder().decode(parts[3]);
                        System.err.println("[guard] proxyInvoke → QEMU so（status=" + status
                                + " mime=" + mime + " body=" + body.length + "B）");
                        return new Object[]{status, mime, new ByteArrayInputStream(body)};
                    }
                    System.err.println("[guard] proxyInvoke → QEMU so 无响应体（可能仅弹窗），回落兜底");
                } else {
                    System.err.println("[guard] proxyInvoke QEMU 失败: " + resp);
                }
            } catch (Throwable t) {
                System.err.println("[guard] proxyInvoke QEMU 通道异常，回落兜底: " + t);
            }
        }
        if (!(b instanceof java.util.Map)) return null;
        java.util.Map<?, ?> m = (java.util.Map<?, ?>) b;
        // so 的 proxyInvoke 本质 = loadClass(ProxyOrigin) + 静态 proxy(Map) 转发（QEMU 反汇编实测），
        // 真正逻辑（含「已登录+启用中」弹窗）在解壳 jar 的 Java 里——桥端等价执行，弹窗经
        // 桥的 Dialog 桩 → UiBridge 上行宿主渲染（与真机行为一致）。
        if (realLoader != null) {
            try {
                Class<?> po = realLoader.loadClass("com.github.catvod.spider.ProxyOrigin");
                Object r = po.getMethod("proxy", java.util.Map.class).invoke(null, b);
                if (r instanceof Object[] arr) { System.err.println("[guard] proxyInvoke → ProxyOrigin.proxy"); return arr; }
            } catch (Throwable t) {
                System.err.println("[guard] proxyInvoke → ProxyOrigin 失败: " + why(t));
            }
        }
        Object doVal = m.get("do");
        if (realLoader != null && doVal instanceof String dv && dv.matches("[a-zA-Z_0-9]+")) {
            try {
                Class<?> clz = realLoader.loadClass("com.github.catvod.spider.Cloud_" + dv);
                java.lang.reflect.Method mm = clz.getMethod("proxy", java.util.Map.class);
                Object r = mm.invoke(null, b);
                if (r instanceof Object[] arr) { System.err.println("[guard] proxyInvoke → Cloud_" + dv); return arr; }
            } catch (Throwable t) {
                System.err.println("[guard] proxyInvoke → Cloud_" + dv + " 失败: " + why(t));
            }
        }
        // 兜底：Pan.proxyInput() 配置推送页 —— 只对「配置/输入」族生效。
        // 必须收窄：它对任何 do 都返回非空 HTML，不加闸的话宿主就分不清
        // 「爬虫不接这个 do」和「爬虫接了」，取流类请求会被整张 Cookie 页吞掉
        // （2026-09-24 排查网盘扫码时确认）。
        String doStr = doVal instanceof String s0 ? s0 : "";
        if (doStr.equalsIgnoreCase("config") || doStr.equalsIgnoreCase("input")) {
            try {
                Class<?> pan = realLoader.loadClass("com.github.catvod.spider.Pan");
                Object r = pan.getMethod("proxyInput").invoke(null);
                if (r instanceof Object[] arr) { System.err.println("[guard] proxyInvoke → Pan.proxyInput"); return arr; }
            } catch (Throwable ignored) { }
        }
        return null;
    }

    /**
     * 反射调用失败的可读根因。
     * <para>{@code InvocationTargetException} 的 toString 只有类名、不含 cause，直接打它等于没打
     * （2026-09-24 排查 {@code Cloud_quark} 时踩到）——剥到最底层 cause 并带栈顶几帧。</para>
     */
    private static String why(Throwable t) {
        Throwable c = t;
        while (c.getCause() != null) c = c.getCause();
        StringBuilder sb = new StringBuilder(c.getClass().getSimpleName())
                .append(": ").append(c.getMessage());
        StackTraceElement[] st = c.getStackTrace();
        for (int i = 0; i < Math.min(5, st.length); i++) sb.append("\n      at ").append(st[i]);
        return sb.toString();
    }

    // ═══════════ 宿主注入 ═══════════

    /** 宿主下发解壳 jar 路径与类名映射（load 壳类时调用）。返回注入用的 realLoader。 */
    public static ClassLoader setRealLoader(String realJarPath, String nameMappingJson) throws Exception {
        URLClassLoader existing = realLoader;
        String key = realJarPath + "#" + new File(realJarPath).lastModified();
        if (existing != null && key.equals(existing instanceof GuardLoader gl ? gl.key() : "")) return existing;

        URL[] urls = {new File(realJarPath).toURI().toURL()};
        realLoader = new GuardLoader(urls, GuardSession.class.getClassLoader(), key);
        if (nameMappingJson != null) {
            try {
                org.json.JSONObject o = new org.json.JSONObject(nameMappingJson);
                for (java.util.Iterator<String> it = o.keys(); it.hasNext(); ) {
                    String k = it.next();
                    REAL_NAMES.put(k, o.optString(k));
                }
            } catch (Throwable ignored) { }
        }
        System.err.println("[guard] 已注入解壳 loader: " + realJarPath);
        return realLoader;
    }

    /** 带标识的 loader（避免重复创建）。 */
    private static final class GuardLoader extends bridge.SleepPatchingLoader {
        private final String key;
        GuardLoader(URL[] urls, ClassLoader parent, String key) {
            super(urls, parent);
            this.key = key;
        }
        String key() { return key; }
    }

}
