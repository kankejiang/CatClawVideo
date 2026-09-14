package com.catclaw.video;

import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.lang.reflect.Array;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.util.zip.ZipEntry;
import java.util.zip.ZipOutputStream;

/**
 * Guard 解密产物（明文 dex）导出器。
 *
 * <p><b>为什么需要</b>：Guard 加固 jar 的解密器是 ARM Android native（{@code assets/*.so}，
 * 用 {@code JNI_OnLoad} + {@code RegisterNatives} 动态注册，只有 arm64/armv7），
 * Windows 上无法执行。但 {@code DexNative.getLoader(context)} 返回的是 <b>DexClassLoader</b>，
 * 而 Android 8+ 的 DexClassLoader 必须由磁盘上的 dex 文件支撑
 * ⇒ <b>解密后的明文 dex 就在磁盘上，可以导出</b>。
 * 导出后交给 PC 端当普通 jar 用（dex2jar → 加载去 Guard 后缀的真实类），
 * 一次导出即可让该 jar 的全部 Guard 站点在 Windows 可用，且之后不依赖手机。</p>
 *
 * <p><b>为什么写在 Java 侧</b>：要从 {@code DexPathList.dexElements} 里取元素，
 * 而 C# 经 JNI 反射拿不到 Java 数组（{@code Java.Lang.Object} 既 cast 不到 {@code Object[]}，
 * 也接不住 {@code JavaObjectArray} 的构造签名变化）。Java 侧直接遍历最稳。</p>
 */
public final class DexExporter {

    private DexExporter() {
    }

    /**
     * 从 DexClassLoader 实例里取出它实际加载的 dex 文件路径。
     * 取不到返回 null（不抛）。
     */
    public static String dexPathOf(Object classLoader) {
        if (classLoader == null) {
            return null;
        }
        try {
            Object pathList = getField(classLoader.getClass(), "pathList", classLoader);
            if (pathList == null) {
                return null;
            }
            Object elements = getField(pathList.getClass(), "dexElements", pathList);
            if (elements == null || !elements.getClass().isArray()) {
                return null;
            }
            int n = Array.getLength(elements);
            for (int i = 0; i < n; i++) {
                Object el = Array.get(elements, i);
                if (el == null) {
                    continue;
                }
                // ① Element.dexFile → DexFile.mFileName / getPath()
                Object dexFile = getField(el.getClass(), "dexFile", el);
                if (dexFile != null) {
                    String p = (String) getField(dexFile.getClass(), "mFileName", dexFile);
                    if (p == null) {
                        p = callString(dexFile, "getPath");
                    }
                    if (p != null && !p.isEmpty()) {
                        return p;
                    }
                }
                // ② 退路：Element.path（File）
                Object path = getField(el.getClass(), "path", el);
                if (path instanceof File) {
                    String p = ((File) path).getAbsolutePath();
                    if (!p.isEmpty()) {
                        return p;
                    }
                }
            }
        } catch (Throwable ignored) {
        }
        return null;
    }

    /**
     * 把 dex 打包成 jar（内含 {@code classes.dex}）写到 outPath。
     * 用 jar 而不是裸 dex：dex2jar 与各类 jar 工具对 jar 形态兼容性最好。
     *
     * @return {@code "OK:<字节数>"} 或 {@code "ERR:<原因>"}
     */
    public static String packDexToJar(String dexPath, String outPath) {
        File src = new File(dexPath);
        if (!src.isFile()) {
            return "ERR:dex 文件不存在 " + dexPath;
        }
        File dst = new File(outPath);
        File parent = dst.getParentFile();
        if (parent != null && !parent.exists() && !parent.mkdirs()) {
            return "ERR:无法创建目录 " + parent;
        }
        try (ZipOutputStream zos = new ZipOutputStream(new FileOutputStream(dst))) {
            zos.setLevel(1);   // 快压：dex 本来就难压，别浪费手机 CPU
            zos.putNextEntry(new ZipEntry("classes.dex"));
            try (FileInputStream in = new FileInputStream(src)) {
                byte[] buf = new byte[64 * 1024];
                int len;
                while ((len = in.read(buf)) > 0) {
                    zos.write(buf, 0, len);
                }
            }
            zos.closeEntry();
        } catch (Throwable t) {
            return "ERR:" + t.getClass().getSimpleName() + ": " + t.getMessage();
        }
        return "OK:" + dst.length();
    }

    // ───────── 反射小工具 ─────────

    private static Object getField(Class<?> cls, String name, Object target) {
        for (Class<?> c = cls; c != null && c != Object.class; c = c.getSuperclass()) {
            try {
                Field f = c.getDeclaredField(name);
                f.setAccessible(true);
                return f.get(target);
            } catch (NoSuchFieldException ignored) {
                // 继续往父类找
            } catch (Throwable ignored) {
                return null;
            }
        }
        return null;
    }

    private static String callString(Object target, String method) {
        try {
            Method m = target.getClass().getMethod(method);
            Object r = m.invoke(target);
            return r instanceof String ? (String) r : null;
        } catch (Throwable ignored) {
            return null;
        }
    }
}
