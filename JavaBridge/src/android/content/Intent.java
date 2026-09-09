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