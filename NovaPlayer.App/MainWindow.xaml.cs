using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using NovaPlayer.App.Controls;
using NovaPlayer.App.Verification;
using NovaPlayer.App.ViewModels;
using NovaPlayer.Core.Playback;
using NovaPlayer.Infrastructure.Playback;
using Serilog;

namespace NovaPlayer.App;

public partial class MainWindow : Window
{
    private readonly MpvPlayerEngine _engine;
    private readonly string[] _args;
    private readonly VideoHost _host;
    private readonly PlayerViewModel _viewModel;
    private PlaybackOverlay? _overlay;
    private WindowState _previousState;
    private bool _closing;

    public MainWindow(MpvPlayerEngine engine, string[] args)
    {
        _engine = engine; _args = args;
        InitializeComponent();
        _viewModel = new PlayerViewModel(engine, Dispatcher); DataContext = _viewModel;
        _viewModel.OpenRequested += (_, _) => OpenFile();
        _viewModel.FullscreenRequested += (_, _) => ToggleFullscreen();
        _host = new VideoHost(engine, Log.Logger);
        _host.RendererCreated += RendererCreated;
        VideoContainer.Children.Add(_host);
        Loaded += (_, _) =>
        {
            _overlay = new PlaybackOverlay(this, _viewModel, VideoContainer) { Owner = this };
            _overlay.Show(); _overlay.SyncBounds();
        };
    }
    private async void RendererCreated(object? sender, EventArgs e)
    {
        try
        {
            var renderer = _host.Renderer!;
            renderer.Failed += (_, error) => Dispatcher.BeginInvoke(() => { _viewModel.Notice = error; _viewModel.IsReady = false; });
            await renderer.Ready.Task;
            _viewModel.IsReady = true; _viewModel.Notice = $"播放内核已就绪 · {renderer.Gpu}";
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (_args.Length >= 3 && _args[0] == "--verify")
            {
                int exit = await StageOneVerification.RunAsync(this, _engine, renderer, _viewModel, _overlay!, _args[1], _args[2]);
                Close(); Application.Current.Shutdown(exit);
            }
            else if (_args.Length > 0 && File.Exists(_args[0])) _viewModel.OpenFile(_args[0]);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Renderer initialization/verification failed");
            _viewModel.Notice = $"播放器初始化失败：{ex.Message}"; _viewModel.IsReady = false;
            if (_args.FirstOrDefault() == "--verify") { Close(); Application.Current.Shutdown(1); }
        }
    }
    private void OpenFile()
    {
        if (!_viewModel.IsReady) return;
        _overlay?.Hide();
        try
        {
            var dialog = new OpenFileDialog { Title = "打开本地视频", CheckFileExists = true,
                Filter = "视频文件|*.mp4;*.mkv;*.avi;*.mov;*.webm;*.wmv;*.flv;*.m4v;*.ts;*.m2ts;*.mpg;*.mpeg|所有文件|*.*" };
            if (dialog.ShowDialog(this) == true) _viewModel.OpenFile(dialog.FileName);
        }
        finally { _overlay?.Show(); _overlay?.SyncBounds(); }
    }
    internal void HandleDrop(DragEventArgs e)
    {
        if (!_viewModel.IsReady || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
        var first = paths.FirstOrDefault(File.Exists);
        if (first is not null) _viewModel.OpenFile(first);
        else _viewModel.Notice = "请拖入视频文件；文件夹导入将在播放列表阶段实现。";
        e.Handled = true;
    }
    private void Files_Drop(object sender, DragEventArgs e) => HandleDrop(e);
    private void Player_KeyDown(object sender, KeyEventArgs e) => HandleKey(e);
    internal void HandleKey(KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox || !_viewModel.IsReady) return;
        bool control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (control && e.Key == Key.O) { OpenFile(); e.Handled = true; return; }
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.Space: _viewModel.TogglePauseCommand.Execute(null); break;
            case Key.Left: _viewModel.BackwardCommand.Execute(null); break;
            case Key.Right: _viewModel.ForwardCommand.Execute(null); break;
            case Key.Up: _viewModel.ChangeVolume(_viewModel.State.Volume + 5); break;
            case Key.Down: _viewModel.ChangeVolume(_viewModel.State.Volume - 5); break;
            case Key.M: _viewModel.ToggleMuteCommand.Execute(null); break;
            case Key.F: ToggleFullscreen(); break;
            case Key.Escape:
                if (_overlay?.CloseMenu() == true) break;
                if (_viewModel.IsFullscreen) ToggleFullscreen();
                break;
            case Key.OemOpenBrackets: _viewModel.ChangeSpeed(Math.Max(PlaybackPolicy.MinimumSpeed, _viewModel.State.Speed - 0.25)); break;
            case Key.OemCloseBrackets: _viewModel.ChangeSpeed(Math.Min(PlaybackPolicy.MaximumSpeed, _viewModel.State.Speed + 0.25)); break;
            case Key.OemPipe: _viewModel.ResetSpeedCommand.Execute(null); break;
            default: return;
        }
        e.Handled = true; _overlay?.ShowControls();
    }
    internal void ToggleFullscreen()
    {
        _overlay?.CloseMenu();
        if (!_viewModel.IsFullscreen)
        {
            _previousState = WindowState; WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            Header.Visibility = Footer.Visibility = Visibility.Collapsed; Viewport.Margin = new Thickness(0);
            Grid.SetRowSpan(Viewport, 3); Grid.SetRow(Viewport, 0);
            WindowState = WindowState.Maximized; _viewModel.IsFullscreen = true;
        }
        else
        {
            WindowState = WindowState.Normal; WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize; Header.Visibility = Footer.Visibility = Visibility.Visible;
            Viewport.Margin = new Thickness(20, 16, 20, 0);
            Grid.SetRowSpan(Viewport, 1); Grid.SetRow(Viewport, 1);
            WindowState = _previousState; _viewModel.IsFullscreen = false;
        }
        _overlay?.ShowControls(); _overlay?.SyncBounds();
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _closing) return;
        _closing = true; _overlay?.Close(); _viewModel.Dispose();
        _host.StopRendering(); _host.Dispose(); _engine.Dispose();
    }
}
