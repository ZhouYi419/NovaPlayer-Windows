using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using NovaPlayer.App.Controls;
using NovaPlayer.App.ViewModels;
using NovaPlayer.Infrastructure.Playback;
using NovaPlayer.Infrastructure.Rendering;

namespace NovaPlayer.App.Verification;

/// <summary>仅在显式启用验收模式时，使用真实媒体文件、libmpv、窗口和渲染表面执行集成验证。</summary>
internal static class StageOneVerification
{
    internal static async Task<int> RunAsync(MainWindow window, MpvPlayerEngine engine, MpvGlRenderer renderer,
        PlayerViewModel vm, PlaybackOverlay overlay, string file, string reportPath)
    {
        var checks = new List<object>();
        bool passed = false;
        string? failure = null;
        double measuredRate = 0;
        var process = Process.GetCurrentProcess();
        long initialMemory = process.PrivateMemorySize64;
        int initialHandles = process.HandleCount;
        void Check(string name, bool ok, object? detail = null)
        {
            checks.Add(new { name, passed = ok, detail });
            if (!ok) throw new InvalidOperationException($"Verification failed: {name}");
        }
        try
        {
            renderer.CaptureVerificationSamples = true;
            engine.Open(file);
            await Until(() => engine.State.IsLoaded && engine.State.Width > 0 && engine.State.Position > 0.3);
            Check("real video decoded", engine.State.Duration > 10 && renderer.Frames > 0,
                new { engine.State.Duration, engine.State.Width, engine.State.Height, renderer.Gpu });
            await Until(() => renderer.DistinctSamples >= 3);
            Check("changing GPU frame pixels", renderer.DistinctSamples >= 3, renderer.DistinctSamples);
            engine.SetPaused(true);
            await Until(() => engine.State.IsPaused);
            await Task.Delay(250);
            double paused = engine.State.Position;
            await Task.Delay(400);
            Check("pause holds position", Math.Abs(engine.State.Position - paused) < 0.15);
            engine.Seek(3);
            await Until(() => Math.Abs(engine.State.Position - 3) < 0.25 && !engine.State.IsSeeking);
            Check("exact seek", Math.Abs(engine.State.Position - 3) < 0.25, engine.State.Position);
            Check("overlay HWND owns native hit test above GPU", overlay.VerifyOverlayHitTest());

            engine.SetVolume(42);
            await Until(() => Math.Abs(engine.State.Volume - 42) < 0.1);
            engine.SetMuted(false); engine.SetSpeed(15);
            await Until(() => Math.Abs(engine.State.Speed - 15) < 0.01 && engine.State.EffectiveMuted);
            Check("15x accepted and auto muted", engine.State.Speed == 15 && !engine.State.UserMuted);
            engine.Seek(1);
            await Until(() => Math.Abs(engine.State.Position - 1) < 0.25 && !engine.State.IsSeeking);
            engine.SetPaused(false);
            await Until(() => !engine.State.IsPaused && engine.State.Position > 1.2);
            double start = engine.State.Position;
            var watch = Stopwatch.StartNew();
            await Task.Delay(1000);
            measuredRate = (engine.State.Position - start) / watch.Elapsed.TotalSeconds;
            Check("15x media clock advances", measuredRate > 9 && measuredRate < 21, measuredRate);
            engine.SetSpeed(1);
            await Until(() => engine.State.Speed == 1 && !engine.State.EffectiveMuted);
            Check("audio restored below 8x", !engine.State.EffectiveMuted && engine.State.Volume == 42);
            engine.SetMuted(true); engine.SetSpeed(15); engine.SetSpeed(1);
            await Until(() => engine.State.Speed == 1 && engine.State.EffectiveMuted && engine.State.UserMuted);
            Check("user mute survives speed round trip", engine.State.UserMuted && engine.State.EffectiveMuted);
            engine.SetMuted(false); engine.SetPaused(true);

            long before = renderer.Frames;
            foreach (var size in new[] { (960d, 620d), (1280d, 760d), (1100d, 700d), (1440d, 900d) })
            { window.Width = size.Item1; window.Height = size.Item2; await Task.Delay(180); }
            Check("resize redraws GPU surface", renderer.Frames > before);
            overlay.ShowControls();
            Check("overlay follows resize", overlay.VerifyOverlayHitTest());
            window.ToggleFullscreen(); await Task.Delay(400);
            Check("fullscreen keeps overlay", vm.IsFullscreen && overlay.VerifyOverlayHitTest());
            window.ToggleFullscreen(); await Task.Delay(300);
            Check("fullscreen restores window", !vm.IsFullscreen && overlay.VerifyOverlayHitTest());
            window.WindowState = WindowState.Minimized; await Task.Delay(200);
            window.WindowState = WindowState.Normal; window.Activate(); await Task.Delay(300);
            Check("minimize restore keeps overlay", overlay.VerifyOverlayHitTest());
            // 比较预热后的等价窗口状态，避免将首次分配 GPU 资源产生的内存增长误判为泄漏。
            process.Refresh();
            long warmMemory = process.PrivateMemorySize64;
            int warmHandles = process.HandleCount;
            for (int cycle = 0; cycle < 8; cycle++)
            {
                window.ToggleFullscreen(); await Task.Delay(90);
                window.ToggleFullscreen(); await Task.Delay(90);
                window.Width = cycle % 2 == 0 ? 1100 : 1440;
                window.Height = cycle % 2 == 0 ? 700 : 900;
                await Task.Delay(90);
            }
            await Task.Delay(500);
            process.Refresh();
            Check("repeated resize/fullscreen resource growth bounded",
                process.PrivateMemorySize64 - warmMemory < 192L * 1024 * 1024 && process.HandleCount - warmHandles < 40,
                new { cycles = 8, memoryDelta = process.PrivateMemorySize64 - warmMemory, handlesDelta = process.HandleCount - warmHandles });
            engine.SetPaused(false);
            await Until(() => !engine.State.IsPaused);
            Check("session survives window transitions", engine.State.IsLoaded && engine.State.Error is null);
            passed = true;
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            renderer.CaptureVerificationSamples = false;
            process.Refresh();
            var report = new { passed, failure, file = Path.GetFullPath(file), gpu = renderer.Gpu,
                decoder = engine.State.Decoder, renderedFrames = renderer.Frames, distinctPixelSamples = renderer.DistinctSamples,
                measuredRate, checks, resources = new { initialMemory, finalMemory = process.PrivateMemorySize64,
                    initialHandles, finalHandles = process.HandleCount },
                note = "Resource values cover one live session; teardown/repeated-process checks are performed by Verify-Stage1.ps1." };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        return passed ? 0 : 1;
    }
    private static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Timed out waiting for real playback state.");
            await Task.Delay(30);
        }
    }
}
