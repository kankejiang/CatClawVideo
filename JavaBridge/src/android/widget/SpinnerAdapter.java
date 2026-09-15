package android.widget;

import android.view.View;
import android.view.ViewGroup;

/** 下拉框适配器（BaseAdapter 同时实现 ListAdapter 与 SpinnerAdapter）。 */
public interface SpinnerAdapter extends Adapter {
    View getDropDownView(int position, View convertView, ViewGroup parent);
}
