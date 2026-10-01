package android.view;

/** android.view.View 上各类监听器的桩实现（spider 常以匿名类 implements 它们，
 *  类加载时若缺这些内部类型会直接 ClassNotFoundException）。 */
public class MotionEvent extends InputEvent {
    public static final int ACTION_DOWN = 0;
    public static final int ACTION_UP = 1;
    public static final int ACTION_MOVE = 2;
    public static final int ACTION_CANCEL = 3;

    public int getAction() { return ACTION_DOWN; }
    public float getX() { return 0f; }
    public float getY() { return 0f; }
    public float getRawX() { return 0f; }
    public float getRawY() { return 0f; }
    public long getEventTime() { return System.currentTimeMillis(); }
    public int getPointerCount() { return 1; }

    // ── 自动补齐：jar 引用到但桩缺失的成员（android-33 签名，空实现）──
    public int getActionMasked() { return 0; }
}
