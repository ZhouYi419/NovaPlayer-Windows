using System.Windows;
using System.Windows.Media;

namespace NovaPlayer.App.Controls;

public sealed class Icon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(Icon), new FrameworkPropertyMetadata("play", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = System.Windows.Documents.TextElement.ForegroundProperty.AddOwner(typeof(Icon), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    private static readonly Dictionary<string, Geometry> Shapes = new()
    {
        ["home"] = Geometry.Parse("M3,10 L12,3 21,10 M5,9 L5,21 10,21 10,14 14,14 14,21 19,21 19,9"),
        ["history"] = Geometry.Parse("M4,5 L4,10 9,10 M4,10 A9,9 0 1 1 4,16 M12,7 L12,12 16,14"),
        ["settings"] = Geometry.Parse("M9,3 L15,3 16,6 19,7 22,11 20,14 20,18 15,21 12,19 9,21 4,18 4,14 2,11 5,7 8,6 Z M12,8 A4,4 0 1 1 11.99,8"),
        ["search"] = Geometry.Parse("M18,10 A8,8 0 1 1 2,10 A8,8 0 1 1 18,10 M16,16 L22,22"),
        ["folder"] = Geometry.Parse("M3,6 L9,6 11,9 21,9 21,20 3,20 Z M3,9 L3,4 9,4 11,6 20,6 20,9"),
        ["plus"] = Geometry.Parse("M12,4 L12,20 M4,12 L20,12"),
        ["chevron"] = Geometry.Parse("M9,5 L16,12 9,19"),
        ["down"] = Geometry.Parse("M5,9 L12,16 19,9"),
        ["close"] = Geometry.Parse("M5,5 L19,19 M19,5 L5,19"),
        ["minimize"] = Geometry.Parse("M4,12 L20,12"),
        ["maximize"] = Geometry.Parse("M5,5 L19,5 19,19 5,19 Z"),
        ["theme"] = Geometry.Parse("M8,3 L4,5 1,9 5,12 7,10 7,21 17,21 17,10 19,12 23,9 20,5 16,3 A4,4 0 0 1 8,3 Z"),
        ["video"] = Geometry.Parse("M3,4 L21,4 21,20 3,20 Z M7,4 L7,20 M17,4 L17,20 M3,9 L7,9 M3,15 L7,15 M17,9 L21,9 M17,15 L21,15"),
        ["play"] = Geometry.Parse("M7,4 L20,12 7,20 Z"),
        ["pause"] = Geometry.Parse("M8,4 L8,20 M16,4 L16,20"),
        ["volume"] = Geometry.Parse("M3,9 L7,9 12,5 12,19 7,15 3,15 Z M16,8 A6,6 0 0 1 16,16 M19,5 A10,10 0 0 1 19,19"),
        ["mute"] = Geometry.Parse("M3,9 L7,9 12,5 12,19 7,15 3,15 Z M16,9 L22,15 M22,9 L16,15"),
        ["fullscreen"] = Geometry.Parse("M3,9 L3,3 9,3 M15,3 L21,3 21,9 M21,15 L21,21 15,21 M9,21 L3,21 3,15"),
        ["pip"] = Geometry.Parse("M3,4 L21,4 21,20 3,20 Z M12,12 L19,12 19,18 12,18 Z"),
        ["subtitles"] = Geometry.Parse("M3,5 L21,5 21,19 3,19 Z M6,10 L10,10 M14,10 L18,10 M6,14 L14,14 M17,14 L18,14"),
        ["more"] = Geometry.Parse("M12,4 L12,5 M12,11 L12,12 M12,18 L12,19"),
        ["list"] = Geometry.Parse("M4,6 L6,6 M10,6 L21,6 M4,12 L6,12 M10,12 L21,12 M4,18 L6,18 M10,18 L21,18"),
        ["info"] = Geometry.Parse("M21,12 A9,9 0 1 1 3,12 A9,9 0 1 1 21,12 M12,10 L12,17 M12,6 L12,7"),
        ["keyboard"] = Geometry.Parse("M2,6 L22,6 22,19 2,19 Z M5,10 L6,10 M9,10 L10,10 M13,10 L14,10 M17,10 L19,10 M5,14 L6,14 M9,14 L19,14"),
        ["back"] = Geometry.Parse("M8,8 L4,5 8,2 M4,5 A9,9 0 1 1 3,15"),
        ["forward"] = Geometry.Parse("M16,8 L20,5 16,2 M20,5 A9,9 0 1 0 21,15"),
        ["previous"] = Geometry.Parse("M5,5 L5,19 M19,5 L8,12 19,19 Z"),
        ["next"] = Geometry.Parse("M19,5 L19,19 M5,5 L16,12 5,19 Z")
    };
    protected override Size MeasureOverride(Size availableSize) => new(20, 20);
    protected override void OnRender(DrawingContext dc)
    {
        if (!Shapes.TryGetValue(Kind ?? "play", out var geometry)) return;
        dc.PushTransform(new ScaleTransform(ActualWidth / 24, ActualHeight / 24));
        dc.DrawGeometry(null, new Pen(Foreground, Kind == "more" ? 3 : 1.65) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, geometry);
        dc.Pop();
    }
}
