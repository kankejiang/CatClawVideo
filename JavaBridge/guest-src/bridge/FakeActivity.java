package bridge;

import android.content.Context;
import android.view.Window;

/**
 * 无头 guest 里的伪 Activity（与 {@link FakeWindow} 配套）。
 *
 * <p>壳拿 Activity 的路子是 {@code ActivityThread.mActivities}（TVBox 的
 * {@code InitOrigin.getActivity()} 遍历该表）；guest 不是 zygote 起的，表里永远是空，
 * 壳因此拿到 null → {@code ProxyOrigin.getan()} 里 {@code getWindow()} NPE。
 * 这里造一个真 Activity 子类塞进表里，把那条支路补上。</p>
 *
 * <p>{@code getWindow()} 直接返回 {@link FakeWindow}（真 Activity 的 mWindow 只在
 * attach() 里由框架填，无头环境永远为 null），其余 UI 动作（startActivity/finish/
 * runOnUiThread）一律降级为同步执行或空操作并打日志。</p>
 */
public final class FakeActivity extends android.app.Activity {

    private Window mWin;

    /**
     * 空构造：不调 {@code attachBaseContext} —— 真 Activity 的实现会顺手做 autofill 注册
     * （{@code newBase.setAutofillClient(this)}），无头 guest 里那条链上还有 null，
     * 实测直接 NPE（2026-09-29）。基 Context 改由 {@code Art} 反射填
     * {@code ContextWrapper.mBase}，效果等价且不惊动框架。
     */
    public FakeActivity() {
    }

    @Override
    public Window getWindow() {
        if (mWin == null) mWin = new FakeWindow(this);
        return mWin;
    }

    // 注：Activity.runOnUiThread 是 final，覆写不了；真 Activity 里它走 mHandler（无头环境为
    // null）→ 壳若调它会 NPE。真出现再单独处理（把 mHandler 反射填成指向主线程的 Handler）。
    @Override
    public void startActivity(android.content.Intent intent) {
        System.err.println("[fakeact] startActivity 忽略: " + intent);
    }

    @Override
    public void finish() {
        System.err.println("[fakeact] finish 忽略");
    }
}
