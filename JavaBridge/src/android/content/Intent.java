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

    // ── 自动补齐：jar 引用到但桩缺失的成员（android-33 签名，空实现）──
    public Intent(android.content.Intent p0) { }
    public Intent(java.lang.String p0) { }
    public android.content.Intent setDataAndType(android.net.Uri p0, java.lang.String p1) { return null; }
    public android.content.Intent putExtra(java.lang.String p0, byte p1) { return null; }
    public android.content.Intent putExtra(java.lang.String p0, char p1) { return null; }
    public android.content.Intent putExtra(java.lang.String p0, short p1) { return null; }
    public android.content.Intent putExtra(java.lang.String p0, long p1) { return null; }
    public android.content.Intent putExtra(java.lang.String p0, float p1) { return null; }
    public android.content.Intent putExtra(java.lang.String p0, double p1) { return null; }
    public android.content.Intent putExtra(java.lang.String p0, java.lang.CharSequence p1) { return null; }
    public android.content.Intent putExtra(java.lang.String p0, android.os.Parcelable p1) { return null; }
    public android.content.Intent putExtra(java.lang.String p0, android.os.Parcelable[] p1) { return null; }
    public android.content.Intent putExtra(java.lang.String p0, java.io.Serializable p1) { return null; }
    public android.content.Intent putExtra(java.lang.String p0, boolean[] p1) { return null; }
    public android.content.Intent putExtra(java.lang.String p0, byte[] p1) { return null; }
    public android.content.Intent putExtra(java.lang.String p0, short[] p1) { return null; }
    public android.content.Intent putExtra(java.lang.String p0, char[] p1) { return null; }
    public android.content.Intent putExtra(java.lang.String p0, int[] p1) { return null; }
    public android.content.Intent putExtra(java.lang.String p0, long[] p1) { return null; }
    public android.content.Intent putExtra(java.lang.String p0, float[] p1) { return null; }
    public android.content.Intent putExtra(java.lang.String p0, double[] p1) { return null; }
    public android.content.Intent putExtra(java.lang.String p0, java.lang.String[] p1) { return null; }
    public android.content.Intent putExtra(java.lang.String p0, java.lang.CharSequence[] p1) { return null; }
    public android.content.Intent putExtra(java.lang.String p0, android.os.Bundle p1) { return null; }
    public android.content.Intent addFlags(int p0) { return null; }
    public android.content.Intent setClassName(android.content.Context p0, java.lang.String p1) { return null; }
    public android.content.Intent setClassName(java.lang.String p0, java.lang.String p1) { return null; }
}