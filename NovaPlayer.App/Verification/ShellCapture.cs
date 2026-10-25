using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NovaPlayer.App.Controls;
using NovaPlayer.Infrastructure.Rendering;

namespace NovaPlayer.App.Verification;

internal static class ShellCapture
{
    // 导出当前应用自身的 WPF 视觉树和真实 GPU 画面，用于布局验收，不捕获其他应用或桌面内容。
    internal static async Task SaveAsync(MainWindow window, PlaybackOverlay overlay, MpvGlRenderer renderer, string path)
    {
        window.UpdateLayout(); overlay.SyncBounds(); overlay.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        int width = Math.Max(1, (int)Math.Round(window.ActualWidth * dpi.DpiScaleX));
        int height = Math.Max(1, (int)Math.Round(window.ActualHeight * dpi.DpiScaleY));
        var shell = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        shell.Render((Visual)window.Content);
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRectangle((Brush)Application.Current.Resources["AppBackground"], null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
            dc.DrawImage(shell, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        }
        if (window.ViewModel.IsHome)
        {
            var frame = await renderer.CaptureFrameAsync();
            var video = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Pixels, frame.Width * 4);
            var chrome = new RenderTargetBitmap(Math.Max(1, (int)Math.Round(overlay.ActualWidth * dpi.DpiScaleX)), Math.Max(1, (int)Math.Round(overlay.ActualHeight * dpi.DpiScaleY)), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            chrome.Render((Visual)overlay.Content);
            var content = (FrameworkElement)window.FindName("VideoContainer");
            var origin = content.TranslatePoint(new Point(), window);
            var rect = new Rect(origin, new Size(content.ActualWidth, content.ActualHeight));
            var composite = new DrawingVisual();
            using (var dc = composite.RenderOpen())
            {
                dc.DrawDrawing(drawing.Drawing);
                dc.PushClip(new RectangleGeometry(rect, 10, 10));
                dc.DrawImage(video, rect); dc.DrawImage(chrome, rect); dc.Pop();
            }
            drawing = composite;
        }
        var image = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        image.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(path); encoder.Save(output);
    }
}
