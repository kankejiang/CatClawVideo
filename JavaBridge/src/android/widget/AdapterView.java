package android.widget;

import android.content.Context;
import android.util.AttributeSet;
import android.view.View;
import android.view.ViewGroup;

/** AdapterView 桩：ListView/GridView/Spinner 的共同父类（spider 常以它声明变量与设监听）。
 *  泛型参数与真实 Android 一致（<c>AdapterView&lt;T extends Adapter&gt;</c>），
 *  这样 spider 里 `AdapterView<?>` / `AdapterView&lt;ListAdapter&gt;` 的引用才能通过校验。 */
public class AdapterView<T extends Adapter> extends ViewGroup {

    public AdapterView() { super(); }
    public AdapterView(Context c) { super(c); }
    public AdapterView(Context c, AttributeSet attrs) { super(c, attrs); }

    public void setAdapter(T adapter) { }
    public T getAdapter() { return null; }
    public void setOnItemClickListener(Object listener) { }
    public void setOnItemLongClickListener(Object listener) { }
    public void setOnItemSelectedListener(Object listener) { }
    public int getCount() { return 0; }
    public Object getItemAtPosition(int position) { return null; }
    public long getItemIdAtPosition(int position) { return position; }
    public int getSelectedItemPosition() { return 0; }
    public Object getSelectedItem() { return null; }
    public void setSelection(int position) { }
    public void setEmptyView(View emptyView) { }
    public View getEmptyView() { return null; }

    public interface OnItemClickListener { void onItemClick(AdapterView<?> parent, View view, int position, long id); }
    public interface OnItemLongClickListener { boolean onItemLongClick(AdapterView<?> parent, View view, int position, long id); }
    public interface OnItemSelectedListener {
        void onItemSelected(AdapterView<?> parent, View view, int position, long id);
        void onNothingSelected(AdapterView<?> parent);
    }
}
