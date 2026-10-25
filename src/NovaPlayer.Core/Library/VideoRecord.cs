namespace NovaPlayer.Core.Library;

public sealed record VideoRecord
{
    public required string FilePath { get; init; }
    public string FileName => Path.GetFileName(FilePath);
    public long FileSize { get; init; }
    public long ModifiedUtcTicks { get; init; }
    public double Duration { get; init; }
    public double Position { get; init; }
    public double Speed { get; init; } = 1;
    public bool HasSpeed { get; init; }
    public DateTime? LastPlayedUtc { get; init; }
    public bool Completed { get; init; }
    public bool HistoryVisible { get; init; }
    public string? ThumbnailPath { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;

    public bool MatchesFile(long size, long ticks) => FileSize == size && ModifiedUtcTicks == ticks;
    public static string Key(string path) => Path.GetFullPath(path).ToUpperInvariant();
}

public sealed record LibrarySnapshot(IReadOnlyList<VideoRecord> Videos, IReadOnlyList<string> Playlist, string? CurrentPath);
public sealed record ThumbnailResult(string Path, double Duration);
