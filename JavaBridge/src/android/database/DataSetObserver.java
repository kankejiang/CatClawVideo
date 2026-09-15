package android.database;

/** 数据集变化观察者（Adapter.registerDataSetObserver 的参数类型）。 */
public abstract class DataSetObserver {
    public void onChanged() { }
    public void onInvalidated() { }
}
