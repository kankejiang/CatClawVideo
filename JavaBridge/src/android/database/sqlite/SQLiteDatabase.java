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
 *
 * <p>⚠️ 2026-10-01 网盘登录态排障：本桩原本**完全静默**，因此"壳有没有用 DB"无法判断。
 * guest 的 {@code framework.jar}（classes.dex…classes4.dex）里其实**有真的 SQLiteDatabase**
 * 且 {@code /system/lib64/libsqlite.so} 也在，但本桩在 boot classpath 里**前置**把它挡住了
 * ⇒ 壳若把登录态写数据库，就会「写入被丢弃、读取恒为空」，界面永远显示未登录。
 * 现在所有读写都打 {@code [sqlite]} 留痕，一次扫码即可判定要不要把它换成真实现。</p>
 */
public class SQLiteDatabase extends SQLiteClosable {

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

    /** 诊断留痕（截断 SQL，避免刷屏）。 */
    private static String cut(String s) {
        if (s == null) return "null";
        s = s.replace('\n', ' ').replace('\r', ' ');
        return s.length() > 150 ? s.substring(0, 150) + "…" : s;
    }

    private static void log(String s) { System.err.println("[sqlite] " + s); }

    public String getPath() { return path; }

    public static SQLiteDatabase openDatabase(String path, CursorFactory factory, int flags) {
        log("openDatabase(" + path + ", flags=0x" + Integer.toHexString(flags) + ")");
        return new SQLiteDatabase(path);
    }

    public static SQLiteDatabase openDatabase(String path, CursorFactory factory, int flags, Object errorHandler) {
        log("openDatabase(" + path + ", flags=0x" + Integer.toHexString(flags) + ", handler)");
        return new SQLiteDatabase(path);
    }

    public static SQLiteDatabase openOrCreateDatabase(String path, CursorFactory factory) {
        log("openOrCreateDatabase(" + path + ")");
        return new SQLiteDatabase(path);
    }

    public static SQLiteDatabase openOrCreateDatabase(java.io.File file, CursorFactory factory) {
        log("openOrCreateDatabase(" + (file == null ? "null" : file.getAbsolutePath()) + ")");
        return new SQLiteDatabase(file == null ? "" : file.getAbsolutePath());
    }

    public void execSQL(String sql) { log("execSQL: " + cut(sql)); }

    public void execSQL(String sql, Object[] bindArgs) {
        log("execSQL(" + (bindArgs == null ? 0 : bindArgs.length) + " 参): " + cut(sql));
    }

    public Cursor rawQuery(String sql, String[] selectionArgs) {
        log("rawQuery(" + (selectionArgs == null ? 0 : selectionArgs.length) + " 参) → 空游标: " + cut(sql));
        return new android.database.EmptyCursor();
    }

    public Cursor rawQuery(String sql, Object[] args) {
        log("rawQuery(" + (args == null ? 0 : args.length) + " 参, Object) → 空游标: " + cut(sql));
        return new android.database.EmptyCursor();
    }

    public Cursor rawQueryWithBindValues(String sql, Object[] args) {
        log("rawQueryWithBindValues → 空游标: " + cut(sql));
        return new android.database.EmptyCursor();
    }

    public Cursor query(String table, String[] columns, String selection, String[] selectionArgs,
                        String groupBy, String having, String orderBy) {
        log("query(table=" + table + ") → 空游标");
        return new android.database.EmptyCursor();
    }

    public Cursor query(String table, String[] columns, String selection, String[] selectionArgs,
                        String groupBy, String having, String orderBy, String limit) {
        log("query(table=" + table + ", limit=" + limit + ") → 空游标");
        return new android.database.EmptyCursor();
    }

    public long insert(String table, String nullColumnHack, android.content.ContentValues values) {
        log("insert(table=" + table + ", " + (values == null ? 0 : values.size()) + " 列) → 丢弃（回复 -1）");
        return -1;
    }

    public long insertOrThrow(String table, String nullColumnHack, android.content.ContentValues values) {
        log("insertOrThrow(table=" + table + ", " + (values == null ? 0 : values.size()) + " 列) → 丢弃（回复 -1）");
        return -1;
    }

    public int delete(String table, String whereClause, String[] whereArgs) {
        log("delete(table=" + table + ") → 丢弃");
        return 0;
    }

    public int update(String table, android.content.ContentValues values, String whereClause, String[] whereArgs) {
        log("update(table=" + table + ", " + (values == null ? 0 : values.size()) + " 列) → 丢弃");
        return 0;
    }

    public void close() { log("close()"); }

    public boolean isOpen() { return true; }

    public boolean isReadOnly() { return false; }

    public void beginTransaction() { log("beginTransaction()"); }

    public void setTransactionSuccessful() { log("setTransactionSuccessful()"); }

    public void endTransaction() { log("endTransaction()"); }

    // ── 自动补齐（第 2 批）：jar 引用到但桩缺失的成员 ──
    public long insertWithOnConflict(java.lang.String p0, java.lang.String p1, android.content.ContentValues p2, int p3) {
        log("insertWithOnConflict(table=" + p0 + ") → 丢弃（回复 0）");
        return 0L;
    }
}
