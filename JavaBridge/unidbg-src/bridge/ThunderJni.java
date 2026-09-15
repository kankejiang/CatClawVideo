package bridge;

import com.github.unidbg.linux.android.dvm.AbstractJni;
import com.github.unidbg.linux.android.dvm.BaseVM;
import com.github.unidbg.linux.android.dvm.DvmClass;
import com.github.unidbg.linux.android.dvm.DvmObject;
import com.github.unidbg.linux.android.dvm.VaList;
import com.github.unidbg.linux.android.dvm.VarArg;

import java.io.File;

/**
 * 迅雷引擎的 JNI 回调实现（unidbg）。
 *
 * <p>先保持最小：{@link AbstractJni} 已为常见回调提供默认实现；
 * 未实现的会抛 {@code UnsupportedOperationException} —— 那时日志里会明确指出是哪个方法，
 * 我们再按需补（与 Guard 那边 {@code GuardJni} 的迭代方式一致）。</p>
 *
 * <p>这里额外做三件事：
 * <ol>
 *   <li>把 native 侧发起的 Java 回调**打印出来**，方便看清它到底要什么；</li>
 *   <li>提供一个"工作目录"概念：引擎要写文件/日志时落到这个目录，便于观察；</li>
 *   <li>不实现任何 Android Context / SharedPreferences 行为 —— 那些属于 Java 层，
 *       我们直接调 native 入口，本来就绕过了 Java 层校验。</li>
 * </ol>
 */
final class ThunderJni extends AbstractJni {

    private final File workDir;
    private int callbacks = 0;

    /**
     * native 回填进 Java 参数对象的值。
     * <p>这个引擎的 API 是「填充参数对象」风格 —— 例如
     * {@code getDownloadLibVersion(GetDownloadLibVersion v)} 由 native 把版本号写进 {@code v.mVersion}。
     * unidbg 的 {@link AbstractJni#setObjectField} 默认抛 {@code UnsupportedOperationException}，
     * 所以我们要在这里把值接住（unidbg 的 DvmObject 本身不存 Java 字段值）。</p>
     */
    private final java.util.Map<String, Object> captured = new java.util.LinkedHashMap<>();

    ThunderJni(File workDir) {
        this.workDir = workDir;
    }

    int callbackCount() {
        return callbacks;
    }

    File workDir() {
        return workDir;
    }

    /** 取 native 回填的字段值（字符串字段会直接给出 String） */
    Object captured(String field) {
        return captured.get(field);
    }

    String capturedString(String field) {
        Object v = captured.get(field);
        if (v == null) return "(未回填)";
        return String.valueOf(v);
    }

    /** 签名形如 {@code com/xunlei/.../GetDownloadLibVersion->mVersion:Ljava/lang/String;} */
    private static String fieldNameOf(String signature) {
        if (signature == null) return "?";
        int arrow = signature.indexOf("->");
        String s = arrow >= 0 ? signature.substring(arrow + 2) : signature;
        int colon = s.indexOf(':');
        return colon > 0 ? s.substring(0, colon) : s;
    }

    private static String classNameOf(String signature) {
        if (signature == null) return "?";
        int arrow = signature.indexOf("->");
        return arrow > 0 ? signature.substring(0, arrow) : "?";
    }

    // ───────── 字段回填（本阶段的关键）─────────

    @Override
    public void setObjectField(BaseVM vm, DvmObject<?> dvmObject, String signature, DvmObject<?> value) {
        callbacks++;
        String name = fieldNameOf(signature);
        Object v = value == null ? null : value.getValue();
        captured.put(name, v);
        System.err.println("  [jni] ★ 回填 " + classNameOf(signature) + "." + name + " = " + v);
    }

    @Override
    public void setIntField(BaseVM vm, DvmObject<?> dvmObject, String signature, int value) {
        callbacks++;
        String name = fieldNameOf(signature);
        captured.put(name, value);
        System.err.println("  [jni] ★ 回填 " + classNameOf(signature) + "." + name + " = " + value);
    }

    @Override
    public void setLongField(BaseVM vm, DvmObject<?> dvmObject, String signature, long value) {
        callbacks++;
        String name = fieldNameOf(signature);
        captured.put(name, value);
        System.err.println("  [jni] ★ 回填 " + classNameOf(signature) + "." + name + " = " + value);
    }

    @Override
    public void setBooleanField(BaseVM vm, DvmObject<?> dvmObject, String signature, boolean value) {
        callbacks++;
        String name = fieldNameOf(signature);
        captured.put(name, value);
        System.err.println("  [jni] ★ 回填 " + classNameOf(signature) + "." + name + " = " + value);
    }

    /** native 读参数对象上的字段（如 magnet 链接、保存路径）时走这里 */
    @Override
    public DvmObject<?> getObjectField(BaseVM vm, DvmObject<?> dvmObject, String signature) {
        callbacks++;
        String name = fieldNameOf(signature);
        System.err.println("  [jni] getObjectField " + classNameOf(signature) + "." + name);
        Object v = captured.get(name);
        if (v instanceof DvmObject<?> d) return d;
        return null;
    }

    // ───────── 回调观测：先看清它要什么，再决定实现哪个 ─────────

    @Override
    public DvmObject<?> callObjectMethodV(BaseVM vm, DvmObject<?> dvmObject, String signature, VaList vaList) {
        callbacks++;
        System.err.println("  [jni] callObjectMethodV  " + signature
                + "  on " + (dvmObject == null ? "null" : dvmObject.getObjectType()));
        return super.callObjectMethodV(vm, dvmObject, signature, vaList);
    }

    @Override
    public DvmObject<?> callObjectMethod(BaseVM vm, DvmObject<?> dvmObject, String signature, VarArg varArg) {
        callbacks++;
        System.err.println("  [jni] callObjectMethod   " + signature
                + "  on " + (dvmObject == null ? "null" : dvmObject.getObjectType()));
        return super.callObjectMethod(vm, dvmObject, signature, varArg);
    }

    @Override
    public DvmObject<?> callStaticObjectMethodV(BaseVM vm, DvmClass dvmClass, String signature, VaList vaList) {
        callbacks++;
        System.err.println("  [jni] callStaticObjectMethodV  " + signature + "  on " + dvmClass);
        return super.callStaticObjectMethodV(vm, dvmClass, signature, vaList);
    }

    @Override
    public DvmObject<?> callStaticObjectMethod(BaseVM vm, DvmClass dvmClass, String signature, VarArg varArg) {
        callbacks++;
        System.err.println("  [jni] callStaticObjectMethod   " + signature + "  on " + dvmClass);
        return super.callStaticObjectMethod(vm, dvmClass, signature, varArg);
    }

    @Override
    public void callVoidMethodV(BaseVM vm, DvmObject<?> dvmObject, String signature, VaList vaList) {
        callbacks++;
        System.err.println("  [jni] callVoidMethodV    " + signature);
        super.callVoidMethodV(vm, dvmObject, signature, vaList);
    }

    @Override
    public void callVoidMethod(BaseVM vm, DvmObject<?> dvmObject, String signature, VarArg varArg) {
        callbacks++;
        System.err.println("  [jni] callVoidMethod     " + signature);
        super.callVoidMethod(vm, dvmObject, signature, varArg);
    }

    @Override
    public DvmObject<?> newObjectV(BaseVM vm, DvmClass dvmClass, String signature, VaList vaList) {
        callbacks++;
        System.err.println("  [jni] newObjectV         " + signature + "  on " + dvmClass);
        return super.newObjectV(vm, dvmClass, signature, vaList);
    }

    @Override
    public DvmObject<?> newObject(BaseVM vm, DvmClass dvmClass, String signature, VarArg varArg) {
        callbacks++;
        System.err.println("  [jni] newObject          " + signature + "  on " + dvmClass);
        return super.newObject(vm, dvmClass, signature, varArg);
    }
}
