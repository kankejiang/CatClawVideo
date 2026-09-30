package android.widget;
import android.content.Context;
public class EditText extends TextView {
    public EditText(Context c) { super(c); }
    public android.text.Editable getText() { return null; }

    // ── 自动补齐：jar 引用到但桩缺失的成员（android-33 签名，空实现）──
    public void setSelection(int p0) { }
    public void selectAll() { }
    public void setEllipsize(android.text.TextUtils.TruncateAt p0) { }
}
