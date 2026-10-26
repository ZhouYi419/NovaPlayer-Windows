using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NovaPlayer.App.Controls;
using NovaPlayer.Infrastructure.Playback;
using NovaPlayer.Infrastructure.Rendering;

namespace NovaPlayer.App.Verification;

/// <summary>直接检查原生帧和 WPF 浮层的透明度，避免合成截图忽略 alpha 后掩盖桌面穿透。</summary>
internal static class SurfaceOpacityVerification
{
    internal static async Task<int> RunAsync(MainWindow window, MpvPlayerEngine engine, MpvGlRenderer renderer,
        PlaybackOverlay overlay, string file, string reportPath)
    {
        var checks = new List<object>();
        bool passed = false;
        string? failure = null;
        void Check(string name, bool ok, object? detail = null)
        { checks.Add(new { name, passed = ok, detail }); if (!ok) throw new InvalidOperationException(name); }
        async Task CheckIdle(string label)
        {
            await Until(() => !engine.State.IsLoaded && !window.ViewModel.State.IsLoaded);
            await Task.Delay(180);
            var frame = await renderer.CaptureFrameAsync();
            int transparent = 0, incorrectColor = 0;
            for (int i = 0; i < frame.Pixels.Length; i += 4)
            {
                if (frame.Pixels[i + 3] != 255) transparent++;
                if (Math.Abs(frame.Pixels[i] - 18) > 1 || Math.Abs(frame.Pixels[i + 1] - 11) > 1 || Math.Abs(frame.Pixels[i + 2] - 8) > 1) incorrectColor++;
            }
            Check(label + "：原生帧完全不透明且为深色底", transparent == 0 && incorrectColor == 0, new { frame.Width, frame.Height, transparent, incorrectColor });
            var root = (FrameworkElement)overlay.Content;
            var bitmap = new RenderTargetBitmap(Math.Max(1, (int)root.ActualWidth), Math.Max(1, (int)root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(8, 8, 1, 1), pixel, 4, 0);
            Check(label + "：空闲浮层自身不透明", pixel[3] == 255, pixel[3]);
        }
        try
        {
            await CheckIdle("首次启动");
            await ShellCapture.SaveAsync(window, overlay, renderer, Path.ChangeExtension(reportPath, ".png"));
            foreach (var size in new[] { (960d, 620d), (1440d, 900d) })
            {
                window.Width = size.Item1; window.Height = size.Item2;
                await CheckIdle($"空闲缩放 {size.Item1}");
            }
            window.ToggleFullscreen(); await CheckIdle("空闲全屏");
            window.ToggleFullscreen();
            window.ViewModel.NavigateCommand.Execute("设置"); await Task.Delay(150);
            window.ViewModel.NavigateCommand.Execute("首页"); await CheckIdle("导航返回");
            await Task.Run(() => engine.Open(file));
            await Until(() => engine.State.IsLoaded && window.ViewModel.State.IsLoaded && engine.State.Position > .3);
            renderer.CaptureVerificationSamples = true;
            int samples = renderer.DistinctSamples;
            await Until(() => renderer.DistinctSamples > samples + 2);
            renderer.CaptureVerificationSamples = false;
            var video = await renderer.CaptureFrameAsync();
            bool opaque = true;
            for (int i = 3; i < video.Pixels.Length; i += 4) opaque &= video.Pixels[i] == 255;
            Check("播放中的视频保持不透明并有变化像素", opaque && engine.State.Error is null);
            Check("播放浮层保留视频显示透明度", ((System.Windows.Controls.Grid)overlay.Content).Background is SolidColorBrush brush && brush.Color.A == 1);
            engine.SetPaused(true); await Until(() => engine.State.IsPaused); await Task.Delay(150);
            long beforePaint = renderer.Frames;
            window.VideoHost.InvalidateSurface();
            await Until(() => renderer.Frames > beforePaint);
            Check("暂停后原生重绘无需下一帧解码即可恢复", engine.State.IsPaused);
            window.ToggleMiniPlayer(); await Task.Delay(250);
            window.ToggleMiniPlayer(); await Task.Delay(250);
            Check("小窗切换不重建渲染器", ReferenceEquals(window.VideoHost.Renderer, renderer) && engine.State.IsLoaded);
            engine.Stop(); await CheckIdle("停止播放");
            string corrupt = Path.Combine(Path.GetDirectoryName(reportPath)!, "无法解码.mp4");
            await File.WriteAllTextAsync(corrupt, "这不是视频文件。");
            await Task.Run(() => engine.Open(corrupt));
            await Until(() => engine.State.Error is not null && !engine.State.IsLoaded);
            await CheckIdle("解码失败");
            passed = true;
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            renderer.CaptureVerificationSamples = false;
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { passed, failure, checks, renderer.Gpu }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return passed ? 0 : 1;
    }
    private static async Task Until(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        { if (timeout.Elapsed.TotalSeconds > 20) throw new TimeoutException("等待原生表面状态超时。"); await Task.Delay(30); }
    }
}
