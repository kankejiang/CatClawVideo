package android.database.sqlite;

import android.database.Cursor;

/**
 * <code>android.database.sqlite.SQLiteDatabase</code> 桩。
 *
 * <p>2026-09-24 实测：{@code InitOrigin.init(ctx)} → 壳框架建自己的库时调静态
 * {@code openDatabase(String, CursorFactory, int)}，缺它抛 NoSuchMethodError
 * （被壳的 try/catch 吞掉，表现是「初始化静默失败」）。</p>
 *
 * <p>桌面不做真 SQL：库路径记录下来，查询一律返回<b>非空的零行游标</b>（见
 * {@link android.database.EmptyCursor}——返回 null 会让爬虫在 moveToFirst() 上 NPE）。</p>
 */
public class SQLiteDatabase {

    // openDatabase 的 flags（jar 里按名引用）
    public static final int OPEN_READONLY = 0x00000001;
    public static final int OPEN_READWRITE = 0x00000000;
    public static final int CREATE_IF_NECESSARY = 0x10000000;
    public static final int NO_LOCALIZED_COLLATORS = 0x00000010;
    public static final int ENABLE_WRITE_AHEAD_LOGGING = 0x20000000;

    /**
     * Android 的 CursorFactory 内部接口。参数型别用 Object 占位——
     * 桌面侧不会真的产生游标，jar 传进来的通常是 null，只要类型存在、签名可解析即可。
     */
    public interface CursorFactory {
        Cursor newCursor(SQLiteDatabase db, Object masterQuery, String editTable, Object query);
    }

    private final String path;

    protected SQLiteDatabase(String path) { this.path = path == null ? "" : path; }

    public String getPath() { return path; }

    public static SQLiteDatabase openDatabase(String path, CursorFactory factory, int flags) {
        return new SQLiteDatabase(path);
    }

    public static SQLiteDatabase openDatabase(String path, CursorFactory factory, int flags, Object errorHandler) {
        return new SQLiteDatabase(path);
    }

    public static SQLiteDatabase openOrCreateDatabase(String path, CursorFactory factory) {
        return new SQLiteDatabase(path);
    }

    public static SQLiteDatabase openOrCreateDatabase(java.io.File file, CursorFactory factory) {
        return new SQLiteDatabase(file == null ? "" : file.getAbsolutePath());
    }

    public void execSQL(String sql) { }

    public void execSQL(String sql, Object[] bindArgs) { }

    public Cursor rawQuery(String sql, String[] selectionArgs) { return new android.database.EmptyCursor(); }

    public Cursor rawQuery(String sql, Object[] args) { return new android.database.EmptyCursor(); }

    public Cursor rawQueryWithBindValues(String sql, Object[] args) { return new android.database.EmptyCursor(); }

    public Cursor query(String table, String[] columns, String selection, String[] selectionArgs,
                        String groupBy, String having, String orderBy) { return new android.database.EmptyCursor(); }

    public Cursor query(String table, String[] columns, String selection, String[] selectionArgs,
                        String groupBy, String having, String orderBy, String limit) { return new android.database.EmptyCursor(); }

    public long insert(String table, String nullColumnHack, android.content.ContentValues values) { return -1; }

    public long insertOrThrow(String table, String nullColumnHack, android.content.ContentValues values) { return -1; }

    public int delete(String table, String whereClause, String[] whereArgs) { return 0; }

    public int update(String table, android.content.ContentValues values, String whereClause, String[] whereArgs) { return 0; }

    public void close() { }

    public boolean isOpen() { return true; }

    public boolean isReadOnly() { return false; }

    public void beginTransaction() { }

    public void setTransactionSuccessful() { }

    public void endTransaction() { }
}
