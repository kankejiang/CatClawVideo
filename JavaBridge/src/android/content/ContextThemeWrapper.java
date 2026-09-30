package android.content;

/**
 * <code>android.content.ContextThemeWrapper</code> 桩。
 *
 * <p>存在的唯一理由是把真机的继承链补全（见 {@link ContextWrapper} 的注释）：
 * 真机 <c>Activity extends ContextThemeWrapper</c>，壳里凡是把 Activity 当
 * ContextThemeWrapper/ContextWrapper 用的代码，都要靠这条链才能过 ART 验证器。</p>
 */
public class ContextThemeWrapper extends ContextWrapper {

    public ContextThemeWrapper() { }

    public ContextThemeWrapper(Context base) { super(base); }
}
