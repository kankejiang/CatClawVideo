package android.os;

/**
 * <code>android.os.BaseBundle</code> 桩 —— 真机 Bundle 的直接父类（标记父类，见 {@link android.view.InputEvent} 的两条理由）。
 *
 * <p>桩里 Bundle 自己带存储与访问器，这里只把继承链接上：壳凡按 {@code BaseBundle} 形参/接收者
 * 用 Bundle 的代码，在 ART 校验期需要这条链才不被拒。</p>
 */
public class BaseBundle {
}
