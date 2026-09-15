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

import java.io.File;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;

/**
 * 迅雷下载引擎（libxl_thunder_sdk.so，AArch64 Android）在 PC 上的 unidbg 运行器。
 *
 * <h3>为什么用 unidbg 而不是 qemu/WSL</h3>
 * <ul>
 *   <li>unidbg 已在工程里（{@code JavaBridge/vendor/unidbg}，为解 Guard 引入）⇒ <b>不增加任何安装要求与体积</b>。</li>
 *   <li>unidbg 的 {@code TcpSocket}/{@code UdpSocket} 把模拟 socket <b>桥接到宿主真实网络</b>
 *       （内部就是 {@link java.net.Socket} / {@link java.net.ServerSocket}），
 *       {@code ARM64SyscallHandler} 还有 {@code clone()} 与 {@code poll} ⇒ 联网多线程引擎可跑。</li>
 *   <li>qemu-user 在 Windows 上不存在（官方明确"没有计划支持"），
 *       退而求其次的 qemu-system 要打包整个虚拟机（+30~45MB）。</li>
 * </ul>
 *
 * <h3>与 GuardUnpacker 的关系</h3>
 * <p>骨架照抄 {@link GuardUnpacker}（同一套 unidbg 0.9.9 API），差别在于：
 * 迅雷引擎的 JNI 入口是 <b>静态导出</b>（{@code Java_com_xunlei_downloadlib_XLLoader_*}，共 70 个），
 * 没有 {@code JNI_OnLoad}、没有 {@code RegisterNatives} —— 所以<b>不需要</b> {@code callJNI_OnLoad}，
 * 直接按名调用即可。</p>
 *
 * <h3>用法</h3>
 * <pre>
 * java -cp "vendor/unidbg/*;bridge" bridge.ThunderRunner &lt;工作目录&gt; [--stage2]
 * </pre>
 * 工作目录里需有：{@code libxl_thunder_sdk.so}、{@code libxl_stat.so}、{@code thunder-wrapper.dex}。
 */
public final class ThunderRunner {

    private static final String PROCESS_NAME = "com.catclaw.video";
    /** AndroidResolver 的 API level：取 23（引擎只用最基础的 libc/liblog/文件行为） */
    private static final int API_LEVEL = 23;

    /**
     * 传给引擎的保存路径必须是 **Android 风格路径**。
     * <p>实测传 Windows 路径（{@code D:\...}）时 {@code createBtMagnetTask} 会
     * {@code UC_ERR_READ_UNMAPPED} 崩在引擎自己起的线程里。用规范路径，
     * 实际落盘再交给 IOResolver 映射到宿主目录。</p>
     */
    private static final String EMU_SAVE_PATH = "/data/data/com.catclaw.video/files";

    public static void main(String[] args) throws Exception {
        // UTF-8 控制台（Windows 默认 GBK，中文日志会乱码）
        System.setOut(new java.io.PrintStream(new java.io.FileOutputStream(java.io.FileDescriptor.out), true, "UTF-8"));
        System.setErr(new java.io.PrintStream(new java.io.FileOutputStream(java.io.FileDescriptor.err), true, "UTF-8"));
        // ⚠️ stdin 必须是立即 EOF 的空流：unidbg 遇到良性异常时会挂起 SimpleARM64Debugger，
        // 而它用 Scanner(System.in) 读交互命令；stdin 若是永不 EOF 的管道就会永久阻塞
        // （GuardUnpacker 踩过这个坑，表现为"解壳卡死"）。
        System.setIn(new java.io.ByteArrayInputStream(new byte[0]));

        boolean stage2 = false;
        List<String> pos = new ArrayList<>();
        for (String a : args) {
            if ("--stage2".equals(a)) stage2 = true;
            else pos.add(a);
        }
        if (pos.isEmpty()) {
            System.err.println("用法: java -cp \"vendor/unidbg/*;bridge\" bridge.ThunderRunner <工作目录> [--stage2]");
            System.exit(2);
        }

        Path work = Path.of(pos.get(0));
        File sdkSo = work.resolve("libxl_thunder_sdk.so").toFile();
        File statSo = work.resolve("libxl_stat.so").toFile();
        File dex = work.resolve("thunder-wrapper.dex").toFile();
        if (!sdkSo.isFile()) {
            System.err.println("[迅雷] 缺 libxl_thunder_sdk.so: " + sdkSo.getAbsolutePath());
            System.exit(2);
        }
        // ⚠️ 不要喂真实的 bionic libc/libm/...！
        // unidbg 自带一套 Android SDK 桩库（`unidbg-android-0.9.9.jar` 内
        // `android/sdk23/lib64/{libc,libm,liblog,libdl,libz,libcpp,libssl,libcrypto}.so`），
        // 由 AndroidResolver(23) 自动提供。实测喂真实 bionic libc 会引入 unidbg 处理不了的
        // TLS 重定位（`Unhandled relocation type 1030`），导致引擎 `.init_array` 构造函数
        // 跳到未映射地址（UC_ERR_FETCH_UNMAPPED）。
        // 只有**我们自己**的 libxl_stat.so 需要额外提供。
        List<File> deps = new ArrayList<>();
        if (statSo.isFile()) deps.add(statSo);

        System.err.println("[迅雷] 工作目录 = " + work.toAbsolutePath());
        System.err.println("[迅雷] 引擎 = " + sdkSo.length() + " 字节；wrapper dex = "
                + (dex.isFile() ? dex.length() + " 字节" : "(缺)"));
        System.err.println("[迅雷] 额外依赖 = " + (deps.isEmpty() ? "无" : deps.get(0).getName())
                + "（libc 等由 unidbg 自带 sdk23 桩库提供）");

        run(sdkSo, deps, dex, work, stage2);
    }

    private static void run(File sdkSo, List<File> deps, File dex, Path work, boolean stage2) throws Exception {
        // 64 位引擎（实测 e_machine=0xB7 = AArch64）
        AndroidEmulator emulator = AndroidEmulatorBuilder.for64Bit().setProcessName(PROCESS_NAME).build();
        try {
            Memory memory = emulator.getMemory();
            // AndroidResolver 同时兼任 LibraryResolver 与 IOResolver；
            // 把依赖库的**绝对路径**作为 needed 传进去，DT_NEEDED 才解析得到。
            // ⚠️ 不要往 AndroidResolver 的 needed 里塞东西：实测传了之后它反而解析不出
            // libxl_stat.so 的 275 个符号（libc/libm/... 全部 missing），init 构造函数随即崩溃。
            // 标准做法就是用 AndroidResolver(apiLevel)，未知库按 **当前工作目录** 查找。
            memory.setLibraryResolver(new AndroidResolver(API_LEVEL));

            VM vm = dex.isFile() ? emulator.createDalvikVM(dex) : emulator.createDalvikVM();
            ThunderJni jni = new ThunderJni(work.toFile());
            vm.setJni(jni);
            vm.setVerbose(Boolean.getBoolean("thunder.verbose"));

            System.err.println("[迅雷] 加载引擎…");
            DalvikModule dm = vm.loadLibrary(sdkSo, true);
            Module module = dm.getModule();
            System.err.println("[迅雷] ✅ 已加载，模块基址 = 0x" + Long.toHexString(module.base));

            if (dex.isFile()) {
                stage1VersionApi(vm, emulator, jni);
            } else {
                System.err.println("[迅雷] 无 wrapper dex —— 只能验证加载（跳过 API 调用）");
            }

            if (stage2) {
                stage2MagnetTask(vm, emulator, work, jni);
            }
        } finally {
            emulator.close();
        }
    }

    /** 阶段一：调用两个只读 API，验证「unidbg 能驱动这个引擎」 */
    private static void stage1VersionApi(VM vm, AndroidEmulator emulator, ThunderJni jni) {
        System.err.println("[迅雷] ── 阶段一：只读 API ──");
        DvmClass xlLoaderCls = vm.resolveClass("com/xunlei/downloadlib/XLLoader");
        if (xlLoaderCls == null) {
            System.err.println("[迅雷] ✗ 找不到 com/xunlei/downloadlib/XLLoader（wrapper dex 不对？）");
            return;
        }
        DvmObject<?> loader = xlLoaderCls.newObject(null);
        if (loader == null) {
            System.err.println("[迅雷] ✗ XLLoader 实例化失败");
            return;
        }

        // ① XYVodSDK_getVersion() → String（直接返回，不需要参数对象）
        try {
            DvmObject<?> v = loader.callJniMethodObject(emulator, "XYVodSDK_getVersion()Ljava/lang/String;");
            System.err.println("[迅雷] ✅ XYVodSDK_getVersion() = " + (v == null ? "(null)" : v.getValue()));
        } catch (Throwable t) {
            System.err.println("[迅雷] ✗ XYVodSDK_getVersion 失败: " + t);
        }

        // ② getDownloadLibVersion(GetDownloadLibVersion) → int，版本号由 native 回填进参数对象
        try {
            DvmClass paramCls = vm.resolveClass("com/xunlei/downloadlib/parameter/GetDownloadLibVersion");
            DvmObject<?> param = paramCls.newObject(null);
            DvmObject<?> r = loader.callJniMethodObject(emulator,
                    "getDownloadLibVersion(Lcom/xunlei/downloadlib/parameter/GetDownloadLibVersion;)I", param);
            System.err.println("[迅雷] ✅ getDownloadLibVersion 返回 " + (r == null ? "?" : r.getValue())
                    + "，mVersion = " + jni.capturedString("mVersion"));
        } catch (Throwable t) {
            System.err.println("[迅雷] ✗ getDownloadLibVersion 失败: " + t);
        }
    }

    /** 阶段二：init(...) —— 让引擎真正启动起来 */
    private static void stage2MagnetTask(VM vm, AndroidEmulator emulator, Path work, ThunderJni jni) {
        System.err.println("[迅雷] ── 阶段二：init ──");
        DvmClass xlLoaderCls = vm.resolveClass("com/xunlei/downloadlib/XLLoader");
        DvmObject<?> loader = xlLoaderCls == null ? null : xlLoaderCls.newObject(null);
        if (loader == null) {
            System.err.println("[迅雷] ✗ XLLoader 实例化失败");
            return;
        }

        // soAppKey：由 appKey 派生，算法已从字节码复刻（见 README 的推导）：
        //   appKey.split("==")[0].replace('^','=') 去掉首尾各 2 字符 → Base64 解码 → rawItems
        //   mAppId = Short.parseShort(rawItems.split(";")[0]) = 6001
        //   Base64( "com.android.providers.downloads" + 0x00 + appId(小端 2 字节) + appType(1=PRODUCT) )
        final String soAppKey = "Y29tLmFuZHJvaWQucHJvdmlkZXJzLmRvd25sb2FkcwBxFwE=";

        File saveRoot = work.resolve("thunder-data").toFile();
        //noinspection ResultOfMethodCallIgnored
        saveRoot.mkdirs();

        // peerid：对齐 XLDownloadManager.getPeerid() 的回落分支 —— 大写十六进制(36) + "004V"
        String peerid = randomHex(36).toUpperCase() + "004V";
        // guid：XLUtil.generateGuid 的默认形态
        String guid = randomHex(14) + "_" + randomHex(12);

        System.err.println("[迅雷]   soAppKey = " + soAppKey);
        System.err.println("[迅雷]   peerid   = " + peerid);
        System.err.println("[迅雷]   guid     = " + guid);
        System.err.println("[迅雷]   savePath = " + saveRoot.getAbsolutePath());

        try {
            int rc = loader.callJniMethodInt(emulator,
                    "init(Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;"
                            + "Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;III)I",
                    soAppKey,                       // ① soAppKey（getSoAppKey()）
                    "com.android.providers.downloads", // ② 硬编码包名
                    "1.0.0",                        // ③ mAppVersion
                    "",                             // ④ 空串
                    peerid,                         // ⑤ getPeerid()
                    guid,                           // ⑥ getGuid()
                    EMU_SAVE_PATH,                     // ⑦ mStatSavePath
                    EMU_SAVE_PATH,                     // ⑧ mStatCfgSavePath
                    1,                              // ⑨ networkType
                    1,                              // ⑩ mPermissionLevel（对齐 XLTaskHelper：1）
                    0);                             // ⑪ mQueryConfOnInit （对齐 XLTaskHelper：0）
            System.err.println("[迅雷] ← init 返回 " + rc + "（0 = 成功；9000/9901 = 失败）");

            if (rc != 0) {
                System.err.println("[迅雷] ⚠️ init 未返回 0 —— 仍继续试建任务，以便观察真实失败点");
            }

            createMagnetTask(vm, emulator, loader, saveRoot, jni);
        } catch (Throwable t) {
            System.err.println("[迅雷] ✗ init 失败: " + t);
            t.printStackTrace();
        }
    }

    /**
     * 建磁力任务 → 轮询状态 → 取本地播放地址。
     * 调用顺序与参数顺序均取自字节码（`XLTaskHelper.addMagentTask` /
     * `XLDownloadManager.createBtMagnetTask`）：native 侧是
     * {@code createBtMagnetTask(url, filePath, fileName, GetTaskId)}。
     */
    private static void createMagnetTask(VM vm, AndroidEmulator emulator, DvmObject<?> loader,
                                         File saveRoot, ThunderJni jni) {
        String magnet = System.getProperty("thunder.magnet",
                "magnet:?xt=urn:btih:1363FB911E8603FDE757C9B02979D508DE2D195B");
        String name = "test-magnet";
        System.err.println("[迅雷] ── 建磁力任务 ──");
        System.err.println("[迅雷]   magnet = " + magnet);

        try {
            DvmClass taskIdCls = vm.resolveClass("com/xunlei/downloadlib/parameter/GetTaskId");
            DvmObject<?> taskId = taskIdCls == null ? null : taskIdCls.newObject(null);
            int rc = loader.callJniMethodInt(emulator,
                    "createBtMagnetTask(Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;"
                            + "Lcom/xunlei/downloadlib/parameter/GetTaskId;)I",
                    magnet, EMU_SAVE_PATH, name, taskId);
            System.err.println("[迅雷] ← createBtMagnetTask 返回 " + rc
                    + "，taskId = " + jni.capturedString("mTaskId"));

            long id = -1;
            Object v = jni.captured("mTaskId");
            if (v instanceof Number n) id = n.longValue();
            if (id <= 0) {
                System.err.println("[迅雷] ⚠️ 没拿到有效 taskId，跳过轮询");
                return;
            }

            // 轮询任务状态：看它是否会去联网、进度是否推进
            DvmClass statusCls = vm.resolveClass("com/xunlei/downloadlib/parameter/BtTaskStatus");
            for (int i = 0; i < 10; i++) {
                DvmObject<?> st = statusCls == null ? null : statusCls.newObject(null);
                int s = loader.callJniMethodInt(emulator,
                        "getBtSubTaskStatus(JLcom/xunlei/downloadlib/parameter/BtTaskStatus;II)I",
                        id, st, 0, 0);
                System.err.println("[迅雷]   轮询#" + i + " getBtSubTaskStatus 返回 " + s
                        + "，mState/" + jni.capturedString("mState")
                        + " mStatus/" + jni.capturedString("mStatus"));
                Thread.sleep(1000);
            }

            // 取本地播放地址
            DvmClass localUrlCls = vm.resolveClass("com/xunlei/downloadlib/parameter/XLTaskLocalUrl");
            DvmObject<?> localUrl = localUrlCls == null ? null : localUrlCls.newObject(null);
            int u = loader.callJniMethodInt(emulator,
                    "getLocalUrl(Ljava/lang/String;Lcom/xunlei/downloadlib/parameter/XLTaskLocalUrl;)I",
                    name, localUrl);
            System.err.println("[迅雷] ← getLocalUrl 返回 " + u
                    + "，mStrUrl = " + jni.capturedString("mStrUrl"));
        } catch (Throwable t) {
            System.err.println("[迅雷] ✗ 建任务失败: " + t);
            t.printStackTrace();
        }
    }

    private static String randomHex(int n) {
        java.security.SecureRandom r = new java.security.SecureRandom();
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < n; i++) sb.append("0123456789abcdef".charAt(r.nextInt(16)));
        return sb.toString();
    }

}
