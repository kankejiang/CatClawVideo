package android.database;

/**
 * <code>android.database.Cursor</code> 的「零行」实现。
 *
 * <p>⚠ 查询类 API <b>不能返回 null</b>：Android 真机上「没有命中」也是一条可用的空游标
 * （<code>getCount()==0</code>、<code>moveToFirst()==false</code>），爬虫因此普遍写成
 * <code>Cursor c = db.rawQuery(...); if (c.moveToFirst()) ...</code> 不做判空。返回 null 会
 * 在 <code>moveToFirst()</code> 处直接 NPE，而壳框架把整段 init 包在 try/catch 里
 * （实测 2026-09-25 桌面日志：{@code Cannot invoke "android.database.Cursor.moveToFirst()"
 * because "&lt;local3&gt;" is null} ×2），表现就是「初始化静默失败」——
 * 网盘源的登录态自检随之走不到。</p>
 */
public class EmptyCursor implements Cursor {

    @Override public int getCount() { return 0; }

    @Override public int getPosition() { return -1; }

    @Override public boolean moveToFirst() { return false; }

    @Override public boolean moveToNext() { return false; }

    @Override public boolean moveToPrevious() { return false; }

    @Override public boolean moveToPosition(int position) { return false; }

    @Override public boolean isFirst() { return false; }

    @Override public boolean isLast() { return false; }

    @Override public boolean isBeforeFirst() { return true; }

    @Override public boolean isAfterLast() { return true; }

    @Override public int getColumnCount() { return 0; }

    @Override public String getColumnName(int columnIndex) { return null; }

    @Override public String[] getColumnNames() { return new String[0]; }

    @Override public int getColumnIndex(String columnName) { return -1; }

    @Override public int getColumnIndexOrThrow(String columnName) {
        throw new IllegalStateException("Column '" + columnName + "' does not exist");
    }

    @Override public String getString(int columnIndex) { return ""; }

    @Override public short getShort(int columnIndex) { return 0; }

    @Override public int getInt(int columnIndex) { return 0; }

    @Override public long getLong(int columnIndex) { return 0; }

    @Override public float getFloat(int columnIndex) { return 0; }

    @Override public double getDouble(int columnIndex) { return 0; }

    @Override public byte[] getBlob(int columnIndex) { return new byte[0]; }

    @Override public boolean isNull(int columnIndex) { return true; }

    @Override public boolean isNull(int columnIndex, boolean throwOnNull) { return true; }

    @Override public void close() { }

    @Override public boolean isClosed() { return false; }
}
