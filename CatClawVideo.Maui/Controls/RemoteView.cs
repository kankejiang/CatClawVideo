namespace CatClawVideo.Maui.Controls;

/// <summary>平台层（Windows 头）要实现的缝：把一帧 JPEG 字节解好并上屏。</summary>
public interface IRemoteViewImplementation
{
    void SubmitFrame(byte[] payload, long receivedTickMs);
}

/// <summary>
/// 远程画面控件：跨平台侧只持有"服务端画面尺寸 / 图像实际矩形 / 窗口缩放比"这三组几何量，
/// 真正的解码上屏与鼠标键盘事件都由平台层做（Windows 头 = WinUI，见 Platforms/Windows/RemoteViewHandler.cs）。
///
/// <para>为什么不使用 MAUI 的 Pan/Tap 手势：那套手势会把"鼠标按下"合成成触摸序列，
/// 拿不到左右中键的区别（右键=安卓返回、中键=HOME 是这里的约定），
/// 而平台层的 Pointer 事件能拿到真实按键与捕获，坐标口径也明确（DIP）。</para>
///
/// <para>坐标换算：手势给 DIP，协议要<b>设备像素</b>。图像矩形与视图同为 DIP，等比缩放里 DPI 自动抵消，
/// 所以 <c>服务端像素 = (DIP − 图像左边距) ÷ 图像DIP宽 × 服务端宽</c> 就是那条"窗口坐标 ÷ 缩放比"；
/// <see cref="RasterizationScale"/> 另外显示到状态栏上，用来核对 HiDPI 是否真的参与了几何。</para>
/// </summary>
public class RemoteView : View
{
    /// <summary>服务端画面宽高（握手后由页面回填）。</summary>
    public int ServerWidth { get; set; }

    public int ServerHeight { get; set; }

    /// <summary>窗口缩放比（WinUI 的 XamlRoot.RasterizationScale，1.0 = 100%）。</summary>
    public double RasterizationScale { get; set; } = 1.0;

    /// <summary>图像在控件内的实际矩形（DIP，含 letterbox 居中偏移），由平台层每次上屏后回填。</summary>
    public Rect ImageRect { get; set; } = Rect.Zero;

    /// <summary>平台层在 ConnectHandler 里回指过来。</summary>
    public IRemoteViewImplementation? Implementation { get; set; }

    /// <summary>一帧真的交给合成器了（携带"收流→上屏"的实测毫秒）。</summary>
    public event Action<double>? FrameDisplayed;

    /// <summary>触摸：action ∈ {0=down,1=up,2=move}，坐标已是服务端设备像素。</summary>
    public event Action<int, int, byte>? TouchRequested;

    public event Action<int, int>? WheelRequested;

    /// <summary>按键：keycode 为 Android 语义，action ∈ {0=down,1=up}。</summary>
    public event Action<uint, byte>? KeyRequested;

    /// <summary>Esc 被按下（协议上 ESC 留给"退出本页"，不发往 Android）。</summary>
    public event Action? EscapePressed;

    public RemoteView()
    {
        BackgroundColor = Microsoft.Maui.Graphics.Colors.Black;
    }

    public void SubmitJpeg(byte[] payload, long receivedTickMs) => Implementation?.SubmitFrame(payload, receivedTickMs);

    /// <summary>控件内的 DIP 坐标 → 服务端设备像素；打在黑边上返回 null（不注入，免得误点）。</summary>
    public (int X, int Y)? MapToServerPixels(double dipX, double dipY)
    {
        if (ServerWidth <= 0 || ServerHeight <= 0) return null;
        var rect = ImageRect;
        if (rect.Width <= 0 || rect.Height <= 0) return null;
        if (dipX < rect.Left - 1 || dipX > rect.Right + 1 || dipY < rect.Top - 1 || dipY > rect.Bottom + 1)
            return null;
        var x = (int)Math.Round((dipX - rect.Left) * ServerWidth / rect.Width);
        var y = (int)Math.Round((dipY - rect.Top) * ServerHeight / rect.Height);
        return (Math.Clamp(x, 0, ServerWidth - 1), Math.Clamp(y, 0, ServerHeight - 1));
    }

    protected internal void RaiseFrameDisplayed(double latencyMs) => FrameDisplayed?.Invoke(latencyMs);

    protected internal void RaiseTouch(int x, int y, byte action) => TouchRequested?.Invoke(x, y, action);

    protected internal void RaiseWheel(int dx, int dy) => WheelRequested?.Invoke(dx, dy);

    protected internal void RaiseKey(uint keycode, byte action) => KeyRequested?.Invoke(keycode, action);

    protected internal void RaiseEscapePressed() => EscapePressed?.Invoke();
}
