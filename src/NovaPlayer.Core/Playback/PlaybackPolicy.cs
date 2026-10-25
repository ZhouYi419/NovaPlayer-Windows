namespace NovaPlayer.Core.Playback;

public static class PlaybackPolicy
{
    public const double MinimumSpeed = 0.25;
    public const double MaximumSpeed = 15;
    public static IReadOnlyList<double> Speeds { get; } = Array.AsReadOnly<double>(
        [0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4, 5, 8, 10, 15]);

    public static double ValidateSpeed(double value) =>
        double.IsFinite(value) && value is >= MinimumSpeed and <= MaximumSpeed
            ? value : throw new ArgumentOutOfRangeException(nameof(value), "倍速必须在 0.25～15 之间。");

    public static bool EffectiveMute(bool userMuted, double speed) => userMuted || speed > 8;

    public static double ClampPosition(double seconds, double duration)
    {
        if (!double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        return Math.Clamp(seconds, 0, Math.Max(0, duration));
    }
}
