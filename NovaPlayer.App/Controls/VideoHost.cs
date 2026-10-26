using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using NovaPlayer.Infrastructure.Playback;
using NovaPlayer.Infrastructure.Rendering;
using Serilog;

namespace NovaPlayer.App.Controls;

public sealed class VideoHost : HwndHost
{
    private nint _window, _dc;
    private readonly MpvPlayerEngine _engine;
    private readonly ILogger _log;
    public MpvGlRenderer? Renderer { get; private set; }
    public event EventHandler? RendererCreated;
    private static readonly WindowProcedure Procedure = DefWindowProc;
    private static readonly Lazy<ushort> WindowClass = new(RegisterClass);
    public VideoHost(MpvPlayerEngine engine, ILogger log)
    {
        _engine = engine; _log = log;
        SizeChanged += (_, _) => ResizeRenderer();
    }
    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        _ = WindowClass.Value;
        _window = CreateWindowEx(0, "NovaPlayerGpuSurface", "", 0x40000000 | 0x10000000 | 0x04000000,
            0, 0, 1, 1, parent.Handle, 0, GetModuleHandle(null), 0);
        if (_window == 0) throw new Win32Exception();
        _dc = GetDC(_window);
        if (_dc == 0) throw new Win32Exception();
        Renderer = new MpvGlRenderer(_dc, _engine, _log);
        ResizeRenderer();
        RendererCreated?.Invoke(this, EventArgs.Empty);
        return new HandleRef(this, _window);
    }
    private void ResizeRenderer()
    {
        if (_window == 0 || Renderer is null) return;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        Renderer.Resize((int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY));
    }
    protected override nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // 原生窗口被擦除或重新显示时，即使视频暂停也要重绘，不能只等待下一帧解码回调。
        if (message is 0x000F or 0x0014) ResizeRenderer();
        return base.WndProc(hwnd, message, wParam, lParam, ref handled);
    }
    internal void InvalidateSurface() => InvalidateRect(_window, 0, true);
    public void StopRendering() => Renderer?.Dispose();
    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        StopRendering();
        if (_dc != 0) { ReleaseDC(_window, _dc); _dc = 0; }
        DestroyWindow(hwnd.Handle); _window = 0;
    }
    private static ushort RegisterClass()
    {
        var wc = new WndClass { Style = 0x20, Procedure = Marshal.GetFunctionPointerForDelegate(Procedure),
            // 使用系统管理的黑色画刷兜底首帧和重绘间隙，不创建需要应用释放的 GDI 画刷。
            Instance = GetModuleHandle(null), Background = GetStockObject(4), ClassName = "NovaPlayerGpuSurface" };
        var result = RegisterClassW(ref wc);
        if (result == 0) throw new Win32Exception();
        return result;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint WindowProcedure(nint hwnd, uint msg, nint wp, nint lp);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WndClass
    {
        public uint Style;
        public nint Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? MenuName, ClassName;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassW(ref WndClass wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint data);
    [DllImport("user32.dll")] private static extern nint DefWindowProc(nint hwnd, uint msg, nint wp, nint lp);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int index);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool InvalidateRect(nint hwnd, nint rectangle, [MarshalAs(UnmanagedType.Bool)] bool erase);
}
