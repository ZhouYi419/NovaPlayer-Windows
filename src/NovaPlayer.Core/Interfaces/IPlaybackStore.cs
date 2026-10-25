using NovaPlayer.Core.Library;

namespace NovaPlayer.Core.Interfaces;

public interface IPlaybackStore : IAsyncDisposable
{
    string DatabasePath { get; }
    string? StartupWarning { get; }
    Task InitializeAsync();
    Task<LibrarySnapshot> ReadAsync();
    Task<IReadOnlyList<VideoRecord>> RegisterAsync(IReadOnlyList<VideoRecord> videos);
    Task SaveVideoAsync(VideoRecord video);
    Task SavePlaylistAsync(IReadOnlyList<string> paths, string? currentPath);
    Task SetThumbnailAsync(VideoRecord video, ThumbnailResult thumbnail);
    Task SetHistoryVisibilityAsync(string? filePath, bool visible);
    Task<string?> ReadSettingAsync(string key);
    Task SaveSettingAsync(string key, string value);
    Task FlushAsync();
}
