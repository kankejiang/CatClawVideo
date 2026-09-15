package android.widget;

import android.database.DataSetObserver;
import android.view.View;
import android.view.ViewGroup;

/** BaseAdapter 桩：所有接口方法都给**具体**实现 ——
 *  spider 的子类只覆写其中几个，若这里留抽象方法会在运行期抛 AbstractMethodError。 */
public class BaseAdapter implements ListAdapter, SpinnerAdapter {

    public BaseAdapter() { }

    @Override public int getCount() { return 0; }
    @Override public Object getItem(int position) { return null; }
    @Override public long getItemId(int position) { return position; }
    @Override public View getView(int position, View convertView, ViewGroup parent) { return convertView; }
    @Override public boolean isEmpty() { return getCount() == 0; }
    @Override public void registerDataSetObserver(DataSetObserver observer) { }
    @Override public void unregisterDataSetObserver(DataSetObserver observer) { }
    @Override public int getItemViewType(int position) { return 0; }
    @Override public int getViewTypeCount() { return 1; }
    @Override public boolean hasStableIds() { return false; }
    @Override public boolean areAllItemsEnabled() { return true; }
    @Override public boolean isEnabled(int position) { return true; }
    @Override public View getDropDownView(int position, View convertView, ViewGroup parent) { return getView(position, convertView, parent); }

    public void notifyDataSetChanged() { }
    public void notifyDataSetInvalidated() { }
}
