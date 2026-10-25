using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Security.Cryptography;
using NovaPlayer.App.Controls;
using NovaPlayer.Core.Playback;
using NovaPlayer.Infrastructure.Playback;
using NovaPlayer.Infrastructure.Rendering;

namespace NovaPlayer.App.Verification;

/// <summary>发布验收使用真实解码、GPU 像素和媒体时钟；报告保留机器信息，避免将单机结果外推到其他系统。</summary>
internal static class StageFiveVerification
{
    internal static async Task<int> RunFileOpenAsync(MainWindow window, MpvPlayerEngine engine, MpvGlRenderer renderer, string file, string reportPath)
    {
        bool passed = false;
        string? failure = null;
        double resumedPosition = 0;
        try
        {
            await Until(() => engine.State.IsLoaded && window.ViewModel.State.IsLoaded && engine.State.Position > .2);
            var stored = await window.ViewModel.ReadStoredAsync();
            bool reopening = stored.Videos.Any(v => v.FilePath == file && v.Position >= 10);
            if (reopening) await Until(() => engine.State.Position >= 11 && !engine.State.IsSeeking);
            engine.SetPaused(true);
            await Until(() => engine.State.IsPaused);
            resumedPosition = engine.State.Position;
            if (Path.GetFullPath(engine.State.FilePath!) != Path.GetFullPath(file) || renderer.Frames == 0) throw new InvalidOperationException("资源管理器命令行未打开目标媒体。");
            engine.Seek(12);
            await Until(() => Math.Abs(engine.State.Position - 12) < .3 && !engine.State.IsSeeking);
            await window.ViewModel.SaveCurrentAsync();
            passed = engine.State.Error is null;
        }
        catch (Exception ex) { failure = ex.ToString(); }
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { passed, failure, resumedPosition, file, state = engine.State, frames = renderer.Frames }));
        return passed ? 0 : 1;
    }

    internal sealed record MediaCase(string Path, string Codec, int Width, int Height);
    internal sealed record Plan(MediaCase[] Cases, int SoakSeconds = 120, bool Transitions = true, bool CycleSpeeds = true, bool SoakHardware = true);

    internal static async Task<int> RunAsync(MainWindow window, MpvPlayerEngine engine, MpvGlRenderer renderer,
        PlaybackOverlay overlay, string planPath, string reportPath)
    {
        var checks = new List<object>();
        var measurements = new List<object>();
        var resources = new List<object>();
        Plan? executedPlan = null;
        bool passed = false;
        string? failure = null;
        var vm = window.ViewModel;
        using var process = Process.GetCurrentProcess();
        void Check(string name, bool ok, object? detail = null)
        {
            checks.Add(new { name, passed = ok, detail });
            if (!ok) throw new InvalidOperationException(name);
        }
        async Task Open(MediaCase item, bool hardware)
        {
            engine.SetSpeed(1);
            engine.Configure(new PlaybackOptions(HardwareDecoding: hardware));
            await Task.Run(() => engine.Open(item.Path));
            await Until(() => engine.State.IsLoaded && engine.State.Position > .15 && engine.State.Width > 0 && engine.State.VideoCodec != "未知");
            if (engine.State.Error is { } error) throw new InvalidOperationException(error);
        }
        try
        {
            var plan = JsonSerializer.Deserialize<Plan>(await File.ReadAllTextAsync(planPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("缺少发布验收计划。");
            executedPlan = plan;
            if (plan.Cases.Length == 0 || plan.SoakSeconds is < 10 or > 86400) throw new InvalidDataException("验收计划超出允许范围。");
            renderer.CaptureVerificationSamples = true;
            Check("应用图标包含在窗口资源中", window.Icon is not null);
            foreach (bool hardware in new[] { true, false })
            foreach (var item in plan.Cases)
            {
                await Open(item, hardware);
                renderer.CaptureVerificationSamples = true;
                long before = renderer.Frames;
                int samples = renderer.DistinctSamples;
                await Until(() => renderer.Frames > before + 3 && renderer.DistinctSamples > samples);
                // 像素回读仅确认真实画面；性能和持续运行阶段使用正式播放路径，避免驱动回读缓冲干扰测量。
                renderer.CaptureVerificationSamples = false;
                Check($"{Path.GetFileName(item.Path)} / {(hardware ? "自动解码" : "软件解码")}",
                    engine.State.Width == item.Width && engine.State.Height == item.Height && engine.State.VideoCodec == item.Codec
                    && (hardware || engine.State.Decoder == "软件解码"), new { engine.State.VideoCodec, engine.State.AudioCodec, engine.State.Decoder });
                engine.SetPaused(true);
                await Until(() => engine.State.IsPaused);
                engine.Seek(5);
                await Until(() => Math.Abs(engine.State.Position - 5) < .4 && !engine.State.IsSeeking);
                engine.SetSpeed(15);
                await Until(() => engine.State.Speed == 15);
                engine.SetPaused(false);
                await Until(() => !engine.State.IsPaused && engine.State.Position > 5.2);
                double start = engine.State.Position;
                var watch = Stopwatch.StartNew();
                await Task.Delay(1100);
                double rate = (engine.State.Position - start) / watch.Elapsed.TotalSeconds;
                var diagnostics = await Task.Run(engine.ReadDiagnostics);
                measurements.Add(new { file = Path.GetFileName(item.Path), hardware, rate, engine.State.Decoder, renderedFrames = renderer.Frames - before, diagnostics });
                // 超高分辨率性能作为实测值保留；低分辨率编码矩阵同时要求媒体时钟达到可接受范围。
                if (item.Width <= 1920) Check($"{Path.GetFileName(item.Path)} 15倍速媒体时钟 / {hardware}", rate is > 9 and < 21, rate);
                Check("渲染与解码过程没有致命错误", engine.State.Error is null);
            }

            var baseline = plan.Cases.First();
            renderer.CaptureVerificationSamples = false;
            await Open(baseline, plan.SoakHardware);
            engine.SetSpeed(15);
            await Until(() => engine.State.Speed == 15);
            // 首次进入全屏/小窗会分配桌面合成和驱动缓冲；预热所有将测量的状态，再比较等价主窗口状态。
            if (plan.Transitions)
                for (int warm = 0; warm < 3; warm++)
                {
                    window.ToggleMiniPlayer(); await Task.Delay(250); window.ToggleMiniPlayer(); await Task.Delay(250);
                    window.ToggleFullscreen(); await Task.Delay(250); window.ToggleFullscreen(); await Task.Delay(250);
                    engine.Seek(0); engine.SetPaused(false); await Task.Delay(150);
                }
            await Task.Delay(1200);
            process.Refresh();
            long warmMemory = process.PrivateMemorySize64;
            int warmHandles = process.HandleCount;
            int warmThreads = process.Threads.Count;
            long warmFrames = renderer.Frames;
            var soak = Stopwatch.StartNew();
            var cpu = process.TotalProcessorTime;
            int cycles = 0;
            while (soak.Elapsed.TotalSeconds < plan.SoakSeconds)
            {
                if (engine.State.Position > baselineDuration() - 20 || engine.State.IsEnded)
                { engine.Seek(0); engine.SetPaused(false); }
                if (plan.CycleSpeeds) engine.SetSpeed(cycles % 3 == 0 ? 1 : 15);
                if (plan.Transitions && cycles % 4 == 0) { window.ToggleMiniPlayer(); await Task.Delay(120); window.ToggleMiniPlayer(); }
                if (plan.Transitions && cycles % 7 == 0) { window.ToggleFullscreen(); await Task.Delay(120); window.ToggleFullscreen(); }
                await Task.Delay(700);
                Check("循环播放和窗口转换保持有效会话", engine.State.IsLoaded && engine.State.Error is null);
                if (cycles % 15 == 0)
                {
                    process.Refresh();
                    resources.Add(new { seconds = soak.Elapsed.TotalSeconds, memory = process.PrivateMemorySize64, managedMemory = GC.GetTotalMemory(false), allocated = GC.GetTotalAllocatedBytes(), collections = GC.CollectionCount(2), handles = process.HandleCount, threads = process.Threads.Count, frames = renderer.Frames });
                }
                cycles++;
            }
            if (vm.IsMiniPlayer) window.ToggleMiniPlayer();
            await Task.Delay(1000);
            process.Refresh();
            Check("持续运行期间真实渲染推进", renderer.Frames - warmFrames > plan.SoakSeconds * 5, renderer.Frames - warmFrames);
            Check("持续运行后资源增长有界", process.PrivateMemorySize64 - warmMemory < 256L * 1024 * 1024 && process.HandleCount - warmHandles < 50 && process.Threads.Count - warmThreads < 20,
                new { cycles, seconds = soak.Elapsed.TotalSeconds, memoryDelta = process.PrivateMemorySize64 - warmMemory, handlesDelta = process.HandleCount - warmHandles, threadDelta = process.Threads.Count - warmThreads,
                    renderedFrames = renderer.Frames - warmFrames, cpuPercentOfMachine = (process.TotalProcessorTime - cpu).TotalSeconds / soak.Elapsed.TotalSeconds / Environment.ProcessorCount * 100 });
            overlay.ShowControls();
            await ShellCapture.SaveAsync(window, overlay, renderer, Path.ChangeExtension(reportPath, ".png"));
            passed = true;
            double baselineDuration() => Math.Max(25, engine.State.Duration);
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            renderer.CaptureVerificationSamples = false;
            using var binary = File.OpenRead(typeof(StageFiveVerification).Assembly.Location);
            string assemblySha256 = Convert.ToHexString(await SHA256.HashDataAsync(binary));
            var report = new { passed, failure, os = RuntimeInformation.OSDescription, osVersion = Environment.OSVersion.Version.ToString(), architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                gpu = renderer.Gpu, runtime = RuntimeInformation.FrameworkDescription, assemblySha256, plan = executedPlan, finalState = engine.State, checks, measurements, resources,
                scope = "仅代表报告中的操作系统、GPU、驱动和生成样片；持续验证是有限时长冒烟，不代表长期无泄漏。" };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        return passed ? 0 : 1;
    }
    private static async Task Until(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed.TotalSeconds > 25) throw new TimeoutException("等待真实播放状态超时。");
            await Task.Delay(30);
        }
    }
}
