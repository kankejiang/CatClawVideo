package android.view;

/**
 * <code>android.view.ContextThemeWrapper</code> 桩 —— 真机 Activity 的直接父类。
 *
 * <p>真机链：{@code Activity → ContextThemeWrapper(android.view) → ContextWrapper → Context}。
 * 桩以前把 ContextThemeWrapper 建在了 <b>{@code android.content}</b> 包里（链的形状对、包名错），
 * 于是按真机全名 {@code android.view.ContextThemeWrapper} 引用的字节码解析不到这一层
 * —— 这正是 {@code tools/check_stub_parents.py} 判出来的第 ① 条真错。</p>
 *
 * <p>⚠ 旧的 {@code android/content/ContextThemeWrapper.java} 已随本次修正删除；
 * 引用它的地方（只有 {@code android.app.Activity} 的 import）一起改成 android.view。</p>
 */
public class ContextThemeWrapper extends android.content.ContextWrapper {

    public ContextThemeWrapper() { }

    public ContextThemeWrapper(android.content.Context base) { super(base); }
}
