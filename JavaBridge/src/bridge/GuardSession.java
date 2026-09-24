package bridge;

import com.github.unidbg.AndroidEmulator;
import com.github.unidbg.Module;
import com.github.unidbg.linux.android.AndroidEmulatorBuilder;
import com.github.unidbg.linux.android.AndroidResolver;
import com.github.unidbg.linux.android.dvm.DalvikModule;
import com.github.unidbg.linux.android.dvm.DvmClass;
import com.github.unidbg.linux.android.dvm.DvmObject;
import com.github.unidbg.linux.android.dvm.StringObject;
import com.github.unidbg.linux.android.dvm.VM;

import java.io.ByteArrayInputStream;
import java.io.File;
import java.net.URL;
import java.net.URLClassLoader;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Base64;
import java.util.Enumeration;
import java.util.List;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;

/**
 * Guard 壳框架的桌面运行会话（桥进程内常驻）。
 *
 * <p><b>架构（2026-09-24 与用户对齐）</b>：TVBox 客户端只提供 Activity，网盘管理对话框/
 * 扫码/凭据管理全部在 jar 的壳框架（{@code BaseSpiderGuard} + {@code DexNative}）里实现，
 * 所有 Guard 源共用、客户端与源作者均零适配。桌面桥此前跑不了壳框架（{@code DexNative}
 * 是 ARM native），本类把它接起来：</p>
 * <ul>
 *   <li><b>解密</b>：{@code decrypt/encrypt/noxSign/native_ting_md5/calcResult} → 常驻
 *       unidbg 会话调 {@code ftyguard*.so}（解壳已验证同一调用模式：JNI_OnLoad +
 *       callStaticJniMethodObject）。</li>
 *   <li><b>真实类注入</b>：{@code getLoader} 不走 so（解壳产物已预转换）——直接返回
 *       桥预建的解壳 jar ClassLoader；{@code getSpider} 从中实例化真实类（去 Guard 后缀）。</li>
 *   <li><b>UI</b>：壳框架调 AlertDialog/Bitmap → 宿主桩（android.app.* 桥接版）→
 *       UiBridge 事件上行 → 宿主渲染。</li>
 * </ul>
 *
 * <p>线程模型：unidbg 调用串行（{@code LOCK}）；调用方多为 spider 请求线程。</p>
 */
public final class GuardSession {

    private static final Object LOCK = new Object();
    private static volatile AndroidEmulator emulator;
    private static volatile VM vm;
    private static volatile DvmClass dexNative;
    private static volatile String loadedSoKey;   // 防重复加载（同 jar 复用会话）
    private static volatile File loadedJar;

    /** 解壳 jar（真实类）的 ClassLoader——由宿主经 op 注入。 */
    private static volatile URLClassLoader realLoader;
    /** 解壳 jar 里的类名映射（MyDriveGuard → MyDrive），C# 侧下发。 */
    private static final java.util.Map<String, String> REAL_NAMES = new java.util.concurrent.ConcurrentHashMap<>();

    private GuardSession() { }

    // ═══════════ 会话 ═══════════

    /**
     * 确保常驻会话就绪（同 jar 复用）。jar = 原始 Guard jar（含 assets/*.so）。
     * 复刻 GuardUnpacker.emulate 的加载序列，但**不关闭** emulator。
     */
    public static void ensureSession(File jar) throws Exception {
        synchronized (LOCK) {
            String key = jar.getAbsolutePath() + "#" + jar.lastModified();
            if (emulator != null && key.equals(loadedSoKey)) return;

            closeSession();

            Path temp = Files.createTempDirectory("guard-session-");
            File so = extractSo(jar, temp);

            AndroidEmulator emu = so.getName().startsWith("arm32") || !isArm64(so)
                    ? AndroidEmulatorBuilder.for32Bit().setProcessName("com.catclaw.video").build()
                    : AndroidEmulatorBuilder.for64Bit().setProcessName("com.catclaw.video").build();
            emulator = emu;
            emu.getMemory().setLibraryResolver(new AndroidResolver(23));
            VM v = emu.createDalvikVM(jar);
            vm = v;
            GuardJni jni = new GuardJni(temp.toFile(), jar, false);
            v.setJni(jni);
            v.setVerbose(false);

            DalvikModule dm = v.loadLibrary(so, true);
            Module module = dm.getModule();
            System.err.println("[guard] so 基址 = 0x" + Long.toHexString(module.base));
            dm.callJNI_OnLoad(emu);

            dexNative = v.resolveClass("com/github/catvod/spider/DexNative");
            loadedJar = jar;
            loadedSoKey = key;

            // ⚠ 会话预热：so 的 decrypt 等函数依赖 getLoader 先跑一遍的内部初始化状态
            //   （解壳序列同款：getLoader 触发解密后壳调 DexClassLoader.loadClass 必然抛
            //   良性异常——产物/状态此时已就位，吞掉继续）。
            try {
                DvmObject<?> ctxObj = v.resolveClass("android/content/Context").newObject(null);
                dexNative.callStaticJniMethodObject(emulator,
                        "getLoader(Ljava/lang/Object;)Ljava/lang/Object;", ctxObj);
            } catch (Throwable t) {
                System.err.println("[guard] 预热 getLoader 良性异常（状态已就位）: " + t);
            }

            System.err.println("[guard] 常驻会话就绪");
        }
    }

    /** 重新打开解壳时的良性异常（壳写完产物后调 DexClassLoader.loadClass 必然失败）。 */
    private static Object callGetLoader(Object ctx) {
        try {
            DvmObject<?> ctxObj = vm.resolveClass("android/content/Context").newObject(null);
            return dexNative.callStaticJniMethodObject(emulator,
                    "getLoader(Ljava/lang/Object;)Ljava/lang/Object;", ctxObj);
        } catch (Throwable t) {
            System.err.println("[guard] getLoader 异常（良性，忽略）: " + t);
            return null;
        }
    }

    public static void closeSession() {
        try { if (emulator != null) emulator.close(); } catch (Throwable ignored) { }
        emulator = null;
        vm = null;
        dexNative = null;
        loadedSoKey = null;
    }

    // ═══════════ DexNative 替身调用的实现 ═══════════

    /**
     * 每次 ARM native 调用留一行「谁调的、多长、由哪条通道服务」。
     * <p>没有这行就没法区分三件事：爬虫**根本没调用**、QEMU 命中、静默回落 unidbg ——
     * 三者在业务日志里长得一模一样，而结论完全相反（2026-09-25 桌面网盘登录态排障实测）。</p>
     */
    private static void trace(String api, int inLen, String via, int outLen) {
        System.err.println("[guard] " + api + " " + inLen + "B → " + via + " " + outLen + "B");
    }

    private static int len(String s) { return s == null ? 0 : s.length(); }

    /** QEMU 通道单参调用（DECRYPT/ENCRYPT/MD5）：失败抛 RuntimeException → 调用方回落 unidbg。 */
    private static String qemuOne(String op, String s) {
        try {
            String resp = QemuGuardChannel.call(op + " " + QemuGuardChannel.b64(s), 60);
            if (!resp.startsWith("OK ")) throw new IllegalStateException("QEMU " + op + ": " + resp);
            String out = QemuGuardChannel.unb64(resp.substring(3).trim());
            trace(op, len(s), "QEMU", len(out));
            return out;
        } catch (java.io.IOException e) {
            throw new RuntimeException(e);
        }
    }

    public static String decrypt(String s) {
        if (QemuGuardChannel.enabled()) {
            try { return qemuOne("DECRYPT", s); }
            catch (Throwable t) {
                System.err.println("[guard] QEMU decrypt 失败，回落 unidbg: " + t);
            }
        }
        trace("DECRYPT", len(s), "unidbg", -1);
        requireSession();
        synchronized (LOCK) {
            try {
                StringObject r = (StringObject) dexNative.callStaticJniMethodObject(emulator,
                        "decrypt(Ljava/lang/String;)Ljava/lang/String;", new StringObject(vm, s == null ? "" : s));
                return r == null ? "" : r.getValue();
            } catch (Throwable t) {
                System.err.println("[guard] decrypt 异常: 入参=" + (s == null ? "null" : s.substring(0, Math.min(s.length(), 40)))
                        + " / " + t);
                throw t instanceof RuntimeException re ? re : new RuntimeException(t);
            }
        }
    }

    public static String encrypt(String s) {
        if (QemuGuardChannel.enabled()) {
            try { return qemuOne("ENCRYPT", s); }
            catch (Throwable t) {
                System.err.println("[guard] QEMU encrypt 失败，回落 unidbg: " + t);
            }
        }
        trace("ENCRYPT", len(s), "unidbg", -1);
        requireSession();
        synchronized (LOCK) {
            StringObject r = (StringObject) dexNative.callStaticJniMethodObject(emulator,
                    "encrypt(Ljava/lang/String;)Ljava/lang/String;", new StringObject(vm, s == null ? "" : s));
            return r == null ? "" : r.getValue();
        }
    }

    public static String noxSign(String a, String b, String c) {
        if (QemuGuardChannel.enabled()) {
            try {
                String resp = QemuGuardChannel.call("NOXSIGN " + QemuGuardChannel.b64(a) + " "
                        + QemuGuardChannel.b64(b) + " " + QemuGuardChannel.b64(c), 60);
                if (resp.startsWith("OK ")) return QemuGuardChannel.unb64(resp.substring(3).trim());
                throw new IllegalStateException(resp);
            } catch (Throwable t) {
                System.err.println("[guard] QEMU noxSign 失败，回落 unidbg: " + t);
            }
        }
        trace("NOXSIGN", 0, "unidbg", -1);
        requireSession();
        synchronized (LOCK) {
            StringObject r = (StringObject) dexNative.callStaticJniMethodObject(emulator,
                    "noxSign(Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;)Ljava/lang/String;",
                    new StringObject(vm, a == null ? "" : a),
                    new StringObject(vm, b == null ? "" : b),
                    new StringObject(vm, c == null ? "" : c));
            return r == null ? "" : r.getValue();
        }
    }

    public static String md5(String s) {
        if (QemuGuardChannel.enabled()) {
            try { return qemuOne("MD5", s); }
            catch (Throwable t) {
                System.err.println("[guard] QEMU md5 失败，回落 unidbg: " + t);
            }
        }
        trace("MD5", 0, "unidbg", -1);
        requireSession();
        synchronized (LOCK) {
            StringObject r = (StringObject) dexNative.callStaticJniMethodObject(emulator,
                    "native_ting_md5(Ljava/lang/String;)Ljava/lang/String;", new StringObject(vm, s == null ? "" : s));
            return r == null ? "" : r.getValue();
        }
    }

    public static int[] calcResult(int[] input) {
        if (QemuGuardChannel.enabled()) {
            try {
                java.nio.ByteBuffer buf = java.nio.ByteBuffer.allocate(4 * (input == null ? 0 : input.length));
                if (input != null) for (int v : input) buf.putInt(v);
                String resp = QemuGuardChannel.call("CALC "
                        + Base64.getEncoder().encodeToString(buf.array()), 60);
                if (resp.startsWith("OK ")) {
                    byte[] out = Base64.getDecoder().decode(resp.substring(3).trim());
                    java.nio.ByteBuffer rb = java.nio.ByteBuffer.wrap(out);
                    int[] r2 = new int[out.length / 4];
                    for (int i = 0; i < r2.length; i++) r2[i] = rb.getInt();
                    return r2;
                }
                throw new IllegalStateException(resp);
            } catch (Throwable t) {
                System.err.println("[guard] QEMU calcResult 失败，回落 unidbg: " + t);
            }
        }
        requireSession();
        synchronized (LOCK) {
            com.github.unidbg.linux.android.dvm.array.IntArray arr =
                    new com.github.unidbg.linux.android.dvm.array.IntArray(vm, input == null ? new int[0] : input);
            DvmObject<?> r = dexNative.callStaticJniMethodObject(emulator,
                    "calcResult([I)[I", arr);
            return r == null ? input : (int[]) r.getValue();
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
    private static final class GuardLoader extends URLClassLoader {
        private final String key;
        GuardLoader(URL[] urls, ClassLoader parent, String key) {
            super(urls, parent);
            this.key = key;
        }
        String key() { return key; }
    }

    // ═══════════ 工具 ═══════════

    private static void requireSession() {
        if (emulator == null && loadedJar != null) {
            // QEMU 通道失败后的懒建兜底会话（load 时 guardPort>0 跳过了 unidbg 预热）
            try { ensureSession(loadedJar); }
            catch (Throwable t) { throw new IllegalStateException("Guard 会话懒建失败: " + t); }
        }
        if (emulator == null || dexNative == null)
            throw new IllegalStateException("Guard 会话未就绪（壳 jar 未加载）");
    }

    /**
     * 登记「懒建会话该用哪个 raw jar」。
     *
     * <p>{@code guardPort>0} 时 load 会跳过 unidbg 预热（QEMU 通道优先），于是
     * {@link #loadedJar} 一直是 null —— QEMU 一旦连不上（VM 没起来／被强杀／端口没绑），
     * {@link #requireSession()} 的懒建兜底就因为不知道 jar 路径而直接抛
     * 「Guard 会话未就绪（壳 jar 未加载）」，网盘 {@code Cloud_quark.init} 随之失败。
     * 这里把路径先记下来，兜底才真的存在（2026-09-25 实测）。</p>
     */
    public static void noteJar(File jar) {
        if (jar != null && jar.isFile()) loadedJar = jar;
    }

    private static boolean isArm64(File f) throws Exception {
        return elfMachine(f) == 0xB7;
    }

    private static int elfMachine(File f) throws Exception {
        byte[] h = Files.readAllBytes(f.toPath());
        if (h.length < 20 || h[0] != 0x7F || h[1] != 'E' || h[2] != 'L' || h[3] != 'F') return -1;
        return (h[18] & 0xFF) | ((h[19] & 0xFF) << 8);
    }

    private static File extractSo(File jar, Path temp) throws Exception {
        File arm64 = null, arm32 = null, any = null;
        try (ZipFile zip = new ZipFile(jar)) {
            Enumeration<? extends ZipEntry> en = zip.entries();
            while (en.hasMoreElements()) {
                ZipEntry e = en.nextElement();
                String n = e.getName();
                if (!n.toLowerCase().endsWith(".so")) continue;
                File f = new File(temp.toFile(), n.replace('/', '_'));
                Files.copy(zip.getInputStream(e), f.toPath(),
                        java.nio.file.StandardCopyOption.REPLACE_EXISTING);
                int m = elfMachine(f);
                if (m == 0xB7) { if (arm64 == null) arm64 = f; }
                else if (m == 0x28) { if (arm32 == null) arm32 = f; }
                else if (any == null) any = f;
            }
        }
        File chosen = arm64 != null ? arm64 : (arm32 != null ? arm32 : any);
        if (chosen == null) throw new IllegalStateException("jar 内没有 .so（非 Guard 包？）");
        return chosen;
    }
}
