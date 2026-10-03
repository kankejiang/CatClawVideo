// ── 移植说明（2026-10-03）────────────────────────────────────────────────────
// 本文件移植自 Richasy/mpv-winui（MIT License）src/Mpv.UI/Common/ 下的
// ContextSettings / FrameBufferBase / RenderContext / FrameBuffer /
// OpenGLRenderControlBase / RenderControl / ISwapChainPanelNative，
// 按本项目命名规范合并重写（类名加 Mpv 前缀）。原许可证声明：
//   MIT License — Copyright (c) 2024 Richasy
// 技术路径：libmpv render API（OpenGL）→ WGL_NV_DX_interop 把 D3D11
// composition swapchain 的 backbuffer 注册为 GL renderbuffer → mpv 直接渲染进
// SwapChainPanel 的交换链。无 ANGLE 依赖；弹幕/控制层留在 XAML 合成层之上。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using OpenTK.Platform.Windows;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using OpenTK.Graphics.Wgl;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using WinRT;

namespace CatClawVideo.Maui.Platforms.Windows.Mpv;

/// <summary>GL 上下文创建参数（默认 GL 3.3 Core，mpv 需要的最低配）。</summary>
public sealed class MpvContextSettings
{
    public int MajorVersion { get; set; } = 3;
    public int MinorVersion { get; set; } = 3;
    public ContextFlags GraphicsContextFlags { get; set; } = ContextFlags.Default;
    public ContextProfile GraphicsProfile { get; set; } = ContextProfile.Core;

    public static bool WouldResultInSameContext(MpvContextSettings a, MpvContextSettings b) =>
        a.MajorVersion == b.MajorVersion && a.MinorVersion == b.MinorVersion &&
        a.GraphicsProfile == b.GraphicsProfile && a.GraphicsContextFlags == b.GraphicsContextFlags;
}

/// <summary>D3D11 设备 + 共享隐藏窗口 GL 上下文 + GL↔DX 互操作句柄。</summary>
public unsafe sealed class MpvRenderContext : IDisposable
{
    private static IGraphicsContext? _sharedContext;
    private static MpvContextSettings? _sharedContextSettings;
    private static int _sharedContextReferenceCount;
    private static IBindingsContext? _sharedBindingContext;

    public IntPtr DxDeviceFactory { get; }
    public IntPtr DxDeviceHandle { get; }
    public IntPtr DxDeviceContext { get; }
    public IntPtr GlDeviceHandle { get; }
    public IGraphicsContext GraphicsContext { get; }

    public MpvRenderContext(MpvContextSettings settings)
    {
        IDXGIFactory2* factory;
        ID3D11Device* device;
        ID3D11DeviceContext* devCtx;

        Guid factoryGuid = typeof(IDXGIFactory2).GetTypeInfo().GUID;
        DXGI.GetApi(null).CreateDXGIFactory2(0, &factoryGuid, (void**)&factory);

        var flags = CreateDeviceFlag.BgraSupport | CreateDeviceFlag.VideoSupport;
        D3D11.GetApi(null).CreateDevice(null, D3DDriverType.Hardware, 0,
            Convert.ToUInt32(flags), null, 0, D3D11.SdkVersion, &device, null, &devCtx);

        DxDeviceFactory = (IntPtr)factory;
        DxDeviceHandle = (IntPtr)device;
        DxDeviceContext = (IntPtr)devCtx;

        GraphicsContext = GetOrCreateSharedOpenGLContext(settings);
        GlDeviceHandle = Wgl.DXOpenDeviceNV((IntPtr)device);
    }

    public static IntPtr GetProcAddress(string name)
    {
        if (_sharedBindingContext == null) return IntPtr.Zero;
        return _sharedBindingContext.GetProcAddress(name);
    }

    private static IGraphicsContext GetOrCreateSharedOpenGLContext(MpvContextSettings settings)
    {
        if (_sharedContext == null)
        {
            var windowSettings = NativeWindowSettings.Default;
            windowSettings.StartFocused = false;
            windowSettings.StartVisible = false;
            windowSettings.NumberOfSamples = 0;
            windowSettings.APIVersion = new Version(settings.MajorVersion, settings.MinorVersion);
            windowSettings.Flags = ContextFlags.Offscreen | settings.GraphicsContextFlags;
            windowSettings.Profile = settings.GraphicsProfile;
            windowSettings.WindowBorder = WindowBorder.Hidden;
            windowSettings.WindowState = WindowState.Minimized;
            var nativeWindow = new NativeWindow(windowSettings);

            _sharedBindingContext = new GLFWBindingsContext();
            Wgl.LoadBindings(_sharedBindingContext);

            _sharedContext = nativeWindow.Context;
            _sharedContextSettings = settings;
            _sharedContext.MakeCurrent();
        }
        else if (!MpvContextSettings.WouldResultInSameContext(settings, _sharedContextSettings))
        {
            throw new ArgumentException("GL 上下文参数与已存在的共享上下文不一致（全 App 只支持一种配置）。");
        }

        Interlocked.Increment(ref _sharedContextReferenceCount);
        return _sharedContext;
    }

    public void Dispose()
    {
        if (GlDeviceHandle != IntPtr.Zero)
        {
            try { Wgl.DXCloseDeviceNV(GlDeviceHandle); } catch { }
        }
        var remaining = Interlocked.Decrement(ref _sharedContextReferenceCount);
        if (remaining <= 0)
        {
            (_sharedContext as IDisposable)?.Dispose();
            _sharedContext = null;
            _sharedContextSettings = null;
            _sharedBindingContext = null;
        }
    }
}

/// <summary>GL FBO ↔ swapchain backbuffer 的一帧画布。</summary>
public unsafe abstract class MpvFrameBufferBase : IDisposable
{
    public abstract int BufferWidth { get; protected set; }
    public abstract int BufferHeight { get; protected set; }
    public abstract IntPtr SwapChainHandle { get; protected set; }
    public abstract void Dispose();
}

public unsafe sealed class MpvFrameBuffer : MpvFrameBufferBase
{
    public MpvRenderContext Context { get; }

    public int GLColorRenderBufferHandle { get; set; }
    public int GLDepthRenderBufferHandle { get; set; }
    public int GLFrameBufferHandle { get; set; }
    public IntPtr DxInteropColorHandle { get; set; }

    public override int BufferWidth { get; protected set; }
    public override int BufferHeight { get; protected set; }
    public override IntPtr SwapChainHandle { get; protected set; }

    public MpvFrameBuffer(MpvRenderContext context, int width, int height, double scaleX, double scaleY)
    {
        Context = context;
        BufferWidth = Convert.ToInt32(width * scaleX);
        BufferHeight = Convert.ToInt32(height * scaleY);

        IDXGISwapChain1* swapChain;
        var desc = new SwapChainDesc1
        {
            Width = (uint)BufferWidth,
            Height = (uint)BufferHeight,
            Format = Format.FormatB8G8R8A8Unorm,
            Stereo = 0,
            SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
            BufferUsage = DXGI.UsageRenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            Flags = 0,
            AlphaMode = AlphaMode.Ignore,
        };
        ((IDXGIFactory2*)Context.DxDeviceFactory)->CreateSwapChainForComposition(
            (IUnknown*)Context.DxDeviceHandle, &desc, null, &swapChain);
        SwapChainHandle = (IntPtr)swapChain;

        GLFrameBufferHandle = GL.GenFramebuffer();
    }

    /// <summary>把 backbuffer 注册进 FBO 并锁定，之后 mpv 往该 FBO 渲染。</summary>
    public void Begin()
    {
        ID3D11Texture2D* colorbuffer;
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, GLFrameBufferHandle);

        Guid texGuid = typeof(ID3D11Texture2D).GetTypeInfo().GUID;
        ((IDXGISwapChain1*)SwapChainHandle)->GetBuffer(0, &texGuid, (void**)&colorbuffer);

        GLColorRenderBufferHandle = GL.GenRenderbuffer();
        GLDepthRenderBufferHandle = GL.GenRenderbuffer();

        DxInteropColorHandle = Wgl.DXRegisterObjectNV(Context.GlDeviceHandle, (IntPtr)colorbuffer,
            (uint)GLColorRenderBufferHandle, (uint)RenderbufferTarget.Renderbuffer,
            WGL_NV_DX_interop.AccessReadWrite);
        if (DxInteropColorHandle == IntPtr.Zero)
        {
            // WGL_NV_DX_interop 注册失败（驱动不支持/设备不匹配）——继续渲染必 AV，
            // 明确抛出让上层回落 FFmpeg 路径，绝不把坏 FBO 递给 mpv。
            colorbuffer->Release();
            throw new InvalidOperationException("WGL_NV_DX_interop 注册失败（GL↔D3D11 互操作不可用）");
        }
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            RenderbufferTarget.Renderbuffer, (uint)GLColorRenderBufferHandle);

        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, GLDepthRenderBufferHandle);
        GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Depth24Stencil8, BufferWidth, BufferHeight);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer, (uint)GLDepthRenderBufferHandle);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.StencilAttachment,
            RenderbufferTarget.Renderbuffer, (uint)GLDepthRenderBufferHandle);

        colorbuffer->Release();

        var interopHandle = DxInteropColorHandle;
        Wgl.DXLockObjectsNV(Context.GlDeviceHandle, 1, new[] { interopHandle });

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, GLFrameBufferHandle);
        GL.Viewport(0, 0, BufferWidth, BufferHeight);

        var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != FramebufferErrorCode.FramebufferComplete)
            throw new InvalidOperationException($"FBO 不完整: {status}（{BufferWidth}x{BufferHeight}）");
    }

    public void End()
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        Wgl.DXUnlockObjectsNV(Context.GlDeviceHandle, 1, new[] { DxInteropColorHandle });
        Wgl.DXUnregisterObjectNV(Context.GlDeviceHandle, DxInteropColorHandle);
        GL.DeleteRenderbuffer(GLColorRenderBufferHandle);
        GL.DeleteRenderbuffer(GLDepthRenderBufferHandle);
        ((IDXGISwapChain1*)SwapChainHandle)->Present(0, 0);
    }

    public void UpdateSize(int width, int height, double scaleX, double scaleY)
    {
        BufferWidth = Convert.ToInt32(width * scaleX);
        BufferHeight = Convert.ToInt32(height * scaleY);
        ((IDXGISwapChain1*)SwapChainHandle)->ResizeBuffers(2, (uint)BufferWidth, (uint)BufferHeight, Format.FormatUnknown, 0);
        ((IDXGISwapChain2*)SwapChainHandle)->SetMatrixTransform(new Matrix3X2F { DXGI11 = 1.0f / (float)scaleX, DXGI22 = 1.0f / (float)scaleY });
    }

    public override void Dispose()
    {
        GL.DeleteFramebuffer(GLFrameBufferHandle);
        GL.DeleteRenderbuffer(GLColorRenderBufferHandle);
        GL.DeleteRenderbuffer(GLDepthRenderBufferHandle);
        GC.SuppressFinalize(this);
    }
}

/// <summary>WinRT ISwapChainPanelNative：把自建 swapchain 挂上面板。</summary>
[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("63aad0b8-7c24-40ff-85a8-640d944cc325")]
public interface IMpvSwapChainPanelNative
{
    [PreserveSig] long SetSwapChain([In] IntPtr swapChain);
    [PreserveSig] ulong Release();
}

/// <summary>承载 SwapChainPanel 的 ContentControl：每帧回调驱动 mpv 渲染。</summary>
public unsafe class MpvRenderControl : ContentControl
{
    private Stopwatch _stopwatch = Stopwatch.StartNew();
    private TimeSpan _lastRenderTime = TimeSpan.FromSeconds(-1);
    private SwapChainPanel? _swapChainPanel;
    private bool _initialized;

    public MpvContextSettings Setting { get; set; } = new();
    public MpvRenderContext? Context { get; private set; }
    public MpvFrameBuffer? FrameBuffer { get; private set; }

    /// <summary>宿主每帧绘制的回调（参数为距上一帧的时间）。</summary>
    public event Action<TimeSpan>? Render;
    public event EventHandler? Ready;

    public double ScaleX => _swapChainPanel?.CompositionScaleX ?? 1;
    public double ScaleY => _swapChainPanel?.CompositionScaleY ?? 1;

    public MpvRenderControl()
    {
        HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch;
        VerticalContentAlignment = Microsoft.UI.Xaml.VerticalAlignment.Stretch;
        SizeChanged += OnSizeChanged;
        Loaded += (_, _) => Initialize();
        // ⚠ Unloaded 只停画，不销毁 GL——退出播放时 Unloaded 先于 Handler 的 DisconnectHandler，
        //   若在这里销毁 GL 上下文，随后的 mpv_render_context_free 就在死 GL 上清理 → AV（13:02 闪退）。
        //   完整释放在 DisconnectHandler 里按「backend.Dispose（free render ctx）→ DisposeAll（销毁 GL）」排序。
        Unloaded += (_, _) => StopPainting();
    }

    private bool _paintingStopped;

    /// <summary>只退出渲染循环（FrameBuffer/Context 保留）。</summary>
    public void StopPainting()
    {
        _paintingStopped = true;
        CompositionTarget.Rendering -= OnRendering;
    }

    /// <summary>完整销毁（DisconnectHandler 专用，且必须在 mpv_render_context_free 之后调用）。</summary>
    public void DisposeAll()
    {
        StopPainting();
        Render = null;
        RenderFailed = null;
        try { FrameBuffer?.Dispose(); } catch { }
        FrameBuffer = null;
        try { Context?.Dispose(); } catch { }
        Context = null;
        _initialized = false;
    }

    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        Context = new MpvRenderContext(Setting);
        _swapChainPanel = new SwapChainPanel();
        _swapChainPanel.CompositionScaleChanged += (_, _) => UpdateFrameBufferSize();
        Content = _swapChainPanel;

        if (!TryLoadFrameBuffer())
            UpdateFrameBufferSize();

        CompositionTarget.Rendering += OnRendering;
        Ready?.Invoke(this, EventArgs.Empty);
    }

    public int GetBufferHandle() => FrameBuffer?.GLFrameBufferHandle ?? 0;

    /// <summary>渲染链路失败后的降级通知（Handler 收到后回落 FFmpeg/MF 路径）。</summary>
    public event Action<string>? RenderFailed;

    private bool _renderBroken;

    private void Draw()
    {
        if (FrameBuffer is null || _renderBroken) return;
        try
        {
            FrameBuffer.Begin();
            Render?.Invoke(_stopwatch.Elapsed);
            _stopwatch.Restart();
            FrameBuffer.End();
        }
        catch (Exception ex)
        {
            // GL/interop 层的失败是确定性的（驱动不支持等）——停渲染循环并上报，
            // 绝不让异常飞进 WinUI 渲染回调把进程带崩。（GL 资源交给 Handler 的 DisposeAll 统一释放）
            _renderBroken = true;
            StopPainting();
            RenderFailed?.Invoke($"mpv 渲染链路失败: {ex.Message}");
        }
    }

    private void OnRendering(object sender, object e)
    {
        if (_renderBroken || _paintingStopped) { CompositionTarget.Rendering -= OnRendering; return; }
        var args = (RenderingEventArgs)e;
        if (_lastRenderTime != args.RenderingTime)
        {
            _lastRenderTime = args.RenderingTime;
            if (FrameBuffer != null)
                Draw();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Context == null || e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
        if (!TryLoadFrameBuffer())
            UpdateFrameBufferSize();
    }

    private void UpdateFrameBufferSize() =>
        FrameBuffer?.UpdateSize((int)ActualWidth, (int)ActualHeight, ScaleX, ScaleY);

    private bool TryLoadFrameBuffer()
    {
        if (FrameBuffer != null || _swapChainPanel is null || Context is null) return false;
        if (ActualWidth < 1 || ActualHeight < 1) return false;

        FrameBuffer = new MpvFrameBuffer(Context, (int)ActualWidth, (int)ActualHeight, ScaleX, ScaleY);
        _swapChainPanel.As<IMpvSwapChainPanelNative>().SetSwapChain(FrameBuffer.SwapChainHandle);
        return true;
    }

}
