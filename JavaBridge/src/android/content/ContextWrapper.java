package android.content;

/**
 * <code>android.content.ContextWrapper</code> 桩。
 *
 * <p><b>为什么必须有这一层</b>（2026-09-30 实测）：guest 的 boot classpath 前置了这套 android 桩，
 * 壳里的 <c>android.app.Activity</c> 因此解析到桩类。真机的链是
 * <c>Activity → ContextThemeWrapper → ContextWrapper → Context</c>，而桩以前是
 * <c>Activity extends Context</c> —— 中间两层缺失，ART 验证器看到
 * 「register v3 has type Reference: android.app.Activity but expected Reference:
 * android.content.ContextWrapper」就直接判 <c>VerifyError</c>、整类被拒，
 * 用户侧表现为「荐片|磁力 不可用 · spider Jianpian 加载失败: VerifyError」（当天日志 76 条）。</p>
 *
 * <p>方法不用逐个转发：桩 {@link Context} 自己的实现就是可用的，继承下来即可。
 * <c>mBase</c> 保留成真机同名 protected 字段 —— 桥注入伪 Activity 与壳都按名字反射它。</p>
 */
public class ContextWrapper extends Context {

    /** 真机同名字段：桥（bridge.Art / FakeActivity）反射写它来给伪 Activity 接上基 Context。 */
    protected Context mBase;

    public ContextWrapper() { }

    public ContextWrapper(Context base) { attachBaseContext(base); }

    protected void attachBaseContext(Context base) { this.mBase = base; }

    /** 没注入过基 Context 时返回自己：桩的 Context 实现本来就能独立工作，返回 null 只会把调用方打 NPE。 */
    public Context getBaseContext() { return mBase != null ? mBase : this; }
}
