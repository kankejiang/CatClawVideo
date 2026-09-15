package bridge;

import com.github.unidbg.linux.android.dvm.AbstractJni;
import com.github.unidbg.linux.android.dvm.BaseVM;
import com.github.unidbg.linux.android.dvm.DvmClass;
import com.github.unidbg.linux.android.dvm.DvmObject;
import com.github.unidbg.linux.android.dvm.StringObject;
import com.github.unidbg.linux.android.dvm.VaList;
import com.github.unidbg.linux.android.dvm.VarArg;
import com.github.unidbg.linux.android.dvm.array.ByteArray;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * Guard 壳（ftyshinidie / <c>ftyguard_v8.so</c>）在 unidbg 里所需的 JNI 回调桩。
 *
 * <p>用途：把壳需要的 Android Framework 侧行为（Context 目录、ClassLoader 资源流、File IO、
 * 输出流）用**宿主真实文件/内存**承载，让 SO 里的解密函数「以为」自己在真机上运行，
 * 从而走完解密逻辑并吐出明文 dex。</p>
 *
 * <h3>两个必须遵守的实现约束（踩过的坑）</h3>
 * <ol>
 *   <li><b>非 V 版的 varargs 才是实际调用路径。</b>壳用的是
 *       {@code CallObjectMethod} / {@code CallVoidMethod} / {@code NewObject} /
 *       {@code CallStaticObjectMethod}（无 {@code V} 后缀），在 unidbg 里落到 {@link VarArg} 重载；
 *       为稳健起见 {@link VaList} 与 {@link VarArg} 两套重载都覆盖。</li>
 *   <li><b>写字节数组必须 {@code ba.setData(...)} 同步到模拟内存。</b>壳随后会用
 *       {@code GetByteArrayElements} 直接读指针，只改 Java 侧数组的话 native 读到全 0。</li>
 * </ol>
 *
 * <p>未覆盖的签名一律回落 {@code super}（unidbg 默认实现会抛
 * {@code UnsupportedOperationException}），便于从日志里发现缺口而不是静默出错。</p>
 */
class GuardJni extends AbstractJni {

    /** 伪装的 Context 工作目录（code_cache / cache / files / sharedb 都落在这里） */
    private final File workDir;

    /** 被解壳的 Guard jar：壳要的资源（{@code assets/*.guard}）从这里取 */
    private final File jarFile;

    private final boolean verbose;

    /** 壳创建的每个输出流的缓冲，按路径建桶 */
    private final Map<String, OutStream> outputs = new LinkedHashMap<>();

    private final List<String> readPaths = new ArrayList<>();

    /** 壳最终交给 DexClassLoader 的路径（通常是明文 dex 落到的那份文件） */
    private String dexPath;

    GuardJni(File workDir, File jarFile, boolean verbose) {
        this.workDir = workDir;
        this.jarFile = jarFile;
        this.verbose = verbose;
    }

    // ═══════════ 状态 ═══════════

    /** 供 ClassLoader.getResourceAsStream 使用的流状态 */
    private static final class ResStream {
        final String name;
        final byte[] data;
        int pos;

        ResStream(String name, byte[] data) {
            this.name = name;
            this.data = data;
        }
    }

    /** 假输出流：承接壳写出的明文 dex（不落真实磁盘，只在内存里攒） */
    private static final class OutStream {
        final String path;
        final ByteArrayOutputStream buf = new ByteArrayOutputStream();

        OutStream(String path) {
            this.path = path;
        }
    }

    public String getDexPath() {
        return dexPath;
    }

    public List<String> getReadPaths() {
        return readPaths;
    }

    /**
     * 挑选真正的解密产物。
     *
     * <p>壳会把 dex 写成 ZIP（头 {@code PK\x03\x04}）或裸 dex（头 {@code dex\n}）。
     * 按魔数筛一遍，再取其中最大的那个 —— 避免误取壳顺带写的配置/签名小文件。</p>
     *
     * @return 明文 dex 的字节；没有合格产物时返回 {@code null}
     */
    public byte[] bestCapture() {
        OutStream best = null;
        for (OutStream os : outputs.values()) {
            if (os.buf.size() == 0) continue;
            if (!looksLikePayload(os.buf)) continue;
            if (best == null || os.buf.size() > best.buf.size()) best = os;
        }
        if (best != null) {
            if (verbose) System.err.println("[jni] 产物来源: " + best.path + " (" + best.buf.size() + " 字节)");
            return best.buf.toByteArray();
        }
        // 退路：没有任何一条带合法魔数 —— 取最大的那条，交给上层判定
        for (OutStream os : outputs.values())
            if (best == null || os.buf.size() > best.buf.size()) best = os;
        if (best == null || best.buf.size() == 0) return null;
        System.err.println("[unpack] 警告: 未匹配到 dex/zip 魔数，取最大输出流 " + best.path);
        return best.buf.toByteArray();
    }

    private static boolean looksLikePayload(ByteArrayOutputStream buf) {
        byte[] d = buf.toByteArray();
        if (d.length < 8) return false;
        boolean zip = d[0] == 0x50 && d[1] == 0x4B && d[2] == 0x03 && d[3] == 0x04;
        boolean dex = d[0] == 'd' && d[1] == 'e' && d[2] == 'x' && d[3] == '\n';
        return zip || dex;
    }

    /**
     * 从真实 jar 里取条目内容（壳要的 {@code assets/*.guard} 就在这里）。
     * 优先精确命中，其次按后缀匹配 —— 不同 Guard 版本的条目路径不完全一致。
     */
    private byte[] readJarEntry(String name) {
        if (jarFile == null || !jarFile.isFile()) return null;
        try (java.util.zip.ZipFile zip = new java.util.zip.ZipFile(jarFile)) {
            String key = name.startsWith("/") ? name.substring(1) : name;
            for (String cand : new String[]{key, "assets/" + key, "classes/" + key}) {
                java.util.zip.ZipEntry e = zip.getEntry(cand);
                if (e != null) {
                    try (java.io.InputStream in = zip.getInputStream(e)) {
                        return in.readAllBytes();
                    }
                }
            }
            java.util.Enumeration<? extends java.util.zip.ZipEntry> en = zip.entries();
            while (en.hasMoreElements()) {
                java.util.zip.ZipEntry e = en.nextElement();
                if (e.getName().endsWith(key)) {
                    try (java.io.InputStream in = zip.getInputStream(e)) {
                        return in.readAllBytes();
                    }
                }
            }
        } catch (Throwable t) {
            System.err.println("[jni] readJarEntry(" + name + ") 失败: " + t);
        }
        return null;
    }

    private void log(String m) {
        if (verbose) System.err.println("[jni] " + m);
    }

    // ═══════════ 参数访问适配（VaList / VarArg 统一）═══════

    private interface A {
        Object o(int i);

        int i(int i);
    }

    private static A a(VaList v) {
        return new A() {
            public Object o(int i) { return v.getObjectArg(i); }

            public int i(int i) { return v.getIntArg(i); }
        };
    }

    private static A a(VarArg v) {
        return new A() {
            public Object o(int i) { return v.getObjectArg(i); }

            public int i(int i) { return v.getIntArg(i); }
        };
    }

    private static String str(Object o) {
        return o instanceof StringObject s ? s.getValue() : String.valueOf(o);
    }

    // ═══════════ 实例方法：CallObjectMethod ═══════════

    @Override
    public DvmObject<?> callObjectMethodV(BaseVM vm, DvmObject<?> o, String sig, VaList va) {
        DvmObject<?> r = onCallObject(vm, o, sig, a(va));
        return r != null ? r : super.callObjectMethodV(vm, o, sig, va);
    }

    @Override
    public DvmObject<?> callObjectMethod(BaseVM vm, DvmObject<?> o, String sig, VarArg va) {
        DvmObject<?> r = onCallObject(vm, o, sig, a(va));
        return r != null ? r : super.callObjectMethod(vm, o, sig, va);
    }

    private DvmObject<?> onCallObject(BaseVM vm, DvmObject<?> self, String sig, A arg) {
        switch (sig) {
            case "android/content/Context->getCodeCacheDir()Ljava/io/File;":
                return dir(vm, "code_cache");
            case "android/content/Context->getCacheDir()Ljava/io/File;":
                return dir(vm, "cache");
            case "android/content/Context->getFilesDir()Ljava/io/File;":
                return dir(vm, "files");
            case "android/content/Context->getClassLoader()Ljava/lang/ClassLoader;":
                log("Context.getClassLoader()");
                return vm.resolveClass("java/lang/ClassLoader").newObject(null);
            case "java/io/File->getAbsolutePath()Ljava/lang/String;":
                log("File.getAbsolutePath() -> " + fileOf(self));
                return new StringObject(vm, String.valueOf(fileOf(self)));
            case "java/io/File->getPath()Ljava/lang/String;":
                return new StringObject(vm, String.valueOf(fileOf(self)));
            case "java/io/File->getParent()Ljava/lang/String;": {
                File f = fileOf(self);
                return new StringObject(vm, f == null ? null : f.getParent());
            }
            case "java/io/File->getName()Ljava/lang/String;": {
                File f = fileOf(self);
                return new StringObject(vm, f == null ? null : f.getName());
            }
            case "java/lang/ClassLoader->getResourceAsStream(Ljava/lang/String;)Ljava/io/InputStream;": {
                String name = str(arg.o(0));
                byte[] data = readJarEntry(name);
                log("ClassLoader.getResourceAsStream(" + name + ") -> "
                        + (data == null ? "null" : data.length + " 字节"));
                if (data == null) return null;
                return vm.resolveClass("java/io/InputStream").newObject(new ResStream(name, data));
            }
            case "java/lang/ClassLoader->getResource(Ljava/lang/String;)Ljava/net/URL;": {
                log("ClassLoader.getResource(" + str(arg.o(0)) + ") -> null");
                return null;
            }
        }
        return null;
    }

    private DvmObject<?> dir(BaseVM vm, String name) {
        File d = new File(workDir, name);
        //noinspection ResultOfMethodCallIgnored
        d.mkdirs();
        log("Context.get" + name + "Dir() -> " + d);
        return vm.resolveClass("java/io/File").newObject(d);
    }

    private static File fileOf(DvmObject<?> o) {
        try {
            Object v = o == null ? null : o.getValue();
            return v instanceof File f ? f : null;
        } catch (Throwable t) {
            return null;
        }
    }

    // ═══════════ 静态方法：CallStaticObjectMethod ═══════════

    @Override
    public DvmObject<?> callStaticObjectMethodV(BaseVM vm, DvmClass c, String sig, VaList va) {
        DvmObject<?> r = onStaticObject(vm, sig);
        return r != null ? r : super.callStaticObjectMethodV(vm, c, sig, va);
    }

    @Override
    public DvmObject<?> callStaticObjectMethod(BaseVM vm, DvmClass c, String sig, VarArg va) {
        DvmObject<?> r = onStaticObject(vm, sig);
        return r != null ? r : super.callStaticObjectMethod(vm, c, sig, va);
    }

    private DvmObject<?> onStaticObject(BaseVM vm, String sig) {
        if ("com/github/catvod/spider/Init->classLoader()Ljava/lang/ClassLoader;".equals(sig)) {
            log("Init.classLoader()");
            return vm.resolveClass("java/lang/ClassLoader").newObject(null);
        }
        return null;
    }

    // ═══════════ 构造器：NewObject ═══════════

    @Override
    public DvmObject<?> newObjectV(BaseVM vm, DvmClass c, String sig, VaList va) {
        DvmObject<?> r = onNewObject(vm, sig, a(va));
        return r != null ? r : super.newObjectV(vm, c, sig, va);
    }

    @Override
    public DvmObject<?> newObject(BaseVM vm, DvmClass c, String sig, VarArg va) {
        DvmObject<?> r = onNewObject(vm, sig, a(va));
        return r != null ? r : super.newObject(vm, c, sig, va);
    }

    private DvmObject<?> onNewObject(BaseVM vm, String sig, A arg) {
        switch (sig) {
            case "java/io/FileInputStream-><init>(Ljava/lang/String;)V": {
                String p = str(arg.o(0));
                readPaths.add(p);
                log("new FileInputStream(" + p + ")");
                return vm.resolveClass("java/io/FileInputStream").newObject(null);
            }
            case "java/io/FileOutputStream-><init>(Ljava/lang/String;)V": {
                String p = str(arg.o(0));
                log("new FileOutputStream(" + p + ")");
                // ⚠ 必须返回 java/io/OutputStream 类的对象：壳的 methodID 是从
                // java/io/OutputStream 解析的，unidbg 用 getObjectType().getMethod(id) 查表，
                // 若造一个 java/io/FileOutputStream 类对象则查不到 → BackendException。
                OutStream os = new OutStream(p);
                outputs.put(p, os);
                return vm.resolveClass("java/io/OutputStream").newObject(os);
            }
            case "java/io/File-><init>(Ljava/io/File;Ljava/lang/String;)V": {
                File f = new File(workDir, str(arg.o(1)));
                log("new File(parent=" + arg.o(0) + ", \"" + str(arg.o(1)) + "\")");
                return vm.resolveClass("java/io/File").newObject(f);
            }
            case "dalvik/system/DexClassLoader-><init>(Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;Ljava/lang/ClassLoader;)V": {
                dexPath = str(arg.o(0));
                log("new DexClassLoader(dexPath=" + dexPath + ", optDir=" + str(arg.o(1)) + ")");
                return vm.resolveClass("dalvik/system/DexClassLoader").newObject(null);
            }
        }
        return null;
    }

    // ═══════════ 捕获解密产物：CallVoidMethod ═══════════

    @Override
    public void callVoidMethodV(BaseVM vm, DvmObject<?> o, String sig, VaList va) {
        if (!onCallVoid(o, sig, a(va))) super.callVoidMethodV(vm, o, sig, va);
    }

    @Override
    public void callVoidMethod(BaseVM vm, DvmObject<?> o, String sig, VarArg va) {
        if (!onCallVoid(o, sig, a(va))) super.callVoidMethod(vm, o, sig, va);
    }

    private boolean onCallVoid(DvmObject<?> self, String sig, A arg) {
        if ("java/io/OutputStream->write([BII)V".equals(sig)
                || "java/io/FileOutputStream->write([BII)V".equals(sig)) {
            Object arr = arg.o(0);
            int off = arg.i(1);
            int len = arg.i(2);
            if (!(arr instanceof ByteArray ba)) {
                log("write(? " + arr + ")");
                return true;
            }
            byte[] src = ba.getValue();
            if (off < 0 || len < 0 || off + len > src.length) {
                System.err.println("[unpack] write 越界 off=" + off + " len=" + len + " cap=" + src.length);
                return true;
            }
            byte[] chunk = new byte[len];
            System.arraycopy(src, off, chunk, 0, len);
            Object v = self == null ? null : self.getValue();
            if (v instanceof OutStream os) {
                os.buf.write(chunk, 0, len);
                if (verbose) System.err.println("[jni] write(" + os.path + ") len=" + len
                        + " 累计=" + os.buf.size());
            } else {
                log("write(? receiver " + v + ") len=" + len);
            }
            return true;
        }
        if (sig.endsWith("close()V")) return true;
        return false;
    }

    // ═══════════ 资源读取：CallIntMethod ═══════════

    @Override
    public int callIntMethodV(BaseVM vm, DvmObject<?> o, String sig, VaList va) {
        Integer r = onCallInt(o, sig, a(va));
        return r != null ? r : super.callIntMethodV(vm, o, sig, va);
    }

    @Override
    public int callIntMethod(BaseVM vm, DvmObject<?> o, String sig, VarArg va) {
        Integer r = onCallInt(o, sig, a(va));
        return r != null ? r : super.callIntMethod(vm, o, sig, va);
    }

    private Integer onCallInt(DvmObject<?> self, String sig, A arg) {
        if ("java/io/InputStream->read([B)I".equals(sig)
                || "java/io/FileInputStream->read([B)I".equals(sig)) {
            Object v = self == null ? null : self.getValue();
            if (!(v instanceof ResStream rs)) {
                log("InputStream.read([B) -> -1（非资源流）");
                return -1;
            }
            Object arr = arg.o(0);
            if (!(arr instanceof ByteArray ba)) return -1;
            int remain = rs.data.length - rs.pos;
            if (remain <= 0) {
                log("read(" + rs.name + ") -> -1 EOF");
                return -1;
            }
            int n = Math.min(ba.getValue().length, remain);
            byte[] chunk = new byte[n];
            System.arraycopy(rs.data, rs.pos, chunk, 0, n);
            // ⚠ 必须用 setData 同步到模拟内存：壳随后会 GetByteArrayElements 拿指针读，
            // 只改 Java 侧数组的话 native 拿到的是全 0（解密产物会变成一堆 0）。
            try {
                ba.setData(0, chunk);
            } catch (Throwable t) {
                System.arraycopy(chunk, 0, ba.getValue(), 0, n);
            }
            rs.pos += n;
            log("read(" + rs.name + ") -> " + n + " 字节 (pos=" + rs.pos + "/" + rs.data.length + ")");
            return n;
        }
        return null;
    }

    // ═══════════ java.io.File 操作层（用真实宿主文件承载）═══════

    @Override
    public boolean callBooleanMethodV(BaseVM vm, DvmObject<?> o, String sig, VaList va) {
        Boolean r = onCallBoolean(sig, o);
        return r != null ? r : super.callBooleanMethodV(vm, o, sig, va);
    }

    @Override
    public boolean callBooleanMethod(BaseVM vm, DvmObject<?> o, String sig, VarArg va) {
        Boolean r = onCallBoolean(sig, o);
        return r != null ? r : super.callBooleanMethod(vm, o, sig, va);
    }

    private Boolean onCallBoolean(String sig, DvmObject<?> o) {
        File file = fileOf(o);
        if (file == null) return null;
        switch (sig) {
            case "java/io/File->exists()Z":
                log("File.exists() -> " + file.exists() + "  " + file);
                return file.exists();
            case "java/io/File->mkdirs()Z":
                log("File.mkdirs() -> " + file);
                return file.mkdirs() || file.isDirectory();
            case "java/io/File->mkdir()Z":
                return file.mkdir() || file.isDirectory();
            case "java/io/File->setReadOnly()Z":
                log("File.setReadOnly() -> " + file);
                return file.setReadOnly();
            case "java/io/File->delete()Z":
                log("File.delete() -> " + file);
                return !file.exists() || file.delete();
            case "java/io/File->isFile()Z":
                return file.isFile();
            case "java/io/File->isDirectory()Z":
                return file.isDirectory();
            case "java/io/File->canRead()Z":
                return file.canRead();
            case "java/io/File->canWrite()Z":
                return file.canWrite();
            case "java/io/File->createNewFile()Z": {
                log("File.createNewFile() -> " + file);
                try {
                    File parent = file.getParentFile();
                    if (parent != null) parent.mkdirs();
                    return file.createNewFile();
                } catch (java.io.IOException e) {
                    return true;
                }
            }
        }
        return null;
    }

    @Override
    public long callLongMethodV(BaseVM vm, DvmObject<?> o, String sig, VaList va) {
        Long r = onCallLong(sig, o);
        return r != null ? r : super.callLongMethodV(vm, o, sig, va);
    }

    @Override
    public long callLongMethod(BaseVM vm, DvmObject<?> o, String sig, VarArg va) {
        Long r = onCallLong(sig, o);
        return r != null ? r : super.callLongMethod(vm, o, sig, va);
    }

    private Long onCallLong(String sig, DvmObject<?> o) {
        File file = fileOf(o);
        if (file == null) return null;
        if ("java/io/File->length()J".equals(sig)) {
            log("File.length() -> " + file.length() + "  " + file);
            return file.length();
        }
        if ("java/io/File->lastModified()J".equals(sig)) return file.lastModified();
        return null;
    }
}
