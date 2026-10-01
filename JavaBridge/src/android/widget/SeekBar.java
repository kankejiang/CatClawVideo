package android.widget;

import android.content.Context;
import android.util.AttributeSet;

/**
 * SeekBar 桩。
 *
 * <p><b>为什么必须自己写、且必须 extends ProgressBar</b>（2026-10-01 扫码链路实测）：桩命名空间
 * 里没有 SeekBar，jar 代码用到它时只能从 framework.jar 解析，而真 SeekBar 的父类是
 * **framework 的** ProgressBar —— 与 ui_stub.dex 里那份 ProgressBar 是两个不同的 Class。
 * 壳把 SeekBar 当 ProgressBar 用的那一行在 ART **校验期**就被拒：
 * {@code VerifyError: 'this' argument 'Precise Reference: android.widget.SeekBar'
 * not instance of 'Reference: android.widget.ProgressBar'}，整个异步任务死掉，
 * 二维码对话框根本没建起来（宿主收不到带 qr 的 ui-dialog）。</p>
 *
 * <p>纪律：桩一旦覆盖某个类（ProgressBar/TextView/…），真机里所有继承它的可见控件都要一起桩掉，
 * 否则就是同一类型两套身份。见 {@code JavaBridge/tools/check_stub_parents.py}。</p>
 */
public class SeekBar extends AbsSeekBar {

    /** 与真机同签名的监听器（壳用它在拖动时回写线程数/音量之类）。 */
    public interface OnSeekBarChangeListener {
        void onProgressChanged(SeekBar seekBar, int progress, boolean fromUser);

        void onStartTrackingTouch(SeekBar seekBar);

        void onStopTrackingTouch(SeekBar seekBar);
    }

    private int max = 100, progress;
    private OnSeekBarChangeListener listener;

    public SeekBar() { super(); }
    public SeekBar(Context c) { super(c); }
    public SeekBar(Context c, AttributeSet attrs) { super(c, attrs); }
    public SeekBar(Context c, AttributeSet attrs, int defStyle) { super(c, attrs, defStyle); }

    public void setMax(int m) { this.max = m; }

    public int getMax() { return max; }

    public void setProgress(int p) { setProgress(p, false); }

    /** 桌面没有拖动输入，只有程序赋值能算 fromUser=false；监听回调必须真的打出去（空覆写=静默丢数据）。 */
    public void setProgress(int p, boolean fromUser) {
        this.progress = p;
        OnSeekBarChangeListener l = listener;
        if (l != null) {
            try { l.onProgressChanged(this, p, fromUser); } catch (Throwable ignored) { }
        }
    }

    public int getProgress() { return progress; }

    public void setOnSeekBarChangeListener(OnSeekBarChangeListener l) { this.listener = l; }

    public OnSeekBarChangeListener getOnSeekBarChangeListener() { return listener; }

    /** 壳有时直接触发拖动回调（线程数设置里“立即生效”那一类）。 */
    public void simulateTouch(boolean start) {
        OnSeekBarChangeListener l = listener;
        if (l == null) return;
        try { if (start) l.onStartTrackingTouch(this); else l.onStopTrackingTouch(this); }
        catch (Throwable ignored) { }
    }
}
