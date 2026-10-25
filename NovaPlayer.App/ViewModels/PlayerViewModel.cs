using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaPlayer.Core.Interfaces;
using NovaPlayer.Core.Playback;

namespace NovaPlayer.App.ViewModels;

public partial class PlayerViewModel : ObservableObject, IDisposable
{
    private readonly IPlayerEngine _engine;
    private readonly Dispatcher _dispatcher;
    [ObservableProperty] private PlayerState _state;
    [ObservableProperty] private string _notice = "正在初始化 GPU 播放内核…";
    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private bool _isFullscreen;
    public event EventHandler? OpenRequested;
    public event EventHandler? FullscreenRequested;
    public IReadOnlyList<double> Speeds => PlaybackPolicy.Speeds;
    public IReadOnlyList<SpeedChoice> SpeedChoices { get; } = PlaybackPolicy.Speeds.Select(s => new SpeedChoice(s)).ToArray();
    public string FileName => State.FilePath is null ? "Nova Player" : Path.GetFileName(State.FilePath);
    public string VideoInfo => State.Width > 0 ? $"{State.Width} × {State.Height} · {State.Decoder}" : "本地视频 · GPU 渲染";
    public string TimeText => $"{FormatTime(State.Position)} / {FormatTime(State.Duration)}";
    public string SpeedText => $"{State.Speed:0.##}× ▾";
    public string PlayText => State.IsPaused || State.IsEnded ? "▶" : "Ⅱ";
    public string MuteText => State.EffectiveMuted ? "静音" : "音量";
    public string Status => State.Error ?? (State.IsLoading ? "正在加载视频…" : State.IsSeeking ? "正在跳转…" :
        State.Speed > 3 && State.Warning is not null ? State.Warning :
        State.Speed > 8 ? "高倍速自动静音 · 降至 8× 或以下恢复原音频状态" : Notice);
    public bool ShowEmpty => !State.IsLoaded && !State.IsLoading;
    public double SeekMaximum => Math.Max(1, State.Duration);

    public PlayerViewModel(IPlayerEngine engine, Dispatcher dispatcher)
    {
        _engine = engine; _dispatcher = dispatcher; _state = engine.State;
        _engine.StateChanged += EngineStateChanged;
        foreach (var choice in SpeedChoices) choice.IsSelected = choice.Value == State.Speed;
    }
    private void EngineStateChanged(object? sender, PlayerState state)
    {
        if (!_dispatcher.HasShutdownStarted)
            _dispatcher.BeginInvoke(() => State = state, DispatcherPriority.DataBind);
    }
    partial void OnStateChanged(PlayerState value)
    {
        foreach (var choice in SpeedChoices) choice.IsSelected = choice.Value == value.Speed;
        foreach (string name in new[] { nameof(FileName), nameof(VideoInfo), nameof(TimeText), nameof(SpeedText),
            nameof(PlayText), nameof(MuteText), nameof(Status), nameof(ShowEmpty), nameof(SeekMaximum) })
            OnPropertyChanged(name);
    }
    partial void OnNoticeChanged(string value) => OnPropertyChanged(nameof(Status));
    public void OpenFile(string path) => Execute(() => { _engine.Open(path); Notice = "视频已打开"; });
    public void SeekTo(double seconds) => Execute(() => _engine.Seek(seconds));
    public void ChangeSpeed(double speed) => Execute(() => _engine.SetSpeed(speed));
    public void ChangeVolume(double volume) => Execute(() => _engine.SetVolume(volume));
    [RelayCommand] private void Open() => OpenRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void Fullscreen() => FullscreenRequested?.Invoke(this, EventArgs.Empty);
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
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        { Notice = ex.Message; }
    }
    public static string FormatTime(double seconds) => TimeSpan.FromSeconds(Math.Max(0, double.IsFinite(seconds) ? seconds : 0)).ToString(@"hh\:mm\:ss");
    public void Dispose() => _engine.StateChanged -= EngineStateChanged;
}

public partial class SpeedChoice(double value) : ObservableObject
{
    public double Value { get; } = value;
    [ObservableProperty] private bool _isSelected;
}
