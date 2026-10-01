package android.view;

/**
 * <code>android.view.InputEvent</code> 桩 —— 真机 KeyEvent/MotionEvent 的直接父类。
 *
 * <p>为什么补：桩以前让 KeyEvent/MotionEvent 直接挂 Object，而真机是
 * {@code KeyEvent → InputEvent}。壳里凡是把事件按父类型传的调用（{@code sendInputEvent(InputEvent)}、
 * 事件队列、{@code InputEvent[]} 数组）在 ART 校验期就会以
 * {@code 'this' argument not instance of 'android.view.InputEvent'} 拒掉整个方法 ——
 * 与 SeekBar/ProgressBar 那次同一形状（见 {@code tools/check_stub_parents.py} 头注）。</p>
 *
 * <p><b>故意只当"标记父类"</b>：这里不声明任何成员。原因有两条 ——
 * ① 真机这些访问器（getDeviceId/getSource/…）在桩里返回 0 就是"恒 0 getter"型静默丢数据
 * （见 {@code check_stubs.py} 的纪律），② 在补这一层之前，桩命名空间里根本没有 InputEvent 这个类，
 * 引用它的字节码本来就跑不通 ⇒ 加一个空父类只会更好，不会改坏任何现在能跑的路径。
 * 真需要这些方法时按调用点逐个补（能给出返回值语义再补）。</p>
 *
 * <p>⚠ 这里<b>不</b> {@code implements Parcelable}（真机有）：桩的 {@code KeyEvent} 没实现
 * {@code describeContents/writeToParcel}，加上会让具体子类编不过。等真有调用点再连着成员一起补。</p>
 */
public abstract class InputEvent {
}
