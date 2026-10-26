using System.Runtime.InteropServices;

namespace NovaPlayer.Infrastructure.Rendering;

internal static class GlNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct PixelFormat
    {
        public ushort Size, Version;
        public uint Flags;
        public byte PixelType, ColorBits, RedBits, RedShift, GreenBits, GreenShift, BlueBits, BlueShift,
            AlphaBits, AlphaShift, AccumBits, AccumRedBits, AccumGreenBits, AccumBlueBits, AccumAlphaBits,
            DepthBits, StencilBits, AuxBuffers, LayerType, Reserved;
        public uint LayerMask, VisibleMask, DamageMask;
    }
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern int ChoosePixelFormat(nint dc, ref PixelFormat pfd);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetPixelFormat(nint dc, int format, ref PixelFormat pfd);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SwapBuffers(nint dc);
    [DllImport("opengl32.dll", SetLastError = true)] internal static extern nint wglCreateContext(nint dc);
    [DllImport("opengl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool wglMakeCurrent(nint dc, nint context);
    [DllImport("opengl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool wglDeleteContext(nint context);
    [DllImport("opengl32.dll", CharSet = CharSet.Ansi)] internal static extern nint wglGetProcAddress(string name);
    [DllImport("opengl32.dll")] internal static extern nint glGetString(uint name);
    [DllImport("opengl32.dll")] internal static extern void glReadPixels(int x, int y, int width, int height, uint format, uint type, nint pixels);
    [DllImport("opengl32.dll")] internal static extern void glReadBuffer(uint mode);
    [DllImport("opengl32.dll")] internal static extern void glClearColor(float red, float green, float blue, float alpha);
    [DllImport("opengl32.dll")] internal static extern void glClear(uint mask);
    [DllImport("opengl32.dll")] internal static extern void glDisable(uint capability);
    [DllImport("opengl32.dll")] internal static extern void glColorMask(byte red, byte green, byte blue, byte alpha);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] internal static extern nint GetProcAddress(nint module, string name);
}
