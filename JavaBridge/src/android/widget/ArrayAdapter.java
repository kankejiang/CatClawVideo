package android.widget;

import android.content.Context;
import java.util.List;

/** ArrayAdapter 桩（泛型按类型擦除后的签名提供）。 */
public class ArrayAdapter<T> extends BaseAdapter {

    public ArrayAdapter(Context context, int resource) { }
    public ArrayAdapter(Context context, int resource, T[] objects) { }
    public ArrayAdapter(Context context, int resource, List<T> objects) { }
    public ArrayAdapter(Context context, int resource, int textViewResourceId) { }

    public void add(T object) { }
    public void addAll(List<T> collection) { }
    public void clear() { }
    public void setNotifyOnChange(boolean notifyOnChange) { }
}
