package android.widget;
import android.content.Context;
import android.util.AttributeSet;
import android.view.View;
public class TextView extends View {
    public TextView() { super(); }
    public TextView(Context c) { super(c); }
    public TextView(Context c, AttributeSet attrs) { super(c, attrs); }
    public void setText(CharSequence t) { }
    public CharSequence getText() { return ""; }
    public void setTextSize(float size) { }
    public void setTextColor(int color) { }
    public void setGravity(int gravity) { }
    public void setSingleLine(boolean singleLine) { }
    public void setMaxLines(int maxLines) { }
    public void setEllipsize(Object where) { }
}