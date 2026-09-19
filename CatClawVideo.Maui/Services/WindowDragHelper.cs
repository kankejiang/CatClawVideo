#if WINDOWS
using System.Runtime.InteropServices;

namespace CatClawVideo.Maui.Services;

/// <summary>
/// 窗口拖拽区（**照抄猫爪音乐 <c>Controls/TitleBar.xaml.cs</c>**）。
///
/// <para><b>为什么不用 <c>AppWindow.TitleBar.SetDragRectangles</c></b>：
/// 那套要自己算坐标（客户区/窗口坐标系换算、DPI 缩放、窗口按钮留白），
/// 而矩形是**算一次就定死**的 —— 布局没就绪、页面切换、窗口尺寸变化时若不重算就会错位甚至归零，
/// 表现就是「窗口突然拖不动，缩放一下才恢复」（2026-09-19 用户实测）。
/// 猫爪音乐的做法是 <c>Window.SetTitleBar(element)</c>：**由框架托管**，
/// 元素移动/窗口缩放都会自动跟随，不需要任何重算。</para>
///
/// <para>另外照抄了它的**指针手动拖拽兜底**：<c>SetTitleBar</c> 万一失效（某些系统/时机），
/// 仍能按住拖拽；双击最大化也一并实现。</para>
/// </summary>
public static class WindowDragHelper
{
    /// <summary>
    /// 把指定元素注册为标题栏拖拽区（系统据此处理拖拽与双击最大化）。
    /// 传 <c>null</c> 表示恢复系统默认。
    /// </summary>
    public static void Attach(Microsoft.UI.Xaml.UIElement? el)
    {
        try
        {
            var win = App.CurrentNativeWindow;
            if (win is null) return;

            win.ExtendsContentIntoTitleBar = true;
            win.SetTitleBar(el);

            if (el is null) return;

            // 指针手动拖拽兜底（SetTitleBar 生效时系统会先拦截，这些不会重复触发）
            el.PointerPressed -= OnPointerPressed;
            el.PointerMoved -= OnPointerMoved;
            el.PointerReleased -= OnPointerReleased;
            el.DoubleTapped -= OnDoubleTapped;
            el.PointerPressed += OnPointerPressed;
            el.PointerMoved += OnPointerMoved;
            el.PointerReleased += OnPointerReleased;
            el.DoubleTapped += OnDoubleTapped;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WindowDrag] Attach 失败: {ex.Message}");
        }
    }

    /// <summary>取消拖拽区（离开页面时调用）。</summary>
    public static void Detach() => Attach(null);

    // ─────────── 手动拖拽兜底（照抄猫爪音乐）───────────

    private static bool _dragging;
    private static int _startMouseX, _startMouseY;
    private static int _startWinX, _startWinY;
    private static Microsoft.UI.Xaml.UIElement? _dragElement;

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
            _dragElement = (Microsoft.UI.Xaml.UIElement)sender;
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

            // 窗口跟随鼠标（保持原有尺寸）
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
        _dragElement = null;
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
    [DllImport("user32.dll")] private static extern bool SetWindowPos(
        IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
}
#endif
