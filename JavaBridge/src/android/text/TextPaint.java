package android.text;

/**
 * <code>android.text.TextPaint</code> 桩。
 *
 * <p><b>为什么必须有</b>（2026-09-30 实测）：真机里 {@code TextPaint extends
 * android.graphics.Paint}。桥的「桩优先」命名空间里 {@code android.graphics.Paint} 是我们
 * 的空桩（为了绕开 guest ART 没有注册的 {@code Paint.nInit} native），而
 * {@code android.text.TextPaint} 以前没进桩表 ⇒ jar 解析到 platform 的 TextPaint
 * （它 extends platform Paint），于是 jar 里 {@code z.P1} 这种「拿 TextPaint 当 Paint 用」
 * 的字节码过不了 ART verifier：
 * <pre>
 * VerifyError: Verifier rejected class ...z.P1:  android.widget.LinearLayout ...a(...)
 *   [0xC3] 'this' argument 'Reference: android.text.TextPaint' not instance of
 *          'Reference: android.graphics.Paint'
 * </pre>
 * 扫码/网盘配置那条路就是死在这个类上（{@code action("quark_scan")} 直接回错，
 * 宿主连 ui-dialog 都收不到）。补上这层，桩命名空间里的继承链才与真机一致。</p>
 *
 * <p>字段/构造按真机签名保留：jar 里常见 {@code new TextPaint()}、{@code new
 * TextPaint(Paint)} 与读 {@code bgColor}/{@code baselineShift}/{@code density}。
 * 方法不用逐个转发 —— 父类桩本来就是「不崩 + 合理值」。</p>
 */
public class TextPaint extends android.graphics.Paint {

    public int bgColor;
    public int baselineShift;
    public float density = 1f;
    public int linkColor;
    public int drawableState;

    public TextPaint() { }

    public TextPaint(int flags) { }

    public TextPaint(android.graphics.Paint p) { }

    public void set(TextPaint tp) {
        if (tp == null) return;
        this.bgColor = tp.bgColor;
        this.baselineShift = tp.baselineShift;
        this.density = tp.density;
        this.linkColor = tp.linkColor;
        this.drawableState = tp.drawableState;
    }
}
