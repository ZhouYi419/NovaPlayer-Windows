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
using NovaPlayer.Core.Library;
using NovaPlayer.Infrastructure.Thumbnails;

namespace NovaPlayer.App.ViewModels;

public partial class PlayerViewModel : ObservableObject, IDisposable
{
    private readonly IPlayerEngine _engine;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, VideoItem> _library = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _importGate = new(1, 1);
    private readonly IPlaybackStore _store;
    private readonly ThumbnailService _thumbnails;
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly DispatcherTimer _saveTimer;
    private PlayerState _lastSnapshot = new();
    private long _playRequest, _handledEndSession, _automaticSession;
    private readonly HashSet<string> _failedAutomaticPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _resumeIssued, _shuttingDown;
    [ObservableProperty] private string? _persistenceWarning;
    [ObservableProperty] private string? _resumePrompt;
    public bool HasResumePrompt => !string.IsNullOrEmpty(ResumePrompt);
    public IReadOnlyList<string> ResumeModes { get; } = ["自动续播", "询问后续播", "始终从头播放"];
    public IReadOnlyList<string> PlaybackModes { get; } = ["顺序播放", "单曲循环", "列表循环", "关闭连播"];
    public int PlaybackModeIndex { get => (int)Preferences.Mode; set { if (value is >= 0 and <= 3) { Preferences.Mode = (PlaybackMode)value; OnPropertyChanged(); } } }
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
    public ShortcutService Shortcuts { get; }
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
    public event EventHandler? ShortcutSettingsRequested;
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
    public string Status => State.Error ?? State.OperationWarning ?? PersistenceWarning ?? (IsImporting ? "正在添加本地视频…" : State.IsLoading ? "正在加载视频…" : State.IsSeeking ? "正在跳转…" :
        State.Speed > 3 && State.Warning is not null ? State.Warning : State.Speed > 8 && Preferences.AutoMuteHighSpeed ? "高倍速已自动静音，降速后恢复原音频状态" : Notice);
    public bool ShowEmpty => !State.IsLoaded && !State.IsLoading;
    public double SeekMaximum => Math.Max(1, State.Duration);
    public bool IsHome => CurrentPage == "首页";
    public bool IsHistory => CurrentPage == "播放历史";
    public bool IsSettings => CurrentPage == "设置";
    public bool HasPlaylist => Playlist.Count > 0;
    public bool HasHistory => FilteredHistory.Count > 0;
    public bool ShowSearch => !string.IsNullOrWhiteSpace(SearchText);
    public string PlaylistIndex => $"{(CurrentVideo is null ? 0 : Math.Max(0, Playlist.IndexOf(CurrentVideo) + 1))} / {Playlist.Count}";
    public VideoItem? RecentVideo => History.Where(v => v.HistoryVisible && !v.IsMissing).OrderByDescending(v => v.LastPlayed).FirstOrDefault(v => !v.Completed)
        ?? History.Where(v => v.HistoryVisible).OrderByDescending(v => v.LastPlayed).FirstOrDefault();
    public string ProgressText => CurrentVideo?.ProgressText ?? "0%";

    public PlayerViewModel(IPlayerEngine engine, Dispatcher dispatcher, PreferencesService preferences, IPlaybackStore store, ThumbnailService thumbnails, ShortcutService shortcuts)
    {
        _engine = engine; _dispatcher = dispatcher; _state = engine.State; Preferences = preferences;
        Shortcuts = shortcuts;
        _store = store; _thumbnails = thumbnails;
        _saveTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, SaveTimerTick, dispatcher);
        _engine.StateChanged += EngineStateChanged;
        Preferences.SaveFailed += SettingsSaveFailed;
        Preferences.PropertyChanged += PreferenceChanged;
        ApplyPlaybackPreferences();
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
        RefreshMediaInformation();
        foreach (var choice in SpeedChoices) choice.IsSelected = choice.Value == value.Speed;
        ObservePlayback(value);
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
        if (!_restoringLibrary) _ = PersistPlaylistAsync();
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
                if (_disposed || _shuttingDown) return;
                var records = await RegisterImportedAsync(imported.Videos);
                foreach (var record in records)
                { var item = Materialize(record); if (!Playlist.Contains(item)) Playlist.Add(item); }
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
    [RelayCommand] private void PlayVideo(VideoItem? item) => _ = PlayItemAsync(item);
    public async Task RelocateAsync(VideoItem item, string replacement)
    {
        await RelocateItemAsync(item, replacement);
    }
    [RelayCommand]
    private void RemoveVideo(VideoItem? item)
    {
        if (item is null) return;
        _ = SaveCurrentAsync();
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
        _ = PersistPlaylistAsync();
    }
    public void Reorder(VideoItem from, VideoItem to)
    {
        int source = Playlist.IndexOf(from), target = Playlist.IndexOf(to);
        if (source >= 0 && target >= 0) Playlist.Move(source, target);
        OnPropertyChanged(nameof(PlaylistIndex));
        _ = PersistPlaylistAsync();
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
    [RelayCommand]
    private void DeleteHistory(VideoItem? item)
    { if (item is not null) { History.Remove(item); item.HistoryVisible = false; _ = PersistAsync(() => _store.SetHistoryVisibilityAsync(item.Path, false)); } RefreshHistory(); }
    [RelayCommand]
    private void ClearHistory()
    {
        if (Confirm?.Invoke("清空全部播放记录？此操作不会删除视频或清空播放列表。") != true) return;
        foreach (var item in _library.Values) item.HistoryVisible = false;
        History.Clear(); RefreshHistory(); Notice = "播放记录已清空，原视频与队列已保留。";
        _ = PersistAsync(() => _store.SetHistoryVisibilityAsync(null, false));
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
        foreach (var item in History.Where(v => v.HistoryVisible && v.Name.Contains(HistorySearch.Trim(), StringComparison.OrdinalIgnoreCase)).OrderByDescending(v => v.LastPlayed)) FilteredHistory.Add(item);
        OnPropertyChanged(nameof(HasHistory)); OnPropertyChanged(nameof(RecentVideo)); RefreshSearch();
    }
    private void NotifyCollections()
    { OnPropertyChanged(nameof(HasPlaylist)); OnPropertyChanged(nameof(PlaylistIndex)); RefreshSearch(); if (!_restoringLibrary) _ = PersistPlaylistAsync(); }
    [RelayCommand] private void Navigate(string page) { if (page is "首页" or "播放历史" or "设置") CurrentPage = page; }
    [RelayCommand] private void TogglePlaylist() => Preferences.PlaylistExpanded = !Preferences.PlaylistExpanded;
    [RelayCommand] private void ToggleInformation() => Preferences.InformationExpanded = !Preferences.InformationExpanded;
    [RelayCommand] private void SetTheme(string theme) { if (Themes.Contains(theme)) Preferences.Theme = theme; }
    [RelayCommand] private void ShowInformation() { CurrentPage = "首页"; Preferences.InformationExpanded = true; InformationTab = 1; }
    [RelayCommand] private void ShowShortcuts() { CurrentPage = "设置"; ShortcutSettingsRequested?.Invoke(this, EventArgs.Empty); }
    [RelayCommand] private void RequestRelocate(VideoItem? item) { if (item is not null) LocateRequested?.Invoke(this, item); }
    [RelayCommand] private void OpenDatabaseFolder() => Execute(() => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetDirectoryName(DatabasePath)}\"") { UseShellExecute = true }));
    [RelayCommand]
    private void Reveal(VideoItem? item)
    {
        var path = item?.Path ?? State.FilePath;
        if (path is null) { Notice = "请先打开视频。"; return; }
        if (!File.Exists(path)) { Notice = "文件不存在，请重新定位。"; return; }
        Execute(() => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }));
    }
    public void SeekTo(double seconds)
    {
        if (!State.IsLoaded) return;
        // 用户主动跳转优先于尚未完成的自动续播，避免旧续播目标阻止后续状态同步。
        _pendingPosition = null; _resumeIssued = false; ResumePrompt = null;
        Execute(() => _engine.Seek(seconds));
    }
    public void ChangeSpeed(double speed) => Execute(() => _engine.SetSpeed(speed));
    public void ChangeVolume(double volume) => Execute(() => _engine.SetVolume(volume));
    [RelayCommand] private void Open() => OpenRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void OpenFolder() => OpenFolderRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void Fullscreen() => FullscreenRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void MiniPlayer() => MiniPlayerRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void LoadSubtitle() => SubtitleRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void ToggleSubtitles() => Execute(() => _engine.SetSubtitlesVisible(!State.SubtitlesVisible));
    public void AddSubtitle(string path) => Execute(() => _engine.LoadSubtitle(path));
    [RelayCommand] private void Restart() { if (CurrentVideo is not null) _ = PlayItemAsync(CurrentVideo, true); else if (State.IsLoaded) Execute(() => { _engine.Seek(0); _engine.SetPaused(false); }); }
    [RelayCommand] private void PlayFromStart(VideoItem? item) { if (item is not null) _ = PlayItemAsync(item, true); }
    [RelayCommand]
    private void TogglePause() => Execute(() =>
    {
        if (!State.IsLoaded) return;
        if (State.IsEnded && CurrentVideo is not null) _ = PlayItemAsync(CurrentVideo, true);
        else _engine.SetPaused(!State.IsPaused);
    });
    [RelayCommand] private void Backward() => SeekTo(State.Position - Preferences.SeekInterval);
    [RelayCommand] private void Forward() => SeekTo(State.Position + Preferences.SeekInterval);
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
        _saveTimer.Stop();
        _engine.StateChanged -= EngineStateChanged;
        Preferences.SaveFailed -= SettingsSaveFailed;
        Preferences.PropertyChanged -= PreferenceChanged;
    }
}

public partial class SpeedChoice(double value) : ObservableObject
{
    public double Value { get; } = value;
    [ObservableProperty] private bool _isSelected;
}
