using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Media3.Common;
using AndroidX.Media3.Session;
using FgsType = Android.Content.PM.ForegroundService;

namespace CatClawVideo.Maui.Platforms.Android;

/// <summary>
/// 后台播放服务（对位 TVBox 的 <c>MusicPlaybackService</c>：锁屏/切后台后继续放音频并给通知栏控制）。
///
/// <para>用 Media3 的 <c>MediaSessionService</c>：会话直接包一个 <c>IPlayer</c>
/// （<c>MediaSession.Builder(context, player)</c>），**没有** 老 androidx 那套
/// <c>MediaSessionConnector</c>（1.10 的绑定里确实已经没有这个类型）。通知栏由 MediaSessionService
/// 自己维护，不用手写 notification。</para>
///
/// <para>类名用 <c>Name</c> 固定成 <c>catclaw.playback.PlaybackService</c>，别落进
/// <c>md5…/crc64…</c> 的生成名 —— 那样每次改代码 manifest 里的服务名都要跟着变。</para>
/// </summary>
// IntentFilter 不是 ServiceAttribute 的具名属性，得单独挂 [IntentFilter]；
// ForegroundServiceType 要写全限定名 —— 否则在特性实参里它会先解析成同名属性而不是枚举类型。
[Service(Name = "catclaw.playback.PlaybackService", Exported = true,
    ForegroundServiceType = FgsType.TypeMediaPlayback)]
[IntentFilter(new[] { "androidx.media3.session.MediaSessionService" })]
public sealed class PlaybackService : MediaSessionService
{
    private MediaSession? _session;
    private IPlayer? _sessionPlayer;

    /// <summary>系统/控制器来连时给一个会话；没有在播的播放器就返回 null（不造空会话）。</summary>
    public override MediaSession? OnGetSession(MediaSession.ControllerInfo? controllerInfo)
    {
        var player = PlayerHandoff.Current;
        if (player is null)
        {
            ReleaseSession();
            return null;
        }
        if (_session is not null && ReferenceEquals(_sessionPlayer, player)) return _session;

        ReleaseSession();
        try
        {
            // 不链式取返回值：绑定里 SetSessionActivity 的重载返回类型标成可空，
            // 链到 .Build() 就是「可能空引用」。Media3 的 builder 本来就是原地改 + 返回 this。
            var builder = new MediaSession.Builder(this, player);
            builder.SetSessionActivity(LaunchIntent());
            _session = builder.Build();
            _sessionPlayer = player;
            AddSession(_session);
            return _session;
        }
        catch (Exception ex)
        {
            Maui.Services.BtFileLog.Write($"[后台播放] 建会话失败：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    void ReleaseSession()
    {
        if (_session is null) return;
        try
        {
            RemoveSession(_session);
            _session.Release();
        }
        catch (Exception ex)
        {
            Maui.Services.BtFileLog.Write($"[后台播放] 释放会话异常：{ex.Message}");
        }
        _session = null;
        _sessionPlayer = null;
    }

    /// <summary>点通知栏要回到应用（Media3 要求给一个 sessionActivity）。</summary>
    PendingIntent LaunchIntent()
    {
        var intent = new Intent(this, Java.Lang.Class.FromType(typeof(MainActivity)));
        intent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        var flags = PendingIntentFlags.UpdateCurrent;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.S) flags |= PendingIntentFlags.Immutable;
        return PendingIntent.GetActivity(this, 0, intent, flags)!;
    }

    public override void OnDestroy()
    {
        ReleaseSession();
        base.OnDestroy();
    }
}
