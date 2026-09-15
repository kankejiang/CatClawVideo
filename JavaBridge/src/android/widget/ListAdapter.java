package android.widget;

/** ListView 用的适配器：spider 里常以 ListAdapter 声明变量 / implements。 */
public interface ListAdapter extends Adapter {
    boolean areAllItemsEnabled();
    boolean isEnabled(int position);
}
