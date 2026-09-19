namespace CatClawVideo.Maui.Services;

/// <summary>
/// 页面自管窗口拖拽区。
///
/// <para>默认规则是「App 按当前页面的顶栏元素统一声明」（<c>App.SyncTitleBarDrag</c>），
/// 但那只覆盖「顶栏某一段是空白」的普通页面。播放页这类**顶栏里到处是控件、
/// 空白分散在控件上下左右**的页面，需要按「整条顶栏的横向补集」来声明
/// （见 <see cref="WindowDragHelper.AttachStrip"/>）—— 这种页面就实现本接口自己接管。</para>
///
/// <para>⚠ 必须由 App 在 <c>Shell.Navigated</c> 时调用：那是每次导航后**唯一**的统一下发点。
/// 页面在 <c>OnAppearing</c> 里自己声明是**无效**的 —— 随后到来的 Navigated 回调
/// 会因为「当前页不是主页」而走默认分支，把拖拽区清零（2026-09-19 实测，播放页拖不动就是这个）。</para>
/// </summary>
public interface IWindowDragArea
{
    /// <summary>
    /// 声明本页的拖拽区。
    /// 返回 <c>true</c> = 本页已接管（App 不再按默认规则处理）；
    /// 返回 <c>false</c> = 本页不接管（App 走默认规则）。
    /// </summary>
    bool ApplyWindowDragArea();
}
