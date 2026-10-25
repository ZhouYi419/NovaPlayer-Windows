namespace NovaPlayer.Core.Playback;

public sealed record PlayerState
{
    public long SessionId { get; init; }
    public string? FilePath { get; init; }
    public double Position { get; init; }
    public double Duration { get; init; }
    public double Speed { get; init; } = 1;
    public double Volume { get; init; } = 70;
    public bool IsPaused { get; init; } = true;
    public bool IsLoading { get; init; }
    public bool IsSeeking { get; init; }
    public bool IsLoaded { get; init; }
    public bool IsEnded { get; init; }
    public bool UserMuted { get; init; }
    public bool EffectiveMuted { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string Decoder { get; init; } = "未加载";
    public string VideoCodec { get; init; } = "未知";
    public string AudioCodec { get; init; } = "未知";
    public double FramesPerSecond { get; init; }
    public bool SubtitlesVisible { get; init; } = true;
    public IReadOnlyList<MediaTrack> Tracks { get; init; } = Array.Empty<MediaTrack>();
    public long? SubtitleId { get; init; }
    public long? AudioId { get; init; }
    public double SubtitleSize { get; init; } = 55;
    public double SubtitleDelay { get; init; }
    public string AspectRatio { get; init; } = "原始比例";
    public string? OperationWarning { get; init; }
    public string? Error { get; init; }
    public string? Warning { get; init; }
}
