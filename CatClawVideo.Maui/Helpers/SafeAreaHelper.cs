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
}
