namespace CatClawVideo.Maui;

/// <summary>
/// 跨平台 SafeArea 辅助：实现已上移至共享库 CatClaw.Shared.Maui.Helpers（与猫爪音乐共用）。
/// 本类为静态转发占位（静态类不可继承），保持既有调用面——页面读 TopInset/BottomInset，
/// 平台代码（MainActivity）调用 UpdateInsets。命名空间不变，全部调用点零改动。
/// </summary>
public static class SafeAreaHelper
{
    /// <summary>系统栏顶部高度（状态栏），单位 dp</summary>
    public static double TopInset => CatClaw.Shared.Maui.Helpers.SafeAreaHelper.TopInset;

    /// <summary>系统栏底部高度（导航栏），单位 dp</summary>
    public static double BottomInset => CatClaw.Shared.Maui.Helpers.SafeAreaHelper.BottomInset;

    /// <summary>更新系统栏高度并触发事件（由平台代码调用）</summary>
    public static void UpdateInsets(double topDp, double bottomDp)
        => CatClaw.Shared.Maui.Helpers.SafeAreaHelper.UpdateInsets(topDp, bottomDp);

    /// <summary>系统栏高度变化时触发（页面订阅此事件以更新 padding）</summary>
    public static event EventHandler? SafeAreaChanged
    {
        add => CatClaw.Shared.Maui.Helpers.SafeAreaHelper.SafeAreaChanged += value;
        remove => CatClaw.Shared.Maui.Helpers.SafeAreaHelper.SafeAreaChanged -= value;
    }

    /// <summary>
    /// 给「Shell 推入式页面」补顶部安全区（<b>只补顶部</b>），让顶栏不压状态栏。
    ///
    /// <para><b>为什么需要</b>：本 App 是 Edge-to-Edge（<c>MainActivity</c> 里
    /// <c>SetDecorFitsSystemWindows(false)</c>），页面从 y=0 起绘。主页 <c>MainPage</c> 与
    /// <c>WatchPage</c> 都自己补了顶部 inset，但<b>搜索 / 源配置 / 关于 / 任务详情</b>这些
    /// <c>GoToAsync</c> 推入的页面没人补 → 顶栏那 14dp padding 远小于状态栏高度，内容侵入状态栏
    /// （2026-09-18 用户实测报告）。</para>
    ///
    /// <para><b>为什么不用官方 <c>SafeAreaEdges.Container</c></b>：它是整页四边内嵌，会连同
    /// <b>底部</b>一起缩进；而本 App 底部已由「窗口层手势条内嵌」处理（见
    /// <c>MainPage.LogLayoutChain</c>：实测 ~56dp，页面内部 padding 够不着，只能用负 margin 抵消）。
    /// 再叠一层底部内嵌 → 推入页底部会多出一条空白。只补顶部可完全避开这个叠加问题。</para>
    ///
    /// <para><b>关于时机</b>：推入页都在用户导航时才创建，那时 insets 早已上报，读一次即准；
    /// 万一页面在 insets 上报前就建好了（理论上只可能发生在启动首帧），
    /// 这里挂一个**一次性**订阅，首次上报补完立即退订 —— 不留静态订阅，避免页面被静态事件钉住。</para>
    /// </summary>
    public static void ApplyPageTopInset(Page page)
    {
        void Apply() => page.Padding = new Thickness(0, TopInset, 0, page.Padding.Bottom);

        Apply();
        if (TopInset > 0) return;

        EventHandler? once = null;
        once = (_, _) =>
        {
            SafeAreaChanged -= once;
            Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(Apply);
        };
        SafeAreaChanged += once;
    }
}
