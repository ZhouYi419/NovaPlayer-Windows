using System.ComponentModel;
using System.Runtime.InteropServices;
using NovaPlayer.Infrastructure.Playback;
using Serilog;

namespace NovaPlayer.Infrastructure.Rendering;

/// <summary>所有 OpenGL 和 mpv 渲染接口调用均在绑定 WGL 上下文的专属线程中执行。</summary>
public sealed class MpvGlRenderer : IDisposable
{
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private readonly MpvNative.GetProcAddress _getProc;
    private readonly MpvNative.RenderUpdate _update;
    private readonly nint _dc;
    private readonly MpvPlayerEngine _engine;
    private readonly ILogger _log;
    private volatile bool _stopping;
    private int _width = 1, _height = 1, _resize = 1;
    private long _frames;
    private int _distinctSamples;
    private string? _sample;
    private TaskCompletionSource<RenderedFrame>? _capture;
    public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public event EventHandler<string>? Failed;
    public long Frames => Interlocked.Read(ref _frames);
    public int DistinctSamples => Volatile.Read(ref _distinctSamples);
    public string Gpu { get; private set; } = "初始化中";

    public MpvGlRenderer(nint dc, MpvPlayerEngine engine, ILogger log)
    {
        _dc = dc; _engine = engine; _log = log;
        _getProc = Resolve;
        _update = _ => _wake.Set(); // 原生回调只唤醒渲染线程，不直接调用 mpv 或 OpenGL。
        _thread = new Thread(RenderLoop) { IsBackground = true, Name = "NovaPlayer GPU renderer" };
        _thread.Start();
    }

    public void Resize(int width, int height)
    {
        Volatile.Write(ref _width, Math.Max(1, width));
        Volatile.Write(ref _height, Math.Max(1, height));
        Interlocked.Exchange(ref _resize, 1);
        _wake.Set();
    }

    private static nint Resolve(nint _, string name)
    {
        var address = GlNative.wglGetProcAddress(name);
        return address == 0 || address == 1 || address == 2 || address == 3 || address == -1
            ? GlNative.GetProcAddress(GlNative.GetModuleHandle("opengl32.dll"), name) : address;
    }

    private unsafe void RenderLoop()
    {
        nint gl = 0, render = 0;
        try
        {
            var pfd = new GlNative.PixelFormat
            {
                Size = (ushort)Marshal.SizeOf<GlNative.PixelFormat>(),
                Version = 1,
                Flags = 0x4 | 0x20 | 0x1,
                ColorBits = 32,
                AlphaBits = 8,
                DepthBits = 0
            };
            int format = GlNative.ChoosePixelFormat(_dc, ref pfd);
            if (format == 0 || !GlNative.SetPixelFormat(_dc, format, ref pfd)) throw new Win32Exception();
            gl = GlNative.wglCreateContext(_dc);
            if (gl == 0 || !GlNative.wglMakeCurrent(_dc, gl)) throw new Win32Exception();
            Gpu = Marshal.PtrToStringAnsi(GlNative.glGetString(0x1F01)) ?? "未知 GPU";
            byte* api = stackalloc byte[] { (byte)'o', (byte)'p', (byte)'e', (byte)'n', (byte)'g', (byte)'l', 0 };
            var init = new MpvNative.GlInit { GetProcAddress = Marshal.GetFunctionPointerForDelegate(_getProc) };
            MpvNative.RenderParam* create = stackalloc MpvNative.RenderParam[3];
            create[0] = new() { Type = 1, Data = (nint)api };
            create[1] = new() { Type = 2, Data = (nint)(&init) };
            create[2] = default;
            MpvNative.Check(MpvNative.mpv_render_context_create(out render, _engine.NativeHandle, create));
            MpvNative.mpv_render_context_set_update_callback(render, _update, 0);
            _log.Information("GPU Render API ready: {Gpu}", Gpu);
            Ready.TrySetResult();
            var fbo = new MpvNative.GlFbo();
            int flip = 1, block = 0;
            MpvNative.RenderParam* args = stackalloc MpvNative.RenderParam[4];
            args[0] = new() { Type = 3, Data = (nint)(&fbo) };
            args[1] = new() { Type = 4, Data = (nint)(&flip) };
            args[2] = new() { Type = 12, Data = (nint)(&block) }; // 参数 12 控制是否等待目标呈现时间，此处关闭等待。
            args[3] = default;
            while (!_stopping)
            {
                _wake.WaitOne(100);
                if (_stopping) break;
                ulong flags = MpvNative.mpv_render_context_update(render);
                bool resized = Interlocked.Exchange(ref _resize, 0) != 0;
                if ((flags & 1) == 0 && !resized) continue;
                fbo.Width = Volatile.Read(ref _width); fbo.Height = Volatile.Read(ref _height);
                MpvNative.Check(MpvNative.mpv_render_context_render(render, args));
                // 仅在显式验收模式中采样像素，正常播放不会从 GPU 回读画面。
                if (CaptureVerificationSamples && _engine.State.IsLoaded) SampleFrame(fbo.Width, fbo.Height);
                if (Interlocked.Exchange(ref _capture, null) is { } capture)
                {
                    try { capture.TrySetResult(ReadFrame(fbo.Width, fbo.Height)); }
                    catch (Exception ex) { capture.TrySetException(ex); }
                }
                if (!GlNative.SwapBuffers(_dc)) throw new Win32Exception();
                MpvNative.mpv_render_context_report_swap(render);
                Interlocked.Increment(ref _frames);
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "GPU renderer failed");
            Ready.TrySetException(ex);
            Failed?.Invoke(this, $"GPU 渲染失败：{ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _capture, null)?.TrySetException(new InvalidOperationException("渲染已停止，无法导出画面。"));
            if (render != 0)
            {
                MpvNative.mpv_render_context_set_update_callback(render, null, 0);
                MpvNative.mpv_render_context_free(render);
            }
            if (gl != 0) { GlNative.wglMakeCurrent(0, 0); GlNative.wglDeleteContext(gl); }
            _log.Information("GPU resources released; frames={Frames}", Frames);
        }
    }

    public bool CaptureVerificationSamples { get; set; }

    public Task<RenderedFrame> CaptureFrameAsync()
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        var request = new TaskCompletionSource<RenderedFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _capture, request, null) is not null) throw new InvalidOperationException("已有画面导出任务正在进行。");
        Interlocked.Exchange(ref _resize, 1); _wake.Set();
        return request.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    private static unsafe RenderedFrame ReadFrame(int width, int height)
    {
        var pixels = new byte[checked(width * height * 4)];
        GlNative.glReadBuffer(0x0405);
        fixed (byte* pointer = pixels) GlNative.glReadPixels(0, 0, width, height, 0x80E1, 0x1401, (nint)pointer);
        var topDown = new byte[pixels.Length];
        int stride = width * 4;
        for (int row = 0; row < height; row++) Buffer.BlockCopy(pixels, row * stride, topDown, (height - row - 1) * stride, stride);
        return new RenderedFrame(width, height, topDown);
    }

    private unsafe void SampleFrame(int width, int height)
    {
        byte* pixel = stackalloc byte[4];
        GlNative.glReadBuffer(0x0405); // 读取尚未交换到屏幕的后缓冲区（GL_BACK）。
        GlNative.glReadPixels(width / 2, height / 2, 1, 1, 0x1908, 0x1401, (nint)pixel);
        string sample = $"{pixel[0]:X2}{pixel[1]:X2}{pixel[2]:X2}";
        if (sample != _sample) { _sample = sample; Interlocked.Increment(ref _distinctSamples); }
    }

    public void Dispose()
    {
        if (_stopping) return;
        _stopping = true;
        _wake.Set();
        _thread.Join(); // 渲染线程不向 WPF 同步调度，因此此处等待不会形成相互等待。
        _wake.Dispose();
        GC.KeepAlive(_getProc); GC.KeepAlive(_update);
    }
}

public sealed record RenderedFrame(int Width, int Height, byte[] Pixels);
