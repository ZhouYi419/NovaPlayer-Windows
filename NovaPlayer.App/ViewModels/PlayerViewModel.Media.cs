using CommunityToolkit.Mvvm.Input;
using NovaPlayer.Core.Playback;

namespace NovaPlayer.App.ViewModels;

public partial class PlayerViewModel
{
    public IReadOnlyList<MediaTrack> SubtitleTracks => State.Tracks.Where(t => t.Kind == MediaTrackKind.Subtitle).ToArray();
    public IReadOnlyList<MediaTrack> AudioTracks => State.Tracks.Where(t => t.Kind == MediaTrackKind.Audio).ToArray();
    public string SubtitleSummary => SubtitleTracks.Count == 0 ? "未检测到字幕" : $"{SubtitleTracks.Count} 条字幕 · {(State.SubtitlesVisible ? "已开启" : "已关闭")}";
    public string MediaDetails => $"时长 {FormatTime(State.Duration)} · 字幕 {SubtitleTracks.Count} 条 · 音轨 {AudioTracks.Count} 条 · 解码 {State.Decoder}";
    public IReadOnlyList<string> AspectRatios { get; } = ["原始比例", "16:9", "4:3", "1:1", "21:9"];
    public IReadOnlyList<double> SeekIntervals { get; } = [5, 10, 15, 30, 60];
    public IReadOnlyList<double> HideIntervals { get; } = [1, 2, 3, 5, 10];
    private void ApplyPlaybackPreferences()
    {
        Execute(() => _engine.Configure(new(Preferences.AutoLoadSubtitles, Preferences.PitchCorrection, Preferences.HardwareDecoding, Preferences.AutoMuteHighSpeed)));
        Execute(() => _engine.SetSubtitleSize(Preferences.SubtitleSize));
    }
    private void RefreshMediaInformation()
    {
        OnPropertyChanged(nameof(SubtitleTracks)); OnPropertyChanged(nameof(AudioTracks));
        OnPropertyChanged(nameof(SubtitleSummary)); OnPropertyChanged(nameof(MediaDetails));
    }
    [RelayCommand] private void SelectSubtitle(MediaTrack? track) { if (track is not null) Execute(() => _engine.SelectSubtitle(track.Id)); }
    [RelayCommand] private void SelectAudio(MediaTrack? track) { if (track is not null) Execute(() => _engine.SelectAudio(track.Id)); }
    public void AdjustSubtitleDelay(double seconds) => Execute(() => _engine.SetSubtitleDelay(seconds));
    [RelayCommand] private void SetAspectRatio(string ratio) => Execute(() => _engine.SetAspectRatio(ratio));
    [RelayCommand]
    private void ResetSettings()
    {
        if (Confirm?.Invoke("恢复默认设置？播放历史、队列与原视频将保留。") != true) return;
        Preferences.Reset(); Shortcuts.Reset(); Notice = "已恢复默认设置。";
    }
    [RelayCommand]
    private void OpenLogs() => Execute(() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe",
        $"\"{System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovaPlayer", "logs")}\"")
    { UseShellExecute = true }));
}
