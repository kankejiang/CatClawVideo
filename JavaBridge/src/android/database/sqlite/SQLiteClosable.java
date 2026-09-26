package android.database.sqlite;

/**
 * {@code android.database.sqlite.SQLiteClosable} 桩 —— 真 Android 里它是 SQLiteDatabase
 * 的父类。桩侧**必须保持这条继承链**：壳代码（merge.Rc 等）在 SQLiteDatabase 实例上调
 * 父类方法时，ART 校验器按解析到的声明类做 instance-of 检查，桩不继承直接
 * VerifyError 拒类（2026-09-26 guest 桩链实测）。
 */
public abstract class SQLiteClosable {

    public void close() { }

    public void releaseReference() { }

    public void releaseLastReference() { }

    public boolean acquireReference() { return true; }

    protected void onAllReferencesReleased() { }

    protected void onAllReferencesReleasedFromContainer() { }
}
