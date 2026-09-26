using AndroidX.Media3.ExoPlayer;

namespace CatClawVideo.Maui.Platforms.Android;

/// <summary>
/// 播放页与后台服务之间交接 ExoPlayer 的地方。
///
/// <para>为什么不改成「服务持有播放器」：那要把 <see cref="VideoPlayerViewHandler"/> 里
/// 创建/重建/释放播放器的整条链搬走，播放是这应用最容易碰坏的东西，为一项后台功能做这种手术不划算。
/// 现在的形状是：播放器仍归页面所有，服务只借用它挂 MediaSession —— 页面释放时必须
/// <see cref="Detach"/>，服务看到 null 就不建会话。</para>
/// </summary>
internal static class PlayerHandoff
{
    static IExoPlayer? _player;

    /// <summary>当前交给服务借用的播放器（null = 没有在播的）。</summary>
    public static IExoPlayer? Current => _player;

    /// <summary>播放器换了（新建/释放/重建）时通知服务重建会话。</summary>
    public static event Action? Changed;

    public static void Attach(IExoPlayer player)
    {
        _player = player;
        Changed?.Invoke();
    }

    /// <summary>只摘掉自己那一个：换台会先建新播放器再放旧的，无条件清空会把新会话也带走。</summary>
    public static void Detach(IExoPlayer player)
    {
        if (!ReferenceEquals(_player, player)) return;
        _player = null;
        Changed?.Invoke();
    }
}
