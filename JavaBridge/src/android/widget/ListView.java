package android.widget;

import android.content.Context;
import android.util.AttributeSet;
import android.view.ViewGroup;

/**
 * <code>android.widget.ListView</code> 桩：AlertDialog.getListView() 等调用需要它存在
 * （桌面无列表交互，仅保证方法链不抛 NoSuchMethodError）。
 */
public class ListView extends ViewGroup {
    public ListView() { super(); }
    public ListView(Context c) { super(c); }
    public ListView(Context c, AttributeSet attrs) { super(c, attrs); }

    public void setAdapter(ListAdapter adapter) { }
    public ListAdapter getAdapter() { return null; }
    public int getFirstVisiblePosition() { return 0; }
    public void setSelection(int position) { }
    public void smoothScrollToPosition(int position) { }
}
