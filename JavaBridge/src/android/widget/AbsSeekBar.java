package android.widget;

/**
 * <code>android.widget.AbsSeekBar</code> 桩 —— 真机 SeekBar 的直接父类（标记父类）。
 *
 * <p>真机链：{@code SeekBar → AbsSeekBar → ProgressBar}。桩以前少一层 —— 而
 * {@code SeekBar → ProgressBar} 那次缺类正是扫码二维码整个对话框起不来的根因
 * （ART 校验期 {@code VerifyError: 'SeekBar' not instance of 'ProgressBar'}，
 * 见 {@code docs/调试报告-磁力播放链路-20260930.md} §十一）。补上这一层后
 * "当 ProgressBar 用"和"当 AbsSeekBar 用"两条都成立。</p>
 */
public abstract class AbsSeekBar extends ProgressBar {

    public AbsSeekBar() { }

    public AbsSeekBar(android.content.Context c) { super(c); }

    public AbsSeekBar(android.content.Context c, android.util.AttributeSet attrs) { super(c, attrs); }

    public AbsSeekBar(android.content.Context c, android.util.AttributeSet attrs, int defStyle) {
        super(c, attrs, defStyle);
    }
}
