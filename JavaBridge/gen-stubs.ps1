# Generate android stub classes for desktop JVM crawler loading.
# Usage: powershell -ExecutionPolicy Bypass -File gen-stubs.ps1
$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot 'src\android'

function Write-Stub([string]$rel, [string]$content) {
  $path = Join-Path $root ($rel -replace '/', '\')
  $dir = Split-Path $path -Parent
  if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
  [System.IO.File]::WriteAllText($path, $content, (New-Object System.Text.UTF8Encoding($false)))
  Write-Host "  + $rel"
}

Write-Stub 'annotation/SuppressLint.java' @'
package android.annotation;

public @interface SuppressLint { String[] value() default {}; }
'@

Write-Stub 'app/ProgressDialog.java' @'
package android.app;

public class ProgressDialog extends AlertDialog {
    public void setMessage(CharSequence message) { }
    public void setIndeterminate(boolean indeterminate) { }
    public void setProgress(int value) { }
}
'@

Write-Stub 'content/ClipboardManager.java' @'
package android.content;

public class ClipboardManager {
    public void setPrimaryClip(ClipData clip) { }
    public ClipData getPrimaryClip() { return null; }
    public boolean hasPrimaryClip() { return false; }
}
'@

Write-Stub 'content/ClipData.java' @'
package android.content;

public class ClipData {
    public static ClipData newPlainText(CharSequence label, CharSequence text) { return new ClipData(); }
    public CharSequence getText() { return ""; }
}
'@

Write-Stub 'content/ComponentName.java' @'
package android.content;

public class ComponentName {
    private final String pkg;
    private final String cls;
    public ComponentName(String pkg, String cls) { this.pkg = pkg; this.cls = cls; }
    public ComponentName(Context pkg, String cls) { this.pkg = ""; this.cls = cls; }
    public String getPackageName() { return pkg; }
    public String getClassName() { return cls; }
    public String flattenToString() { return pkg + "/" + cls; }
    public String flattenToShortString() { return flattenToString(); }
    public static ComponentName unflattenFromString(String str) {
        int slash = str.indexOf('/');
        if (slash < 0) return new ComponentName("", str);
        return new ComponentName(str.substring(0, slash), str.substring(slash + 1));
    }
    @Override public String toString() { return "ComponentInfo{" + pkg + "/" + cls + "}"; }
}
'@

Write-Stub 'content/ContentProvider.java' @'
package android.content;

public class ContentProvider { }
'@

Write-Stub 'content/ContentResolver.java' @'
package android.content;

public class ContentResolver { }
'@

Write-Stub 'content/Intent.java' @'
package android.content;

import android.net.Uri;

public class Intent {
    private ComponentName component;
    public Intent() { }
    public Intent(Context packageContext, Class<?> cls) { }
    public Intent(String action, Uri uri) { }
    public Intent setComponent(ComponentName component) { this.component = component; return this; }
    public Intent setClass(Context packageContext, Class<?> cls) { return this; }
    public Intent setPackage(String packageName) { return this; }
    public Intent setFlags(int flags) { return this; }
    public Intent putExtra(String name, String value) { return this; }
    public Intent putExtra(String name, int value) { return this; }
    public Intent putExtra(String name, boolean value) { return this; }
    public ComponentName getComponent() { return component; }
    public String getStringExtra(String name) { return null; }
    public String getAction() { return null; }
    public Uri getData() { return null; }
}
'@

Write-Stub 'content/pm/PackageItemInfo.java' @'
package android.content.pm;

public class PackageItemInfo {
    public String name = "";
    public String packageName = "";
}
'@

Write-Stub 'content/pm/PackageManager.java' @'
package android.content.pm;

public class PackageManager {
    public PackageInfo getPackageInfo(String packageName, int flags) { return new PackageInfo(); }
    public ApplicationInfo getApplicationInfo(String packageName, int flags) { return new ApplicationInfo(); }
}
'@

Write-Stub 'content/pm/ProviderInfo.java' @'
package android.content.pm;

public class ProviderInfo extends PackageItemInfo { }
'@

Write-Stub 'content/res/ColorStateList.java' @'
package android.content.res;

public class ColorStateList { }
'@

Write-Stub 'content/res/Configuration.java' @'
package android.content.res;

public class Configuration { }
'@

Write-Stub 'content/res/Resources.java' @'
package android.content.res;

public class Resources { }
'@

Write-Stub 'content/res/XmlResourceParser.java' @'
package android.content.res;

public class XmlResourceParser { }
'@

Write-Stub 'graphics/Bitmap.java' @'
package android.graphics;

public class Bitmap { }
'@

Write-Stub 'graphics/BitmapFactory.java' @'
package android.graphics;

public class BitmapFactory {
    public static Bitmap decodeByteArray(byte[] data, int offset, int length) { return null; }
    public static Bitmap decodeFile(String pathName) { return null; }
}
'@

Write-Stub 'graphics/Canvas.java' @'
package android.graphics;

public class Canvas { }
'@

Write-Stub 'graphics/Paint.java' @'
package android.graphics;

public class Paint { }
'@

Write-Stub 'graphics/Path.java' @'
package android.graphics;

public class Path { }
'@

Write-Stub 'graphics/drawable/Drawable.java' @'
package android.graphics.drawable;

public abstract class Drawable { }
'@

Write-Stub 'graphics/drawable/BitmapDrawable.java' @'
package android.graphics.drawable;

public class BitmapDrawable extends Drawable { }
'@

Write-Stub 'graphics/drawable/ColorDrawable.java' @'
package android.graphics.drawable;

public class ColorDrawable extends Drawable { }
'@

Write-Stub 'graphics/drawable/GradientDrawable.java' @'
package android.graphics.drawable;

public class GradientDrawable extends Drawable { }
'@

Write-Stub 'graphics/drawable/StateListDrawable.java' @'
package android.graphics.drawable;

public class StateListDrawable extends Drawable { }
'@

Write-Stub 'net/UrlQuerySanitizer.java' @'
package android.net;

public class UrlQuerySanitizer {
    public UrlQuerySanitizer() { }
    public UrlQuerySanitizer(String url) { }
    public void parseUrl(String url) { }
    public java.util.Set<String> getParameterKeys() { return new java.util.HashSet<>(); }
    public String getValue(String parameter) { return null; }
}
'@

Write-Stub 'net/wifi/WifiInfo.java' @'
package android.net.wifi;

public class WifiInfo { }
'@

Write-Stub 'net/wifi/WifiManager.java' @'
package android.net.wifi;

public class WifiManager {
    public WifiInfo getConnectionInfo() { return new WifiInfo(); }
}
'@

Write-Stub 'os/AsyncTask.java' @'
package android.os;

public abstract class AsyncTask<Params, Progress, Result> { }
'@

Write-Stub 'os/Bundle.java' @'
package android.os;

public class Bundle {
    public String getString(String key) { return null; }
    public void putString(String key, String value) { }
}
'@

Write-Stub 'os/SystemClock.java' @'
package android.os;

public class SystemClock {
    public static long elapsedRealtime() { return System.currentTimeMillis(); }
    public static long currentThreadTimeMillis() { return System.currentTimeMillis(); }
}
'@

Write-Stub 'provider/Settings.java' @'
package android.provider;

public class Settings {
    public static final class Secure {
        public static String getString(android.content.ContentResolver resolver, String name) { return null; }
    }
    public static final class System {
        public static String getString(android.content.ContentResolver resolver, String name) { return null; }
    }
}
'@

Write-Stub 'QuickJSLoader.java' @'
package android;

public class QuickJSLoader { }
'@

Write-Stub 'util/DisplayMetrics.java' @'
package android.util;

public class DisplayMetrics {
    public int widthPixels;
    public int heightPixels;
    public float density = 1.0f;
}
'@

Write-Stub 'util/Pair.java' @'
package android.util;

public class Pair<F, S> {
    public final F first;
    public final S second;
    public Pair(F first, S second) { this.first = first; this.second = second; }
    public static <A, B> Pair<A, B> create(A a, B b) { return new Pair<>(a, b); }
}
'@

Write-Stub 'util/TypedValue.java' @'
package android.util;

public class TypedValue {
    public float applyDimension(int unit, float value, DisplayMetrics metrics) { return value; }
}
'@

Write-Stub 'view/KeyEvent.java' @'
package android.view;

public class KeyEvent { }
'@

Write-Stub 'view/ViewParent.java' @'
package android.view;

public class ViewParent { }
'@

Write-Stub 'view/ViewTreeObserver.java' @'
package android.view;

public class ViewTreeObserver { }
'@

Write-Stub 'view/WindowManager.java' @'
package android.view;

public class WindowManager { }
'@

Write-Stub 'widget/Button.java' @'
package android.widget;

public class Button extends TextView { }
'@

Write-Stub 'widget/FrameLayout.java' @'
package android.widget;

public class FrameLayout extends ViewGroup { }
'@

Write-Stub 'widget/HorizontalScrollView.java' @'
package android.widget;

public class HorizontalScrollView extends FrameLayout { }
'@

Write-Stub 'widget/ImageView.java' @'
package android.widget;

public class ImageView extends View { }
'@

Write-Stub 'widget/ScrollView.java' @'
package android.widget;

public class ScrollView extends FrameLayout { }
'@

Write-Host "stubs generated."
