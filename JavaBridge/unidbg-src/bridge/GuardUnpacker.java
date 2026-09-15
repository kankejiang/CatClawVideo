package bridge;

import com.github.unidbg.AndroidEmulator;
import com.github.unidbg.Module;
import com.github.unidbg.linux.android.AndroidEmulatorBuilder;
import com.github.unidbg.linux.android.AndroidResolver;
import com.github.unidbg.linux.android.dvm.DalvikModule;
import com.github.unidbg.linux.android.dvm.DvmClass;
import com.github.unidbg.linux.android.dvm.DvmObject;
import com.github.unidbg.linux.android.dvm.VM;
import com.github.unidbg.memory.Memory;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.util.ArrayList;
import java.util.Enumeration;
import java.util.List;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;
import java.util.zip.ZipInputStream;
import java.util.zip.ZipOutputStream;

/**
 * Guard 加固 jar 的 unidbg 脱壳器（PC 侧，离线运行）。
 *
 * <h3>原理</h3>
 * <p>Guard（ftyshinidie / ftyguard）把真正的 spider dex 加密成 {@code assets/ftyshinidie.guard}，
 * 解密器是 ARM native（{@code assets/ftyguard_v8.so}，通过 {@code JNI_OnLoad} +
 * {@code RegisterNatives} 注册 {@code com.github.catvod.spider.DexNative}）。x64 JVM 无法直接执行
 * ARM 指令，因此这里的做法是：<b>用 unidbg（Unicorn）模拟一个 ARM Android 进程</b>，加载这个 SO、
 * 补全它需要的 JNI/Framework 行为（{@link GuardJni}），让 SO 认为自己在真机上，从而触发解密逻辑，
 * 最后把壳写出的明文 dex 截获出来。</p>
 *
 * <h3>关键事实（实测确认）</h3>
 * <ul>
 *   <li>该壳<b>没有反模拟检测</b>：SO 只有 18 个外部符号且全是 libc（malloc/memcpy/strstr…），
 *       不读 {@code /proc}、不调 {@code ptrace}、不查 {@code __system_property_get}。</li>
 *   <li>SO 用 OLLVM 8.0.0 混淆（控制流平坦化 + 符号改名），这只妨碍静态分析，不妨碍模拟执行。</li>
 *   <li>解密产物是完整 ZIP（头 {@code 50 4B 03 04}），内含真实 {@code classes.dex}；
 *       真实类名<b>不带</b> {@code Guard} 后缀（外壳 {@code DouDouGuard} → 真实 {@code DouDou}）。</li>
 *   <li>壳在写完产物之后会走 {@code DexClassLoader.loadClass}（unidbg 里必然失败）。
 *       该异常发生在<b>产物写完之后</b>，属良性，故此处捕获后继续用已截获的字节。</li>
 * </ul>
 *
 * <h3>用法</h3>
 * <pre>
 * java -cp "vendor/unidbg/*" bridge.GuardUnpacker &lt;guarded.jar&gt; &lt;out.jar&gt; [--verbose] [--keep-temp]
 * </pre>
 * 退出码：0 成功；2 参数/输入问题；3 模拟执行失败；4 产物不是合法 dex/zip。
 */
public final class GuardUnpacker {

    private static final int EXIT_OK = 0;
    private static final int EXIT_USAGE = 2;
    private static final int EXIT_EMU_FAIL = 3;
    private static final int EXIT_BAD_PAYLOAD = 4;

    private static final String PROCESS_NAME = "com.catclaw.video";
    /** AndroidResolver 的 API level；取 23（壳只用最基础的 File/Context/ClassLoader 行为） */
    private static final int API_LEVEL = 23;

    public static void main(String[] args) {
        // 强制 stdout/stderr 为 UTF-8（Windows 控制台代码页是 GBK，中文日志会变乱码；
        // 与 bridge.Server 的处理方式一致）。注意：-Dfile.encoding=UTF-8 在 JDK 19+ 不再
        // 影响 stdout/stderr.encoding，必须在这里显式包一层 PrintStream。
        try {
            System.setOut(new java.io.PrintStream(new java.io.FileOutputStream(java.io.FileDescriptor.out), true, "UTF-8"));
            System.setErr(new java.io.PrintStream(new java.io.FileOutputStream(java.io.FileDescriptor.err), true, "UTF-8"));
        } catch (java.io.UnsupportedEncodingException e) {
            // UTF-8 一定存在，进不来
        }

        boolean verbose = false;
        boolean keepTemp = false;
        List<String> pos = new ArrayList<>();
        for (String a : args) {
            if ("--verbose".equals(a)) verbose = true;
            else if ("--keep-temp".equals(a)) keepTemp = true;
            else pos.add(a);
        }
        if (pos.size() < 2) {
            System.err.println("用法: java -cp \"vendor/unidbg/*\" bridge.GuardUnpacker "
                    + "<guarded.jar> <out.jar> [--verbose] [--keep-temp]");
            System.exit(EXIT_USAGE);
        }

        File jar = new File(pos.get(0));
        File out = new File(pos.get(1));
        if (!jar.isFile()) {
            System.err.println("[unpack] 输入不存在: " + jar.getAbsolutePath());
            System.exit(EXIT_USAGE);
        }

        long t0 = System.currentTimeMillis();
        Path temp = null;
        try {
            temp = Files.createTempDirectory("guard-unpack-");
            System.err.println("[unpack] jar = " + jar.getAbsolutePath() + " (" + jar.length() + " 字节)");

            File so = extractSo(jar, temp, verbose);
            byte[] payload = emulate(jar, so, temp, verbose);
            if (payload == null || payload.length == 0) {
                System.err.println("[unpack] 失败: 未截获到解密产物");
                System.exit(EXIT_EMU_FAIL);
            }

            // 统一成 jar（= ZIP）：裸 dex 包一层 classes.dex，后续 dex2jar 直接吃
            byte[] jarBytes = isZip(payload) ? payload : wrapBareDex(payload);
            if (!isZip(jarBytes)) {
                System.err.println("[unpack] 失败: 产物既不是 ZIP 也不是合法 dex");
                System.exit(EXIT_BAD_PAYLOAD);
            }

            File parent = out.getParentFile();
            if (parent != null) parent.mkdirs();
            Files.write(out.toPath(), jarBytes);

            int dexCount = countDexEntries(jarBytes);
            System.err.println("[unpack] 成功: " + out.getAbsolutePath()
                    + " (" + jarBytes.length + " 字节, classes*.dex x" + dexCount + ")"
                    + " 用时 " + (System.currentTimeMillis() - t0) + "ms");
            if (dexCount == 0) {
                System.err.println("[unpack] 警告: 产物里没有 classes*.dex");
                System.exit(EXIT_BAD_PAYLOAD);
            }
        } catch (Throwable t) {
            System.err.println("[unpack] 异常: " + t);
            if (verbose) t.printStackTrace();
            System.exit(EXIT_EMU_FAIL);
        } finally {
            if (temp != null) {
                if (keepTemp) System.err.println("[unpack] 临时目录保留: " + temp);
                else deleteRecursively(temp.toFile());
            }
        }
    }

    // ═══════════ 模拟执行 ═══════════

    private static byte[] emulate(File jar, File so, Path temp, boolean verbose) throws Exception {
        boolean arm64 = isArm64(so);
        System.err.println("[unpack] SO = " + so.getName() + " (" + (arm64 ? "arm64" : "arm32") + ")");

        AndroidEmulator emulator = arm64
                ? AndroidEmulatorBuilder.for64Bit().setProcessName(PROCESS_NAME).build()
                : AndroidEmulatorBuilder.for32Bit().setProcessName(PROCESS_NAME).build();

        try {
            Memory memory = emulator.getMemory();
            memory.setLibraryResolver(new AndroidResolver(API_LEVEL));

            // createDalvikVM(jar) 让 unidbg 认识壳里的类（含 DexNative 声明）
            VM vm = emulator.createDalvikVM(jar);
            GuardJni jni = new GuardJni(temp.toFile(), jar, verbose);
            vm.setJni(jni);
            vm.setVerbose(verbose);

            DalvikModule dm = vm.loadLibrary(so, true);
            Module module = dm.getModule();
            System.err.println("[unpack] 模块基址 = 0x" + Long.toHexString(module.base));

            dm.callJNI_OnLoad(emulator);

            DvmClass dexNative = vm.resolveClass("com/github/catvod/spider/DexNative");
            DvmObject<?> ctx = vm.resolveClass("android/content/Context").newObject(null);

            // 解密入口。壳在产出之后必然因 DexClassLoader.loadClass 抛异常，
            // 但产物此时已全部写到被截获的输出流里 —— 所以这里吞掉异常继续。
            try {
                dexNative.callStaticJniMethodObject(
                        emulator, "getLoader(Ljava/lang/Object;)Ljava/lang/Object;", ctx);
            } catch (Throwable t) {
                System.err.println("[unpack] getLoader 抛出（解密产物已截获，忽略）: " + t);
            }

            byte[] payload = jni.bestCapture();
            if (payload != null && payload.length >= 4) {
                System.err.println("[unpack] 截获 " + payload.length + " 字节, 头 = "
                        + String.format("%02x %02x %02x %02x", payload[0], payload[1], payload[2], payload[3]));
            }
            return payload;
        } finally {
            emulator.close();
        }
    }

    // ═══════════ SO 提取与识别 ═══════════

    /**
     * 从 Guard jar 里挑并解出 native 解密器。
     * 按 ELF 头 {@code e_machine} 判架构（{@code 0xB7}=AArch64，{@code 0x28}=ARM），
     * 优先 arm64 —— 与文件名（v7/v8）无关，避免不同 Guard 版本改命名规则时选错。
     */
    private static File extractSo(File jar, Path temp, boolean verbose) throws IOException {
        File arm64 = null, arm32 = null, any = null;
        List<String> seen = new ArrayList<>();
        try (ZipFile zip = new ZipFile(jar)) {
            Enumeration<? extends ZipEntry> en = zip.entries();
            while (en.hasMoreElements()) {
                ZipEntry e = en.nextElement();
                String n = e.getName();
                if (!n.toLowerCase().endsWith(".so")) continue;
                seen.add(n);
                File f = new File(temp.toFile(), n.replace('/', '_'));
                try (InputStream in = zip.getInputStream(e)) {
                    Files.copy(in, f.toPath(), StandardCopyOption.REPLACE_EXISTING);
                }
                if (isArm64(f)) {
                    if (arm64 == null) arm64 = f;
                } else if (isArm32(f)) {
                    if (arm32 == null) arm32 = f;
                } else if (any == null) {
                    any = f;
                }
            }
        }
        if (verbose) System.err.println("[unpack] jar 内 .so: " + seen);

        File chosen = arm64 != null ? arm64 : (arm32 != null ? arm32 : any);
        if (chosen == null) {
            System.err.println("[unpack] jar 内没有 .so，不是 Guard 加固包？");
            System.exit(EXIT_USAGE);
        }
        return chosen;
    }

    private static boolean isArm64(File f) {
        return elfMachine(f) == 0xB7;
    }

    private static boolean isArm32(File f) {
        return elfMachine(f) == 0x28;
    }

    private static int elfMachine(File f) {
        try {
            byte[] h = new byte[20];
            try (InputStream in = Files.newInputStream(f.toPath())) {
                if (in.read(h) < 20) return -1;
            }
            if (h[0] != 0x7F || h[1] != 'E' || h[2] != 'L' || h[3] != 'F') return -1;
            return (h[18] & 0xFF) | ((h[19] & 0xFF) << 8);
        } catch (IOException e) {
            return -1;
        }
    }

    // ═══════════ 产物处理 ═══════════

    private static boolean isZip(byte[] d) {
        return d.length > 4 && d[0] == 0x50 && d[1] == 0x4B && d[2] == 0x03 && d[3] == 0x04;
    }

    private static byte[] wrapBareDex(byte[] dex) throws IOException {
        ByteArrayOutputStream bos = new ByteArrayOutputStream();
        try (ZipOutputStream zos = new ZipOutputStream(bos)) {
            zos.putNextEntry(new ZipEntry("classes.dex"));
            zos.write(dex);
            zos.closeEntry();
        }
        return bos.toByteArray();
    }

    private static int countDexEntries(byte[] jarBytes) {
        int n = 0;
        try (ZipInputStream zis = new ZipInputStream(new ByteArrayInputStream(jarBytes))) {
            ZipEntry e;
            while ((e = zis.getNextEntry()) != null) {
                String name = e.getName();
                if (name.startsWith("classes") && name.endsWith(".dex")) n++;
            }
        } catch (IOException ignored) {
        }
        return n;
    }

    private static void deleteRecursively(File f) {
        try {
            File[] kids = f.isDirectory() ? f.listFiles() : null;
            if (kids != null) for (File k : kids) deleteRecursively(k);
            //noinspection ResultOfMethodCallIgnored
            f.delete();
        } catch (Throwable ignored) {
        }
    }
}
