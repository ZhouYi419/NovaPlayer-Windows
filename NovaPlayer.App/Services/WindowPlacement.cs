using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using NovaPlayer.Core.Presentation;

namespace NovaPlayer.App.Services;

internal static class WindowPlacement
{
    internal static WindowGeometry Capture(Window window)
    {
        Rect bounds = window.WindowState == WindowState.Normal ? new(window.Left, window.Top, window.Width, window.Height) : window.RestoreBounds;
        return new(bounds.Left, bounds.Top, bounds.Width, bounds.Height, window.WindowState == WindowState.Maximized);
    }
    internal static Rect WorkArea(Window window, WindowGeometry? target = null)
    {
        nint monitor = MonitorFromWindow(new WindowInteropHelper(window).Handle, 2);
        var source = PresentationSource.FromVisual(window)?.CompositionTarget;
        if (target is { IsValid: true })
        {
            var toDevice = source?.TransformToDevice ?? Matrix.Identity;
            Point start = toDevice.Transform(new Point(target.Left, target.Top));
            Point end = toDevice.Transform(new Point(target.Left + target.Width, target.Top + target.Height));
            var rectangle = new NativeRect { Left = (int)start.X, Top = (int)start.Y, Right = (int)end.X, Bottom = (int)end.Y };
            monitor = MonitorFromRect(ref rectangle, 2);
        }
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return SystemParameters.WorkArea;
        var transform = source?.TransformFromDevice ?? Matrix.Identity;
        return new Rect(transform.Transform(new Point(info.Work.Left, info.Work.Top)), transform.Transform(new Point(info.Work.Right, info.Work.Bottom)));
    }
    internal static void Apply(Window window, WindowGeometry geometry, double minWidth, double minHeight, bool preserveSize = false)
    {
        if (!geometry.IsValid) return;
        Rect area = WorkArea(window, geometry);
        double width = Math.Clamp(geometry.Width, minWidth, preserveSize ? 20000 : Math.Max(minWidth, area.Width));
        double height = Math.Clamp(geometry.Height, minHeight, preserveSize ? 20000 : Math.Max(minHeight, area.Height));
        window.WindowState = WindowState.Normal;
        window.Width = width; window.Height = height;
        window.Left = Math.Clamp(geometry.Left, area.Left, Math.Max(area.Left, area.Right - width));
        window.Top = Math.Clamp(geometry.Top, area.Top, Math.Max(area.Top, area.Bottom - height));
        if (geometry.Maximized) window.WindowState = WindowState.Maximized;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromRect(ref NativeRect rectangle, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
