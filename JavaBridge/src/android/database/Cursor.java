package android.database;

/**
 * <code>android.database.Cursor</code> 桩接口。
 *
 * <p>⚠️ 2026-09-24 实测：{@code InitOrigin.init(ctx)} 建库后立刻 {@code moveToFirst()}，
 * 原来只声明了 {@code close()} ⇒ {@code NoSuchMethodError}，被壳的 try/catch 吞掉，
 * 表现是「初始化静默失败」。桌面无真库，一律返回空结果。</p>
 */
public interface Cursor {

    int getCount();

    int getPosition();

    boolean moveToFirst();

    boolean moveToNext();

    boolean moveToPrevious();

    boolean moveToPosition(int position);

    boolean isFirst();

    boolean isLast();

    boolean isBeforeFirst();

    boolean isAfterLast();

    int getColumnCount();

    String getColumnName(int columnIndex);

    String[] getColumnNames();

    int getColumnIndex(String columnName);

    int getColumnIndexOrThrow(String columnName);

    String getString(int columnIndex);

    short getShort(int columnIndex);

    int getInt(int columnIndex);

    long getLong(int columnIndex);

    float getFloat(int columnIndex);

    double getDouble(int columnIndex);

    byte[] getBlob(int columnIndex);

    boolean isNull(int columnIndex);

    void close();

    boolean isClosed();

    boolean isNull(int columnIndex, boolean throwOnNull);
}
