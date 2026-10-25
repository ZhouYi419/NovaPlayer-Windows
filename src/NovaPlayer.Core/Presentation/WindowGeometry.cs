namespace NovaPlayer.Core.Presentation;

public sealed record WindowGeometry(double Left, double Top, double Width, double Height, bool Maximized = false)
{
    public bool IsValid => new[] { Left, Top, Width, Height }.All(double.IsFinite) && Left is >= -100000 and <= 100000 && Top is >= -100000 and <= 100000 && Width is >= 280 and <= 20000 && Height is >= 150 and <= 20000;
}
