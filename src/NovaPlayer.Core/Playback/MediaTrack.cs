namespace NovaPlayer.Core.Playback;

public enum MediaTrackKind { Video, Audio, Subtitle }

public sealed record MediaTrack(long Id, MediaTrackKind Kind, string? Title, string? Language,
    string? Codec, bool IsExternal, string? ExternalPath)
{
    public string LanguageName => Language?.ToLowerInvariant() switch
    {
        "zh" or "chi" or "zho" => "中文",
        "en" or "eng" => "英语",
        "ja" or "jpn" => "日语",
        "ko" or "kor" => "韩语",
        "fr" or "fre" or "fra" => "法语",
        null or "" or "und" => "语言未知",
        _ => Language
    };
    private string Label => IsExternal && !string.IsNullOrWhiteSpace(ExternalPath) ? Path.GetFileName(ExternalPath) : !string.IsNullOrWhiteSpace(Title) ? Title :
        Kind switch { MediaTrackKind.Audio => $"音轨 {Id}", MediaTrackKind.Video => $"视频 {Id}", _ => $"字幕 {Id}" };
    public string DisplayName => $"{Label} · {LanguageName}" +
        (string.IsNullOrWhiteSpace(Codec) ? "" : $" · {Codec}") + (IsExternal ? " · 外挂" : "");
}

public sealed record PlaybackOptions(bool AutoLoadSubtitles = true, bool PitchCorrection = true,
    bool HardwareDecoding = true, bool AutoMuteHighSpeed = true);
