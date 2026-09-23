using Android.App;
using Android.Runtime;
using Com.Github.Catvod.Crawler;
using Com.Github.Tvbox.Osc.Base;
using Com.Github.Tvbox.Osc.Util;

namespace CatClawVideo.Maui.Platforms.Android;

/// <summary>
/// TVBox 爬虫兼容桥：Guard 系 jar（饭太硬 ftyshinidie 壳内 BaseSpiderGuard 家族）
/// 弹网盘配置对话框（「已登录+启用中」列表、扫码登录浮层）前，会反射
/// {@code com.github.tvbox.osc.base.App} / {@code com.github.tvbox.osc.util.AppManager}
/// 取**当前前台 Activity**（TVBox 语义；SpiderLog 可见 jar 输出 Activity 类名）。
///
/// <para>本桥把宿主的 MainActivity 生命周期上报给 Java 侧同名兼容类
/// （Platforms/Android/Java/ 下随包编译，AndroidJavaSource 已生成 C# 绑定），
/// jar 拿到可弹窗的 Activity 后，网盘配置 UI 即可与 TVBox 完全一致地工作。</para>
/// </summary>
internal static class TvBoxCompatBridge
{
    /// <summary>Activity 前台（MainActivity.OnResume）：置顶兼容层 Activity 栈 + 写 SpiderApi.currentActivity。</summary>
    public static void ReportActivity(Activity activity) => Run(() =>
    {
        AppManager.Instance!.AddActivity(activity);
        SpiderApi.SetCurrentActivity(activity);
    });

    /// <summary>Activity 销毁（MainActivity.OnDestroy）：出栈。</summary>
    public static void RemoveActivity(Activity activity) => Run(() =>
    {
        AppManager.Instance!.FinishActivity(activity);
    });

    /// <summary>应用启动（MainApplication.OnCreate）：把宿主 Application 注入兼容 App 壳。</summary>
    public static void AttachHost(global::Android.App.Application application) => Run(() =>
    {
        global::Com.Github.Tvbox.Osc.Base.App.AttachHost(application);
    });

    /// <summary>proxy 就绪后上报端口（SpiderApi.hostProxyPort，Guard jar 拼云盘配置 URL 用）。</summary>
    public static void SetProxyPort(int port) => Run(() =>
    {
        SpiderApi.SetHostProxyPort(port);
    });

    /// <summary>兼容层是「锦上添花」：任何 JNI 异常都只记日志，绝不影响宿主主流程。</summary>
    private static void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("TvBoxCompat", $"桥调用失败: {ex.Message}");
        }
    }
}
