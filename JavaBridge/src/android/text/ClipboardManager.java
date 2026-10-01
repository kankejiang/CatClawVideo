package android.text;

/**
 * <code>android.text.ClipboardManager</code> 桩 —— 真机（已废弃的）{@code android.content.ClipboardManager}
 * 的直接父类，标记父类。
 *
 * <p>android.jar(API 33) 里 {@code public abstract class android.content.ClipboardManager extends
 * android.text.ClipboardManager} —— 桩以前让 content 那份直接挂 Object，凡是按老类型
 * {@code android.text.ClipboardManager} 收参数的调用（老壳里常见）在 ART 校验期就会被拒。
 * 真机的 {@code getText/setText}（CharSequence 版）不在这里造行为：桩里 content 那份已有自己的
 * clip 存取实现，补空父类只为把继承链接上。</p>
 */
public abstract class ClipboardManager {
}
