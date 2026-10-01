package android.graphics.drawable;

/**
 * <code>android.graphics.drawable.DrawableContainer</code> 桩 —— 真机 StateListDrawable 的直接父类（标记父类）。
 *
 * <p>真机链：{@code StateListDrawable → DrawableContainer → Drawable}。桩以前少一层；
 * 壳里凡是按 DrawableContainer 处理"容器型 drawable"（{@code setCurrentState}、mutate 传播）
 * 的代码需要这一层。{@link Drawable} 在桩里是 abstract 但没有抽象成员，所以这里可以是非抽象标记类。</p>
 */
public class DrawableContainer extends Drawable {
}
