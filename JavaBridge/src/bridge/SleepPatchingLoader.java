package bridge;

import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.net.URL;
import java.net.URLClassLoader;
import java.util.jar.JarEntry;
import java.util.jar.JarFile;

/**
 * spider jar 的类加载器：类加载期经 {@link SleepPatcher} 把 {@code Thread.sleep} 改道到
 * 可伸缩的 {@code android.os.SystemClock.sleep}（扫码登录窗口的节拍拉长，见两者注释）。
 * 父优先委派不变：android.* 桩、桥自身类与 JDK 类不受影响。
 */
public class SleepPatchingLoader extends URLClassLoader {

    public SleepPatchingLoader(URL[] urls, ClassLoader parent) {
        super(urls, parent);
    }

    @Override
    protected Class<?> findClass(String name) throws ClassNotFoundException {
        String path = name.replace('.', '/') + ".class";
        for (URL url : getURLs()) {
            File f = new File(url.getFile());
            if (!f.isFile()) continue;
            try (JarFile jar = new JarFile(f)) {
                JarEntry entry = jar.getJarEntry(path);
                if (entry == null) continue;
                byte[] raw;
                try (InputStream in = jar.getInputStream(entry)) {
                    raw = in.readAllBytes();
                }
                byte[] patched = SleepPatcher.patch(raw);
                return defineClass(name, patched, 0, patched.length);
            } catch (IOException ignored) {
                // 换下一个 URL 再找
            }
        }
        throw new ClassNotFoundException(name);
    }
}
