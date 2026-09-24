package android.content;

/**
 * <code>android.content.ContentResolver</code> 桩。
 *
 * <p>⚠ 查询方法必须返回<b>非空</b>游标：真机上「查不到」也是一条零行 Cursor，
 * 爬虫普遍不判空（{@code resolver.query(...).moveToFirst()}）。返回 null 会让
 * 整段 init 被壳的 try/catch 吞掉（2026-09-25 桌面实测）。</p>
 */
public class ContentResolver {

    public android.database.Cursor query(android.net.Uri uri, String[] projection, String selection,
                                         String[] selectionArgs, String sortOrder) {
        return new android.database.EmptyCursor();
    }

    public android.database.Cursor query(android.net.Uri uri, String[] projection) {
        return new android.database.EmptyCursor();
    }

    public android.database.Cursor query(android.net.Uri uri, String[] projection, String selection,
                                         String[] selectionArgs, String sortOrder, String limit) {
        return new android.database.EmptyCursor();
    }

    public android.net.Uri insert(android.net.Uri uri, android.content.ContentValues values) { return uri; }

    public int update(android.net.Uri uri, android.content.ContentValues values, String selection,
                      String[] selectionArgs) { return 0; }

    public int delete(android.net.Uri uri, String selection, String[] selectionArgs) { return 0; }

    public void notifyChange(android.net.Uri uri, Object observer) { }

    public boolean takePersistableUriPermission(android.net.Uri uri, int flags) { return true; }

    public boolean releasePersistableUriPermission(android.net.Uri uri, int flags) { return true; }
}
