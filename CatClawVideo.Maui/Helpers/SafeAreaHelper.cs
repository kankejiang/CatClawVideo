namespace CatClawVideo.Maui;

/// <summary>
/// 跨平台 SafeArea 辅助：提供系统栏高度（dp）并通知页面更新 padding。
/// Android 平台由 MainActivity 的 insets 监听调用 UpdateInsets；Windows 平台默认为 0。
/// </summary>
public static class SafeAreaHelper
{
    /// <summary>系统栏顶部高度（状态栏），单位 dp</summary>
    public static double TopInset { get; private set; }

    /// <summary>系统栏底部高度（导航栏），单位 dp</summary>
    public static double BottomInset { get; private set; }

    /// <summary>更新系统栏高度并触发事件（由平台代码调用）</summary>
    public static void UpdateInsets(double topDp, double bottomDp)
    {
        bool changed = Math.Abs(topDp - TopInset) > 0.5 || Math.Abs(bottomDp - BottomInset) > 0.5;
        TopInset = topDp;
        BottomInset = bottomDp;

        if (changed)
            SafeAreaChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>系统栏高度变化时触发（页面订阅此事件以更新 padding）</summary>
    public static event EventHandler? SafeAreaChanged;
}
