using NovaPlayer.Core.Playback;

namespace NovaPlayer.Core.Interfaces;

public interface IPlayerEngine : IDisposable
{
    PlayerState State { get; }
    event EventHandler<PlayerState>? StateChanged;
    /// <summary>打开媒体，调用方应在后台执行，避免加载前的同步解码配置阻塞界面。</summary>
    void Open(string localFile);
    void SetPaused(bool paused);
    void Seek(double seconds);
    void SetSpeed(double speed);
    void SetVolume(double volume);
    void SetMuted(bool muted);
    void Stop();
    void LoadSubtitle(string localFile);
    void SetSubtitlesVisible(bool visible);
    void SelectSubtitle(long id);
    void SelectAudio(long id);
    void SetSubtitleSize(double size);
    void SetSubtitleDelay(double seconds);
    void SetAspectRatio(string ratio);
    void Configure(PlaybackOptions options);
}
