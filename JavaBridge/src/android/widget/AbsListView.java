package android.widget;

/**
 * <code>android.widget.AbsListView</code> 桩 —— 真机 ListView 的直接父类（标记父类）。
 *
 * <p>真机链：{@code ListView → AbsListView → AdapterView<ListAdapter>}。桩以前是
 * {@code ListView → AdapterView} 少一层；壳里凡是把 ListView 当 AbsListView 用
 * （{@code setChoiceMode(int)}、{@code getCheckedItemPositions()}、滚动状态判断）的代码
 * 需要这一层存在才能过 ART 校验。成员仍由 {@link AdapterView} 提供，故这里只链接、不造行为。</p>
 */
public abstract class AbsListView extends AdapterView<ListAdapter> {

    public AbsListView() { }

    public AbsListView(android.content.Context c) { super(c); }

    public AbsListView(android.content.Context c, android.util.AttributeSet attrs) { super(c, attrs); }
}
