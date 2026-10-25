using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaPlayer.App.Services;
using NovaPlayer.Core.Interfaces;
using NovaPlayer.Core.Playback;
using NovaPlayer.Infrastructure.FileSystem;

namespace NovaPlayer.App.ViewModels;

public partial class PlayerViewModel : ObservableObject, IDisposable
{
    private readonly IPlayerEngine _engine;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, VideoItem> _library = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<VideoItem> _hiddenHistory = [];
    private readonly SemaphoreSlim _importGate = new(1, 1);
    private double? _pendingPosition;
    private bool _disposed;
    [ObservableProperty] private PlayerState _state;
    [ObservableProperty] private string _notice = "正在初始化播放内核…";
    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private bool _isFullscreen;
    [ObservableProperty] private bool _isMiniPlayer;
    [ObservableProperty] private string _currentPage = "首页";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _historySearch = "";
    [ObservableProperty] private bool _isImporting;
    [ObservableProperty] private int _informationTab;
    [ObservableProperty] private VideoItem? _currentVideo;
    public PreferencesService Preferences { get; }
    public ObservableCollection<VideoItem> Playlist { get; } = [];
    public ObservableCollection<VideoItem> History { get; } = [];
    public ObservableCollection<VideoItem> FilteredHistory { get; } = [];
    public ObservableCollection<VideoItem> SearchResults { get; } = [];
    public IReadOnlyList<double> Speeds => PlaybackPolicy.Speeds;
    public IReadOnlyList<string> Themes { get; } = ["深色", "浅色", "跟随系统"];
    public IReadOnlyList<SpeedChoice> SpeedChoices { get; } = PlaybackPolicy.Speeds.Select(s => new SpeedChoice(s)).ToArray();
    public event EventHandler? OpenRequested;
    public event EventHandler? OpenFolderRequested;
    public event EventHandler? FullscreenRequested;
    public event EventHandler? MiniPlayerRequested;
    public event EventHandler? SubtitleRequested;
    public event EventHandler<VideoItem>? LocateRequested;
    public Func<string, bool>? Confirm { get; set; }
    public string FileName => State.FilePath is null ? "Nova Player" : Path.GetFileName(State.FilePath);
    public string VideoInfo => State.Width > 0 ? $"{State.Width} × {State.Height} · {CurrentVideo?.SizeText ?? "大小未知"}" : "正在读取视频信息";
    public string Resolution => State.Width > 0 ? $"{State.Width} × {State.Height}" : "未知";
    public string FrameRate => State.FramesPerSecond > 0 ? $"{State.FramesPerSecond:0.##} FPS" : "未知";
    public string TimeText => $"{FormatTime(State.Position)} / {FormatTime(State.Duration)}";
    public string SpeedText => $"{State.Speed:0.0#}×";
    public string PlayIcon => State.IsPaused || State.IsEnded ? "play" : "pause";
    public string VolumeIcon => State.EffectiveMuted ? "mute" : "volume";
    public string PlayText => State.IsPaused || State.IsEnded ? "继续播放" : "暂停播放";
    public string Status => State.Error ?? (IsImporting ? "正在添加本地视频…" : State.IsLoading ? "正在加载视频…" : State.IsSeeking ? "正在跳转…" :
        State.Speed > 3 && State.Warning is not null ? State.Warning : State.Speed > 8 ? "高倍速已自动静音，降速后恢复原音频状态" : Notice);
    public bool ShowEmpty => !State.IsLoaded && !State.IsLoading;
    public double SeekMaximum => Math.Max(1, State.Duration);
    public bool IsHome => CurrentPage == "首页";
    public bool IsHistory => CurrentPage == "播放历史";
    public bool IsSettings => CurrentPage == "设置";
    public bool HasPlaylist => Playlist.Count > 0;
    public bool HasHistory => FilteredHistory.Count > 0;
    public bool ShowSearch => !string.IsNullOrWhiteSpace(SearchText);
    public string PlaylistIndex => $"{(CurrentVideo is null ? 0 : Math.Max(0, Playlist.IndexOf(CurrentVideo) + 1))} / {Playlist.Count}";
    public VideoItem? RecentVideo => History.FirstOrDefault(v => v.Percentage < 95) ?? History.FirstOrDefault();
    public string ProgressText => CurrentVideo?.ProgressText ?? "0%";

    public PlayerViewModel(IPlayerEngine engine, Dispatcher dispatcher, PreferencesService preferences)
    {
        _engine = engine; _dispatcher = dispatcher; _state = engine.State; Preferences = preferences;
        _engine.StateChanged += EngineStateChanged;
        Preferences.SaveFailed += SettingsSaveFailed;
        foreach (var choice in SpeedChoices) choice.IsSelected = choice.Value == State.Speed;
    }
    private void SettingsSaveFailed(object? sender, string message)
    {
        if (!_disposed) _dispatcher.BeginInvoke(() => Notice = message);
    }
    private void EngineStateChanged(object? sender, PlayerState state)
    {
        // 回到界面线程后读取最新快照，避免多个线程排队的旧事件覆盖较新状态。
        if (!_disposed && !_dispatcher.HasShutdownStarted)
            _dispatcher.BeginInvoke(() => { if (!_disposed) State = _engine.State; }, DispatcherPriority.DataBind);
    }
    partial void OnStateChanged(PlayerState value)
    {
        foreach (var choice in SpeedChoices) choice.IsSelected = choice.Value == value.Speed;
        if (value.FilePath is { } path && _library.TryGetValue(path, out var item))
        {
            if (CurrentVideo != item) CurrentVideo = item;
            if (value.IsLoaded)
            {
                item.Duration = value.Duration;
                item.Position = value.Position;
                item.Speed = value.Speed;
                item.HasSpeed = true;
                if (!History.Contains(item) && !_hiddenHistory.Contains(item)) { item.LastPlayed = DateTime.UtcNow; History.Insert(0, item); RefreshHistory(); }
                if (_pendingPosition is { } position && value.Duration > 0)
                { _pendingPosition = null; if (position > 0) _engine.Seek(position); }
            }
        }
        foreach (string name in new[] { nameof(FileName), nameof(VideoInfo), nameof(Resolution), nameof(FrameRate), nameof(TimeText),
            nameof(SpeedText), nameof(PlayText), nameof(PlayIcon), nameof(VolumeIcon), nameof(Status), nameof(ShowEmpty), nameof(SeekMaximum), nameof(ProgressText), nameof(RecentVideo) })
            OnPropertyChanged(name);
    }
    partial void OnCurrentPageChanged(string value)
    {
        OnPropertyChanged(nameof(IsHome)); OnPropertyChanged(nameof(IsHistory)); OnPropertyChanged(nameof(IsSettings));
        SearchText = "";
    }
    partial void OnCurrentVideoChanged(VideoItem? value)
    {
        foreach (var item in Playlist) item.IsCurrent = item == value;
        OnPropertyChanged(nameof(PlaylistIndex)); OnPropertyChanged(nameof(VideoInfo));
    }
    partial void OnNoticeChanged(string value) => OnPropertyChanged(nameof(Status));
    partial void OnIsImportingChanged(bool value) => OnPropertyChanged(nameof(Status));
    partial void OnSearchTextChanged(string value) => RefreshSearch();
    partial void OnHistorySearchChanged(string value) => RefreshHistory();

    public void OpenFile(string path) => _ = ImportFilesAsync([path]);
    public async Task ImportFilesAsync(IEnumerable<string> paths)
    {
        try
        {
            await _importGate.WaitAsync(_lifetime.Token);
            try
            {
                IsImporting = true;
                var imported = await MediaImportService.ImportAsync(paths, _lifetime.Token);
                if (_disposed) return;
                foreach (var video in imported.Videos)
                {
                    if (!_library.TryGetValue(video.Path, out var item))
                    { item = new VideoItem(video.Path, video.Size); _library.Add(video.Path, item); }
                    if (!Playlist.Contains(item)) Playlist.Add(item);
                }
                NotifyCollections();
                if (imported.Videos.Count > 0)
                {
                    Notice = $"已添加 {imported.Videos.Count} 个视频" + (imported.Skipped > 0 ? $"，忽略 {imported.Skipped} 个无法导入的项目" : "") + (imported.Truncated ? "；单次最多导入 10000 个视频" : "");
                    if (!State.IsLoaded && !State.IsLoading) PlayVideo(_library[imported.Videos[0].Path]);
                }
                else Notice = "没有找到支持的视频文件。";
            }
            finally { IsImporting = false; _importGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { Notice = ex.Message; }
    }
    [RelayCommand] private void PlayVideo(VideoItem? item)
    {
        if (item is null || !IsReady) return;
        if (!item.Exists) { Notice = "视频文件不存在，请重新定位。"; LocateRequested?.Invoke(this, item); return; }
        CurrentPage = "首页"; SearchText = "";
        _hiddenHistory.Remove(item);
        if (string.Equals(State.FilePath, item.Path, StringComparison.OrdinalIgnoreCase) && State.IsLoaded)
        { Execute(() => { if (State.IsEnded) _engine.Seek(0); _engine.SetPaused(false); }); return; }
        Execute(() =>
        {
            _pendingPosition = ResumePolicy.Position(item.Position, item.Duration, Preferences.AutoResume);
            CurrentVideo = item;
            _engine.SetSpeed(item.HasSpeed ? item.Speed : Preferences.DefaultSpeed);
            _engine.Open(item.Path);
            item.LastPlayed = DateTime.UtcNow;
            if (History.Remove(item)) History.Insert(0, item);
            Notice = "视频已打开";
            RefreshHistory();
        });
    }
    public async Task RelocateAsync(VideoItem item, string replacement)
    {
        await ImportFilesAsync([replacement]);
        if (_library.TryGetValue(Path.GetFullPath(replacement), out var located))
        { located.Position = item.Position; located.Duration = item.Duration; located.Speed = item.Speed; located.HasSpeed = item.HasSpeed; PlayVideo(located); }
    }
    [RelayCommand] private void RemoveVideo(VideoItem? item)
    {
        if (item is null) return;
        int index = Playlist.IndexOf(item);
        bool current = item == CurrentVideo;
        Playlist.Remove(item);
        if (current)
        {
            if (Playlist.Count > 0) PlayVideo(Playlist[Math.Clamp(index, 0, Playlist.Count - 1)]);
            else { Execute(_engine.Stop); CurrentVideo = null; }
        }
        NotifyCollections();
    }
    [RelayCommand] private void MoveUp(VideoItem? item) => Move(item, -1);
    [RelayCommand] private void MoveDown(VideoItem? item) => Move(item, 1);
    private void Move(VideoItem? item, int offset)
    {
        if (item is null) return;
        int index = Playlist.IndexOf(item), target = index + offset;
        if (index >= 0 && target >= 0 && target < Playlist.Count) Playlist.Move(index, target);
        OnPropertyChanged(nameof(PlaylistIndex));
    }
    public void Reorder(VideoItem from, VideoItem to)
    {
        int source = Playlist.IndexOf(from), target = Playlist.IndexOf(to);
        if (source >= 0 && target >= 0) Playlist.Move(source, target);
        OnPropertyChanged(nameof(PlaylistIndex));
    }
    [RelayCommand] private void Next() => Step(1);
    [RelayCommand] private void Previous() => Step(-1);
    private void Step(int offset)
    {
        int index = CurrentVideo is null ? -1 : Playlist.IndexOf(CurrentVideo);
        for (int next = index + offset; next >= 0 && next < Playlist.Count; next += offset)
            if (Playlist[next].Exists) { PlayVideo(Playlist[next]); return; }
        Notice = offset > 0 ? "已经是最后一个可播放视频。" : "已经是第一个可播放视频。";
    }
    [RelayCommand] private void DeleteHistory(VideoItem? item)
    { if (item is not null) { History.Remove(item); _hiddenHistory.Add(item); } RefreshHistory(); }
    [RelayCommand] private void ClearHistory()
    {
        if (Confirm?.Invoke("清空本次会话的播放记录？此操作不会删除视频或清空播放列表。") != true) return;
        _hiddenHistory.UnionWith(History); History.Clear(); RefreshHistory(); Notice = "播放记录已清空，原视频与队列已保留。";
    }
    private void RefreshSearch()
    {
        SearchResults.Clear();
        if (ShowSearch)
            foreach (var item in Playlist.Concat(History).Distinct().Where(v => v.Name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase)).Take(30)) SearchResults.Add(item);
        OnPropertyChanged(nameof(ShowSearch));
    }
    private void RefreshHistory()
    {
        FilteredHistory.Clear();
        foreach (var item in History.Where(v => v.Name.Contains(HistorySearch.Trim(), StringComparison.OrdinalIgnoreCase))) FilteredHistory.Add(item);
        OnPropertyChanged(nameof(HasHistory)); OnPropertyChanged(nameof(RecentVideo)); RefreshSearch();
    }
    private void NotifyCollections()
    { OnPropertyChanged(nameof(HasPlaylist)); OnPropertyChanged(nameof(PlaylistIndex)); RefreshSearch(); }
    [RelayCommand] private void Navigate(string page) { if (page is "首页" or "播放历史" or "设置") CurrentPage = page; }
    [RelayCommand] private void TogglePlaylist() => Preferences.PlaylistExpanded = !Preferences.PlaylistExpanded;
    [RelayCommand] private void ToggleInformation() => Preferences.InformationExpanded = !Preferences.InformationExpanded;
    [RelayCommand] private void SetTheme(string theme) { if (Themes.Contains(theme)) Preferences.Theme = theme; }
    [RelayCommand] private void ShowInformation() { CurrentPage = "首页"; Preferences.InformationExpanded = true; InformationTab = 1; }
    [RelayCommand] private void ShowShortcuts() => CurrentPage = "设置";
    [RelayCommand] private void Reveal(VideoItem? item)
    {
        var path = item?.Path ?? State.FilePath;
        if (path is null) { Notice = "请先打开视频。"; return; }
        if (!File.Exists(path)) { Notice = "文件不存在，请重新定位。"; return; }
        Execute(() => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }));
    }
    public void SeekTo(double seconds) { if (State.IsLoaded) Execute(() => _engine.Seek(seconds)); }
    public void ChangeSpeed(double speed) => Execute(() => _engine.SetSpeed(speed));
    public void ChangeVolume(double volume) => Execute(() => _engine.SetVolume(volume));
    [RelayCommand] private void Open() => OpenRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void OpenFolder() => OpenFolderRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void Fullscreen() => FullscreenRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void MiniPlayer() => MiniPlayerRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void LoadSubtitle() => SubtitleRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void ToggleSubtitles() => Execute(() => _engine.SetSubtitlesVisible(!State.SubtitlesVisible));
    public void AddSubtitle(string path) => Execute(() => _engine.LoadSubtitle(path));
    [RelayCommand] private void Restart() { if (State.IsLoaded) Execute(() => { _engine.Seek(0); _engine.SetPaused(false); }); }
    [RelayCommand] private void TogglePause() => Execute(() =>
    {
        if (!State.IsLoaded) return;
        if (State.IsEnded) { _engine.Seek(0); _engine.SetPaused(false); }
        else _engine.SetPaused(!State.IsPaused);
    });
    [RelayCommand] private void Backward() => SeekTo(State.Position - 10);
    [RelayCommand] private void Forward() => SeekTo(State.Position + 10);
    [RelayCommand] private void ToggleMute() => Execute(() => _engine.SetMuted(!State.UserMuted));
    [RelayCommand] private void ResetSpeed() => ChangeSpeed(1);
    private void Execute(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or Win32Exception)
        { Serilog.Log.Warning(ex, "播放界面操作失败"); Notice = ex.Message; }
    }
    public static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(double.IsFinite(seconds) ? Math.Clamp(seconds, 0, TimeSpan.MaxValue.TotalSeconds - 1) : 0);
        return $"{(long)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";
    }
    public void Dispose()
    {
        _disposed = true; _lifetime.Cancel();
        _engine.StateChanged -= EngineStateChanged;
        Preferences.SaveFailed -= SettingsSaveFailed;
    }
}

public partial class SpeedChoice(double value) : ObservableObject
{
    public double Value { get; } = value;
    [ObservableProperty] private bool _isSelected;
}
