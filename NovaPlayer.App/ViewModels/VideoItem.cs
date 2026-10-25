using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NovaPlayer.App.ViewModels;

public partial class VideoItem(string path, long size) : ObservableObject
{
    public string Path { get; } = path;
    public long Size { get; } = size;
    public string Name => System.IO.Path.GetFileName(Path);
    [ObservableProperty] private double _duration;
    [ObservableProperty] private double _position;
    [ObservableProperty] private double _speed = 1;
    [ObservableProperty] private bool _hasSpeed;
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private DateTime? _lastPlayed;
    public string DurationText => Duration > 0 ? PlayerViewModel.FormatTime(Duration) : "时长待读取";
    public double Percentage => Duration > 0 ? Math.Clamp(Position / Duration * 100, 0, 100) : 0;
    public string ProgressText => $"{Percentage:0}%";
    public string SearchDetail => $"{DurationText} · {(LastPlayed is null ? "尚未观看" : $"上次 {PlayerViewModel.FormatTime(Position)}")}";
    public string LastPlayedText => LastPlayed?.ToLocalTime().ToString("MM-dd HH:mm") ?? "尚未观看";
    public string SizeText => Size >= 1024L * 1024 * 1024 ? $"{Size / (1024d * 1024 * 1024):0.##} GB" : $"{Size / (1024d * 1024):0.##} MB";
    public bool Exists => File.Exists(Path);
    partial void OnDurationChanged(double value) { OnPropertyChanged(nameof(DurationText)); ProgressChanged(); }
    partial void OnPositionChanged(double value) => ProgressChanged();
    partial void OnLastPlayedChanged(DateTime? value) { OnPropertyChanged(nameof(LastPlayedText)); OnPropertyChanged(nameof(SearchDetail)); }
    private void ProgressChanged() { OnPropertyChanged(nameof(Percentage)); OnPropertyChanged(nameof(ProgressText)); OnPropertyChanged(nameof(SearchDetail)); }
}
