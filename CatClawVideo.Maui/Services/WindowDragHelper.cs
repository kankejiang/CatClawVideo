#if WINDOWS
using System.Runtime.InteropServices;

namespace CatClawVideo.Maui.Services;

/// <summary>
/// 窗口拖拽区（沉浸式顶栏）。
///
/// <para><b>为什么用 <c>AppWindow.TitleBar.SetDragRectangles</c>，而不是 <c>Window.SetTitleBar</c></b>：
/// 两者是**相反的白名单方向**，这决定了沉浸式顶栏能不能点。</para>
///
/// <list type="number">
/// <item><b>SetDragRectangles（本实现）＝ 反向白名单</b>：只声明「顶栏哪一段能拖」，
/// 其余部分自动归**客户区**，点击直达控件。所以顶栏即使压在窗口标题栏那条非客户区上，
/// tab / 搜索框照样能点 —— 沉浸式与可点兼得。</item>
///
/// <item><b>Window.SetTitleBar(元素)</b> ＝ 正向声明：把它指定为标题栏元素，
/// 顶栏其余部分可否点击取决于框架算出的 Passthrough 区域，而实测
/// <b>Passthrough 对<b>物理鼠标</b>不可靠</b>（SendInput 能过、真实点击仍被吞）。
/// 2026-09-19 用户实测：SetTitleBar 方案下顶栏 tab「要点好几次才有反应」，
/// 撤掉该方案后（去掉给内容加的 32px 下移）tab 立刻正常 —— 正是这条。</item>
/// </list>
///
/// <para><b>当年「有时候拖不动」的根因已修掉</b>：旧实现算不出矩形时
/// （布局未就绪、元素尺寸为 0）会 <c>SetDragRectangles(空数组)</c> 把拖拽区**清零**，
/// 于是窗口再也拖不动，要等某次重算才恢复。现在：元素不可用时**什么都不做**
/// （保留上一次的矩形），并额外在窗口尺寸变化时重算 —— 矩形始终跟着顶栏那段空白。</para>
/// </summary>
public static class WindowDragHelper
{
    /// <summary>
    /// 把指定元素声明为窗口拖拽区（元素须已布局）。
    /// <para>元素为 null 或尺寸为 0 时**不动**已有拖拽区 —— 清零会导致窗口拖不动。</para>
    /// </summary>
    public static void Attach(Microsoft.UI.Xaml.FrameworkElement? el)
    {
        try
        {
            var win = App.CurrentNativeWindow;
            var aw = App.CurrentAppWindow;
            var hwnd = App.MainWindowHwnd;
            if (win is null || aw is null || hwnd == IntPtr.Zero) return;

            win.ExtendsContentIntoTitleBar = true;

            AttachPointerFallback(el);

            if (el is null || el.ActualWidth <= 0 || el.ActualHeight <= 0) return;   // 未布局：不动

            var scale = GetDpiForWindow(hwnd) / 96.0;

            // 元素矩形是**客户区坐标**；SetDragRectangles 要的是**窗口坐标**，故补上客户区原点偏移
            var b = el.TransformToVisual(null).TransformBounds(
                new Windows.Foundation.Rect(0, 0, el.ActualWidth, el.ActualHeight));

            if (!GetClientRect(hwnd, out _)) return;
            var origin = new POINT { X = 0, Y = 0 };
            ClientToScreen(hwnd, ref origin);
            var offX = origin.X - aw.Position.X;
            var offY = origin.Y - aw.Position.Y;

            var rect = new Windows.Graphics.RectInt32
            {
                X = offX + (int)Math.Round(b.X * scale),
                Y = offY + (int)Math.Round(b.Y * scale),
                Width = (int)Math.Round(b.Width * scale),
                Height = (int)Math.Round(b.Height * scale),
            };
            if (rect.Width <= 0 || rect.Height <= 0) return;

            aw.TitleBar.SetDragRectangles([rect]);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WindowDrag] Attach 失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 清空拖拽区。**当前页面没有拖拽元素时**才调用 ——
    /// 否则上一个页面留下的拖拽区会在这个页面上误吞点击（顶栏那条会被当标题栏）。
    /// </summary>
    public static void Detach()
    {
        try
        {
            AttachPointerFallback(null);
            App.CurrentAppWindow?.TitleBar.SetDragRectangles([]);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WindowDrag] Detach 失败: {ex.Message}");
        }
    }

    // ─────────── 手动拖拽兜底 ───────────
    //
    // 拖拽矩形正确时由系统接管（鼠标按下在非客户区，事件根本到不了应用），这几段不会触发；
    // 万一系统没接管（矩形算错/系统差异），仍能按住拖拽，不至于完全拖不动。

    private static bool _dragging;
    private static int _startMouseX, _startMouseY;
    private static int _startWinX, _startWinY;
    private static Microsoft.UI.Xaml.FrameworkElement? _dragElement;

    private static void AttachPointerFallback(Microsoft.UI.Xaml.FrameworkElement? el)
    {
        if (ReferenceEquals(_dragElement, el)) return;

        if (_dragElement is not null)
        {
            _dragElement.PointerPressed -= OnPointerPressed;
            _dragElement.PointerMoved -= OnPointerMoved;
            _dragElement.PointerReleased -= OnPointerReleased;
            _dragElement.DoubleTapped -= OnDoubleTapped;
        }

        _dragElement = el;

        if (el is null) return;
        el.PointerPressed += OnPointerPressed;
        el.PointerMoved += OnPointerMoved;
        el.PointerReleased += OnPointerReleased;
        el.DoubleTapped += OnDoubleTapped;
    }

    private static void OnPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        try
        {
            if (!e.GetCurrentPoint((Microsoft.UI.Xaml.UIElement)sender).Properties.IsLeftButtonPressed) return;
            var appWindow = App.CurrentAppWindow;
            if (appWindow is null) return;

            // 最大化状态下系统自己处理，不手动拖
            if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p
                && p.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized) return;

            var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(appWindow.Id);
            GetCursorPos(out var pt);
            GetWindowRect(hwnd, out var rc);
            _startMouseX = pt.X; _startMouseY = pt.Y;
            _startWinX = rc.Left; _startWinY = rc.Top;

            _dragging = true;
            _dragElement = (Microsoft.UI.Xaml.FrameworkElement)sender;
            _dragElement.CapturePointer(e.Pointer);
            e.Handled = true;
        }
        catch { }
    }

    private static void OnPointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        try
        {
            var appWindow = App.CurrentAppWindow;
            if (appWindow is null) return;

            GetCursorPos(out var pt);
            var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(appWindow.Id);
            GetWindowRect(hwnd, out var rc);

            SetWindowPos(hwnd, IntPtr.Zero,
                _startWinX + (pt.X - _startMouseX),
                _startWinY + (pt.Y - _startMouseY),
                rc.Right - rc.Left, rc.Bottom - rc.Top,
                SWP_NOZORDER | SWP_NOACTIVATE);
            e.Handled = true;
        }
        catch { }
    }

    private static void OnPointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        try { _dragElement?.ReleasePointerCapture(e.Pointer); } catch { }
        e.Handled = true;
    }

    /// <summary>双击拖拽区 → 最大化/还原（与系统标题栏行为一致）。</summary>
    private static void OnDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        try
        {
            if (App.CurrentAppWindow?.Presenter is not Microsoft.UI.Windowing.OverlappedPresenter p) return;
            if (p.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized) p.Restore();
            else p.Maximize();
        }
        catch { }
    }

    // ─────────── Win32 ───────────

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(
        IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
}
#endif
