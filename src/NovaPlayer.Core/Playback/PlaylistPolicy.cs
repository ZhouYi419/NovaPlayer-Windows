namespace NovaPlayer.Core.Playback;

public enum PlaybackMode { Sequential, SingleRepeat, ListRepeat, StopAfterCurrent }

public static class PlaylistPolicy
{
    public static IReadOnlyList<int> NextCandidates(int current, int count, PlaybackMode mode)
    {
        if (count <= 0 || current < 0 || current >= count || mode == PlaybackMode.StopAfterCurrent) return [];
        if (mode == PlaybackMode.SingleRepeat) return [current];
        var result = Enumerable.Range(current + 1, count - current - 1);
        if (mode == PlaybackMode.ListRepeat) result = result.Concat(Enumerable.Range(0, current + 1));
        return result.ToArray();
    }
}
