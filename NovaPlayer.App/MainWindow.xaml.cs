using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using NovaPlayer.App.Controls;
using NovaPlayer.App.Services;
using NovaPlayer.App.Verification;
using NovaPlayer.App.ViewModels;
using NovaPlayer.Core.Playback;
using NovaPlayer.Core.Presentation;
using NovaPlayer.Infrastructure.Playback;
using Serilog;
using NovaPlayer.Core.Interfaces;
using NovaPlayer.Infrastructure.Thumbnails;

namespace NovaPlayer.App;

public partial class MainWindow : Window
{
    private readonly MpvPlayerEngine _engine;
    private readonly string[] _args;
    private readonly VideoHost _host;
    private readonly PlayerViewModel _viewModel;
    private readonly ThemeService _theme;
    private PlaybackOverlay? _overlay;
    private WindowGeometry? _beforeFullscreen, _beforeMini;
    private string _beforeMiniPage = "首页";
    private bool _presentationChanging;
    private bool _closing, _dialogOpen;
    private Point _dragOrigin;
    private bool _closeCompleted;
    private Task? _shutdownTask;
    internal bool ShouldShowPlayerOverlay => !_closing && !_dialogOpen && _viewModel.IsHome;
    internal PlayerViewModel ViewModel => _viewModel;
    internal VideoHost VideoHost => _host;

    public MainWindow(MpvPlayerEngine engine, string[] args, PreferencesService preferences, IPlaybackStore store, ThumbnailService thumbnails, ShortcutService shortcuts)
    {
        _engine = engine; _args = args;
        InitializeComponent();
        _theme = new ThemeService(preferences);
        _viewModel = new PlayerViewModel(engine, Dispatcher, preferences, store, thumbnails, shortcuts); DataContext = _viewModel;
        _viewModel.OpenRequested += (_, _) => OpenFiles();
        _viewModel.OpenFolderRequested += (_, _) => OpenFolder();
        _viewModel.FullscreenRequested += (_, _) => ToggleFullscreen();
        _viewModel.MiniPlayerRequested += (_, _) => ToggleMiniPlayer();
        _viewModel.SubtitleRequested += (_, _) => OpenSubtitle();
        _viewModel.ShortcutSettingsRequested += (_, _) => Dispatcher.BeginInvoke(() => SettingsPage.ShowShortcuts(), DispatcherPriority.Loaded);
        _viewModel.LocateRequested += Relocate;
        _viewModel.Confirm = message => WithDialog(() => MessageBox.Show(this, message, "Nova Player", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
        _viewModel.PropertyChanged += ViewModelChanged;
        preferences.PropertyChanged += PreferencesChanged;
        _host = new VideoHost(engine, Log.Logger);
        _host.RendererCreated += RendererCreated;
        VideoContainer.Children.Add(_host);
        Loaded += (_, _) =>
        {
            if (preferences.RememberWindow && preferences.MainGeometry is { } geometry) WindowPlacement.Apply(this, geometry, 960, 620);
            _overlay = new PlaybackOverlay(this, _viewModel, VideoContainer) { Owner = this };
            _overlay.Show(); ApplyLayout();
        };
        StateChanged += (_, _) => ApplyLayout();
    }

    private async void RendererCreated(object? sender, EventArgs e)
    {
        try
        {
            var renderer = _host.Renderer!;
            renderer.Failed += (_, error) => Dispatcher.BeginInvoke(() => { _viewModel.Notice = error; _viewModel.IsReady = false; });
            await renderer.Ready.Task;
            await _viewModel.InitializeLibraryAsync();
            _viewModel.IsReady = true; _viewModel.Notice = "播放内核已就绪";
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (_args.Length >= 3 && _args[0].StartsWith("--verify", StringComparison.Ordinal))
            {
                int exit = _args[0].StartsWith("--verify-experience", StringComparison.Ordinal)
                    ? await StageFourVerification.RunAsync(this, _engine, renderer, _overlay!, _args[0], _args[1], _args[2])
                    : _args[0].StartsWith("--verify-library-", StringComparison.Ordinal)
                    ? await StageThreeVerification.RunAsync(this, _engine, renderer, _overlay!, _args[0], _args[1], _args[2])
                    : _args[0] == "--verify-shell"
                    ? await StageTwoVerification.RunAsync(this, _engine, renderer, _overlay!, _args[1], _args[2])
                    : await StageOneVerification.RunAsync(this, _engine, renderer, _viewModel, _overlay!, _args[1], _args[2]);
                if (_viewModel.IsMiniPlayer) ToggleMiniPlayer();
                await CloseSessionAsync(); Application.Current.Shutdown(exit);
            }
            else if (_args.Length > 0) await _viewModel.ImportFilesAsync(_args.Where(File.Exists));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "播放器初始化或验收失败");
            _viewModel.Notice = $"播放器初始化失败：{ex.Message}"; _viewModel.IsReady = false;
            if (_args.FirstOrDefault()?.StartsWith("--verify", StringComparison.Ordinal) == true) { await CloseSessionAsync(); Application.Current.Shutdown(1); }
        }
    }
    private async void OpenFiles()
    {
        if (!_viewModel.IsReady) return;
        var files = WithDialog(() =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "添加本地视频",
                CheckFileExists = true,
                Multiselect = true,
                Filter = "视频文件|*.mp4;*.mkv;*.avi;*.mov;*.webm;*.wmv;*.flv;*.m4v;*.ts;*.m2ts;*.mpg;*.mpeg|所有文件|*.*"
            };
            return dialog.ShowDialog(this) == true ? dialog.FileNames : [];
        });
        await _viewModel.ImportFilesAsync(files);
    }
    private async void OpenFolder()
    {
        if (!_viewModel.IsReady) return;
        string? folder = WithDialog(() =>
        {
            var dialog = new OpenFolderDialog { Title = "添加视频文件夹" };
            return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
        });
        if (folder is not null) await _viewModel.ImportFilesAsync([folder]);
    }
    private void OpenSubtitle()
    {
        if (!_viewModel.State.IsLoaded) { _viewModel.Notice = "请先打开视频，再加载字幕。"; return; }
        var path = WithDialog(() =>
        {
            var dialog = new OpenFileDialog { Title = "加载字幕", CheckFileExists = true, Filter = "字幕文件|*.srt;*.ass;*.ssa|所有文件|*.*" };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        });
        if (path is not null) _viewModel.AddSubtitle(path);
    }
    private async void Relocate(object? sender, VideoItem item)
    {
        string? path = WithDialog(() =>
        {
            var dialog = new OpenFileDialog { Title = $"重新定位：{item.Name}", CheckFileExists = true, Filter = "视频文件|*.mp4;*.mkv;*.avi;*.mov;*.webm;*.wmv;*.m4v;*.ts|所有文件|*.*" };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        });
        if (path is not null) await _viewModel.RelocateAsync(item, path);
    }
    private T WithDialog<T>(Func<T> action)
    {
        _dialogOpen = true; _overlay?.CloseMenu(); _overlay?.Hide();
        try { return action(); }
        finally { _dialogOpen = false; _overlay?.SyncBounds(); }
    }
    internal async void HandleDrop(DragEventArgs e)
    {
        if (!_viewModel.IsReady || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true;
        await _viewModel.ImportFilesAsync((string[])e.Data.GetData(DataFormats.FileDrop));
    }
    private void Files_Drop(object sender, DragEventArgs e) => HandleDrop(e);
    private void Player_KeyDown(object sender, KeyEventArgs e) => HandleKey(e);
    internal void HandleKey(KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (TryHandleShortcut(key, Keyboard.Modifiers, e.IsRepeat, Keyboard.FocusedElement as DependencyObject)) e.Handled = true;
    }
    internal bool TryHandleShortcut(Key key, ModifierKeys modifiers, bool repeated, DependencyObject? focus = null)
    {
        if (!_viewModel.IsReady || IsEditing(focus)) return false;
        var action = _viewModel.Shortcuts.Resolve(key, modifiers, repeated);
        switch (action)
        {
            case ShortcutAction.TogglePause: _viewModel.TogglePauseCommand.Execute(null); break;
            case ShortcutAction.Backward: _viewModel.BackwardCommand.Execute(null); break;
            case ShortcutAction.Forward: _viewModel.ForwardCommand.Execute(null); break;
            case ShortcutAction.VolumeUp: _viewModel.ChangeVolume(_viewModel.State.Volume + 5); break;
            case ShortcutAction.VolumeDown: _viewModel.ChangeVolume(_viewModel.State.Volume - 5); break;
            case ShortcutAction.ToggleMute: _viewModel.ToggleMuteCommand.Execute(null); break;
            case ShortcutAction.Fullscreen: ToggleFullscreen(); break;
            case ShortcutAction.MiniPlayer: ToggleMiniPlayer(); break;
            case ShortcutAction.ToggleSubtitles: _viewModel.ToggleSubtitlesCommand.Execute(null); break;
            case ShortcutAction.OpenFile: OpenFiles(); break;
            case ShortcutAction.OpenFolder: OpenFolder(); break;
            case ShortcutAction.Next: _viewModel.NextCommand.Execute(null); break;
            case ShortcutAction.Previous: _viewModel.PreviousCommand.Execute(null); break;
            case ShortcutAction.Slower: _viewModel.ChangeSpeed(Math.Max(PlaybackPolicy.MinimumSpeed, _viewModel.State.Speed - .25)); break;
            case ShortcutAction.Faster: _viewModel.ChangeSpeed(Math.Min(PlaybackPolicy.MaximumSpeed, _viewModel.State.Speed + .25)); break;
            case ShortcutAction.NormalSpeed: _viewModel.ResetSpeedCommand.Execute(null); break;
            case ShortcutAction.ExitView:
                if (_overlay?.CloseMenu() == true) break;
                if (_viewModel.IsMiniPlayer) ToggleMiniPlayer();
                else if (_viewModel.IsFullscreen) ToggleFullscreen();
                else _viewModel.SearchText = "";
                break;
            default: return false;
        }
        _overlay?.ShowControls(); return true;
    }
    internal static bool IsEditing(DependencyObject? focus)
    {
        for (var current = focus; current is not null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is TextBoxBase or PasswordBox or ComboBox or ComboBoxItem) return true;
        return false;
    }
    internal void ToggleFullscreen()
    {
        if (_viewModel.IsMiniPlayer) ToggleMiniPlayer();
        _viewModel.CurrentPage = "首页"; _overlay?.CloseMenu();
        if (!_viewModel.IsFullscreen)
        {
            _beforeFullscreen = WindowPlacement.Capture(this); WindowState = WindowState.Normal;
            _viewModel.IsFullscreen = true; ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowState = WindowState.Normal; _viewModel.IsFullscreen = false;
            ResizeMode = ResizeMode.CanResize;
            if (_beforeFullscreen is { } geometry) WindowPlacement.Apply(this, geometry, 960, 620, true);
        }
        ApplyLayout(); _overlay?.ShowControls();
    }
    internal void ToggleMiniPlayer()
    {
        if (_presentationChanging || _closing) return;
        _presentationChanging = true;
        try
        {
            if (!_viewModel.IsMiniPlayer)
            {
                if (!_viewModel.State.IsLoaded) { _viewModel.Notice = "请先打开视频。"; return; }
                if (_viewModel.IsFullscreen) ToggleFullscreen();
                _beforeMini = WindowPlacement.Capture(this); _beforeMiniPage = _viewModel.CurrentPage;
                _overlay?.CloseMenu(); WindowState = WindowState.Normal;
                _viewModel.CurrentPage = "首页"; _viewModel.IsMiniPlayer = true;
                MinWidth = 280; MinHeight = 158;
                Rect area = WindowPlacement.WorkArea(this);
                var target = _viewModel.Preferences.MiniGeometry ?? new WindowGeometry(area.Right - 424, area.Bottom - 249, 400, 225);
                WindowPlacement.Apply(this, target with { Maximized = false }, 280, 158);
            }
            else
            {
                _viewModel.Preferences.MiniGeometry = WindowPlacement.Capture(this) with { Maximized = false };
                _overlay?.CloseMenu(); _viewModel.IsMiniPlayer = false; MinWidth = 960; MinHeight = 620;
                if (_beforeMini is { } geometry) WindowPlacement.Apply(this, geometry, 960, 620, true);
                _viewModel.CurrentPage = _beforeMiniPage;
                _ = _viewModel.Preferences.SaveAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "画中画布局切换失败，恢复主窗口");
            _viewModel.IsMiniPlayer = false; MinWidth = 960; MinHeight = 620;
            if (_beforeMini is { } geometry) WindowPlacement.Apply(this, geometry, 960, 620, true);
            _viewModel.Notice = "画中画切换失败，已保留主窗口与当前播放。";
        }
        finally { _presentationChanging = false; ApplyLayout(); _overlay?.ShowControls(); }
    }
    internal void ResizeMiniPlayer(string edges, double horizontal, double vertical)
    {
        if (!_viewModel.IsMiniPlayer || !double.IsFinite(horizontal) || !double.IsFinite(vertical)) return;
        double width = Width, height = Height, left = Left, top = Top;
        if (edges.Contains("Left", StringComparison.Ordinal)) { width = Math.Max(MinWidth, Width - horizontal); left += Width - width; }
        if (edges.Contains("Right", StringComparison.Ordinal)) width = Math.Max(MinWidth, Width + horizontal);
        if (edges.Contains("Top", StringComparison.Ordinal)) { height = Math.Max(MinHeight, Height - vertical); top += Height - height; }
        if (edges.Contains("Bottom", StringComparison.Ordinal)) height = Math.Max(MinHeight, Height + vertical);
        Left = left; Top = top; Width = width; Height = height;
    }
    private void ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.IsHome) or nameof(PlayerViewModel.IsFullscreen) or nameof(PlayerViewModel.IsMiniPlayer)) ApplyLayout();
    }
    private void PreferencesChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName is nameof(PreferencesService.PlaylistExpanded) or nameof(PreferencesService.InformationExpanded) or nameof(PreferencesService.AlwaysOnTop)) ApplyLayout(); }
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) { if (_viewModel is not null) ApplyLayout(); }
    private void ApplyLayout()
    {
        bool immersive = _viewModel.IsFullscreen || _viewModel.IsMiniPlayer;
        var layout = ShellLayout.Calculate(ActualWidth, _viewModel.Preferences.PlaylistExpanded);
        Header.Visibility = Sidebar.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
        HeaderRow.Height = new GridLength(immersive ? 0 : 64);
        NavigationColumn.Width = new GridLength(immersive ? 0 : layout.NavigationWidth);
        HomeLabel.Visibility = HistoryLabel.Visibility = SettingsLabel.Visibility = layout.CompactNavigation ? Visibility.Collapsed : Visibility.Visible;
        RecentCard.MaxHeight = layout.CompactNavigation ? 0 : double.PositiveInfinity;
        PlaylistPanel.Visibility = !immersive && layout.ShowPlaylist ? Visibility.Visible : Visibility.Collapsed;
        PlaylistColumn.Width = new GridLength(!immersive && layout.ShowPlaylist ? 312 : 0);
        HomeShell.Margin = immersive ? new Thickness(0) : new Thickness(0, 4, 18, 18);
        InformationRow.Height = new GridLength(!immersive && _viewModel.Preferences.InformationExpanded ? layout.StackInformation ? 300 : 210 : 0);
        InformationArea.Visibility = !immersive && _viewModel.Preferences.InformationExpanded ? Visibility.Visible : Visibility.Collapsed;
        bool stack = layout.StackInformation;
        QuickColumn.Width = new GridLength(stack ? 0 : 340);
        QuickRow.Height = new GridLength(stack ? 110 : 0);
        Grid.SetColumnSpan(InformationCard, stack ? 2 : 1);
        InformationCard.Margin = stack ? new Thickness(0, 0, 0, 10) : new Thickness(0, 0, 18, 0);
        Grid.SetColumn(QuickSettings, stack ? 0 : 1); Grid.SetRow(QuickSettings, stack ? 1 : 0); Grid.SetColumnSpan(QuickSettings, stack ? 2 : 1);
        QuickSettings.Padding = stack ? new Thickness(20, 10, 20, 10) : new Thickness(20, 16, 20, 16);
        Grid.SetColumnSpan(ResumeSetting, stack ? 1 : 2);
        ResumeSetting.Margin = stack ? new Thickness(0, 0, 20, 0) : new Thickness(0);
        Grid.SetRow(SpeedSetting, stack ? 0 : 2); Grid.SetColumn(SpeedSetting, stack ? 1 : 0); Grid.SetColumnSpan(SpeedSetting, stack ? 1 : 2);
        QuickSeparator.Visibility = stack ? Visibility.Collapsed : Visibility.Visible;
        Topmost = _viewModel.IsMiniPlayer || _viewModel.Preferences.AlwaysOnTop;
        var chrome = WindowChrome.GetWindowChrome(this);
        chrome.CaptionHeight = immersive ? 0 : 64;
        chrome.ResizeBorderThickness = new Thickness(_viewModel.IsFullscreen ? 0 : 6);
        PlayerFrame.BorderThickness = new Thickness(immersive ? 0 : 1);
        _overlay?.SyncBounds();
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void CloseSearch_Click(object sender, RoutedEventArgs e) => _viewModel.SearchText = "";
    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var theme in _viewModel.Themes)
        {
            var item = new MenuItem { Header = theme, IsCheckable = true, IsChecked = _viewModel.Preferences.Theme == theme };
            item.Click += (_, _) => _viewModel.SetThemeCommand.Execute(theme); menu.Items.Add(item);
        }
        menu.PlacementTarget = ThemeButton; menu.IsOpen = true;
    }
    private void QueueMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: VideoItem video } button) return;
        var menu = new ContextMenu { PlacementTarget = button };
        void Add(string text, System.Windows.Input.ICommand command)
        { menu.Items.Add(new MenuItem { Header = text, Command = command, CommandParameter = video }); }
        Add("播放", _viewModel.PlayVideoCommand); Add("打开文件所在位置", _viewModel.RevealCommand);
        Add("上移", _viewModel.MoveUpCommand); Add("下移", _viewModel.MoveDownCommand); Add("从播放列表移除", _viewModel.RemoveVideoCommand);
        menu.IsOpen = true;
    }
    private void Playlist_MouseDown(object sender, MouseButtonEventArgs e) => _dragOrigin = e.GetPosition(PlaylistList);
    private void Playlist_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || e.OriginalSource is not DependencyObject original) return;
        var point = e.GetPosition(PlaylistList);
        if (Math.Abs(point.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (ItemsControl.ContainerFromElement(PlaylistList, original) is ListBoxItem { DataContext: VideoItem video })
            DragDrop.DoDragDrop(PlaylistList, new DataObject(typeof(VideoItem), video), DragDropEffects.Move);
    }
    private void Playlist_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(VideoItem)) || e.OriginalSource is not DependencyObject original) return;
        if (ItemsControl.ContainerFromElement(PlaylistList, original) is ListBoxItem { DataContext: VideoItem target })
            _viewModel.Reorder((VideoItem)e.Data.GetData(typeof(VideoItem)), target);
        e.Handled = true;
    }
    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closeCompleted) { base.OnClosing(e); return; }
        if (_viewModel.IsMiniPlayer && !_closing) { e.Cancel = true; ToggleMiniPlayer(); return; }
        base.OnClosing(e);
        if (e.Cancel) return;
        e.Cancel = true;
        await CloseSessionAsync();
    }
    internal Task CloseSessionAsync() => _shutdownTask ??= CloseCoreAsync();
    private async Task CloseCoreAsync()
    {
        _viewModel.Preferences.MainGeometry = _viewModel.IsMiniPlayer ? _beforeMini : _viewModel.IsFullscreen ? _beforeFullscreen : WindowPlacement.Capture(this);
        if (_viewModel.IsMiniPlayer) _viewModel.Preferences.MiniGeometry = WindowPlacement.Capture(this) with { Maximized = false };
        _closing = true; _overlay?.Close();
        try { await _viewModel.ShutdownAsync(); }
        catch (Exception ex) { Log.Error(ex, "退出时保存播放记录失败"); }
        finally
        {
            _viewModel.Dispose();
            _viewModel.PropertyChanged -= ViewModelChanged; _viewModel.Preferences.PropertyChanged -= PreferencesChanged; _theme.Dispose();
            _host.StopRendering(); _host.Dispose(); _engine.Dispose();
            _closeCompleted = true; Close();
        }
    }
}
