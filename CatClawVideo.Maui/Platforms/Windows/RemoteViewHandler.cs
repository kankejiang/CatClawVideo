#if WINDOWS
using CatClawVideo.Maui.Controls;
using CatClawVideo.Streaming;
using Microsoft.Maui.Handlers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using System.IO;
using WinUIImage = Microsoft.UI.Xaml.Controls.Image;

namespace CatClawVideo.Maui.Platforms.Windows;

/// <summary>
/// 远程画面（Windows 头）：ContentControl 里放一个 WinUI Image，帧走
/// <c>BitmapImage.SetSourceAsync</c>（JPEG 解码在系统异步管线里，不占 UI 线程），换帧策略是"最新帧优先"。
///
/// <para>为什么不用 WriteableBitmap 自管像素：这一版要的是 15~30fps 的 MJPEG，
/// 系统解码 + 合成器已经够，撕裂由交换链负责（"双缓冲/无撕裂"这条要求白送）；
/// T2 的 H.264 解码器交付后换成逐帧位图提交，只改 <see cref="SubmitFrame"/> 一处。</para>
///
/// <para>输入用 WinUI 的 Pointer/Key 事件而不是 MAUI 手势：要区分左/右/中键
/// （右键=安卓返回、中键=HOME），并且拖出控件也要收到 up（CapturePointer）。</para>
/// </summary>
public sealed class RemoteViewHandler : ViewHandler<RemoteView, Microsoft.UI.Xaml.Controls.ContentControl>, IRemoteViewImplementation
{
    private readonly WinUIImage _frame = new()
    {
        Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
        HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
        VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Stretch,
    };

    private int _frameGen;
    private long _lastMoveMs;
    private bool _dragging;

    public RemoteViewHandler() : base(ViewHandler.ViewMapper)
    {
    }

    protected override Microsoft.UI.Xaml.Controls.ContentControl CreatePlatformView() => new()
    {
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black),
        Content = _frame,
        HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
        VerticalContentAlignment = Microsoft.UI.Xaml.VerticalAlignment.Stretch,
        IsTabStop = true,
    };

    protected override void ConnectHandler(Microsoft.UI.Xaml.Controls.ContentControl platformView)
    {
        base.ConnectHandler(platformView);
        platformView.PointerPressed += OnPointerPressed;
        platformView.PointerMoved += OnPointerMoved;
        platformView.PointerReleased += OnPointerReleased;
        platformView.PointerWheelChanged += OnPointerWheelChanged;
        platformView.KeyDown += OnKeyDown;
        platformView.KeyUp += OnKeyUp;
        VirtualView.Implementation = this;
    }

    protected override void DisconnectHandler(Microsoft.UI.Xaml.Controls.ContentControl platformView)
    {
        platformView.PointerPressed -= OnPointerPressed;
        platformView.PointerMoved -= OnPointerMoved;
        platformView.PointerReleased -= OnPointerReleased;
        platformView.PointerWheelChanged -= OnPointerWheelChanged;
        platformView.KeyDown -= OnKeyDown;
        platformView.KeyUp -= OnKeyUp;
        if (VirtualView is { } v) v.Implementation = null;
        _frame.Source = null;
        base.DisconnectHandler(platformView);
    }

    // ── 帧：解码 + 上屏 + 几何回填 ──────────────────────────────────

    public void SubmitFrame(byte[] payload, long receivedTickMs)
    {
        // 收流可以比合成器快：只让"最后一帧"生效，被覆盖的计入丢帧
        var gen = ++_frameGen;
        _ = SubmitAsync(payload, receivedTickMs, gen);
    }

    private async Task SubmitAsync(byte[] payload, long receivedTickMs, int gen)
    {
        var view = VirtualView;
        if (view is null) return;
        var ras = new InMemoryRandomAccessStream();
        try
        {
            var ws = ras.AsStreamForWrite();
            await ws.WriteAsync(payload);
            await ws.FlushAsync();
            ras.Seek(0);
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(ras);
            if (gen != _frameGen) return;               // 期间来了更新的帧：这一帧丢弃
            _frame.Source = bmp;
            UpdateGeometry(view, bmp);
            view.RaiseFrameDisplayed(Environment.TickCount64 - receivedTickMs);
        }
        catch
        {
            // 畸形 JPEG：丢这一帧不断链路（与 T2 客户端同语义）
        }
        finally
        {
            ras.Dispose();
        }
    }

    /// <summary>把「图像在控件内的实际矩形」（DIP）与窗口缩放比交回跨平台层做坐标反算。</summary>
    private void UpdateGeometry(RemoteView view, BitmapSource bmp)
    {
        var boxW = PlatformView?.ActualWidth ?? 0;
        var boxH = PlatformView?.ActualHeight ?? 0;
        if (boxW <= 0 || boxH <= 0 || bmp.PixelWidth <= 0 || bmp.PixelHeight <= 0) return;
        // Image(Stretch=Uniform) 的绘制矩形：等比 fit + 居中
        var scale = Math.Min(boxW / bmp.PixelWidth, boxH / bmp.PixelHeight);
        var w = bmp.PixelWidth * scale;
        var h = bmp.PixelHeight * scale;
        view.ImageRect = new Microsoft.Maui.Graphics.Rect((boxW - w) / 2, (boxH - h) / 2, w, h);
        view.RasterizationScale = PlatformView?.XamlRoot?.RasterizationScale ?? 1.0;
    }

    // ── 输入：鼠标 → 触摸；滚轮 → 0x12；键盘 → Android keycode ──────

    private void SendTouchAt(Microsoft.UI.Xaml.Controls.ContentControl host, PointerRoutedEventArgs e, byte action)
    {
        if (VirtualView is not { } view) return;
        var p = e.GetCurrentPoint(host).Position;
        var mapped = view.MapToServerPixels(p.X, p.Y);
        // 坐标映射是 N4-2 的核心断言，必须留下"控件看到的 DIP / letterbox 矩形 / 设备像素"三元组，
        // 出偏差时才能一眼看出是 rect 回填错了还是点击位置算错了。
        CatClawVideo.Maui.DiagLog.Write(
            $"[remote] 输入 dip=({p.X:F0},{p.Y:F0}) rect=({view.ImageRect.Left:F0},{view.ImageRect.Top:F0}," +
            $"{view.ImageRect.Width:F0}x{view.ImageRect.Height:F0}) box=({host.ActualWidth:F0}x{host.ActualHeight:F0}) " +
            $"scale={view.RasterizationScale:F2} -> 设备={(mapped is null ? "黑边内忽略" : $"{mapped.Value.X},{mapped.Value.Y}")}");
        if (mapped is not { } m) return;
        view.RaiseTouch(m.X, m.Y, action);
    }

    private void OnPointerPressed(object? sender, PointerRoutedEventArgs e)
    {
        if (sender is not Microsoft.UI.Xaml.Controls.ContentControl host || VirtualView is not { } view) return;
        var props = e.GetCurrentPoint(host).Properties;
        if (props.IsRightButtonPressed) { SendAndroidKey(AndroidKeycodes.KeycodeBack); return; }
        if (props.IsMiddleButtonPressed) { SendAndroidKey(AndroidKeycodes.KeycodeHome); return; }
        if (!props.IsLeftButtonPressed) return;
        _ = host.Focus(FocusState.Pointer);             // 拿到键盘焦点，后续按键才能进来
        _dragging = true;
        host.CapturePointer(e.Pointer);                 // 拖出控件也要收到 move/up
        SendTouchAt(host, e, Protocol.TouchDown);
    }

    private void OnPointerMoved(object? sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || sender is not Microsoft.UI.Xaml.Controls.ContentControl host) return;
        // move 能到几百 Hz，按 60Hz 上限发（T2 的实测节流值），否则输入帧挤满链路
        var now = Environment.TickCount64;
        if (now - _lastMoveMs < 16) return;
        _lastMoveMs = now;
        SendTouchAt(host, e, Protocol.TouchMove);
    }

    private void OnPointerReleased(object? sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || sender is not Microsoft.UI.Xaml.Controls.ContentControl host) return;
        _dragging = false;
        host.ReleasePointerCapture(e.Pointer);
        SendTouchAt(host, e, Protocol.TouchUp);
    }

    private void OnPointerWheelChanged(object? sender, PointerRoutedEventArgs e)
    {
        if (sender is not Microsoft.UI.Xaml.Controls.ContentControl host) return;
        var delta = e.GetCurrentPoint(host).Properties.MouseWheelDelta;
        if (delta != 0) VirtualView?.RaiseWheel(0, delta);
        e.Handled = true;
    }

    private void OnKeyDown(object? sender, KeyRoutedEventArgs e) => NotifyKey((int)e.Key, true, e);

    private void OnKeyUp(object? sender, KeyRoutedEventArgs e) => NotifyKey((int)e.Key, false, e);

    /// <summary>
    /// ESC 归页面用来退出（N4-2 的"退出"约定），不发往 Android；
    /// 其余按 Win32 VK → Android keycode 映射，映射不到就放行给系统（焦点导航还能用）。
    /// </summary>
    private void NotifyKey(int vk, bool down, KeyRoutedEventArgs e)
    {
        if (VirtualView is not { } view) return;
        if (vk == 0x1B)
        {
            if (down) view.RaiseEscapePressed();
            e.Handled = true;
            return;
        }
        var code = AndroidKeycodes.FromVirtualKey(vk);
        if (code == AndroidKeycodes.Unknown) return;
        view.RaiseKey(code, down ? Protocol.KeyDown : Protocol.KeyUp);
        e.Handled = true;
    }

    private void SendAndroidKey(uint keycode)
    {
        if (VirtualView is not { } view) return;
        view.RaiseKey(keycode, Protocol.KeyDown);
        view.RaiseKey(keycode, Protocol.KeyUp);
    }
}
#endif
