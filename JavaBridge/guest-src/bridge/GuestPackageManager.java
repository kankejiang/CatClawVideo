package bridge;

import android.content.ComponentName;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.pm.ApplicationInfo;
import android.content.pm.ActivityInfo;
import android.content.pm.FeatureInfo;
import android.content.pm.InstrumentationInfo;
import android.content.pm.PackageInfo;
import android.content.pm.PackageInstaller;
import android.content.pm.PackageItemInfo;
import android.content.pm.PackageManager;
import android.content.pm.PermissionGroupInfo;
import android.content.pm.PermissionInfo;
import android.content.pm.ProviderInfo;
import android.content.pm.ResolveInfo;
import android.content.pm.ServiceInfo;
import android.content.pm.SharedLibraryInfo;
import android.content.pm.VersionedPackage;
import android.content.res.Resources;
import android.content.res.XmlResourceParser;
import android.graphics.Rect;
import android.graphics.drawable.Drawable;
import android.os.UserHandle;

import java.util.Collections;
import java.util.List;

/**
 * guest 端 PackageManager 桩：<b>继承 boot classpath 的真 android.content.pm.PackageManager</b>
 * （抽象类），给壳框架的 {@code ctx.getPackageManager().getPackageInfo(...)} 族调用一个不炸的实现。
 * 抽象方法清单来自 javap android-35 的真类（全部实现；超集覆盖 API 28 真类的抽象集）。
 *
 * <p><b>为什么单独编译（build.cmd 的第二段 javac，-cp android.jar）</b>：主编译的类路径里没有
 * 真 android.jar——桩环境里同名类型是我们自己写的桩。本类的 superclass 描述符是
 * {@code Landroid/content/pm/PackageManager;}，<b>两个运行环境各自解析</b>：</p>
 * <ul>
 *   <li>guest（真 ART）：解析到 boot 真抽象类 → 抽象方法全部实现（javap 清单），调用不炸。</li>
 *   <li>JRE 桥（桩环境）：解析到同名桩 PackageManager；桩环境里壳走桩 Context 自带的 getter，
 *       本类只是兜底，不受影响。</li>
 * </ul>
 *
 * <p><b>为什么由 {@code Art.App.getPackageManager()} 反射实例化</b>：主编译没有真类型符号，
 * 主源码里 {@code new GuestPackageManager()} 编不过；反射把「桩编译环境」与「guest 真类环境」
 * 的边界隔开（类缺失时回落 super，行为与现状一致）。</p>
 *
 * <p>实现面对齐 JRE 桩语义：getPackageInfo / getApplicationInfo 返回空壳记录
 * （packageName 填 {@code com.catclaw.video}，签名字段留 null——壳在 JRE 桥同样拿到 null 签名）；
 * 其余抽象方法返回 null/空集/占位常量。</p>
 */
public final class GuestPackageManager extends PackageManager {

    private static final String PACKAGE = "com.catclaw.video";

    /** 空壳记录：packageName/versionName 填包名，signatures 给占位（壳做签名数组运算，null 会 NPE）。 */
    private PackageInfo stubInfo() {
        PackageInfo pi = new PackageInfo();
        pi.packageName = PACKAGE;
        pi.versionName = "1.0";
        pi.signatures = new android.content.pm.Signature[]{
                new android.content.pm.Signature("3082015a3082010aa003020102020101")};
        return pi;
    }

    private ApplicationInfo stubApp() {
        ApplicationInfo ai = new ApplicationInfo();
        ai.packageName = PACKAGE;
        return ai;
    }

    // ═════════ 壳实际会调的（真实现）═════════

    @Override public PackageInfo getPackageInfo(String packageName, int flags) throws NameNotFoundException { return stubInfo(); }
    @Override public PackageInfo getPackageInfo(VersionedPackage versionedPackage, int flags) throws NameNotFoundException { return stubInfo(); }
    @Override public ApplicationInfo getApplicationInfo(String packageName, int flags) throws NameNotFoundException { return stubApp(); }

    // ═════════ 其余抽象方法：占位（javap android-35 清单全量）═════════

    @Override public String[] currentToCanonicalPackageNames(String[] names) { return names; }
    @Override public String[] canonicalToCurrentPackageNames(String[] names) { return names; }
    @Override public Intent getLaunchIntentForPackage(String packageName) { return null; }
    @Override public Intent getLeanbackLaunchIntentForPackage(String packageName) { return null; }
    @Override public int[] getPackageGids(String packageName) throws NameNotFoundException { return null; }
    @Override public int[] getPackageGids(String packageName, int flags) throws NameNotFoundException { return null; }
    @Override public int getPackageUid(String packageName, int flags) throws NameNotFoundException { return 0; }
    @Override public PermissionInfo getPermissionInfo(String name, int flags) throws NameNotFoundException { return null; }
    @Override public List<PermissionInfo> queryPermissionsByGroup(String group, int flags) throws NameNotFoundException { return Collections.emptyList(); }
    @Override public PermissionGroupInfo getPermissionGroupInfo(String name, int flags) throws NameNotFoundException { return null; }
    @Override public List<PermissionGroupInfo> getAllPermissionGroups(int flags) { return Collections.emptyList(); }
    @Override public ActivityInfo getActivityInfo(ComponentName component, int flags) throws NameNotFoundException { return null; }
    @Override public ActivityInfo getReceiverInfo(ComponentName component, int flags) throws NameNotFoundException { return null; }
    @Override public ServiceInfo getServiceInfo(ComponentName component, int flags) throws NameNotFoundException { return null; }
    @Override public ProviderInfo getProviderInfo(ComponentName component, int flags) throws NameNotFoundException { return null; }
    @Override public List<PackageInfo> getInstalledPackages(int flags) { return Collections.emptyList(); }
    @Override public List<PackageInfo> getPackagesHoldingPermissions(String[] permissions, int flags) { return Collections.emptyList(); }
    @Override public int checkPermission(String permName, String pkgName) { return PERMISSION_GRANTED; }
    @Override public boolean isPermissionRevokedByPolicy(String permName, String pkgName) { return false; }
    @Override public boolean addPermission(PermissionInfo info) { return false; }
    @Override public boolean addPermissionAsync(PermissionInfo info) { return false; }
    @Override public void removePermission(String name) { }
    @Override public int checkSignatures(String pkg1, String pkg2) { return SIGNATURE_MATCH; }
    @Override public int checkSignatures(int uid1, int uid2) { return SIGNATURE_MATCH; }
    @Override public String[] getPackagesForUid(int uid) { return new String[]{PACKAGE}; }
    @Override public String getNameForUid(int uid) { return PACKAGE; }
    @Override public List<ApplicationInfo> getInstalledApplications(int flags) { return Collections.emptyList(); }
    @Override public boolean isInstantApp() { return false; }
    @Override public boolean isInstantApp(String packageName) { return false; }
    @Override public int getInstantAppCookieMaxBytes() { return 0; }
    @Override public byte[] getInstantAppCookie() { return new byte[0]; }
    @Override public void clearInstantAppCookie() { }
    @Override public void updateInstantAppCookie(byte[] cookie) { }
    @Override public String[] getSystemSharedLibraryNames() { return null; }
    @Override public List<SharedLibraryInfo> getSharedLibraries(int flags) { return Collections.emptyList(); }
    @Override public android.content.pm.ChangedPackages getChangedPackages(int sequenceNumber) { return null; }
    @Override public FeatureInfo[] getSystemAvailableFeatures() { return new FeatureInfo[0]; }
    @Override public boolean hasSystemFeature(String feature) { return false; }
    @Override public boolean hasSystemFeature(String feature, int version) { return false; }
    @Override public ResolveInfo resolveActivity(Intent intent, int flags) { return null; }
    @Override public List<ResolveInfo> queryIntentActivities(Intent intent, int flags) { return Collections.emptyList(); }
    @Override public List<ResolveInfo> queryIntentActivityOptions(ComponentName caller, Intent[] specifics, Intent intent, int flags) { return Collections.emptyList(); }
    @Override public List<ResolveInfo> queryBroadcastReceivers(Intent intent, int flags) { return Collections.emptyList(); }
    @Override public ResolveInfo resolveService(Intent intent, int flags) { return null; }
    @Override public List<ResolveInfo> queryIntentServices(Intent intent, int flags) { return Collections.emptyList(); }
    @Override public List<ResolveInfo> queryIntentContentProviders(Intent intent, int flags) { return Collections.emptyList(); }
    @Override public ProviderInfo resolveContentProvider(String name, int flags) { return null; }
    @Override public List<ProviderInfo> queryContentProviders(String processName, int uid, int flags) { return Collections.emptyList(); }
    @Override public InstrumentationInfo getInstrumentationInfo(ComponentName component, int flags) throws NameNotFoundException { return null; }
    @Override public List<InstrumentationInfo> queryInstrumentation(String targetPackage, int flags) { return Collections.emptyList(); }
    @Override public Drawable getDrawable(String packageName, int resid, ApplicationInfo appInfo) { return null; }
    @Override public Drawable getActivityIcon(ComponentName activityName) throws NameNotFoundException { return null; }
    @Override public Drawable getActivityIcon(Intent intent) throws NameNotFoundException { return null; }
    @Override public Drawable getActivityBanner(ComponentName activityName) throws NameNotFoundException { return null; }
    @Override public Drawable getActivityBanner(Intent intent) throws NameNotFoundException { return null; }
    @Override public Drawable getDefaultActivityIcon() { return null; }
    @Override public Drawable getApplicationIcon(ApplicationInfo info) { return null; }
    @Override public Drawable getApplicationIcon(String packageName) throws NameNotFoundException { return null; }
    @Override public Drawable getApplicationBanner(ApplicationInfo info) { return null; }
    @Override public Drawable getApplicationBanner(String packageName) { return null; }
    @Override public Drawable getActivityLogo(ComponentName activityName) throws NameNotFoundException { return null; }
    @Override public Drawable getActivityLogo(Intent intent) throws NameNotFoundException { return null; }
    @Override public Drawable getApplicationLogo(ApplicationInfo info) { return null; }
    @Override public Drawable getApplicationLogo(String packageName) throws NameNotFoundException { return null; }
    @Override public Drawable getUserBadgedIcon(Drawable icon, UserHandle user) { return icon; }
    @Override public Drawable getUserBadgedDrawableForDensity(Drawable icon, UserHandle user, Rect density, int badgeDensity) { return icon; }
    @Override public CharSequence getUserBadgedLabel(CharSequence label, UserHandle user) { return label; }
    @Override public CharSequence getText(String packageName, int resid, ApplicationInfo appInfo) { return null; }
    @Override public XmlResourceParser getXml(String packageName, int resid, ApplicationInfo appInfo) { return null; }
    @Override public CharSequence getApplicationLabel(ApplicationInfo info) { return PACKAGE; }
    @Override public Resources getResourcesForActivity(ComponentName activityName) throws NameNotFoundException { return null; }
    @Override public Resources getResourcesForApplication(ApplicationInfo app) throws NameNotFoundException { return null; }
    @Override public Resources getResourcesForApplication(String appPackageName) throws NameNotFoundException { return null; }
    @Override public void verifyPendingInstall(int id, int verificationCode) { }
    @Override public void extendVerificationTimeout(int id, int verificationCodeAtTimeout, long millisecondsToDelay) { }
    @Override public void setInstallerPackageName(String targetPackage, String installerPackageName) { }
    @Override public String getInstallerPackageName(String packageName) { return null; }
    @Override public void addPackageToPreferred(String packageName) { }
    @Override public void removePackageFromPreferred(String packageName) { }
    @Override public List<PackageInfo> getPreferredPackages(int flags) { return Collections.emptyList(); }
    @Override public void addPreferredActivity(IntentFilter filter, int match, ComponentName[] set, ComponentName activity) { }
    @Override public void clearPackagePreferredActivities(String packageName) { }
    @Override public int getPreferredActivities(List<IntentFilter> outFilters, List<ComponentName> outActivities, String packageName) { return 0; }
    @Override public void setComponentEnabledSetting(ComponentName componentName, int newState, int flags) { }
    @Override public int getComponentEnabledSetting(ComponentName componentName) { return 0; }
    @Override public void setApplicationEnabledSetting(String packageName, int newState, int flags) { }
    @Override public int getApplicationEnabledSetting(String packageName) { return 0; }
    @Override public boolean isSafeMode() { return false; }
    @Override public void setApplicationCategoryHint(String packageName, int categoryHint) { }
    @Override public PackageInstaller getPackageInstaller() { return null; }
    @Override public boolean canRequestPackageInstalls() { return false; }
}
