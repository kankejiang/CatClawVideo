package android.widget;

import android.database.DataSetObserver;
import android.view.View;
import android.view.ViewGroup;

/** android.widget.Adapter 桩：spider 常 implements/继承它做列表（缺类会 ClassNotFoundException）。 */
public interface Adapter {
    int getCount();
    Object getItem(int position);
    long getItemId(int position);
    View getView(int position, View convertView, ViewGroup parent);
    boolean isEmpty();
    void registerDataSetObserver(DataSetObserver observer);
    void unregisterDataSetObserver(DataSetObserver observer);
    int getItemViewType(int position);
    int getViewTypeCount();
    boolean hasStableIds();
}
