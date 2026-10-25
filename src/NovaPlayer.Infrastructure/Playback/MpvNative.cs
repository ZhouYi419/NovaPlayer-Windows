using System.Runtime.InteropServices;

namespace NovaPlayer.Infrastructure.Playback;

// 原生接口的二进制布局遵循 mpv 的 client.h 和 render_gl.h，进程仅支持 x64。
internal static unsafe class MpvNative
{
    private const string Library = "libmpv-2.dll";
    static MpvNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(MpvNative).Assembly, (name, _, _) =>
            name == Library ? NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "native", "win-x64", Library)) : 0);
    }
    internal const int String = 1, Flag = 3, Int64 = 4, Double = 5, Node = 6, NodeArray = 7, NodeMap = 8;

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    internal struct NodeValue
    {
        [FieldOffset(0)] public nint Pointer;
        [FieldOffset(0)] public long Integer;
        [FieldOffset(0)] public int Boolean;
        [FieldOffset(0)] public double Number;
        [FieldOffset(8)] public int Format;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct NodeList { public int Count; public nint Values, Keys; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Event { public int Id, Error; public ulong UserData; public nint Data; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Property { public nint Name; public int Format; public nint Data; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct EndFile { public int Reason, Error; public long PlaylistId, InsertId; public int InsertCount; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct RenderParam { public int Type; public nint Data; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct GlInit { public nint GetProcAddress, Context; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct GlFbo { public int Fbo, Width, Height, InternalFormat; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate nint GetProcAddress(nint context, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void RenderUpdate(nint context);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint mpv_create();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_initialize(nint handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void mpv_terminate_destroy(nint handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void mpv_wakeup(nint handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint mpv_wait_event(nint handle, double timeout);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_set_option_string(nint handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_observe_property(nint handle, ulong id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_set_property_async(nint handle, ulong id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, void* value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_set_property(nint handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, void* value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_command_async(nint handle, ulong id, nint args);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint mpv_error_string(int error);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_request_log_messages(nint handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string level);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_render_context_create(out nint context, nint handle, RenderParam* args);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void mpv_render_context_set_update_callback(nint context, RenderUpdate? callback, nint data);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong mpv_render_context_update(nint context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_render_context_render(nint context, RenderParam* args);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void mpv_render_context_report_swap(nint context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void mpv_render_context_free(nint context);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint mpv_get_property_string(nint handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void mpv_free(nint data);

    internal static void Check(int result)
    {
        if (result < 0) throw new InvalidOperationException($"libmpv: {Marshal.PtrToStringUTF8(mpv_error_string(result))} ({result})");
    }
}
