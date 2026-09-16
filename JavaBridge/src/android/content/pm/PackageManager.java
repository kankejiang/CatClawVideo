package android.content.pm;

import java.util.ArrayList;
import java.util.List;

/**
 * <code>android.content.pm.PackageManager</code> 桩。
 *
 * <p>⚠ 嵌套的 {@link NameNotFoundException} <b>必须存在</b>：B 站系爬虫（Bili / 小学课堂 /
 * 初中课堂 / 少儿教育 等，同用类名 <c>Bili</c>）在 <c>catch</c> 块里捕获它，
 * 加载期解析常量池时就要解析该类型 → 缺了直接抛
 * <c>NoClassDefFoundError: android/content/pm/PackageManager$NameNotFoundException</c>，
 * 整站 load 失败（实测 2026-09-16，4 个站点因此全挂）。</p>
 *
 * <p>Java 里写成嵌套类即可，编译产物名就是 <c>PackageManager$NameNotFoundException</c>。</p>
 */
public class PackageManager {

    public PackageInfo getPackageInfo(String packageName, int flags) { return new PackageInfo(); }

    public PackageInfo getPackageInfo(String packageName, int flags, int userId) { return new PackageInfo(); }

    public ApplicationInfo getApplicationInfo(String packageName, int flags) { return new ApplicationInfo(); }

    public ApplicationInfo getApplicationInfo(String packageName, int flags, int userId) {
        return new ApplicationInfo();
    }

    public List<PackageInfo> getInstalledPackages(int flags) { return new ArrayList<>(); }

    public List<ApplicationInfo> getInstalledApplications(int flags) { return new ArrayList<>(); }

    /** Android 用 <c>AndroidException</c> 作基类；本宿主没有该桩，直接用 Exception（捕获处只认类型）。 */
    public static class NameNotFoundException extends Exception {
        public NameNotFoundException() { super(); }

        public NameNotFoundException(String name) { super(name); }
    }
}
