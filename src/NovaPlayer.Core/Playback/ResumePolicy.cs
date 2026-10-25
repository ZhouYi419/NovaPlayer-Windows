namespace NovaPlayer.Core.Playback;

public static class ResumePolicy
{
    public static double Position(double position, double duration, bool enabled) =>
        enabled && double.IsFinite(position) && double.IsFinite(duration) &&
        position >= 5 && duration - position >= 30 && position / duration < 0.95 ? position : 0;
}
