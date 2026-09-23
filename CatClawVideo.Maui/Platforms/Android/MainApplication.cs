using Android.App;
using Android.Runtime;

namespace CatClawVideo.Maui;

/// <summary>Android 应用入口类。</summary>
[Application]
public class MainApplication : MauiApplication
{
    public MainApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override void OnCreate()
    {
        base.OnCreate();
        // TVBox 爬虫兼容桥：把宿主 Application 注入 Java 侧同名兼容壳
        // （jar 反射 App.getInstance() 取 Context / getCurrentActivity()）
        Platforms.Android.TvBoxCompatBridge.AttachHost(this);
    }
}
