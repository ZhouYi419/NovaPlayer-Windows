using NovaPlayer.Core.Playback;

namespace NovaPlayer.Core.Interfaces;

public interface IPlayerEngine : IDisposable
{
    PlayerState State { get; }
    event EventHandler<PlayerState>? StateChanged;
    void Open(string localFile);
    void SetPaused(bool paused);
    void Seek(double seconds);
    void SetSpeed(double speed);
    void SetVolume(double volume);
    void SetMuted(bool muted);
}
