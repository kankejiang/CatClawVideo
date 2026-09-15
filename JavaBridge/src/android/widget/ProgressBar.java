package android.widget;

import android.content.Context;
import android.util.AttributeSet;
import android.view.View;

/** ProgressBar 桩（真实父类是 View，保持同层级以免造型转换失配）。 */
public class ProgressBar extends View {

    public ProgressBar() { super(); }
    public ProgressBar(Context c) { super(c); }
    public ProgressBar(Context c, AttributeSet attrs) { super(c, attrs); }
    public ProgressBar(Context c, AttributeSet attrs, int defStyleAttr) { super(c, attrs); }

    public void setProgress(int progress) { }
    public int getProgress() { return 0; }
    public void setMax(int max) { }
    public int getMax() { return 100; }
    public void setIndeterminate(boolean indeterminate) { }
    public boolean isIndeterminate() { return false; }
    public void setSecondaryProgress(int secondaryProgress) { }
    public void incrementProgressBy(int diff) { }
}
