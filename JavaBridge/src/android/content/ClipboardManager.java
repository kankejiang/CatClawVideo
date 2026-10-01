package android.content;

public class ClipboardManager extends android.text.ClipboardManager {
    public void setPrimaryClip(ClipData clip) { }
    public ClipData getPrimaryClip() { return null; }
    public boolean hasPrimaryClip() { return false; }
}