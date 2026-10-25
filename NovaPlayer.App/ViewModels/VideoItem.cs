using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;
using NovaPlayer.Core.Library;

namespace NovaPlayer.App.ViewModels;

public partial class VideoItem(string path, long size) : ObservableObject
{
    public string Path { get; } = path;
    public long Size { get; private set; } = size;
    public long ModifiedUtcTicks { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public bool HistoryVisible { get; set; }
    public bool Completed { get; set; }
    public Func<VideoItem, CancellationToken, Task>? ThumbnailLoader { get; set; }
    [ObservableProperty] private string? _thumbnailPath;
    [ObservableProperty] private ImageSource? _thumbnail;
    [ObservableProperty] private bool _isMissing;
    public string Name => System.IO.Path.GetFileName(Path);
    [ObservableProperty] private double _duration;
    [ObservableProperty] private double _position;
    [ObservableProperty] private double _speed = 1;
    [ObservableProperty] private bool _hasSpeed;
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private DateTime? _lastPlayed;
    public string DurationText => Duration > 0 ? PlayerViewModel.FormatTime(Duration) : "时长待读取";
    public string AvailabilityText => IsMissing ? "文件不存在 · 请重新定位" : DurationText;
    public double Percentage => Duration > 0 ? Math.Clamp(Position / Duration * 100, 0, 100) : 0;
    public string ProgressText => $"{Percentage:0}%";
    public string SearchDetail => $"{DurationText} · {(LastPlayed is null ? "尚未观看" : $"上次 {PlayerViewModel.FormatTime(Position)}")}";
    public string LastPlayedText => LastPlayed?.ToLocalTime().ToString("MM-dd HH:mm") ?? "尚未观看";
    public string SizeText => Size >= 1024L * 1024 * 1024 ? $"{Size / (1024d * 1024 * 1024):0.##} GB" : $"{Size / (1024d * 1024):0.##} MB";
    public bool Exists => File.Exists(Path);
    public VideoRecord ToRecord() => new()
    {
        FilePath = Path,
        FileSize = Size,
        ModifiedUtcTicks = ModifiedUtcTicks,
        Duration = Duration,
        Position = Position,
        Speed = Speed,
        HasSpeed = HasSpeed,
        LastPlayedUtc = LastPlayed,
        Completed = Completed,
        HistoryVisible = HistoryVisible,
        ThumbnailPath = ThumbnailPath,
        CreatedUtc = CreatedUtc
    };
    public void Apply(VideoRecord record)
    {
        bool changed = Size != record.FileSize || ModifiedUtcTicks != record.ModifiedUtcTicks;
        Size = record.FileSize; ModifiedUtcTicks = record.ModifiedUtcTicks; CreatedUtc = record.CreatedUtc;
        Duration = record.Duration; Position = record.Position; Speed = record.Speed; HasSpeed = record.HasSpeed;
        LastPlayed = record.LastPlayedUtc; Completed = record.Completed; HistoryVisible = record.HistoryVisible; ThumbnailPath = record.ThumbnailPath;
        if (changed) Thumbnail = null;
        OnPropertyChanged(nameof(SizeText));
    }
    partial void OnDurationChanged(double value) { OnPropertyChanged(nameof(DurationText)); OnPropertyChanged(nameof(AvailabilityText)); ProgressChanged(); }
    partial void OnIsMissingChanged(bool value) => OnPropertyChanged(nameof(AvailabilityText));
    partial void OnPositionChanged(double value) => ProgressChanged();
    partial void OnLastPlayedChanged(DateTime? value) { OnPropertyChanged(nameof(LastPlayedText)); OnPropertyChanged(nameof(SearchDetail)); }
    private void ProgressChanged() { OnPropertyChanged(nameof(Percentage)); OnPropertyChanged(nameof(ProgressText)); OnPropertyChanged(nameof(SearchDetail)); }
}
